' BrowserForWP — the remote engine: pages drawn by Chromium on a server, this
' device holding the picture and the fingers.
'
' WHAT THIS ENGINE IS HONEST ABOUT. It reports SupportsScripting = True, and it
' is right to: the page really does run its scripts, inside Chromium, on a
' machine this one can neither see nor invoke. InvokeScriptAsync therefore returns
' nothing at all, and the shell tells the two apart with a TYPE test rather than by
' reading a flag that is describing somebody else's computer.
'
' TWO SERVERS, ONE ORDER. RemoteServers.Order decides which server answers: the
' primary, then the secondary when the primary cannot be reached, then nothing.
' The engine walks that list and stops at the first connection that completes a
' handshake, so a person whose primary is down is still reading their page rather
' than watching a spinner.
'
' THE PORT IS NOT WHAT Uri SAYS. Uri.TryCreate fills in the SCHEME's default port,
' so "https://render.example.com" reports 443 and not "no port at all". Connecting
' where Uri points would therefore miss this protocol's own port on every server
' typed without one, which is all of them. The rule is IsDefaultPort: a url that
' did not spell out a port gets DefaultPort, and one that did keeps what it said.
'
' WHAT IS NOT HERE. The user's pin table is not consulted by this path: Tls13Client
' takes a host and nothing else, so a pin is enforced by the diagnostics fetch in
' Diagnostics.NetDocumentFetcher and NOT on the render channel. That is a real gap
' and it is written down in docs/MAINTAINING.md rather than left for a reader to
' discover by trusting the feature.

Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine
Imports BrowserForWP.Core.Engine.Remote
' TWO namespaces, and they are both needed. RemoteServers lives in
' BrowserForWP.Core.Remote (Task 4 wrote `Namespace Remote`), while the wire
' format -- RemoteMessages, RemoteMessageType, RemoteFramePayload -- lives in
' BrowserForWP.Core.Engine.Remote. The two files sit in the same FOLDER, which is
' why the first build of this file failed with BC30451 on RemoteServers and
' BC30002 on RemoteServerSettings: a folder is not a namespace, and the checker's
' import test passed because the namespace that was imported does exist.
Imports BrowserForWP.Core.Remote
Imports BrowserForWP.Rendering
Imports Windows.UI.Xaml.Controls

