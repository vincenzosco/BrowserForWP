' BrowserForWP — the render protocol's wire format, in VB.
'
' The server implements this same protocol in JavaScript, in the repository
' Docker-BrowserForWP. Two implementations in two languages disagree SILENTLY: a
' field read at the wrong offset is a garbled screen, not an exception. So this
' file is checked against protocol/vectors.json, which was produced by the
' server's own code and is the only statement of the protocol that neither
' implementation wrote.
'
' Two rules it follows, both because the device has no BitConverter it can trust:
' every integer is written and read with explicit shifts in BIG-ENDIAN order, and
' every read is bounds-checked. BitConverter is little-endian and unguarded, so
' using it would produce a protocol that works on x86 by accident.
'
' It lives in Core because it is pure: no socket, no key, no XAML. Core may not
' reference BrowserForWP.Net or BrowserForWP.Crypto (docs/ARCHITECTURE.md), and
' the layer that holds a key is in Net, where it belongs.
'
' ONE SIGNED TYPE, DELIBERATELY. The protocol has exactly one signed field, the
' scroll delta, so there is exactly one signed method here and a vector that
' exercises it with a negative value. I8 and I32 were written first and then
' removed: each would have needed its own overflow-free conversion, and none of
' them would have been covered by a byte of the vectors. Adding one back means
' adding its vector with it.

Imports System
Imports System.Text

