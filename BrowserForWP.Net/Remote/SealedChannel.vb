' BrowserForWP — the sealed frame layer, over the crypto this repository already
' ships.
'
' The transport is TLS 1.3, so why a second layer? Because TLS is terminated by
' whatever is in front of the server, and a reverse proxy or a load balancer in
' the path turns "encrypted" into "encrypted as far as that box". The device token
' never leaves the phone except to the server that issued it, so sealing on top of
' TLS means a middlebox cannot read a page even when it can see every byte.
'
' It lives in Net, not Core, because Core may not reference Crypto
' (docs/ARCHITECTURE.md) and this is the layer that holds a key.
'
' WHY THE HEADER COMES IN AS A DELEGATE. The header format belongs to
' RemoteProtocol, which is in Core, and BrowserForWP.Net references only
' BrowserForWP.Crypto -- deliberately, because the dependency direction is strictly
' downward (docs/ARCHITECTURE.md). Building the header HERE would be a second
' implementation of the one thing protocol/vectors.json exists to pin, so instead
' the one implementation is injected. The constructor refuses a null one.
'
' Three details are load-bearing, and protocol/vectors.json pins each of them in
' concrete bytes:
'
'   * The nonce IS the sequence number, big-endian, in the last 4 bytes of 12.
'   * The AAD IS the 16 header bytes. That is what stops a frame being relabelled
'     or truncated, and it is why the header is rebuilt from the same values the
'     decoder reported rather than trusted.
'   * The counter advances only AFTER a tag verifies, so a forged frame cannot
'     make the next genuine one look like a replay.

Imports System
Imports System.Text
Imports BrowserForWP.Crypto