Namespace Engine

    ''' <summary>
    ''' How a navigation ended, in the same shape the shell already handles for the
    ''' WebView, so one handler can report a page from either engine.
    ''' </summary>
    Public NotInheritable Class RemoteNavigationResult

        Public Property IsSuccess As Boolean
        Public Property Url As String

        ''' <summary>A resource key, never a sentence: this layer holds no prose.</summary>
        Public Property StatusKey As String

        ''' <summary>Engine-level detail shown beside the localized reason.</summary>
        Public Property Detail As String

    End Class

    Public NotInheritable Class RemoteEngine
        Implements IBrowserEngine

        ''' <summary>The port this protocol listens on when a url does not say.</summary>
        Public Const DefaultPort As Integer = 8443

        ''' <summary>Where the device id lives in LocalSettings. It is not a secret.</summary>
        Private Const DeviceIdKey As String = "remoteDeviceId"

        Public Event Navigated As EventHandler(Of RemoteNavigationResult)

        ''' <summary>
        ''' The server has a sound to play. The url is the SERVER's, and the shell
        ''' plays it with MediaElement over Schannel: this TLS stack cannot feed a
        ''' media pipeline and does not need to.
        ''' </summary>
        Public Event Audio(playing As Boolean, url As String)

        Private ReadOnly _capabilities As EngineCapabilities
        Private ReadOnly _host As New Windows.UI.Xaml.Controls.Grid()
        Private ReadOnly _screen As RemoteScreen
        Private ReadOnly _settings As BrowserForWP.Core.Storage.AppSettings

        Private _channel As RemoteChannel
        Private _connectedServer As String = String.Empty
        Private _deviceId As String
        Private _currentUrl As String = String.Empty
        Private _ackedFrame As UInteger
        Private _viewportWidth As Integer
        Private _viewportHeight As Integer
        Private _devicePixelRatio As Integer

        ''' <param name="pinTable">
        ''' Accepted for the same call shape as the other engine, and deliberately
        ''' unused: see the header. The render channel does not consult pins yet.
        ''' </param>
        Public Sub New(settings As BrowserForWP.Core.Storage.AppSettings, pinTable As Object)
            _settings = settings
            _deviceId = LoadOrCreateDeviceId()

            _capabilities = New EngineCapabilities()
            _capabilities.Name = "Remote"
            _capabilities.RenderingEngine = "Chromium on a server"
            _capabilities.SupportsTls13 = True

            ' True, and the truth is about the PAGE rather than about this device.
            _capabilities.SupportsScripting = True
            _capabilities.SupportsModernJavaScript = True
            _capabilities.SupportsWebSocket = True
            _capabilities.SupportsFetch = True

            _screen = New RemoteScreen(AddressOf OnInput, AddressOf OnScroll, AddressOf OnKey,
                                       MeasurePixelRatio())
            _host.Children.Add(DirectCast(_screen.Source, Windows.UI.Xaml.UIElement))

            ' The screen is built ONCE, here, and a rotation never rebuilds it: the
            ' hidden field that owns the soft keyboard is a child of that canvas, and
            ' a field rebuilt when the phone turns loses the word the person was
            ' halfway through typing. tools/proto/remote-input.mjs asserts the count.
            Try
                AddHandler Windows.UI.Xaml.Window.Current.SizeChanged, AddressOf OnWindowSizeChanged
                ApplyViewport()
            Catch
                ' A shell with no window yet keeps the viewport the first navigation
                ' gives it; ApplyViewport runs again there.
            End Try
        End Sub

        Public ReadOnly Property Capabilities As EngineCapabilities Implements IBrowserEngine.Capabilities
            Get
                Return _capabilities
            End Get
        End Property

        Public ReadOnly Property Source As Object Implements IBrowserEngine.Source
            Get
                Return _host
            End Get
        End Property

        ''' <summary>
        ''' The frame sequence this device last drew and acknowledged. Read by
        ''' nothing yet, and kept because a dropped connection's recovery needs to
        ''' know where the picture stopped.
        ''' </summary>
        Public ReadOnly Property AckedFrame As UInteger
            Get
                Return _ackedFrame
            End Get
        End Property

        ''' <summary>
        ''' Which SERVER is answering, or empty when none is. It is the url from the
        ''' settings screen rather than the page's own url, because this is the
        ''' question "am I on my primary or my backup", and a page url cannot answer
        ''' it.
        ''' </summary>
        Public ReadOnly Property ConnectedServer As String
            Get
                If _channel Is Nothing OrElse Not _channel.IsOpen Then Return String.Empty
                Return _connectedServer
            End Get
        End Property

        Public Sub Navigate(url As String) Implements IBrowserEngine.Navigate
            _currentUrl = url
            ' Fire and forget on purpose: IBrowserEngine.Navigate is a Sub and the
            ' shell calls it from a click handler. Everything it can go wrong with is
            ' reported through the Navigated event, which is where the shell is
            ' already listening.
            NavigateCore(url)
        End Sub

        Private Async Sub NavigateCore(url As String)
            Try
                ApplyViewport()

                ' The toggle is the disclosure, and this is where it is honoured.
                ' Choosing this engine in the picker is not the same statement as
                ' agreeing to send every page through somebody else's machine, and
                ' the automatic fallback can pick this engine without anybody
                ' choosing anything at all -- so the switch is read HERE, on every
                ' navigation, rather than where the engine was built.
                If Not _settings.RemoteEnabled Then
                    RaiseEvent Navigated(Me, Failed("EngineReasonRemoteNotConfigured",
                                                    "the hosted engine is switched off", url))
                    Return
                End If

                Dim candidates As List(Of String) = RemoteServers.Order(CurrentRemoteSettings())
                If candidates.Count = 0 Then
                    ' Nothing is configured, and nothing was sent anywhere. On a
                    ' fresh install the address IS set -- it is AppSettings'
                    ' DefaultHostedUrl -- so what lands here is an install whose
                    ' server field was cleared, or whose settings predate the hosted
                    ' default.
                    RaiseEvent Navigated(Me, Failed("EngineReasonRemoteNotConfigured",
                                                    "no render server is configured", url))
                    Return
                End If

                Await ConnectAndNavigateAsync(url, candidates)
            Catch ex As Exception
                RaiseEvent Navigated(Me, Failed("EngineReasonRemoteUnreachable", ex.Message, url))
            End Try
        End Sub

        ''' <summary>
        ''' Walks the candidates and stops at the first server that answers. The
        ''' reason it reports names the ROLE that answered, so a person reading
        ''' "the backup server is in use" knows their primary is down without
        ''' opening a settings screen.
        ''' </summary>
        Private Async Function ConnectAndNavigateAsync(url As String,
                                                       candidates As List(Of String)) As Task
            Dim settings As RemoteServerSettings = CurrentRemoteSettings()
            Dim primaryWasTried As Boolean = False
            Dim lastFailure As String = String.Empty

            For index As Integer = 0 To candidates.Count - 1
                Dim candidate As String = candidates(index)
                If index = 0 Then primaryWasTried = True

                Dim token As String = RemoteServers.TokenFor(settings, candidate)
                If token.Length = 0 Then
                    lastFailure = candidate & " has no device token"
                    Continue For
                End If

                Dim parsed As Uri = Nothing
                If Not Uri.TryCreate(candidate, UriKind.Absolute, parsed) Then
                    lastFailure = candidate & " is not a web address"
                    Continue For
                End If

                Dim channel As New RemoteChannel()
                Try
                    Await channel.ConnectAsync(parsed.Host, PortFor(parsed), _deviceId, token,
                                               _viewportWidth, _viewportHeight, _devicePixelRatio,
                                               "BrowserForWP Windows Phone 8.1")
                Catch ex As Exception
                    lastFailure = candidate & ": " & ex.Message
                    channel.Disconnect()
                    Continue For
                End Try

                ' The old connection goes only once the new one is open, so a
                ' failed attempt at the secondary does not cost the user the page
                ' they are reading.
                ReplaceChannel(channel)
                _connectedServer = candidate
                Await _channel.NavigateAsync(url)
                channel.StartReading(AddressOf OnServerMessage, AddressOf OnChannelClosed)
                Await SendLocalSettingsAsync()

                Dim explanation As String = RemoteServers.Explain(settings, candidate, primaryWasTried)
                Dim result As New RemoteNavigationResult()
                result.IsSuccess = True
                result.Url = url
                result.StatusKey = explanation
                result.Detail = candidate
                RaiseEvent Navigated(Me, result)
                Return
            Next

            RaiseEvent Navigated(Me, Failed("EngineReasonRemoteUnreachable", lastFailure, url))
        End Function

        Public Sub GoBack() Implements IBrowserEngine.GoBack
            Send(RemoteMessageType.Back, RemoteMessages.EncodeEmpty())
        End Sub

        Public Sub [GoForward]() Implements IBrowserEngine.GoForward
            Send(RemoteMessageType.Forward, RemoteMessages.EncodeEmpty())
        End Sub

        Public Sub Reload() Implements IBrowserEngine.Reload
            Send(RemoteMessageType.Reload, RemoteMessages.EncodeEmpty())
        End Sub

        ''' <summary>
        ''' Drops the connection. The protocol has a STOP message and this does not
        ''' use it: what a person wants when they press stop is for the picture to
        ''' stop moving, and a closed socket does that without leaving a session
        ''' running on a machine they cannot see.
        ''' </summary>
        Public Sub [Stop]() Implements IBrowserEngine.Stop
            If _channel Is Nothing Then Return
            _channel.Disconnect()
            _channel = Nothing
            _connectedServer = String.Empty
        End Sub

        Public Function InvokeScriptAsync(script As String) As Task(Of String) Implements IBrowserEngine.InvokeScriptAsync
            ' No script host HERE. The page's scripts run inside Chromium on the
            ' server, where this device cannot reach them, and claiming otherwise
            ' is exactly the lie EngineCapabilities exists to prevent.
            Return Task.FromResult(String.Empty)
        End Function

        ' ── The server's messages ─────────────────────────────────────────

        ''' <summary>
        ''' One sealed message from the server, awaited by the read loop in order.
        ''' </summary>
        Private Async Function OnServerMessage(messageType As Byte, sequence As UInteger,
                                               payload As Byte()) As Task
            Try
                Select Case messageType
                    Case RemoteMessageType.Frame
                        Await DrawAndAckAsync(sequence, payload)
                    Case RemoteMessageType.Url
                        ' The server reports where it actually LANDED, which is how a
                        ' redirect becomes visible to the address bar.
                        Dim landed As RemoteUrl = RemoteMessages.DecodeUrl(payload)
                        _currentUrl = landed.Url
                    Case RemoteMessageType.LoadState
                        Dim state As RemoteLoadState = RemoteMessages.DecodeLoadState(payload)
                        If state.State = RemoteLoadState.Failed Then
                            RaiseEvent Navigated(Me, Failed("ErrorNavigationFailed", state.Detail, _currentUrl))
                        End If
                    Case RemoteMessageType.Audio
                        Dim sound As RemoteAudio = RemoteMessages.DecodeAudio(payload)
                        RaiseEvent Audio(sound.Playing, sound.Url)
                    Case RemoteMessageType.Focus
                        ' The page's focus, and therefore whether the soft keyboard
                        ' belongs on screen. It arrives AFTER the tap that caused it,
                        ' so the keyboard rises a moment after the finger lifts --
                        ' which is the price of asking somebody who can actually
                        ' see the field. Raising it optimistically instead is what
                        ' put a keyboard over every link in the first place.
                        Dim focused As RemoteFocus = RemoteMessages.DecodeFocus(payload)
                        _screen.SetPageFocus(focused.Editable)
                    Case RemoteMessageType.Title, RemoteMessageType.FindResult, RemoteMessageType.Pong
                        ' Accepted and not yet shown. Every one of them is a message
                        ' this client knows how to read, which is the point: an
                        ' unknown type would be a protocol this build cannot speak.
                        Return
                    Case RemoteMessageType.Hello, RemoteMessageType.HelloAck, RemoteMessageType.ErrorMessage
                        ' Nothing to do: the read loop already refuses a plaintext
                        ' frame after the handshake, so reaching this arm is a bug in
                        ' that rule rather than a message to act on.
                        Return
                End Select
            Catch ex As Exception
                ' A message this client cannot act on must not take the read loop
                ' down with it, because the loop's own error reporting is what tells
                ' the person anything at all.
                RaiseEvent Navigated(Me, Failed("ErrorPageFailed", ex.Message, _currentUrl))
            End Try
        End Function

        ''' <summary>
        ''' Draws a frame and only then acknowledges it. The order matters and is
        ''' the whole of the flow control: the server holds its screencast until an
        ''' ACK for the frame in flight arrives, so an acknowledgement sent BEFORE
        ''' the picture is on screen buys a queue of frames the device cannot draw.
        ''' </summary>
        Private Async Function DrawAndAckAsync(sequence As UInteger, payload As Byte()) As Task
            If _channel Is Nothing Then Return

            Dim frame As RemoteFramePayload = RemoteMessages.DecodeFramePayload(payload)
            Dim isFull As Boolean = (frame.Flags And RemoteFramePayload.FlagFull) <> 0
            Await _screen.ShowFrameAsync(frame.Tiles, isFull)

            _ackedFrame = sequence
            Await _channel.SendAsync(RemoteMessageType.Ack, RemoteMessages.EncodeAck(sequence))
        End Function

        Private Sub OnChannelClosed(reason As String)
            RaiseEvent Navigated(Me, Failed("EngineReasonRemoteUnreachable", reason, _currentUrl))
        End Sub

        ' ── The device's fingers, as messages ─────────────────────────────

        Private Sub OnInput(x As Integer, y As Integer)
            ' Buttons 1 is the left button; one click. A long press, a right button
            ' and a double tap are all things this protocol could carry and this
            ' screen does not yet produce.
            Send(RemoteMessageType.Tap, RemoteMessages.EncodeTap(x, y, 1, 1))
        End Sub

        Private Sub OnScroll(x As Integer, y As Integer, deltaX As Integer, deltaY As Integer)
            ' The deltas arrive already clamped to the i16 the wire carries: see
            ' RemoteScreen, where clamping is separated from emitting so that a fast
            ' flick cannot wrap into a scroll the other way.
            Send(RemoteMessageType.Scroll,
                 RemoteMessages.EncodeScroll(x, y, CShort(deltaX), CShort(deltaY)))
        End Sub

        ''' <summary>
        ''' Presses a named key on the page, for the shell's keys bar. The names are
        ''' Playwright's, and a name the server does not recognise is a key that does
        ''' nothing rather than a failure -- which is why the eight this shell offers
        ''' are asserted against a list in tools/proto/remote-input.mjs instead of
        ''' being typed into the XAML.
        '''
        ''' The modifier byte goes as 0, and not because it is unavailable: the
        ''' server reads { key, text } and drops { modifiers }, so sending one would
        ''' be a byte that does nothing. Deferred item 18.
        ''' </summary>
        Public Sub TypeKey(keyName As String)
            If String.IsNullOrEmpty(keyName) Then Return
            Send(RemoteMessageType.Key, RemoteMessages.EncodeKey(keyName, 0, String.Empty))
        End Sub

        Private Sub OnKey(keyName As String, modifiers As Integer, text As String)
            If String.IsNullOrEmpty(keyName) Then
                Send(RemoteMessageType.Text, RemoteMessages.EncodeText(text))
                Return
            End If
            Send(RemoteMessageType.Key, RemoteMessages.EncodeKey(keyName, modifiers, text))
        End Sub

        ''' <summary>
        ''' Seals a payload if there is anywhere to send it. A tap on a picture that
        ''' is not connected is not an error: it is a finger on a photograph.
        ''' </summary>
        Private Sub Send(messageType As Byte, payload As Byte())
            If _channel Is Nothing OrElse Not _channel.IsOpen Then Return
            SendAsync(messageType, payload)
        End Sub

        Private Async Sub SendAsync(messageType As Byte, payload As Byte())
            Try
                Await _channel.SendAsync(messageType, payload)
            Catch ex As Exception
                RaiseEvent Navigated(Me, Failed("EngineReasonRemoteUnreachable", ex.Message, _currentUrl))
            End Try
        End Sub

        ''' <summary>
        ''' Tells the server how this person likes pages, once per connection.
        ''' </summary>
        ''' <remarks>
        ''' Night mode is sent as False on purpose even when the preference is on.
        ''' It is a Trident feature in this browser, its toggle is disabled on this
        ''' engine, and quietly applying a second, different night mode on the
        ''' server would be a setting that does two things.
        ''' </remarks>
        Private Async Function SendLocalSettingsAsync() As Task
            If _channel Is Nothing Then Return
            Dim payload As Byte() = RemoteMessages.EncodeSettings(False, _settings.DesktopMode,
                                                                  _settings.BlockTrackers)
            Try
                Await _channel.SendAsync(RemoteMessageType.Settings, payload)
            Catch ex As Exception
                ' A server that will not take a settings message is still a server
                ' that draws pages; only the preference is lost.
            End Try
        End Function

        ' ── Plumbing ──────────────────────────────────────────────────────

        Private Sub ReplaceChannel(channel As RemoteChannel)
            Dim previous As RemoteChannel = _channel
            _channel = channel
            If previous IsNot Nothing Then
                ' Safe to call twice, and its own read loop does not report a close
                ' it was asked for as a failure.
                previous.Disconnect()
            End If
        End Sub

        ''' <summary>
        ''' The port to connect to. Uri has already filled in the scheme's default
        ''' when the url did not carry one, so IsDefaultPort -- and not "is the port
        ''' zero" -- is what tells the two cases apart.
        ''' </summary>
        Private Shared Function PortFor(server As Uri) As Integer
            If server.IsDefaultPort Then Return DefaultPort
            Return server.Port
        End Function

        ''' <summary>
        ''' What the user configured, as plain data. One definition for this and for
        ''' the shell, which asks the same snapshot whether the hosted engine is
        ''' usable at all -- and built on every use rather than cached, because the
        ''' settings screen can change the server between two navigations: an engine
        ''' that kept the first answer would send the next page to the server the
        ''' person just stopped using.
        ''' </summary>
        Private Function CurrentRemoteSettings() As RemoteServerSettings
            Return _settings.RemoteSettings()
        End Function

        Private Sub ApplyViewport()
            Dim bounds As Windows.Foundation.Rect = Windows.UI.Xaml.Window.Current.Bounds
            _viewportWidth = Math.Max(1, CInt(bounds.Width))
            _viewportHeight = Math.Max(1, CInt(bounds.Height))
            _devicePixelRatio = MeasurePixelRatio()
            _screen.SetViewport(_viewportWidth, _viewportHeight, _devicePixelRatio)
        End Sub

        ''' <summary>
        ''' The phone turned. Two numbers have to move together, and this is the only
        ''' place both do: the viewport the server draws at, and the viewport a finger
        ''' is mapped through. Move only the second and every tap is offset by the
        ''' difference; move only the first and the picture is letterboxed against a
        ''' mapping that no longer describes it. A picture that looks right with taps
        ''' that land in the wrong place is the failure this method exists to stop.
        ''' </summary>
        ''' <remarks>
        ''' The event is on the Window (Windows.UI.Xaml) and its arguments are in
        ''' Windows.UI.Core -- a split that cost the first build of this method BC30002
        ''' on Windows.UI.Xaml.WindowSizeChangedEventArgs, which does not exist. The
        ''' compiler is the only thing that would have said so: no checker here reads
        ''' a WinRT type name.
        ''' </remarks>
        Private Sub OnWindowSizeChanged(sender As Object, e As Windows.UI.Core.WindowSizeChangedEventArgs)
            If _channel Is Nothing OrElse Not _channel.IsOpen Then Return

            ' The PAGE's answer, not the hidden field's focus state: the tree was
            ' just re-arranged, and the field reports Unfocused whether or not the
            ' person was halfway through a sentence.
            Dim wantsKeyboard As Boolean = _screen.WantsKeyboard
            ApplyViewport()
            If wantsKeyboard Then _screen.FocusKeyboard()

            Send(RemoteMessageType.Resize,
                 RemoteMessages.EncodeResize(_viewportWidth, _viewportHeight, _devicePixelRatio))
        End Sub

        ''' <summary>
        ''' How many physical pixels the platform puts in one layout pixel. Asked of
        ''' the display, and defaulted to 1 rather than guessed: a wrong ratio here
        ''' is a picture that is the wrong size, and 1 is the only value that cannot
        ''' make it worse.
        ''' </summary>
        ''' <remarks>
        ''' RawPixelsPerViewPixel, and NOT ResolutionScale. The first version of this
        ''' asked for ResolutionScale and the phone's own compiler answered, in
        ''' Italian, with BC40019: "ResolutionScale is not supported on Windows
        ''' Phone, and can return incorrect results. Instead, use
        ''' RawPixelsPerViewPixel". A deprecated-API warning that names its own
        ''' replacement, on the platform this app actually ships to, is not a
        ''' warning to read past.
        ''' </remarks>
        Private Shared Function MeasurePixelRatio() As Integer
            Try
                Dim display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView()
                Dim ratio As Double = display.RawPixelsPerViewPixel
                If ratio >= 1.0 Then Return CInt(Math.Round(ratio))
            Catch
                ' No display information yet, or a platform that will not say.
            End Try
            Return 1
        End Function

        ''' <summary>
        ''' The device id: generated once, then kept. It identifies this handset to
        ''' the server, it is not a secret (the token is), and it survives a restart
        ''' because a server that re-registers a device on every launch is a server
        ''' whose operator cannot tell one device from another.
        ''' </summary>
        Private Shared Function LoadOrCreateDeviceId() As String
            Try
                Dim localValues = Windows.Storage.ApplicationData.Current.LocalSettings.Values
                If localValues.ContainsKey(DeviceIdKey) Then
                    Dim stored As String = TryCast(localValues(DeviceIdKey), String)
                    If Not String.IsNullOrEmpty(stored) Then Return stored
                End If
                Dim created As String = Guid.NewGuid().ToString("N")
                localValues(DeviceIdKey) = created
                Return created
            Catch
                ' Storage that will not answer still gets a usable id for this run.
                Return Guid.NewGuid().ToString("N")
            End Try
        End Function

        ''' <summary>
        ''' A failure carries the page that was asked for, not an empty string.
        '''
        ''' The shell needs it: when the hosted engine cannot be used at all, the page
        ''' is handed to the on-device engine, and that engine has to be pointed at
        ''' the page the person asked for. `_session.ActiveTab.Url` is the PREVIOUS
        ''' page, so a fallback that read it would quietly load the wrong one -- and
        ''' look like it worked.
        ''' </summary>
        Private Shared Function Failed(statusKey As String, detail As String, pageUrl As String) As RemoteNavigationResult
            Dim result As New RemoteNavigationResult()
            result.IsSuccess = False
            result.Url = If(pageUrl, String.Empty)
            result.StatusKey = statusKey
            result.Detail = detail
            Return result
        End Function

    End Class

End Namespace
