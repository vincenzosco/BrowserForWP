' BrowserForWP — IDocumentFetcher over the app's own TLS 1.3 transport.
'
' App layer on purpose: Core must not know about BrowserForWP.Net. This is the
' first code path where a PAGE LOAD travels over Tls13Client rather than through
' Schannel, which is the only way anything in this product can load a page above
' TLS 1.2.
'
' Two deliberate limits: no Accept-Encoding is sent, because HttpClient13 cannot
' decompress gzip and a compressed body would arrive as binary; and redirects are
' followed explicitly, because GetAsync returns whatever the socket returned.

Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine.Native
Imports BrowserForWP.Net.Dns
Imports BrowserForWP.Net.Http
Imports BrowserForWP.Net.Tls13

Namespace Diagnostics

    ''' <summary>Fetches documents over DoH + TLS 1.3. Never throws.</summary>
    Public NotInheritable Class NetDocumentFetcher
        Implements IDocumentFetcher

        Private Const MaxRedirects As Integer = 3

        ''' <summary>
        ''' Optional pin table. A host the user has pinned is refused unless the leaf
        ''' certificate's SPKI matches, and that happens before any body is decoded.
        ''' It reuses CertificateValidator.VerifyPin rather than comparing anything
        ''' here, so this and the TLS probe cannot drift apart.
        '''
        ''' Why this matters more here than anywhere else: the handshake already
        ''' validates chain trust and hostname, but a pin is what defends against a
        ''' trusted CA that has been compromised, which chain validation cannot. And
        ''' until this class existed, nothing but the probe used this transport, so
        ''' README's claim that pinning protects the app's own transport layer was
        ''' true by accident. It is true by construction now.
        ''' </summary>
        Private ReadOnly _pinTable As PinStore

        Public Sub New(Optional pinTable As PinStore = Nothing)
            _pinTable = pinTable
        End Sub

        Public Async Function FetchAsync(url As String, dohUrl As String) As Task(Of DocumentResponse) Implements IDocumentFetcher.FetchAsync
            Dim parsedUri As Uri = Nothing
            If String.IsNullOrEmpty(url) OrElse Not Uri.TryCreate(url, UriKind.Absolute, parsedUri) Then
                Dim invalidResult As New DocumentResponse()
                invalidResult.ErrorMessage = "invalid url"
                Return invalidResult
            End If
            If parsedUri.Scheme <> "https" AndAlso parsedUri.Scheme <> "http" Then
                Dim schemeResult As New DocumentResponse()
                schemeResult.ErrorMessage = "unsupported scheme"
                Return schemeResult
            End If

            Dim dohEndpoint As String = If(String.IsNullOrEmpty(dohUrl), DohResolver.DefaultServerUrl, dohUrl)
            Try
                Using doh As New DohResolver(dohEndpoint)
                    Try
                        Await doh.ResolveAsync(parsedUri.Host)
                    Catch ex As Exception
                        ' Resolution failure is not fatal: the handshake below still
                        ' tries the OS path and reports a real error if it fails.
                    End Try
                End Using

                Return Await ReadWithRedirectsAsync(parsedUri.ToString(), 0)
            Catch ex As Exception
                Dim failureResult As New DocumentResponse()
                failureResult.ErrorMessage = ex.Message
                Return failureResult
            End Try
        End Function

        Private Async Function ReadWithRedirectsAsync(url As String, depth As Integer) As Task(Of DocumentResponse)
            Dim result As New DocumentResponse()
            If depth > MaxRedirects Then
                result.ErrorMessage = "too many redirects"
                Return result
            End If

            Dim parsedUri As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, parsedUri) Then
                result.ErrorMessage = "invalid url"
                Return result
            End If

            Dim port As Integer = parsedUri.Port
            If port <= 0 Then port = 443

            Using client As New HttpClient13(parsedUri.Host, port)
                Dim httpResponse As HttpResponse = Await client.GetAsync(url)
                result.StatusCode = httpResponse.StatusCode
                result.FinalUrl = httpResponse.FinalUrl

                If _pinTable IsNot Nothing AndAlso _pinTable.Contains(parsedUri.Host) Then
                    Dim sessionInfo As TlsSessionInfo = client.SessionInfo
                    ' A certificate that cannot be read is not a pass. The user stored
                    ' a pin for this host; it is verified or the fetch fails.
                    If sessionInfo Is Nothing OrElse
                       Not CertificateValidator.VerifyPin(sessionInfo.LeafCertificateDer, parsedUri.Host, _pinTable) Then
                        result.ErrorMessage = "pin mismatch for " & parsedUri.Host
                        Return result
                    End If
                End If

                Dim locationHeader As String = HeaderValue(httpResponse, "Location")
                If httpResponse.StatusCode >= 300 AndAlso httpResponse.StatusCode < 400 AndAlso Not String.IsNullOrEmpty(locationHeader) Then
                    Dim nextUrl As String = ResolveRelative(url, locationHeader)
                    Return Await ReadWithRedirectsAsync(nextUrl, depth + 1)
                End If

                Dim contentType As String = HeaderValue(httpResponse, "Content-Type")
                result.ContentType = MediaTypeOf(contentType)
                result.Charset = CharsetOf(contentType)

                If result.StatusCode <> 200 Then
                    result.ErrorMessage = "HTTP " & result.StatusCode
                    Return result
                End If
                If Not result.IsHtml Then
                    result.ErrorMessage = "not a document: " & If(String.IsNullOrEmpty(result.ContentType), "(no content-type)", result.ContentType)
                    Return result
                End If

                result.Text = DecodeBody(httpResponse.Body, result.Charset)
                Return result
            End Using
        End Function

        ''' <summary>Case-insensitive header read: servers choose their own casing.</summary>
        Private Shared Function HeaderValue(response As HttpResponse, headerName As String) As String
            If response Is Nothing OrElse response.Headers Is Nothing Then Return String.Empty
            For Each pairItem In response.Headers
                If String.Equals(pairItem.Key, headerName, StringComparison.OrdinalIgnoreCase) Then
                    Return pairItem.Value
                End If
            Next
            Return String.Empty
        End Function

        Private Shared Function MediaTypeOf(contentType As String) As String
            If String.IsNullOrEmpty(contentType) Then Return String.Empty
            Dim semicolon As Integer = contentType.IndexOf(";"c)
            Dim mediaType As String = If(semicolon < 0, contentType, contentType.Substring(0, semicolon))
            Return mediaType.Trim().ToLowerInvariant()
        End Function

        Private Shared Function CharsetOf(contentType As String) As String
            If String.IsNullOrEmpty(contentType) Then Return String.Empty
            Dim lowered As String = contentType.ToLowerInvariant()
            Dim at As Integer = lowered.IndexOf("charset=", StringComparison.Ordinal)
            If at < 0 Then Return String.Empty
            Dim rest As String = contentType.Substring(at + 8).Trim()
            Dim semicolon As Integer = rest.IndexOf(";"c)
            If semicolon >= 0 Then rest = rest.Substring(0, semicolon)
            Return rest.Trim().Trim(""""c).ToLowerInvariant()
        End Function

        ''' <summary>
        ''' Decode the body. UTF-8 is the default; only a charset the platform can
        ''' actually decode is honoured, and anything else falls back to UTF-8
        ''' rather than guessing.
        ''' </summary>
        Private Shared Function DecodeBody(body As Byte(), charset As String) As String
            If body Is Nothing OrElse body.Length = 0 Then Return String.Empty
            If charset = "iso-8859-1" OrElse charset = "latin1" OrElse charset = "windows-1252" Then
                Dim latinEncoding As System.Text.Encoding = TryGetEncoding("ISO-8859-1")
                If latinEncoding IsNot Nothing Then
                    Return latinEncoding.GetString(body, 0, body.Length)
                End If
            End If
            Return System.Text.Encoding.UTF8.GetString(body, 0, body.Length)
        End Function

        ''' <summary>
        ''' Asks the platform for an encoding, and accepts "no" as an answer.
        '''
        ''' It compiles: the guest build accepts System.Text.Encoding.GetEncoding,
        ''' so this is NOT the BC30456 profile-gap family, and dropping the latin1
        ''' branch on compile evidence would have been wrong. Whether a code page
        ''' name RESOLVES at run time is a separate question, and Microsoft's own
        ''' documentation for Encoding.GetEncoding states that unsupported code
        ''' pages throw -- ArgumentException for some, NotSupportedException for
        ''' others -- and that callers must therefore catch rather than trust. Only
        ''' a handset could settle which way it goes here, and there is no handset,
        ''' so the code asks instead of assuming: a page in latin1 decodes as latin1
        ''' where the platform has that encoding, and falls back to UTF-8 where it
        ''' does not.
        ''' </summary>
        Private Shared Function TryGetEncoding(name As String) As System.Text.Encoding
            Try
                Return System.Text.Encoding.GetEncoding(name)
            Catch ex As Exception
                Return Nothing
            End Try
        End Function

        Private Shared Function ResolveRelative(baseUrl As String, reference As String) As String
            Dim absoluteUri As Uri = Nothing
            If Uri.TryCreate(New Uri(baseUrl), reference, absoluteUri) Then Return absoluteUri.ToString()
            Return reference
        End Function
    End Class

End Namespace
