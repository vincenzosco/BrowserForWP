' BrowserForWP — TLS 1.3 client over a raw StreamSocket.
'
' WHAT THIS IS FOR
' ----------------
' The OS TLS stack on Windows Phone 8.1 tops out at TLS 1.2: StreamSocket's
' UpgradeToSslAsync goes through Schannel, and Schannel on that OS knows nothing
' about TLS 1.3. There is no setting, no registry key and no API that changes
' this. The only way to speak TLS 1.3 on the handset is to speak it ourselves
' over a plain TCP socket, which is what this class does.
'
' StreamSocket is therefore used as a transport ONLY. UpgradeToSslAsync is
' deliberately never called — every byte above TCP is ours.
'
' VERIFIED DESIGN
' ---------------
' tools/proto/tls13.mjs performs this exact sequence against live servers
' (Google, Cloudflare, example.com) and completes the handshake and an HTTP
' request. This file is the transliteration. The three bugs that prototype
' exposed are called out where they live:
'
'   * TLSInnerPlaintext field order      -> TlsRecordLayer.vb
'   * server_name / key_share lengths    -> ClientHelloBuilder.vb
'   * ALPN belongs to EncryptedExtensions -> ServerMessageParser.vb
'
' HONEST LIMITATION
' -----------------
' This gives BrowserForWP's own network stack TLS 1.3. It gives the WebView
' nothing. The WebView renders through Trident and uses the OS network stack, so
' pages it loads are still bounded by Schannel's TLS 1.2. See docs/ARCHITECTURE.md.

Imports System.Threading.Tasks
Imports Windows.Networking
Imports Windows.Networking.Sockets
Imports Windows.Storage.Streams
Imports BrowserForWP.Crypto