Namespace Remote

    Public NotInheritable Class SealedChannel

        Public Const KeySize As Integer = 32
        Public Const NonceSize As Integer = 12
        Public Const TagSize As Integer = 16
        Public Const SaltSize As Integer = 32
        Public Const HeaderSize As Integer = 16

        ' Domain separation: two keys, so a frame cannot be reflected back at its
        ' own sender and neither direction can consume the other's sequence numbers.
        Private Shared ReadOnly InfoClientToServer As Byte() = Encoding.UTF8.GetBytes("bfwp/render/v1/c2s")
        Private Shared ReadOnly InfoServerToClient As Byte() = Encoding.UTF8.GetBytes("bfwp/render/v1/s2c")

        Private ReadOnly _outKey As Byte()
        Private ReadOnly _inKey As Byte()
        Private ReadOnly _buildHeader As Func(Of Byte, UInteger, UInteger, Byte())
        Private _outSequence As UInteger
        Private _inSequence As UInteger

        ''' <summary>
        ''' One key per direction, from the device token and the connection's salt.
        ''' The token is the key material: a device that never received one can
        ''' derive nothing, and a fresh salt makes every connection's key different.
        ''' </summary>
        Public Sub New(token As Byte(), sessionSalt As Byte(),
                       buildHeader As Func(Of Byte, UInteger, UInteger, Byte()))
            If token Is Nothing OrElse token.Length = 0 Then
                Throw New SealedChannelException("a device token is required")
            End If
            If sessionSalt Is Nothing OrElse sessionSalt.Length <> SaltSize Then
                Throw New SealedChannelException("a session salt must be 32 bytes")
            End If
            If buildHeader Is Nothing Then
                Throw New SealedChannelException(
                    "a header builder is required: the header format lives in Core and this layer may not reference it")
            End If

            Dim prk As Byte() = Hkdf.Extract(sessionSalt, token)
            _outKey = Hkdf.Expand(prk, InfoServerToClient, KeySize)
            _inKey = Hkdf.Expand(prk, InfoClientToServer, KeySize)
            _buildHeader = buildHeader
            _outSequence = 0UI
            _inSequence = 0UI
        End Sub

        Public ReadOnly Property FramesSent As UInteger
            Get
                Return _outSequence
            End Get
        End Property

        Public ReadOnly Property FramesReceived As UInteger
            Get
                Return _inSequence
            End Get
        End Property

        ''' <summary>
        ''' Returns the whole frame: header, ciphertext, tag, in that order, which is
        ''' the order the server builds and expects.
        ''' </summary>
        Public Function Seal(messageType As Byte, payload As Byte()) As Byte()
            ' `next` is a VB keyword (For...Next) and cannot be a local name. The
            ' guest build reported BC30201 on this line and then "header is not
            ' declared" for eleven following lines, all of them naming the wrong
            ' thing -- which is why tools/check-vb.mjs now checks declared names.
            Dim frameSeq As UInteger = _outSequence + 1UI
            If frameSeq = 0UI Then
                Throw New SealedChannelException("the frame counter wrapped; reconnect to rekey")
            End If
            _outSequence = frameSeq

            Dim body As Byte() = If(payload, New Byte() {})
            Dim header As Byte() = _buildHeader(messageType, frameSeq, CUInt(body.Length + TagSize))
            If header Is Nothing OrElse header.Length <> HeaderSize Then
                Throw New SealedChannelException("the header builder returned a header of the wrong length")
            End If

            Dim sealedResult As AeadResult = AesGcm.Seal(_outKey, NonceFor(frameSeq), header, body)

            Dim frame(header.Length + sealedResult.Ciphertext.Length + sealedResult.Tag.Length - 1) As Byte
            Array.Copy(header, 0, frame, 0, header.Length)
            Array.Copy(sealedResult.Ciphertext, 0, frame, header.Length, sealedResult.Ciphertext.Length)
            Array.Copy(sealedResult.Tag, 0, frame,
                       header.Length + sealedResult.Ciphertext.Length, sealedResult.Tag.Length)
            Return frame
        End Function

        ''' <summary>
        ''' Opens a received frame, and refuses to move backwards.
        '''
        ''' `header` and `sequence` are the values the decoder reported, and the header
        ''' is authenticated AS AAD: if the device and the server ever disagreed about
        ''' a byte of it, every frame would fail to open, which is at least the loud
        ''' kind of failure. A plain apostrophe inside a doc comment ends the doc block
        ''' and leaves the closing tag unmatched, which is BC42301 plus BC42304 -- a
        ''' warning, but a warning that hides the next real one.
        ''' </summary>
        Public Function Open(header As Byte(), sequence As UInteger, sealedPayload As Byte()) As Byte()
            If header Is Nothing OrElse header.Length <> HeaderSize Then
                Throw New SealedChannelException("a frame header is 16 bytes")
            End If
            If sealedPayload Is Nothing OrElse sealedPayload.Length < TagSize Then
                Throw New SealedChannelException("a sealed frame is shorter than its own tag")
            End If
            If sequence <= _inSequence Then
                Throw New SealedChannelException(
                    "replayed or reordered frame: seq " & sequence.ToString() &
                    " after " & _inSequence.ToString())
            End If

            Dim ciphertextLength As Integer = sealedPayload.Length - TagSize
            Dim ciphertext(ciphertextLength - 1) As Byte
            Dim tag(TagSize - 1) As Byte
            Array.Copy(sealedPayload, 0, ciphertext, 0, ciphertextLength)
            Array.Copy(sealedPayload, ciphertextLength, tag, 0, TagSize)

            ' AesGcm.Open raises on a tag mismatch, which is the fail-closed behaviour
            ' this layer depends on: a forged frame never reaches the caller, and the
            ' counter below is never reached either.
            Dim plaintext As Byte() = AesGcm.Open(_inKey, NonceFor(sequence), header, ciphertext, tag)

            ' Advanced only now. Advancing before the tag verified would let an
            ' attacker forge frame N and make the genuine frame N look like a replay.
            _inSequence = sequence
            Return plaintext
        End Function

        ''' <summary>The nonce IS the sequence number, in the last four of twelve bytes.</summary>
        Public Shared Function NonceFor(sequence As UInteger) As Byte()
            Dim nonce(NonceSize - 1) As Byte
            nonce(8) = CByte((sequence >> 24) And &HFFUI)
            nonce(9) = CByte((sequence >> 16) And &HFFUI)
            nonce(10) = CByte((sequence >> 8) And &HFFUI)
            nonce(11) = CByte(sequence And &HFFUI)
            Return nonce
        End Function

    End Class

    ''' <summary>Raised on a replayed frame, a bad tag or a broken key schedule.</summary>
    Public Class SealedChannelException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub

    End Class

End Namespace
