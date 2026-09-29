' BrowserForWP — one render connection: the handshake, the sealed frames after it,
' and a read loop. Nothing else.
'
' WHY THIS IS IN THE APP AND NOT IN BrowserForWP.Net. It needs the protocol
' (BrowserForWP.Core) AND the TLS client (BrowserForWP.Net), and Net references
' only Crypto -- deliberately, because the dependency direction in
' docs/ARCHITECTURE.md is strictly downward. The app is the only layer that may
' see both, so this is where they are joined.
'
' It runs on the SAME TLS 1.3 stack as the rest of the browser. That is why there
' is no WebSocket here: Tls13Client already hands back a bidirectional byte stream
' (ConnectAsync / WriteAsync / ReadAsync), so the framed protocol needs no second
' transport and no fall back to Schannel.
'
' This file has no offline referee, and that is a limit rather than a design
' choice: it needs a handshake with a live server. Everything it does that can go
' wrong SILENTLY -- the framing, the keys, the replay rule -- is in RemoteProtocol
' and SealedChannel, and both are pinned by vectors. What is left here is a
' socket, a loop, and error handling, which fail loudly.

Imports System
Imports System.Text
Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine.Remote
Imports BrowserForWP.Net.Remote
Imports BrowserForWP.Net.Tls13

Namespace Engine

    ''' <summary>
    ''' One device's connection to one server. Single-use: a refused handshake or a
    ''' dropped socket means making a new one, which is what rekeys the channel.
    ''' </summary>
    Public NotInheritable Class RemoteChannel

        Private ReadOnly _reader As New RemoteFrameReader(131072)

        ''' <summary>
        ''' One writer at a time, and it covers the SEAL as well as the write.
        '''
        ''' Both halves matter and neither is obvious. Sealing is what allocates the
        ''' sequence number, so two callers inside Seal at once can read the same
        ''' number and encrypt two different records with the same nonce: not a
        ''' garbled screen but a broken channel, and one the server answers by closing
        ''' it. And Tls13Client.WriteAsync does not serialise its callers -- two
        ''' concurrent writes interleave their records on one socket.
        '''
        ''' This went unnoticed until the keyboard arrived, because until then every
        ''' message came from one place at a time. Typing is many small messages from
        ''' two: the UI thread sends keystrokes while the read loop sends the frame
        ''' acknowledgement for the frame that shows them.
        ''' </summary>
        Private ReadOnly _writeGate As New System.Threading.SemaphoreSlim(1, 1)

        Private _tls As Tls13Client
        Private _channel As SealedChannel
        Private _helloAck As RemoteHelloAck
        Private _activeUrl As String = String.Empty
        Private _readLoop As Task
        Private _closed As Boolean
        Private _closedOnPurpose As Boolean

        Public ReadOnly Property IsOpen As Boolean
            Get
                Return _channel IsNot Nothing AndAlso Not _closed
            End Get
        End Property

        Public ReadOnly Property HelloAck As RemoteHelloAck
            Get
                Return _helloAck
            End Get
        End Property

        ''' <summary>The PAGE url this connection was last asked for, not a server.</summary>
        Public ReadOnly Property ActiveUrl As String
            Get
                Return _activeUrl
            End Get
        End Property

        Public ReadOnly Property FramesSent As UInteger
            Get
                If _channel Is Nothing Then Return 0UI
                Return _channel.FramesSent
            End Get
        End Property

        ''' <summary>
        ''' Sends HELLO and reads HELLO_ACK. Throws on a refusal, with the server's
        ''' own reason in the message: "your token is wrong" is a different sentence
        ''' from "this device is not registered", and the person holding the phone
        ''' needs to know which one it is.
        ''' </summary>
        Public Async Function ConnectAsync(host As String, port As Integer, deviceId As String,
                                           token As String, viewportWidth As Integer,
                                           viewportHeight As Integer,
                                           devicePixelRatio As Integer,
                                           clientName As String) As Task(Of RemoteHelloAck)
            If String.IsNullOrEmpty(host) Then Throw New RemoteChannelException("no server host")
            If String.IsNullOrEmpty(deviceId) Then Throw New RemoteChannelException("no device id")
            If String.IsNullOrEmpty(token) Then Throw New RemoteChannelException("no device token")

            _closed = False
            _closedOnPurpose = False
            _tls = New Tls13Client(host)
            Await _tls.ConnectAsync(host, port)

            Dim hello As Byte() = RemoteMessages.EncodeHello(1, deviceId, token, viewportWidth,
                                                             viewportHeight, devicePixelRatio, clientName)
            Await WriteFrameAsync(RemoteMessageType.Hello, 0UI, hello)

            Dim answerFrame As RemoteFrame = Await ReadNextFrameAsync()
            If answerFrame Is Nothing Then
                Throw New RemoteChannelException("the server closed the connection during the handshake")
            End If
            If answerFrame.Type <> RemoteMessageType.HelloAck Then
                Throw New RemoteChannelException("the server did not answer the handshake")
            End If

            Dim answer As RemoteHelloAck = RemoteMessages.DecodeHelloAck(answerFrame.Payload)
            If Not answer.Ok Then
                Disconnect()
                Throw New RemoteChannelException(
                    "the server refused this device (" & answer.Code.ToString() & "): " & answer.Message)
            End If

            ' The header builder is injected because RemoteProtocol lives in Core and
            ' Net, which holds the key, may not reference it.
            _channel = New SealedChannel(Encoding.UTF8.GetBytes(token), answer.SessionSalt,
                                         AddressOf RemoteProtocol.EncodeHeader)
            _helloAck = answer
            Return answer
        End Function

        ''' <summary>
        ''' Starts the read loop. `onMessage` receives the type, the SEQUENCE NUMBER
        ''' and the decrypted payload of every sealed server message; `onClosed`
        ''' receives a reason, once, when the connection ends for any cause.
        '''
        ''' THE SEQUENCE NUMBER IS HANDED OVER, AND IT IS AWAITED. Both parts of that
        ''' sentence are load-bearing and both were wrong in the first version:
        '''
        '''   * The sequence is what a FRAME acknowledgement names. The server holds
        '''     its screencast until an ACK arrives for the frame it has in flight
        '''     (src/session.js, rule 4), so a handler that cannot see the number
        '''     cannot acknowledge anything, and the client would receive exactly one
        '''     frame per connection while looking perfectly healthy.
        '''   * The handler is a Task and the loop awaits it, so two messages are never
        '''     processed at once. Drawing a frame is asynchronous -- it decodes a JPEG
        '''     -- and a handler that returned at its first Await would let the next
        '''     message overtake the one still being drawn.
        ''' </summary>
        Public Sub StartReading(onMessage As Func(Of Byte, UInteger, Byte(), Task),
                               onClosed As Action(Of String))
            If _channel Is Nothing Then Throw New RemoteChannelException("not connected")

            Dim channel As SealedChannel = _channel
            _readLoop = ReadLoopAsync(channel, onMessage, onClosed)
        End Sub

        Private Async Function ReadLoopAsync(channel As SealedChannel,
                                             onMessage As Func(Of Byte, UInteger, Byte(), Task),
                                             onClosed As Action(Of String)) As Task
            Dim reason As String = "the connection ended"
            Try
                Do
                    Dim frame As RemoteFrame = Await ReadNextFrameAsync()
                    If frame Is Nothing Then
                        reason = "the server closed the connection"
                        Exit Do
                    End If

                    ' A handshake-type frame after the handshake is a downgrade
                    ' attempt, not a mistake: anyone able to rewrite the stream
                    ' could otherwise strip the seal layer by claiming it had not
                    ' started. The server enforces the same rule in the other
                    ' direction.
                    If Not RemoteProtocol.IsSealed(frame.Type) Then
                        reason = "a plaintext frame after the handshake"
                        Exit Do
                    End If

                    ' Something asked this connection to stop WHILE the read above was
                    ' waiting. Dispatching now would hand a replaced connection's
                    ' message to a handler that no longer belongs to it -- a frame
                    ' from the page the person just left, drawn over the one they
                    ' just asked for.
                    If _closed Then
                        reason = "this connection was closed on purpose"
                        Exit Do
                    End If

                    Dim payload As Byte() = channel.Open(frame.Header, frame.Seq, frame.Payload)
                    ' Awaited, so the handler finishes before the next frame is read.
                    ' An exception it does not catch is reported as a connection
                    ' failure rather than dropped, because a message that cannot be
                    ' handled is not a message to continue past.
                    Await onMessage(frame.Type, frame.Seq, payload)
                Loop
            Catch ex As SealedChannelException
                reason = "a sealed frame was refused: " & ex.Message
            Catch ex As RemoteProtocolException
                reason = "the server sent something this client cannot read: " & ex.Message
            Catch ex As Exception
                reason = "the connection failed: " & ex.Message
            End Try

            _closed = True

            ' A close THIS CLIENT asked for is not a failure to report. Without
            ' this, every second navigation would raise "the connection failed"
            ' moments after the page it replaced had rendered: ReplaceChannel
            ' disconnects the old connection asynchronously, so the read loop of the
            ' connection just retired reaches its end AFTER the new page has been
            ' reported, and the shell would show an error over a working page.
            If _closedOnPurpose Then Return

            Try
                onClosed(reason)
            Catch
                ' A handler that throws must not take the loop's error reporting
                ' with it; the loop is already over.
            End Try
        End Function

        ''' <summary>
        ''' Asks the server for a page and remembers which one. The url is kept
        ''' because the shell asks this connection where it is pointed, and a
        ''' connection that only knows other people's urls is a connection the shell
        ''' has to keep a second copy of.
        ''' </summary>
        Public Async Function NavigateAsync(url As String) As Task
            _activeUrl = url
            Await SendAsync(RemoteMessageType.Navigate, RemoteMessages.EncodeNavigate(url))
        End Function

        ''' <summary>Seals a payload and writes it. One call, one frame.</summary>
        Public Async Function SendAsync(messageType As Byte, payload As Byte()) As Task
            If _channel Is Nothing Then Throw New RemoteChannelException("not connected")
            If _closed Then Throw New RemoteChannelException("the connection is closed")

            ' Both are read BEFORE the gate, so a Disconnect that lands while this
            ' call is queued cannot turn a valid send into a null dereference on the
            ' other side of the wait.
            Dim channel As SealedChannel = _channel
            Dim tls As Tls13Client = _tls
            If tls Is Nothing Then Throw New RemoteChannelException("not connected")

            Await _writeGate.WaitAsync()
            Try
                Dim frame As Byte() = channel.Seal(messageType, payload)
                Await tls.WriteAsync(frame)
            Finally
                _writeGate.Release()
            End Try
        End Function

        ''' <summary>
        ''' The unsealed handshake write. Only ever a HELLO, and the salt it will
        ''' provoke is what makes every later frame sealable.
        ''' </summary>
        Private Async Function WriteFrameAsync(messageType As Byte, sequence As UInteger,
                                               payload As Byte()) As Task
            Dim header As Byte() = RemoteProtocol.EncodeHeader(messageType, sequence,
                                                              CUInt(payload.Length))
            Dim frame(header.Length + payload.Length - 1) As Byte
            Array.Copy(header, 0, frame, 0, header.Length)
            Array.Copy(payload, 0, frame, header.Length, payload.Length)

            Dim tls As Tls13Client = _tls
            If tls Is Nothing Then Throw New RemoteChannelException("not connected")

            ' The same gate as SendAsync. The handshake is the one write that is not
            ' sealed, and it is still a write: taking the gate here is what makes "a
            ' send is serialised" a property of this class rather than of one method.
            Await _writeGate.WaitAsync()
            Try
                Await tls.WriteAsync(frame)
            Finally
                _writeGate.Release()
            End Try
        End Function

        ''' <summary>
        ''' The next whole frame, reading more bytes as needed. A chunk is not a
        ''' message: one read can carry two frames and one frame can span several.
        ''' </summary>
        Private Async Function ReadNextFrameAsync() As Task(Of RemoteFrame)
            Do
                Dim frame As RemoteFrame = Nothing
                If _reader.TryTakeFrame(frame) Then Return frame
                If _tls Is Nothing Then Return Nothing

                Dim chunk As Byte() = Await _tls.ReadAsync()
                If chunk Is Nothing OrElse chunk.Length = 0 Then Return Nothing
                _reader.Append(chunk)
            Loop
        End Function

        ''' <summary>
        ''' Drops the socket and the keys. Safe to call twice, which matters because
        ''' both the read loop and the shell's Stop path can reach it.
        ''' </summary>
        Public Sub Disconnect()
            _closed = True
            _closedOnPurpose = True
            _channel = Nothing
            _helloAck = Nothing
            Dim tls As Tls13Client = _tls
            _tls = Nothing
            If tls IsNot Nothing Then
                Try
                    tls.Dispose()
                Catch
                    ' The socket may already be gone; a failed dispose is not news.
                End Try
            End If
        End Sub

    End Class

    ''' <summary>A connection-level failure, always with a reason a person can read.</summary>
    Public Class RemoteChannelException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub

    End Class

End Namespace
