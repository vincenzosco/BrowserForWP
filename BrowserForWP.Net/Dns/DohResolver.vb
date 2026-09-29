' BrowserForWP — DNS over HTTPS (RFC 8484) with an RFC 1035 wire format.
'
' WHY DoH
' -------
' A Windows Phone 8.1 handset typically resolves through whatever the local
' network offers, which on a stale or hostile network means an old resolver, or
' worse, one that injects answers. Since BrowserForWP already has its own modern
' TLS channel, resolution can ride in it: the DNS message is the same RFC 1035
' format it has always been, only the transport is new. The benefit is that
' resolution inherits TLS 1.3, not that the records change.
'
' THE BOOTSTRAP PROBLEM — read this before "fixing" it
' ---------------------------------------------------
' DoH resolves names over HTTPS, and HTTPS needs a name resolved. That circle has
' to be broken somewhere:
'
'   * The DoH server's own host name is resolved ONCE by the OS resolver, via
'     StreamSocket.ConnectAsync's hostname overload. That single lookup is the
'     only one that leaves the app's own path, and it cannot be avoided without
'     shipping a hard-coded IP list — which then breaks whenever the provider
'     renumbers.
'   * Every other name goes through DoH.
'
' This is a deliberate, documented trade-off rather than an oversight.

Imports System.Collections.Generic
Imports System.Text
Imports System.Threading.Tasks
Imports BrowserForWP.Crypto
Imports BrowserForWP.Net.Http

