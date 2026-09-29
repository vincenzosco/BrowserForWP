' BrowserForWP — a minimal HTTP/1.1 client carried over BrowserForWP's own
' TLS 1.3 channel.
'
' WHY NOT Windows.Web.Http
' ------------------------
' Windows.Web.Http would be far less code, but it goes through the OS network
' stack, which on Windows Phone 8.1 stops at TLS 1.2. Fetching over
' HttpClient13 is what makes the TLS 1.3 work actually observable to the rest of
' the app, so the HTTP parsing is written here on purpose.
'
' SCOPE
' -----
' Deliberately small and HTTP/1.1 only, matching the single protocol we offer in
' ALPN. Supported: GET and POST, Content-Length, chunked transfer-encoding,
' redirect following, a response size cap. Not supported: HTTP/2 (we do not
' advertise it), caching, cookies, compression. Anything outside that range
' raises rather than returning something that looks like a valid response.

Imports System.Collections.Generic
Imports System.Text
Imports System.Threading.Tasks
Imports BrowserForWP.Net.Tls13

Namespace Http

    ''' <summary>A complete HTTP response.</summary>
    Public NotInheritable Class HttpResponse

        Public Property StatusCode As Integer
        Public Property ReasonPhrase As String
        Public Property Headers As Dictionary(Of String, String)
        Public Property Body As Byte()
        Public Property FinalUrl As String

        Public Function BodyAsText() As String
            If Body Is Nothing Then Return String.Empty
            Return Encoding.UTF8.GetString(Body, 0, Body.Length)
        End Function

        Friend Shared Function NewHeaderMap() As Dictionary(Of String, String)
            ' Ordinal-ignore-case: HTTP header names are case-insensitive, and
            ' servers vary the casing freely.
            Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        End Function
    End Class

    Public NotInheritable Class HttpClient13
        Implements IDisposable

        ''' <summary>
        ''' Cap on a response body. DoH replies are tiny; this exists so a hostile
        ''' or broken server cannot exhaust a phone's memory.
        ''' </summary>
        Public Const MaxResponseBytes As Integer = 4 * 1024 * 1024

        Public Const MaxRedirects As Integer = 5

        Private ReadOnly _host As String
        Private ReadOnly _port As Integer
        Private _client As Tls13Client
        Private _inbound As New List(Of Byte)()
        Private _endOfStream As Boolean

        Public Sub New(host As String, Optional port As Integer = 443)
            _host = host
            _port = port
        End Sub

        Public ReadOnly Property SessionInfo As TlsSessionInfo
            Get
                Return If(_client Is Nothing, Nothing, _client.SessionInfo)
            End Get
        End Property

        ''' <summary>Performs a GET, following redirects.</summary>
        Public Async Function GetAsync(url As String) As Task(Of HttpResponse)
            Return Await SendAsync("GET", url, Nothing, Nothing, 0)
        End Function

        ''' <summary>Performs a POST with a body.</summary>
        Public Async Function PostAsync(url As String, contentType As String,
                                        body As Byte()) As Task(Of HttpResponse)
            Return Await SendAsync("POST", url, contentType, body, 0)
        End Function

        Private Async Function SendAsync(method As String, url As String,
                                         contentType As String, body As Byte(),
                                         redirectCount As Integer) As Task(Of HttpResponse)
            If redirectCount > MaxRedirects Then
                Throw New HttpProtocolException("too many redirects")
            End If

            Dim target = ParsedUrl.Parse(url)
            Await EnsureConnectedAsync(target)

            Dim request As New StringBuilder()
            request.Append(method).Append(" ").Append(target.PathAndQuery)
            request.Append(" HTTP/1.1").Append(vbCrLf)
            request.Append("Host: ").Append(target.HostHeader).Append(vbCrLf)
            request.Append("User-Agent: BrowserForWP/0.1").Append(vbCrLf)
            request.Append("Accept: */*").Append(vbCrLf)
            request.Append("Connection: keep-alive").Append(vbCrLf)
            If body IsNot Nothing Then
                request.Append("Content-Type: ").Append(contentType).Append(vbCrLf)
                request.Append("Content-Length: ").Append(body.Length).Append(vbCrLf)
            End If
            request.Append(vbCrLf)

            Dim head = Encoding.UTF8.GetBytes(request.ToString())
            Dim payload As Byte()
            If body IsNot Nothing Then
                payload = New Byte(head.Length + body.Length - 1) {}
                Array.Copy(head, 0, payload, 0, head.Length)
                Array.Copy(body, 0, payload, head.Length, body.Length)
            Else
                payload = head
            End If

            Await _client.WriteAsync(payload)

            Dim response = Await ReadResponseAsync(target, method)
            response.FinalUrl = url

            ' Redirects are followed here rather than surfaced, because every
            ' caller in this app wants the resource, not the hop.
            If response.StatusCode >= 300 AndAlso response.StatusCode < 400 Then
                Dim location As String = Nothing
                If response.Headers.TryGetValue("Location", location) Then
                    Dim nextUrl = If(location.StartsWith("http"), location,
                                     "https://" & target.Host & location)
                    Return Await SendAsync("GET", nextUrl, Nothing, Nothing, redirectCount + 1)
                End If
            End If

            Return response
        End Function

        ''' <summary>
        ''' Connects if needed, and reconnects when the target host changes — a
        ''' redirect can cross hosts, and reusing the old channel would send the
        ''' request to the wrong server (or fail certificate checks confusingly).
        ''' </summary>
        Private Async Function EnsureConnectedAsync(target As ParsedUrl) As Task
            If _client IsNot Nothing AndAlso _client.IsConnected AndAlso
               String.Equals(_client.HostName, target.Host, StringComparison.OrdinalIgnoreCase) Then
                Return
            End If

            Dispose()
            _inbound.Clear()
            _endOfStream = False

            _client = New Tls13Client(target.Host)
            Await _client.ConnectAsync(target.Host, target.Port)
        End Function

        Private Async Function ReadResponseAsync(target As ParsedUrl,
                                                 method As String) As Task(Of HttpResponse)
            Dim headerBytes = Await ReadUntilDoubleCrLfAsync()
            Dim headerText = Encoding.UTF8.GetString(headerBytes, 0, headerBytes.Length)
            Dim lines = headerText.Split(New String() {vbCrLf}, StringSplitOptions.None)

            If lines.Length = 0 Then Throw New HttpProtocolException("empty response")

            Dim statusParts = lines(0).Split(" "c)
            If statusParts.Length < 2 Then
                Throw New HttpProtocolException("malformed status line: " & lines(0))
            End If

            Dim response As New HttpResponse()
            Dim statusCode As Integer
            If Not Integer.TryParse(statusParts(1), statusCode) Then
                Throw New HttpProtocolException("malformed status code: " & lines(0))
            End If
            response.StatusCode = statusCode
            response.ReasonPhrase = If(statusParts.Length > 2, statusParts(2), String.Empty)
            response.Headers = HttpResponse.NewHeaderMap()

            For i As Integer = 1 To lines.Length - 1
                Dim line = lines(i)
                If line.Length = 0 Then Continue For
                Dim colon = line.IndexOf(":"c)
                If colon <= 0 Then Continue For
                Dim name = line.Substring(0, colon).Trim()
                Dim value = line.Substring(colon + 1).Trim()
                ' Duplicates are joined rather than overwritten; folding is legal
                ' and some servers send several Set-Cookie lines.
                Dim existing As String = Nothing
                If response.Headers.TryGetValue(name, existing) Then
                    response.Headers(name) = existing & ", " & value
                Else
                    response.Headers(name) = value
                End If
            Next

            ' HEAD and 204/304 carry no body, whatever the headers claim.
            If method = "HEAD" OrElse statusCode = 204 OrElse statusCode = 304 Then
                response.Body = New Byte() {}
                Return response
            End If

            response.Body = Await ReadBodyAsync(response.Headers)
            Return response
        End Function

        Private Async Function ReadBodyAsync(headers As Dictionary(Of String, String)) As Task(Of Byte())
            Dim transferEncoding As String = Nothing
            If headers.TryGetValue("Transfer-Encoding", transferEncoding) AndAlso
               transferEncoding.ToLowerInvariant().Contains("chunked") Then
                Return Await ReadChunkedBodyAsync()
            End If

            Dim contentLength As String = Nothing
            If headers.TryGetValue("Content-Length", contentLength) Then
                Dim length As Integer
                If Not Integer.TryParse(contentLength, length) Then
                    Throw New HttpProtocolException("malformed Content-Length: " & contentLength)
                End If
                If length > MaxResponseBytes Then
                    Throw New HttpProtocolException("response body too large: " & length)
                End If
                Return Await ReadExactlyAsync(length)
            End If

            ' No framing information: read until the server closes. This is the
            ' only case where "read to EOF" is correct, and it is the reason
            ' Connection: close exists.
            Return Await ReadToEndAsync()
        End Function

        Private Async Function ReadChunkedBodyAsync() As Task(Of Byte())
            Dim output As New List(Of Byte)()
            Do
                Dim sizeLine = Await ReadLineAsync()
                Dim semicolon = sizeLine.IndexOf(";"c)
                If semicolon >= 0 Then sizeLine = sizeLine.Substring(0, semicolon)
                Dim size As Integer
                If Not Integer.TryParse(sizeLine.Trim(),
                                        Globalization.NumberStyles.HexNumber,
                                        Globalization.CultureInfo.InvariantCulture, size) Then
                    Throw New HttpProtocolException("malformed chunk size: " & sizeLine)
                End If

                If size = 0 Then
                    ' Trailer headers follow, terminated by a blank line.
                    Do
                        Dim trailer = Await ReadLineAsync()
                        If trailer.Length = 0 Then Exit Do
                    Loop
                    Return output.ToArray()
                End If

                If output.Count + size > MaxResponseBytes Then
                    Throw New HttpProtocolException("chunked body exceeds the size cap")
                End If

                output.AddRange(Await ReadExactlyAsync(size))
                Dim terminator = Await ReadLineAsync()
                If terminator.Length <> 0 Then
                    Throw New HttpProtocolException("missing CRLF after a chunk")
                End If
            Loop
        End Function

        Private Async Function ReadUntilDoubleCrLfAsync() As Task(Of Byte())
            Dim output As New List(Of Byte)()
            Do
                Dim b = Await ReadByteAsync()
                output.Add(b)
                Dim n = output.Count
                If n >= 4 AndAlso output(n - 1) = 10 AndAlso output(n - 2) = 13 AndAlso
                   output(n - 3) = 10 AndAlso output(n - 4) = 13 Then
                    Return output.ToArray()
                End If
                If n > 64 * 1024 Then
                    Throw New HttpProtocolException("response header block exceeds 64 KiB")
                End If
            Loop
        End Function

        Private Async Function ReadLineAsync() As Task(Of String)
            Dim output As New List(Of Byte)()
            Do
                Dim b = Await ReadByteAsync()
                ' Tolerate a bare LF: RFC 7230 is explicit that recipients may.
                If b = 10 Then
                    If output.Count > 0 AndAlso output(output.Count - 1) = 13 Then
                        output.RemoveAt(output.Count - 1)
                    End If
                    Return Encoding.UTF8.GetString(output.ToArray(), 0, output.Count)
                End If
                output.Add(b)
            Loop
        End Function

        Private Async Function ReadByteAsync() As Task(Of Byte)
            If _inbound.Count = 0 Then Await FillAsync()
            If _inbound.Count = 0 Then Throw New HttpProtocolException("connection closed unexpectedly")
            Dim b = _inbound(0)
            _inbound.RemoveAt(0)
            Return b
        End Function

        Private Async Function ReadExactlyAsync(count As Integer) As Task(Of Byte())
            While _inbound.Count < count
                Await FillAsync()
                If _inbound.Count = 0 AndAlso _endOfStream Then
                    Throw New HttpProtocolException(
                        "connection closed after " & _inbound.Count & " of " & count & " expected bytes")
                End If
            End While
            Dim result(count - 1) As Byte
            For i As Integer = 0 To count - 1
                result(i) = _inbound(i)
            Next
            _inbound.RemoveRange(0, count)
            Return result
        End Function

        Private Async Function ReadToEndAsync() As Task(Of Byte())
            Do
                Await FillAsync()
            Loop While Not _endOfStream
            Dim result = _inbound.ToArray()
            _inbound.Clear()
            Return result
        End Function

        Private Async Function FillAsync() As Task
            Try
                Dim chunk = Await _client.ReadAsync()
                If chunk IsNot Nothing AndAlso chunk.Length > 0 Then
                    _inbound.AddRange(chunk)
                    If _inbound.Count > MaxResponseBytes Then
                        Throw New HttpProtocolException("response exceeds the size cap")
                    End If
                    Return
                End If
                _endOfStream = True
            Catch ex As TlsProtocolException
                ' close_notify surfaces as a protocol exception from the record
                ' layer. For HTTP that is a normal end of body, not an error.
                If ex.Message.Contains("close_notify") Then
                    _endOfStream = True
                Else
                    Throw
                End If
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _client IsNot Nothing Then
                _client.Dispose()
                _client = Nothing
            End If
        End Sub
    End Class

    ''' <summary>A parsed absolute HTTPS URL.</summary>
    Public NotInheritable Class ParsedUrl

        Public Property Scheme As String
        Public Property Host As String
        Public Property Port As Integer
        Public Property PathAndQuery As String

        ''' <summary>The Host header value, carrying the port only when non-default.</summary>
        Public ReadOnly Property HostHeader As String
            Get
                Return If(Port = 443, Host, Host & ":" & Port)
            End Get
        End Property

        Public Shared Function Parse(url As String) As ParsedUrl
            If String.IsNullOrEmpty(url) Then Throw New ArgumentException("url required", "url")

            Dim result As New ParsedUrl()
            Dim rest = url

            Dim schemeEnd = url.IndexOf("://", StringComparison.Ordinal)
            If schemeEnd > 0 Then
                result.Scheme = url.Substring(0, schemeEnd).ToLowerInvariant()
                rest = url.Substring(schemeEnd + 3)
            Else
                result.Scheme = "https"
            End If

            ' Refuse plaintext rather than silently upgrading: quietly turning
            ' http:// into https:// hides a caller's mistake.
            If result.Scheme <> "https" Then
                Throw New HttpProtocolException("only https is supported, got " & result.Scheme)
            End If

            Dim pathStart = rest.IndexOf("/"c)
            Dim authority = If(pathStart < 0, rest, rest.Substring(0, pathStart))
            result.PathAndQuery = If(pathStart < 0, "/", rest.Substring(pathStart))

            Dim colon = authority.IndexOf(":"c)
            If colon >= 0 Then
                result.Host = authority.Substring(0, colon)
                ' Named `portNumber` and not `port`: this class has a Port property,
                ' and a local called port hides it for the whole method -- which is
                ' how a check on a member turns into a variable declared later.
                Dim portNumber As Integer
                If Not Integer.TryParse(authority.Substring(colon + 1), portNumber) Then
                    Throw New HttpProtocolException("malformed port in " & url)
                End If
                result.Port = portNumber
            Else
                result.Host = authority
                result.Port = 443
            End If

            If String.IsNullOrEmpty(result.Host) Then
                Throw New HttpProtocolException("no host in " & url)
            End If
            Return result
        End Function
    End Class

    ''' <summary>A transport-level deviation from HTTP.</summary>
    Public Class HttpProtocolException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

End Namespace