Namespace Tls13

    ''' <summary>Everything negotiated by a completed handshake.</summary>
    Public NotInheritable Class TlsSessionInfo

        Public Property CipherSuite As Integer
        Public Property Alpn As String
        Public Property CertificateValid As Boolean
        Public Property CertificateChainStatus As String
        Public Property MatchedName As String
        Public Property Resumed As Boolean
        ''' <summary>Leaf certificate DER, for pin computation. May be Nothing.</summary>
        Public Property LeafCertificateDer As Byte()
    End Class

    Public NotInheritable Class Tls13Client
        Implements IDisposable

        Private ReadOnly _hostName As String
        Private _socket As StreamSocket
        Private _writer As DataWriter
        Private _reader As DataReader

        ''' <summary>Bytes received from the socket but not yet consumed.</summary>
        Private _inbound As New List(Of Byte)()

        Private _transcript As TranscriptHash
        Private _readLayer As TlsRecordLayer
        Private _writeLayer As TlsRecordLayer

        ''' <summary>Handshake messages reassembled across record boundaries.</summary>
        Private _handshakeBuffer As New List(Of Byte)()

        Public Sub New(hostName As String)
            If String.IsNullOrEmpty(hostName) Then
                Throw New ArgumentException("host name required", "hostName")
            End If
            _hostName = hostName
        End Sub

        ''' <summary>The SNI value and the name certificate verification checks against.</summary>
        Public ReadOnly Property HostName As String
            Get
                Return _hostName
            End Get
        End Property

        Public Property SessionInfo As TlsSessionInfo
        Public Property IsConnected As Boolean

        ''' <summary>
        ''' Connects, completes the handshake, and leaves the client ready to
        ''' carry application data.
        ''' </summary>
        Public Async Function ConnectAsync(host As String, port As Integer) As Task
            _socket = New StreamSocket()

            ' PlainSocket: raw TCP with no TLS. This is the entire point — see the
            ' file header. Using SocketProtectionLevel.Tls12 here would silently
            ' hand our handshake bytes to Schannel.
            '
            ' The first argument must be a Windows.Networking.HostName, not a
            ' String: there is no (String, String, SocketProtectionLevel)
            ' overload, and passing a String reports BC30311 "cannot convert
            ' String to Windows.Networking.HostName".
            Await _socket.ConnectAsync(New HostName(host), port.ToString(),
                                       SocketProtectionLevel.PlainSocket)

            _writer = New DataWriter(_socket.OutputStream)

            ' One reader for the connection's lifetime. InputStreamOptions.Partial
            ' makes LoadAsync return as soon as any data is available rather than
            ' waiting for the full count, which is essential: the peer is not
            ' obliged to send whole records, or to respect our requested sizes.
            _reader = New DataReader(_socket.InputStream)
            _reader.InputStreamOptions = InputStreamOptions.Partial

            Await PerformHandshakeAsync()
        End Function

        Private Async Function PerformHandshakeAsync() As Task
            _transcript = New TranscriptHash()

            ' ── Ephemeral key pair ──────────────────────────────────────────────
            Dim privateKey = X25519.GeneratePrivateKey()
            Dim publicKey = X25519.PublicFromPrivate(privateKey)

            Dim random = WinRtCrypto.RandomBytes(TlsLimits.RandomLength)
            Dim sessionId = WinRtCrypto.RandomBytes(TlsLimits.CompatSessionIdLength)

            Dim clientHello = ClientHelloBuilder.BuildHandshake(_hostName, publicKey, random, sessionId)
            _transcript.Add(clientHello)

            Await SendRawAsync(ClientHelloBuilder.BuildRecord(_hostName, publicKey, random, sessionId))

            ' Compatibility mode: a dummy CCS immediately after ClientHello.
            Await SendRawAsync(ClientHelloBuilder.BuildCompatibilityChangeCipherSpec())

            ' ── ServerHello (the last plaintext message we receive) ─────────────
            Dim serverHelloRecord = Await ReadRecordAsync()
            If CType(serverHelloRecord.ContentType, ContentType) = ContentType.Alert Then
                Throw New TlsProtocolException(
                    "server rejected the ClientHello: " & DescribeAlert(serverHelloRecord.Payload))
            End If
            If CType(serverHelloRecord.ContentType, ContentType) <> ContentType.Handshake Then
                Throw New TlsProtocolException("expected a handshake record for ServerHello")
            End If

            Dim serverHello = TakeHandshakeMessage(serverHelloRecord.Payload)
            If CType(serverHello(0), HandshakeType) <> HandshakeType.ServerHello Then
                Throw New TlsProtocolException("expected ServerHello, got handshake type " & serverHello(0))
            End If

            Dim serverHelloBody = Slice(serverHello, 4, serverHello.Length - 4)
            Dim helloInfo = ServerMessageParser.ParseServerHello(serverHelloBody)
            _transcript.Add(serverHello)

            If helloInfo.IsHelloRetryRequest Then
                ' Not implemented, and it must not be guessed at: the key share in
                ' an HRR is not usable, and proceeding would derive keys from a
                ' group the server did not accept.
                Throw New TlsProtocolException(
                    "server sent a HelloRetryRequest, which BrowserForWP does not implement")
            End If
            If helloInfo.SelectedVersion <> TlsLimits.Tls13Version Then
                Throw New TlsProtocolException(
                    "server did not select TLS 1.3 (got 0x" & helloInfo.SelectedVersion.ToString("X4") & ")")
            End If
            If helloInfo.CipherSuite <> CInt(CipherSuite.Aes128GcmSha256) Then
                Throw New TlsProtocolException(
                    "server selected an unoffered cipher suite 0x" & helloInfo.CipherSuite.ToString("X4"))
            End If
            If helloInfo.KeyShareGroup <> CInt(NamedGroup.X25519) OrElse helloInfo.KeyShare Is Nothing Then
                Throw New TlsProtocolException("server did not return an x25519 key share")
            End If

            ' ── Shared secret and key schedule ──────────────────────────────────
            Dim sharedSecret = X25519.Agreement(privateKey, helloInfo.KeyShare)

            Dim schedule As New KeySchedule()
            schedule.Start()
            schedule.AddSharedSecret(sharedSecret)

            Dim afterServerHello = _transcript.Compute()
            Dim clientHandshakeKeys = schedule.ClientHandshakeTraffic(afterServerHello)
            Dim serverHandshakeKeys = schedule.ServerHandshakeTraffic(afterServerHello)

            _readLayer = New TlsRecordLayer(serverHandshakeKeys.Key, serverHandshakeKeys.Iv)
            _writeLayer = New TlsRecordLayer(clientHandshakeKeys.Key, clientHandshakeKeys.Iv)

            ' ── EncryptedExtensions ─────────────────────────────────────────────
            Dim encryptedExtensions = Await NextHandshakeMessageAsync()
            RequireHandshakeType(encryptedExtensions, HandshakeType.EncryptedExtensions)
            _transcript.Add(encryptedExtensions)
            Dim extensionsInfo = ServerMessageParser.ParseEncryptedExtensions(
                Slice(encryptedExtensions, 4, encryptedExtensions.Length - 4))

            ' ── Certificate ─────────────────────────────────────────────────────
            Dim certificateMessage = Await NextHandshakeMessageAsync()
            RequireHandshakeType(certificateMessage, HandshakeType.Certificate)
            Dim chain = ServerMessageParser.ParseCertificate(
                Slice(certificateMessage, 4, certificateMessage.Length - 4))
            _transcript.Add(certificateMessage)

            ' ── CertificateVerify ───────────────────────────────────────────────
            Dim certificateVerify = Await NextHandshakeMessageAsync()
            RequireHandshakeType(certificateVerify, HandshakeType.CertificateVerify)
            Dim verifyInfo = ServerMessageParser.ParseCertificateVerify(
                Slice(certificateVerify, 4, certificateVerify.Length - 4))

            Dim signatureOk = CertificateValidator.VerifyCertificateVerify(
                verifyInfo, chain(0), _transcript.Compute())
            If Not signatureOk Then
                Throw New TlsProtocolException(
                    "CertificateVerify signature is invalid: the peer does not hold " &
                    "the private key for the certificate it sent")
            End If
            _transcript.Add(certificateVerify)

            ' Chain trust and hostname are checked separately from the signature,
            ' and both before any application data is exchanged.
            Dim certificateResult = Await CertificateValidator.ValidateAsync(chain, _hostName)
            If Not certificateResult.IsValid Then
                Dim reason = If(certificateResult.IsChainTrusted,
                                "certificate is not valid for " & _hostName,
                                "certificate chain is not trusted (" & certificateResult.ChainStatus & ")")
                Throw New TlsProtocolException(reason)
            End If

            ' ── Finished (server) ───────────────────────────────────────────────
            Dim serverFinished = Await NextHandshakeMessageAsync()
            RequireHandshakeType(serverFinished, HandshakeType.Finished)

            Dim expectedFinished = KeySchedule.ComputeFinished(
                serverHandshakeKeys.FinishedKey, _transcript.Compute())
            Dim receivedFinished = ServerMessageParser.ParseFinished(
                Slice(serverFinished, 4, serverFinished.Length - 4))

            If Not WinRtCrypto.FixedTimeEquals(expectedFinished, receivedFinished) Then
                Throw New TlsProtocolException(
                    "server Finished does not verify: the handshake has been tampered with")
            End If
            _transcript.Add(serverFinished)

            ' Application secrets are derived over the transcript up to and
            ' including the server's Finished (RFC 8446 §7.1) — not our own.
            Dim afterServerFinished = _transcript.Compute()
            Dim clientApplicationKeys = schedule.ClientApplicationTraffic(afterServerFinished)
            Dim serverApplicationKeys = schedule.ServerApplicationTraffic(afterServerFinished)

            ' ── Finished (client) ───────────────────────────────────────────────
            Dim clientVerifyData = KeySchedule.ComputeFinished(
                clientHandshakeKeys.FinishedKey, afterServerFinished)
            ' `With`, not a leading-dot chain: VB 12 rejects a line break after a
            ' '.', and the resulting BC30203 plus phantom "X is not declared"
            ' errors point at every method name instead of at the line break.
            Dim finishedWriter As New TlsWriter()
            With finishedWriter
                .U8(CInt(HandshakeType.Finished))
                .U24(clientVerifyData.Length)
                .Bytes(clientVerifyData)
            End With
            Dim clientFinished = finishedWriter.ToArray()
            _transcript.Add(clientFinished)

            Await SendRecordAsync(_writeLayer.Seal(ContentType.Handshake, clientFinished))

            ' Handshake complete: swap to application keys for both directions.
            _readLayer = New TlsRecordLayer(serverApplicationKeys.Key, serverApplicationKeys.Iv)
            _writeLayer = New TlsRecordLayer(clientApplicationKeys.Key, clientApplicationKeys.Iv)

            SessionInfo = New TlsSessionInfo With {
                .CipherSuite = helloInfo.CipherSuite,
                .Alpn = extensionsInfo.Alpn,
                .CertificateValid = certificateResult.IsValid,
                .CertificateChainStatus = certificateResult.ChainStatus,
                .MatchedName = certificateResult.MatchedName,
                .Resumed = False,
                .LeafCertificateDer = chain(0)}
            IsConnected = True
        End Function

        ''' <summary>Encrypts and sends application data.</summary>
        Public Async Function WriteAsync(data As Byte()) As Task
            If Not IsConnected Then Throw New InvalidOperationException("handshake not complete")
            If data Is Nothing OrElse data.Length = 0 Then Return

            ' Records are capped at 2^14 bytes of plaintext (§5.1). A larger write
            ' must be split, not truncated.
            Dim offset = 0
            While offset < data.Length
                Dim count = Math.Min(TlsLimits.MaxPlaintextLength, data.Length - offset)
                Dim chunk(count - 1) As Byte
                Array.Copy(data, offset, chunk, 0, count)
                Await SendRecordAsync(_writeLayer.Seal(ContentType.ApplicationData, chunk))
                offset += count
            End While
        End Function

        ''' <summary>
        ''' Reads the next batch of application data. Handshake messages arriving
        ''' post-handshake (NewSessionTicket) are skipped silently; an alert raises.
        ''' </summary>
        Public Async Function ReadAsync() As Task(Of Byte())
            If Not IsConnected Then Throw New InvalidOperationException("handshake not complete")

            Do
                Dim record = Await ReadRecordAsync()

                If CType(record.ContentType, ContentType) = ContentType.Alert Then
                    Throw New TlsProtocolException("peer sent an alert: " & DescribeAlert(record.Payload))
                End If

                If CType(record.ContentType, ContentType) = ContentType.ApplicationData Then
                    ' Post-handshake messages are interleaved with application data on
                    ' the same record type, so an application-data record is not
                    ' necessarily application data. Demultiplex on the handshake byte.
                    If record.Payload.Length >= 4 AndAlso
                       CType(record.Payload(0), HandshakeType) = HandshakeType.NewSessionTicket Then
                        Continue Do
                    End If
                    Return record.Payload
                End If

                If CType(record.ContentType, ContentType) = ContentType.Handshake Then
                    Continue Do
                End If
            Loop
        End Function

        ' ── Handshake plumbing ─────────────────────────────────────────────────

        ''' <summary>
        ''' Pulls the next complete handshake message out of the encrypted stream.
        ''' Handshake messages may be split across records and several may share
        ''' one record, so reassembly is required rather than optional.
        ''' </summary>
        Private Async Function NextHandshakeMessageAsync() As Task(Of Byte())
            Do
                Dim message = TryTakeHandshakeMessage()
                If message IsNot Nothing Then Return message

                Dim record = Await ReadRecordAsync()
                Select Case CType(record.ContentType, ContentType)
                    Case ContentType.ChangeCipherSpec
                        ' Compatibility mode sends an unencrypted CCS. RFC 8446
                        ' §5 says to discard it; it carries no key change here.
                        Continue Do
                    Case ContentType.Alert
                        Throw New TlsProtocolException(
                            "peer sent an alert during the handshake: " & DescribeAlert(record.Payload))
                    Case ContentType.Handshake
                        AppendHandshake(record.Payload)
                    Case ContentType.ApplicationData
                        ' The peer encrypted with a different epoch than we expected.
                        Throw New TlsProtocolException(
                            "received application data before the handshake finished")
                End Select
            Loop
        End Function

        Private Sub AppendHandshake(payload As Byte())
            If payload IsNot Nothing AndAlso payload.Length > 0 Then
                _handshakeBuffer.AddRange(payload)
            End If
        End Sub

        ''' <summary>
        ''' Returns one complete handshake message, or Nothing if fewer than a whole
        ''' message has arrived.
        ''' </summary>
        Private Function TryTakeHandshakeMessage() As Byte()
            If _handshakeBuffer.Count < 4 Then Return Nothing

            Dim length = (CInt(_handshakeBuffer(1)) << 16) Or
                         (CInt(_handshakeBuffer(2)) << 8) Or
                         CInt(_handshakeBuffer(3))
            If _handshakeBuffer.Count < 4 + length Then Return Nothing

            Dim message(3 + length) As Byte
            For i As Integer = 0 To message.Length - 1
                message(i) = _handshakeBuffer(i)
            Next
            _handshakeBuffer.RemoveRange(0, 4 + length)
            Return message
        End Function

        Private Function TakeHandshakeMessage(payload As Byte()) As Byte()
            AppendHandshake(payload)
            Dim message = TryTakeHandshakeMessage()
            If message Is Nothing Then
                Throw New TlsProtocolException("ServerHello was split across records; not supported")
            End If
            Return message
        End Function

        Private Shared Sub RequireHandshakeType(message As Byte(), expected As HandshakeType)
            If CType(message(0), HandshakeType) <> expected Then
                Throw New TlsProtocolException(
                    "expected " & expected.ToString() & ", received handshake type " & message(0))
            End If
        End Sub

        ''' <summary>One received record: its 5-byte header and its body.</summary>
        Private NotInheritable Class RawRecord

            Public Sub New(header As Byte(), body As Byte())
                Me.Header = header
                Me.Body = body
            End Sub

            Public ReadOnly Header As Byte()
            Public ReadOnly Body As Byte()

            Public ReadOnly Property ContentType As Byte
                Get
                    Return Header(0)
                End Get
            End Property

            ''' <summary>
            ''' The payload. For an unencrypted record that is the body; for an
            ''' encrypted one the caller must go through the record layer.
            ''' </summary>
            Public ReadOnly Property Payload As Byte()
                Get
                    Return Body
                End Get
            End Property
        End Class

        ' ── Socket I/O ─────────────────────────────────────────────────────────

        ''' <summary>
        ''' Reads one record, returning its header and DECRYPTED payload. ChangeCipherSpec
        ''' and other unencrypted records are returned as-is.
        ''' </summary>
        Private Async Function ReadRecordAsync() As Task(Of RawRecord)
            Dim header = Await ReadExactAsync(5)
            Dim length = (CInt(header(3)) << 8) Or CInt(header(4))

            If length > TlsLimits.MaxPlaintextLength + TlsLimits.MaxCiphertextOverhead Then
                Throw New TlsProtocolException("record length " & length & " exceeds the maximum permitted")
            End If

            Dim body = Await ReadExactAsync(length)
            Dim contentType = CType(header(0), ContentType)

            ' Exactly two kinds of record are NOT protected by the record layer:
            '   * anything arriving before we have handshake keys, which is
            '     ServerHello and a plaintext alert;
            '   * the compatibility-mode ChangeCipherSpec, which is always plaintext
            '     and carries no key change.
            If _readLayer Is Nothing OrElse contentType = ContentType.ChangeCipherSpec Then
                Return New RawRecord(header, body)
            End If

            Dim opened = _readLayer.Open(header, body)

            ' Re-frame the opened record so callers see a uniform shape: the header
            ' carries the INNER content type, and the body is the plaintext.
            Dim innerHeader As Byte() = {CByte(opened.ContentType), 0, 0, 0, 0}
            Return New RawRecord(innerHeader, opened.Payload)
        End Function

        ''' <summary>Reads exactly <paramref name="count"/> bytes, filling as needed.</summary>
        Private Async Function ReadExactAsync(count As Integer) As Task(Of Byte())
            While _inbound.Count < count
                Dim received = Await _reader.LoadAsync(CUInt(count - _inbound.Count))
                If received = 0 Then
                    Throw New TlsProtocolException(
                        "connection closed while reading (wanted " & count &
                        " bytes, had " & _inbound.Count & ")")
                End If

                ' LoadAsync returns UInteger, and UInteger - Integer widens to
                ' Long in VB, so `received - 1` is not a valid array bound
                ' (BC30512, Long to Integer). Narrow it explicitly.
                Dim chunk(CInt(received) - 1) As Byte
                _reader.ReadBytes(chunk)
                _inbound.AddRange(chunk)
            End While

            Dim result(count - 1) As Byte
            For i As Integer = 0 To count - 1
                result(i) = _inbound(i)
            Next
            _inbound.RemoveRange(0, count)
            Return result
        End Function

        Private Async Function SendRawAsync(data As Byte()) As Task
            _writer.WriteBytes(data)
            Await _writer.StoreAsync()
            Await _writer.FlushAsync()
        End Function

        Private Async Function SendRecordAsync(record As Byte()) As Task
            Await SendRawAsync(record)
        End Function

        Private Shared Function Slice(source As Byte(), offset As Integer, count As Integer) As Byte()
            Dim result(count - 1) As Byte
            Array.Copy(source, offset, result, 0, count)
            Return result
        End Function

        ''' <summary>Human-readable alert, for diagnostics.</summary>
        Public Shared Function DescribeAlert(payload As Byte()) As String
            If payload Is Nothing OrElse payload.Length < 2 Then
                Return "malformed alert"
            End If
            Dim level = If(payload(0) = 2, "fatal", "warning")
            Dim description = CType(payload(1), AlertDescription)
            Return level & " " & CInt(payload(1)) & " (" & description.ToString() & ")"
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            IsConnected = False
            If _reader IsNot Nothing Then
                Try
                    _reader.Dispose()
                Catch
                End Try
                _reader = Nothing
            End If
            If _writer IsNot Nothing Then
                Try
                    _writer.Dispose()
                Catch
                End Try
                _writer = Nothing
            End If
            If _socket IsNot Nothing Then
                Try
                    _socket.Dispose()
                Catch
                End Try
                _socket = Nothing
            End If
        End Sub
    End Class

End Namespace