Namespace Dns

    ''' <summary>One resolved answer: an address plus how long it may be cached.</summary>
    Public NotInheritable Class DnsAnswer

        Public Sub New(address As String, ttlSeconds As Integer, isIpv6 As Boolean)
            Me.Address = address
            Me.TtlSeconds = ttlSeconds
            Me.IsIpv6 = isIpv6
        End Sub

        Public ReadOnly Address As String
        Public ReadOnly TtlSeconds As Integer
        Public ReadOnly IsIpv6 As Boolean
    End Class

    Public NotInheritable Class DohResolver
        Implements IDisposable

        Public Const DefaultServerUrl As String = "https://cloudflare-dns.com/dns-query"

        ''' <summary>Record type codes we ask for.</summary>
        Private Const TypeA As Integer = 1
        Private Const TypeAaaa As Integer = 28
        Private Const ClassInternet As Integer = 1

        Private ReadOnly _serverUrl As String
        Private _client As HttpClient13
        Private ReadOnly _cache As New Dictionary(Of String, CachedEntry)()

        ''' <summary>Cached answers with expiry (TTL respected, floor 60s, cap 1h).</summary>
        Private NotInheritable Class CachedEntry

            Public Sub New(answers As IList(Of DnsAnswer), expiresUtc As DateTime)
                Me.Answers = answers
                Me.ExpiresUtc = expiresUtc
            End Sub

            Public ReadOnly Answers As IList(Of DnsAnswer)
            Public ReadOnly ExpiresUtc As DateTime
        End Class

        Public Sub New(Optional serverUrl As String = Nothing)
            _serverUrl = If(String.IsNullOrEmpty(serverUrl), DefaultServerUrl, serverUrl)
        End Sub

        Public ReadOnly Property ServerUrl As String
            Get
                Return _serverUrl
            End Get
        End Property

        ''' <summary>
        ''' Resolves a host name. A and AAAA are queried; AAAA is attempted first
        ''' and its failure is not fatal, because plenty of networks are v4-only.
        ''' </summary>
        Public Async Function ResolveAsync(host As String) As Task(Of IList(Of DnsAnswer))
            If String.IsNullOrEmpty(host) Then Throw New ArgumentException("host required", "host")

            Dim cacheKey As String = host.Trim().ToLowerInvariant()
            Dim hit As CachedEntry = Nothing
            If _cache.TryGetValue(cacheKey, hit) Then
                If hit.ExpiresUtc > DateTime.UtcNow Then
                    Return hit.Answers
                End If
                _cache.Remove(cacheKey)
            End If

            Dim answers As New List(Of DnsAnswer)()

            Try
                answers.AddRange(Await QueryAsync(host, TypeAaaa))
            Catch ex As HttpProtocolException
                ' No IPv6 records is a normal answer, not a failure.
            End Try

            Try
                answers.AddRange(Await QueryAsync(host, TypeA))
            Catch ex As HttpProtocolException
            End Try

            If answers.Count = 0 Then
                Throw New HttpProtocolException("no address records for " & host)
            End If
            Dim ttlFloor As Integer = 3600
            For Each ansItem In answers
                If ansItem.TtlSeconds > 0 AndAlso ansItem.TtlSeconds < ttlFloor Then
                    ttlFloor = ansItem.TtlSeconds
                End If
            Next
            If ttlFloor < 60 Then
                ttlFloor = 60
            End If
            If ttlFloor > 3600 Then
                ttlFloor = 3600
            End If
            _cache(cacheKey) = New CachedEntry(answers, DateTime.UtcNow.AddSeconds(ttlFloor))
            Return answers
        End Function

        ''' <summary>Clear the TTL cache (used when the DoH server changes).</summary>
        Public Sub ClearCache()
            _cache.Clear()
        End Sub

        Private Async Function QueryAsync(host As String, recordType As Integer) As Task(Of IList(Of DnsAnswer))
            Dim transactionId As UShort = BitConverter.ToUInt16(WinRtCrypto.RandomBytes(2), 0)
            Dim query = BuildQuery(host, transactionId, recordType)

            EnsureClient()

            Dim response = Await _client.PostAsync(_serverUrl, "application/dns-message", query)
            If response.StatusCode <> 200 Then
                Throw New HttpProtocolException(
                    "DoH server returned HTTP " & response.StatusCode & ": " & response.BodyAsText())
            End If

            Return ParseResponse(response.Body, recordType)
        End Function

        ''' <summary>
        ''' Creates the HTTP client for the DoH endpoint, once.
        '''
        ''' Note what does NOT happen here: no name is resolved eagerly. The
        ''' HttpClient13 connects lazily on first use, and that connect is the
        ''' single place the OS resolver is consulted — for the DoH provider's own
        ''' host name. See the file header for why that circle cannot be closed.
        ''' </summary>
        Private Sub EnsureClient()
            If _client IsNot Nothing Then Return
            Dim target = ParsedUrl.Parse(_serverUrl)
            _client = New HttpClient13(target.Host, target.Port)
        End Sub

        ''' <summary>
        ''' Builds a standard single-question query (RFC 1035 §4.1). The wire format
        ''' is unchanged by DoH; only the transport is new.
        ''' </summary>
        Public Shared Function BuildQuery(host As String, transactionId As UShort,
                                          Optional recordType As Integer = TypeA) As Byte()
            If String.IsNullOrEmpty(host) Then Throw New ArgumentException("host required", "host")

            Dim writer As New List(Of Byte)()

            writer.Add(CByte((CInt(transactionId) >> 8) And &HFF))
            writer.Add(CByte(CInt(transactionId) And &HFF))
            writer.Add(1)                                  ' flags: standard query, recursion desired
            writer.Add(0)
            writer.Add(0)                                  ' QDCOUNT high
            writer.Add(1)                                  ' QDCOUNT low
            writer.Add(0) : writer.Add(0)                  ' ANCOUNT
            writer.Add(0) : writer.Add(0)                  ' NSCOUNT
            writer.Add(0) : writer.Add(0)                  ' ARCOUNT

            ' QNAME: each label length-prefixed, terminated by a zero length.
            For Each label In host.TrimEnd("."c).Split("."c)
                Dim bytes = AsciiBytes(label)
                If bytes.Length = 0 OrElse bytes.Length > 63 Then
                    Throw New HttpProtocolException("invalid DNS label: '" & label & "'")
                End If
                writer.Add(CByte(bytes.Length))
                writer.AddRange(bytes)
            Next
            writer.Add(0)

            writer.Add(CByte((recordType >> 8) And &HFF))
            writer.Add(CByte(recordType And &HFF))
            writer.Add(CByte((ClassInternet >> 8) And &HFF))
            writer.Add(CByte(ClassInternet And &HFF))

            Return writer.ToArray()
        End Function

        ''' <summary>
        ''' The bytes of an ASCII string, without relying on Encoding.ASCII.
        '''
        ''' System.Text.Encoding.ASCII is NOT available in the .NET for Windows
        ''' Store apps profile this project targets — it is backed by
        ''' ASCIIEncoding, which the profile removes — so the compiler rejects it
        ''' with BC30456 ("'ASCII' is not a member of 'System.Text.Encoding'")
        ''' even though the property exists in full .NET.
        '''
        ''' A DNS QNAME label is ASCII by definition, so anything above 0x7F is a
        ''' caller error (an un-punycoded IDN) rather than something to quietly
        ''' transcode: Encoding.UTF8 would produce different, wrong wire bytes for
        ''' those labels instead of failing.
        ''' </summary>
        Private Shared Function AsciiBytes(text As String) As Byte()
            Dim bytes(text.Length - 1) As Byte
            For i As Integer = 0 To text.Length - 1
                ' AscW, not CInt: VB has no Char-to-Integer conversion under
                ' Option Strict (BC32006 tells you to use AscW explicitly).
                Dim code = AscW(text(i))
                If code > &H7F Then
                    Throw New ArgumentException(
                        "DNS labels must be ASCII; punycode the name first: '" & text & "'")
                End If
                bytes(i) = CByte(code)
            Next
            Return bytes
        End Function

        ''' <summary>
        ''' Parses a response, collecting answers of the requested type.
        '''
        ''' Name compression (RFC 1035 §4.1.4) is implemented because every real
        ''' resolver uses it and a parser that ignores it reads garbage.
        ''' </summary>
        Public Shared Function ParseResponse(response As Byte(),
                                             Optional recordType As Integer = TypeA) As IList(Of DnsAnswer)
            Dim answers As New List(Of DnsAnswer)()
            If response Is Nothing OrElse response.Length < 12 Then
                Throw New HttpProtocolException("DNS response is shorter than its header")
            End If

            Dim rcode = response(3) And &H0F
            If rcode <> 0 Then
                ' NXDOMAIN(3) and NOERROR-with-no-answers both mean "no records";
                ' the caller decides whether that is fatal.
                Throw New HttpProtocolException("DNS response code " & rcode)
            End If

            Dim questionCount = (CInt(response(4)) << 8) Or CInt(response(5))
            Dim answerCount = (CInt(response(6)) << 8) Or CInt(response(7))
            Dim authorityCount = (CInt(response(8)) << 8) Or CInt(response(9))
            Dim additionalCount = (CInt(response(10)) << 8) Or CInt(response(11))

            Dim offset = 12
            For i As Integer = 1 To questionCount
                offset = SkipName(response, offset)
                offset += 4                      ' QTYPE + QCLASS
            Next

            ' Only the answer section is read; authority and additional records are
            ' skipped because nothing here consumes them.
            For i As Integer = 1 To answerCount
                offset = SkipName(response, offset)
                If offset + 10 > response.Length Then
                    Throw New HttpProtocolException("truncated DNS answer record")
                End If

                Dim rrType = (CInt(response(offset)) << 8) Or CInt(response(offset + 1))
                Dim ttl = (CInt(response(offset + 4)) << 24) Or (CInt(response(offset + 5)) << 16) Or
                          (CInt(response(offset + 6)) << 8) Or CInt(response(offset + 7))
                Dim dataLength = (CInt(response(offset + 8)) << 8) Or CInt(response(offset + 9))
                Dim dataOffset = offset + 10

                If dataOffset + dataLength > response.Length Then
                    Throw New HttpProtocolException("truncated DNS record data")
                End If

                If rrType = recordType Then
                    If recordType = TypeA AndAlso dataLength = 4 Then
                        answers.Add(New DnsAnswer(FormatIpv4(response, dataOffset), ttl, False))
                    ElseIf recordType = TypeAaaa AndAlso dataLength = 16 Then
                        answers.Add(New DnsAnswer(FormatIpv6(response, dataOffset), ttl, True))
                    End If
                End If

                offset = dataOffset + dataLength
            Next

            Return answers
        End Function

        ''' <summary>
        ''' Advances past a (possibly compressed) name. A compression pointer ends
        ''' the name immediately; it is 2 bytes and never followed here, because
        ''' we only need to step over names, not resolve them.
        ''' </summary>
        Private Shared Function SkipName(message As Byte(), offset As Integer) As Integer
            Do
                If offset >= message.Length Then
                    Throw New HttpProtocolException("truncated DNS name")
                End If
                Dim length = CInt(message(offset))
                If length = 0 Then Return offset + 1
                If (length And &HC0) = &HC0 Then Return offset + 2
                offset += 1 + length
            Loop
        End Function

        Private Shared Function FormatIpv4(message As Byte(), offset As Integer) As String
            Return message(offset).ToString() & "." & message(offset + 1).ToString() & "." &
                   message(offset + 2).ToString() & "." & message(offset + 3).ToString()
        End Function

        ''' <summary>RFC 5952 formatting: lowercase hex, longest zero run compressed once.</summary>
        Private Shared Function FormatIpv6(message As Byte(), offset As Integer) As String
            Dim groups(7) As Integer
            For i As Integer = 0 To 7
                groups(i) = (CInt(message(offset + i * 2)) << 8) Or CInt(message(offset + i * 2 + 1))
            Next

            Dim bestStart = -1, bestLength = 0
            Dim runStart = -1
            For i As Integer = 0 To 8
                If i < 8 AndAlso groups(i) = 0 Then
                    If runStart < 0 Then runStart = i
                ElseIf runStart >= 0 Then
                    Dim runLength = i - runStart
                    If runLength > bestLength AndAlso runLength >= 2 Then
                        bestStart = runStart
                        bestLength = runLength
                    End If
                    runStart = -1
                End If
            Next

            Dim builder As New StringBuilder()
            Dim index = 0
            While index < 8
                If index = bestStart Then
                    builder.Append("::")
                    index += bestLength
                    If index >= 8 Then Exit While
                Else
                    If builder.Length > 0 AndAlso Not builder.ToString().EndsWith(":") Then
                        builder.Append(":"c)
                    End If
                    builder.Append(groups(index).ToString("x"))
                    index += 1
                End If
            End While
            Return builder.ToString()
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _client IsNot Nothing Then
                _client.Dispose()
                _client = Nothing
            End If
        End Sub
    End Class

End Namespace