Namespace Engine

    Namespace Remote

        ''' <summary>
        ''' The constants and the header. Nothing here allocates a stream or holds a
        ''' key: it turns numbers into bytes and bytes back into numbers.
        ''' </summary>
        Public NotInheritable Class RemoteProtocol

            Public Const Magic As UShort = &HB752
            Public Const Version As Byte = 1
            Public Const HeaderSize As Integer = 16
            Public Const MaxPayload As Integer = 8 * 1024 * 1024

            ''' <summary>The first type that is sealed. Below it is the handshake.</summary>
            Public Const SealedFrom As Byte = &H10

            Private Sub New()
            End Sub

            Public Shared Function IsSealed(messageType As Byte) As Boolean
                Return messageType >= SealedFrom
            End Function

            ''' <summary>Builds the 16 header bytes. Big-endian, every field explicit.</summary>
            Public Shared Function EncodeHeader(messageType As Byte, sequence As UInteger, length As UInteger) As Byte()
                Dim header(HeaderSize - 1) As Byte
                header(0) = CByte((CUInt(Magic) >> 8) And &HFFUI)
                header(1) = CByte(CUInt(Magic) And &HFFUI)
                header(2) = Version
                header(3) = messageType
                header(4) = CByte((length >> 24) And &HFFUI)
                header(5) = CByte((length >> 16) And &HFFUI)
                header(6) = CByte((length >> 8) And &HFFUI)
                header(7) = CByte(length And &HFFUI)
                header(8) = CByte((sequence >> 24) And &HFFUI)
                header(9) = CByte((sequence >> 16) And &HFFUI)
                header(10) = CByte((sequence >> 8) And &HFFUI)
                header(11) = CByte(sequence And &HFFUI)
                header(12) = 0
                header(13) = 0
                header(14) = 0
                header(15) = 0
                Return header
            End Function

            ''' <summary>
            ''' Reads a header out of a buffer at an offset. Throws rather than
            ''' returning a default: a default would be decoded as a message and shown
            ''' to a person.
            ''' </summary>
            Public Shared Function DecodeHeader(buffer As Byte(), offset As Integer) As RemoteFrame
                If buffer Is Nothing OrElse offset < 0 OrElse buffer.Length - offset < HeaderSize Then
                    Throw New RemoteProtocolException("a frame header is 16 bytes")
                End If

                Dim magic As UShort = CUShort((CUInt(buffer(offset)) << 8) Or CUInt(buffer(offset + 1)))
                If magic <> Magic Then
                    Throw New RemoteProtocolException("bad magic: this is not a render frame")
                End If
                If buffer(offset + 2) <> Version Then
                    Throw New RemoteProtocolException(
                        "unsupported protocol version " & buffer(offset + 2).ToString() &
                        ", this client speaks " & Version.ToString())
                End If
                If buffer(offset + 12) <> 0 OrElse buffer(offset + 13) <> 0 OrElse
                   buffer(offset + 14) <> 0 OrElse buffer(offset + 15) <> 0 Then
                    Throw New RemoteProtocolException("reserved header field is not zero")
                End If

                Dim declared As UInteger =
                    (CUInt(buffer(offset + 4)) << 24) Or (CUInt(buffer(offset + 5)) << 16) Or
                    (CUInt(buffer(offset + 6)) << 8) Or CUInt(buffer(offset + 7))
                If declared > CUInt(MaxPayload) Then
                    Throw New RemoteProtocolException(
                        "declared payload of " & declared.ToString() & " bytes exceeds the limit")
                End If

                Dim frame As New RemoteFrame()
                frame.Type = buffer(offset + 3)
                frame.Length = declared
                frame.Seq = (CUInt(buffer(offset + 8)) << 24) Or (CUInt(buffer(offset + 9)) << 16) Or
                            (CUInt(buffer(offset + 10)) << 8) Or CUInt(buffer(offset + 11))
                frame.Header = New Byte(HeaderSize - 1) {}
                Array.Copy(buffer, offset, frame.Header, 0, HeaderSize)
                Return frame
            End Function

        End Class

        ''' <summary>One frame: header fields, and the payload once it has been read.</summary>
        Public NotInheritable Class RemoteFrame

            Public Property Type As Byte
            Public Property Seq As UInteger
            Public Property Length As UInteger
            Public Property Header As Byte()
            Public Property Payload As Byte()

        End Class

        ''' <summary>
        ''' A protocol violation. Thrown, never logged and continued past: when the
        ''' two ends disagree about the layout, continuing produces nonsense that
        ''' looks like a rendering bug.
        ''' </summary>
        Public Class RemoteProtocolException
            Inherits Exception

            Public Sub New(message As String)
                MyBase.New(message)
            End Sub

        End Class

        ''' <summary>
        ''' Splits a byte stream into frames.
        '''
        ''' A chunk is not a message: TCP has no boundaries, and a decoder that assumes
        ''' one frame per read works on a fast link and fails on a slow one. The buffer
        ''' is a single array with a start offset and a count, compacted in place, and
        ''' it is that shape rather than a List(Of Byte) because a frame is a JPEG of
        ''' up to two megabytes and a per-byte list would copy every byte twice on a
        ''' device with 512 MB of memory.
        '''
        ''' The apostrophes above are all TRIPLED on purpose. A single one ends a doc
        ''' block, so the closing summary tag below would belong to a second comment
        ''' that never opened -- BC42301 plus BC42304, documentation discarded. Twice
        ''' in one round; tools/check-vb.mjs group 13 now refuses the shape.
        '''
        ''' Note that the closing tag is not spelled out anywhere in this prose. A
        ''' doc comment is parsed as XML, so writing one in a sentence closes the
        ''' element early and the block's own closing tag is then unmatched -- the
        ''' same trap at one remove, and it was written here while fixing it above.
        ''' </summary>
        Public NotInheritable Class RemoteFrameReader

            Private _buffer As Byte()
            Private _start As Integer
            Private _count As Integer

            Public Sub New(Optional initialCapacity As Integer = 65536)
                If initialCapacity < RemoteProtocol.HeaderSize Then
                    initialCapacity = RemoteProtocol.HeaderSize
                End If
                _buffer = New Byte(initialCapacity - 1) {}
                _start = 0
                _count = 0
            End Sub

            Public ReadOnly Property BufferedBytes As Integer
                Get
                    Return _count
                End Get
            End Property

            ''' <summary>Appends what a socket read returned.</summary>
            Public Sub Append(chunk As Byte(), count As Integer)
                If chunk Is Nothing OrElse count <= 0 Then Return
                If count > chunk.Length Then count = chunk.Length
                EnsureRoom(_count + count)
                Array.Copy(chunk, 0, _buffer, _start + _count, count)
                _count += count
            End Sub

            Public Sub Append(chunk As Byte())
                If chunk Is Nothing Then Return
                Append(chunk, chunk.Length)
            End Sub

            ''' <summary>
            ''' Yields the next complete frame, or nothing. Safe to call in a loop:
            ''' several frames can arrive in one read and one frame can span several.
            ''' </summary>
            Public Function TryTakeFrame(ByRef frame As RemoteFrame) As Boolean
                frame = Nothing
                If _count < RemoteProtocol.HeaderSize Then Return False

                Dim candidate As RemoteFrame = RemoteProtocol.DecodeHeader(_buffer, _start)
                Dim total As Integer = RemoteProtocol.HeaderSize + CInt(candidate.Length)
                If _count < total Then Return False

                If candidate.Length > 0UI Then
                    candidate.Payload = New Byte(CInt(candidate.Length) - 1) {}
                    Array.Copy(_buffer, _start + RemoteProtocol.HeaderSize, candidate.Payload, 0, CInt(candidate.Length))
                End If

                _start += total
                _count -= total
                If _count = 0 Then _start = 0

                frame = candidate
                Return True
            End Function

            Private Sub EnsureRoom(required As Integer)
                If _start + required <= _buffer.Length Then Return

                ' Compact first: the space already held by consumed bytes is free,
                ' and growing before compacting would double a two-megabyte buffer
                ' while most of it is dead.
                If _start > 0 Then
                    If _count > 0 Then
                        Array.Copy(_buffer, _start, _buffer, 0, _count)
                    End If
                    _start = 0
                End If

                If required > _buffer.Length Then
                    Dim grown(required * 2 - 1) As Byte
                    If _count > 0 Then
                        Array.Copy(_buffer, 0, grown, 0, _count)
                    End If
                    _buffer = grown
                End If
            End Sub

        End Class

        ''' <summary>Builds a payload. Every write is a big-endian shift.</summary>
        Public NotInheritable Class RemoteWriter

            Private ReadOnly _parts As New System.Collections.Generic.List(Of Byte)()

            Public Function U8(value As Byte) As RemoteWriter
                _parts.Add(value)
                Return Me
            End Function

            Public Function U16(value As UShort) As RemoteWriter
                _parts.Add(CByte((CUInt(value) >> 8) And &HFFUI))
                _parts.Add(CByte(CUInt(value) And &HFFUI))
                Return Me
            End Function

            Public Function U32(value As UInteger) As RemoteWriter
                _parts.Add(CByte((value >> 24) And &HFFUI))
                _parts.Add(CByte((value >> 16) And &HFFUI))
                _parts.Add(CByte((value >> 8) And &HFFUI))
                _parts.Add(CByte(value And &HFFUI))
                Return Me
            End Function

            ''' <summary>
            ''' The protocol's only signed field. Two's complement, and the
            ''' conversion is written out rather than left to CUShort, which throws on
            ''' a negative value.
            ''' </summary>
            Public Function I16(value As Short) As RemoteWriter
                Dim raw As UShort
                If value < 0S Then
                    raw = CUShort(CInt(value) + 65536)
                Else
                    raw = CUShort(value)
                End If
                Return U16(raw)
            End Function

            ''' <summary>A length-prefixed byte string. The prefix counts bytes.</summary>
            Public Function Blob(value As Byte()) As RemoteWriter
                Dim bytes As Byte() = If(value, New Byte() {})
                U32(CUInt(bytes.Length))
                For index As Integer = 0 To bytes.Length - 1
                    _parts.Add(bytes(index))
                Next
                Return Me
            End Function

            ''' <summary>
            ''' A length-prefixed UTF-8 string. .NET counts UTF-16 code units, so the
            ''' prefix must be the ENCODED byte count or a page with an accent in it
            ''' breaks the frame.
            ''' </summary>
            Public Function Str(value As String) As RemoteWriter
                If value Is Nothing Then Return Blob(New Byte() {})
                Return Blob(Encoding.UTF8.GetBytes(value))
            End Function

            Public Function Build() As Byte()
                Return _parts.ToArray()
            End Function

        End Class

        ''' <summary>
        ''' Reads a payload. Every read is bounds-checked, and RequireEnd is not
        ''' optional: a field nobody reads is a field nobody wrote.
        ''' </summary>
        Public NotInheritable Class RemoteReader

            Private ReadOnly _buffer As Byte()
            Private _offset As Integer

            Public Sub New(buffer As Byte())
                _buffer = If(buffer, New Byte() {})
                _offset = 0
            End Sub

            Public ReadOnly Property Remaining As Integer
                Get
                    Return _buffer.Length - _offset
                End Get
            End Property

            Private Sub Need(width As Integer)
                If _offset + width > _buffer.Length Then
                    Throw New RemoteProtocolException("truncated payload")
                End If
            End Sub

            Public Function U8() As Byte
                Need(1)
                _offset += 1
                Return _buffer(_offset - 1)
            End Function

            Public Function U16() As UShort
                Need(2)
                Dim value As UShort = CUShort((CUInt(_buffer(_offset)) << 8) Or CUInt(_buffer(_offset + 1)))
                _offset += 2
                Return value
            End Function

            Public Function U32() As UInteger
                Need(4)
                Dim value As UInteger =
                    (CUInt(_buffer(_offset)) << 24) Or (CUInt(_buffer(_offset + 1)) << 16) Or
                    (CUInt(_buffer(_offset + 2)) << 8) Or CUInt(_buffer(_offset + 3))
                _offset += 4
                Return value
            End Function

            ''' <summary>
            ''' The protocol's only signed field. The branch matters: CShort(-65536)
            ''' overflows, so subtracting the modulus unconditionally would throw on
            ''' every non-negative value.
            ''' </summary>
            Public Function I16() As Short
                Dim raw As UShort = U16()
                If raw >= &H8000US Then
                    Return CShort(CInt(raw) - 65536)
                End If
                Return CShort(raw)
            End Function

            Public Function Blob() As Byte()
                Dim length As Integer = CInt(U32())
                If length <= 0 Then Return New Byte() {}
                Need(length)
                Dim value(length - 1) As Byte
                Array.Copy(_buffer, _offset, value, 0, length)
                _offset += length
                Return value
            End Function

            Public Function Str() As String
                Dim bytes As Byte() = Blob()
                If bytes.Length = 0 Then Return String.Empty
                Return Encoding.UTF8.GetString(bytes, 0, bytes.Length)
            End Function

            ''' <summary>
            ''' Called at the end of every parse. Trailing bytes mean the two ends
            ''' disagree about the layout, so this is an exception rather than an
            ''' ignored extension.
            ''' </summary>
            Public Sub RequireEnd()
                If Remaining <> 0 Then
                    Throw New RemoteProtocolException(
                        Remaining.ToString() & " trailing byte(s) in the payload")
                End If
            End Sub

        End Class

        ''' <summary>The message types, as the server numbers them.</summary>
        Public NotInheritable Class RemoteMessageType

            ' The handshake, in the clear: the salt they carry is what the keys come
            ' from, so there is nothing to seal them with yet.
            Public Const Hello As Byte = &H1
            Public Const HelloAck As Byte = &H2
            Public Const ErrorMessage As Byte = &H3

            ' Client to server, all sealed.
            Public Const Navigate As Byte = &H10
            Public Const Back As Byte = &H11
            Public Const Forward As Byte = &H12
            Public Const Reload As Byte = &H13
            Public Const StopLoading As Byte = &H14
            Public Const Resize As Byte = &H15
            Public Const Tap As Byte = &H16
            Public Const Scroll As Byte = &H17
            Public Const Key As Byte = &H18
            Public Const Text As Byte = &H19
            Public Const Find As Byte = &H1A
            Public Const Settings As Byte = &H1B
            Public Const Ping As Byte = &H1C
            Public Const Ack As Byte = &H1D

            ' Server to client, all sealed.
            Public Const Title As Byte = &H20
            Public Const Url As Byte = &H21
            Public Const LoadState As Byte = &H22
            Public Const Frame As Byte = &H23
            Public Const FindResult As Byte = &H24
            Public Const Audio As Byte = &H25
            Public Const Pong As Byte = &H26

            Private Sub New()
            End Sub

        End Class

        ''' <summary>
        ''' The messages, one encoder per field list. The field ORDER in these
        ''' functions IS the protocol, and tools/proto/remote-protocol.mjs reads this
        ''' file to assert each one against the server's encoder.
        ''' </summary>
        Public NotInheritable Class RemoteMessages

            Private Sub New()
            End Sub

            ' ── The handshake ──────────────────────────────────────────────

            Public Shared Function EncodeHello(protocolVersion As Integer, deviceId As String,
                                               token As String, viewportWidth As Integer,
                                               viewportHeight As Integer, devicePixelRatio As Integer,
                                               clientName As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.U8(CByte(protocolVersion))
                writer.Str(deviceId)
                writer.Str(token)
                writer.U16(CUShort(viewportWidth))
                writer.U16(CUShort(viewportHeight))
                writer.U8(CByte(devicePixelRatio))
                writer.Str(clientName)
                Return writer.Build()
            End Function

            ''' <summary>
            ''' The server's answer. On success it carries the session salt, which is
            ''' the whole reason this message is not sealed.
            ''' </summary>
            Public Shared Function DecodeHelloAck(payload As Byte()) As RemoteHelloAck
                Dim reader As New RemoteReader(payload)
                Dim answer As New RemoteHelloAck()
                answer.Ok = reader.U8() <> 0
                If Not answer.Ok Then
                    answer.Code = reader.U16()
                    answer.Message = reader.Str()
                    reader.RequireEnd()
                    Return answer
                End If
                answer.SessionSalt = reader.Blob()
                answer.MaxFrameBytes = reader.U32()
                answer.Flags = reader.U8()
                answer.ServerName = reader.Str()
                answer.AudioUrl = reader.Str()
                reader.RequireEnd()
                If answer.SessionSalt Is Nothing OrElse answer.SessionSalt.Length = 0 Then
                    Throw New RemoteProtocolException("the handshake carried no session salt")
                End If
                Return answer
            End Function

            Public Shared Function DecodeError(payload As Byte()) As RemoteMessageError
                Dim reader As New RemoteReader(payload)
                Dim errorMessage As New RemoteMessageError()
                errorMessage.Code = reader.U16()
                errorMessage.Message = reader.Str()
                reader.RequireEnd()
                Return errorMessage
            End Function

            ' ── Client to server ───────────────────────────────────────────

            Public Shared Function EncodeNavigate(url As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.Str(url)
                Return writer.Build()
            End Function

            ''' <summary>BACK, FORWARD, RELOAD and STOP carry no fields at all.</summary>
            Public Shared Function EncodeEmpty() As Byte()
                Return New Byte() {}
            End Function

            Public Shared Function EncodeResize(width As Integer, height As Integer,
                                                devicePixelRatio As Integer) As Byte()
                Dim writer As New RemoteWriter()
                writer.U16(CUShort(width))
                writer.U16(CUShort(height))
                writer.U8(CByte(devicePixelRatio))
                Return writer.Build()
            End Function

            Public Shared Function EncodeTap(x As Integer, y As Integer, buttons As Integer,
                                             clickCount As Integer) As Byte()
                Dim writer As New RemoteWriter()
                writer.U16(CUShort(x))
                writer.U16(CUShort(y))
                writer.U8(CByte(buttons))
                writer.U8(CByte(clickCount))
                Return writer.Build()
            End Function

            Public Shared Function EncodeScroll(x As Integer, y As Integer, deltaX As Short,
                                                deltaY As Short) As Byte()
                Dim writer As New RemoteWriter()
                writer.U16(CUShort(x))
                writer.U16(CUShort(y))
                writer.I16(deltaX)
                writer.I16(deltaY)
                Return writer.Build()
            End Function

            ''' <summary>
            ''' A Playwright key NAME ("Enter", "Backspace"), not a scan code, so the
            ''' device needs no key table. A non-empty text inserts instead of pressing.
            ''' </summary>
            Public Shared Function EncodeKey(keyName As String, modifiers As Integer,
                                             text As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.Str(keyName)
                writer.U8(CByte(modifiers))
                writer.Str(text)
                Return writer.Build()
            End Function

            Public Shared Function EncodeText(text As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.Str(text)
                Return writer.Build()
            End Function

            Public Shared Function EncodeFind(text As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.Str(text)
                Return writer.Build()
            End Function

            Public Shared Function EncodeSettings(nightMode As Boolean, desktopMode As Boolean,
                                                  blockTrackers As Boolean) As Byte()
                Dim flags As Integer = 0
                If nightMode Then flags = flags Or 1
                If desktopMode Then flags = flags Or 2
                If blockTrackers Then flags = flags Or 4
                Dim writer As New RemoteWriter()
                writer.U8(CByte(flags))
                Return writer.Build()
            End Function

            ''' <summary>PING and PONG share this layout: a bare u32 to echo back.</summary>
            Public Shared Function EncodeNonce(nonce As UInteger) As Byte()
                Dim writer As New RemoteWriter()
                writer.U32(nonce)
                Return writer.Build()
            End Function

            ''' <summary>
            ''' Acknowledges the frame whose sequence number is `frameSeq`. The server
            ''' sends at most one frame at a time and restarts its screencast only when
            ''' this arrives, which is the entire flow control.
            ''' </summary>
            Public Shared Function EncodeAck(frameSeq As UInteger) As Byte()
                Dim writer As New RemoteWriter()
                writer.U32(frameSeq)
                Return writer.Build()
            End Function

            ' ── Server to client ───────────────────────────────────────────

            Public Shared Function DecodeTitle(payload As Byte()) As RemoteTitle
                Dim reader As New RemoteReader(payload)
                Dim title As New RemoteTitle()
                title.Title = reader.Str()
                reader.RequireEnd()
                Return title
            End Function

            Public Shared Function DecodeUrl(payload As Byte()) As RemoteUrl
                Dim reader As New RemoteReader(payload)
                Dim url As New RemoteUrl()
                url.Url = reader.Str()
                reader.RequireEnd()
                Return url
            End Function

            Public Shared Function DecodeLoadState(payload As Byte()) As RemoteLoadState
                Dim reader As New RemoteReader(payload)
                Dim state As New RemoteLoadState()
                state.State = reader.U8()
                state.Detail = reader.Str()
                reader.RequireEnd()
                Return state
            End Function

            Public Shared Function DecodeFindResult(payload As Byte()) As RemoteFindResult
                Dim reader As New RemoteReader(payload)
                Dim result As New RemoteFindResult()
                result.Found = reader.U8() <> 0
                result.Matches = reader.U32()
                reader.RequireEnd()
                Return result
            End Function

            Public Shared Function DecodeAudio(payload As Byte()) As RemoteAudio
                Dim reader As New RemoteReader(payload)
                Dim audio As New RemoteAudio()
                audio.Playing = reader.U8() <> 0
                audio.Url = reader.Str()
                reader.RequireEnd()
                Return audio
            End Function

            ''' <summary>
            ''' A frame: a tile list rather than one rectangle, because the primitive
            ''' that produces them today hands back a whole viewport and a differ that
            ''' sends only what changed is the obvious next step. It costs two bytes
            ''' now and saves a protocol version later.
            ''' </summary>
            Public Shared Function DecodeFramePayload(payload As Byte()) As RemoteFramePayload
                Dim reader As New RemoteReader(payload)
                Dim result As New RemoteFramePayload()
                result.Format = reader.U8()
                result.Flags = reader.U8()
                Dim tileCount As Integer = CInt(reader.U16())
                For index As Integer = 0 To tileCount - 1
                    Dim tile As New RemoteFrameTile()
                    tile.X = CInt(reader.U16())
                    tile.Y = CInt(reader.U16())
                    tile.Width = CInt(reader.U16())
                    tile.Height = CInt(reader.U16())
                    tile.Data = reader.Blob()
                    result.Tiles.Add(tile)
                Next
                reader.RequireEnd()
                Return result
            End Function

            Public Shared Function EncodeTitle(title As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.Str(title)
                Return writer.Build()
            End Function

            Public Shared Function EncodeLoadState(state As Byte, detail As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.U8(state)
                writer.Str(detail)
                Return writer.Build()
            End Function

            Public Shared Function EncodeFindResult(found As Boolean, matches As UInteger) As Byte()
                Dim writer As New RemoteWriter()
                writer.U8(If(found, CByte(1), CByte(0)))
                writer.U32(matches)
                Return writer.Build()
            End Function

            Public Shared Function EncodeAudio(playing As Boolean, url As String) As Byte()
                Dim writer As New RemoteWriter()
                writer.U8(If(playing, CByte(1), CByte(0)))
                writer.Str(url)
                Return writer.Build()
            End Function

        End Class

        ''' <summary>The server's handshake answer, in the clear.</summary>
        Public NotInheritable Class RemoteHelloAck

            Public Property Ok As Boolean
            Public Property Code As UShort
            Public Property Message As String
            Public Property SessionSalt As Byte()
            Public Property MaxFrameBytes As UInteger
            Public Property Flags As Byte
            Public Property ServerName As String
            Public Property AudioUrl As String

            ''' <summary>Bit 0 of Flags: this server can stream audio.</summary>
            Public ReadOnly Property HasAudio As Boolean
                Get
                    Return (Flags And CByte(1)) <> 0
                End Get
            End Property

        End Class

        Public NotInheritable Class RemoteMessageError

            Public Property Code As UShort
            Public Property Message As String

        End Class

        Public NotInheritable Class RemoteTitle

            Public Property Title As String

        End Class

        Public NotInheritable Class RemoteUrl

            Public Property Url As String

        End Class

        ''' <summary>State is 0 started, 1 done, 2 failed.</summary>
        Public NotInheritable Class RemoteLoadState

            Public Const Started As Byte = 0
            Public Const Done As Byte = 1
            Public Const Failed As Byte = 2

            Public Property State As Byte
            Public Property Detail As String

        End Class

        Public NotInheritable Class RemoteFindResult

            Public Property Found As Boolean
            Public Property Matches As UInteger

        End Class

        Public NotInheritable Class RemoteAudio

            Public Property Playing As Boolean
            Public Property Url As String

        End Class

        Public NotInheritable Class RemoteFramePayload

            ''' <summary>The only tile format the server sends: a JPEG.</summary>
            Public Const FormatJpeg As Byte = 1

            ''' <summary>
            ''' Bit 0 of Flags: this frame describes the WHOLE viewport, so a tile it
            ''' does not mention is gone rather than unchanged. The server sets it on
            ''' every frame it sends today, which is exactly why the client must not
            ''' assume it: the differ that sends only what changed is the next step,
            ''' and it is the frame that does NOT carry this bit.
            ''' </summary>
            Public Const FlagFull As Byte = &H1

            Public Sub New()
                Tiles = New System.Collections.Generic.List(Of RemoteFrameTile)()
            End Sub

            Public Property Format As Byte
            Public Property Flags As Byte
            Public Property Tiles As System.Collections.Generic.List(Of RemoteFrameTile)

        End Class

        Public NotInheritable Class RemoteFrameTile

            Public Property X As Integer
            Public Property Y As Integer
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property Data As Byte()

        End Class

    End Namespace

End Namespace
