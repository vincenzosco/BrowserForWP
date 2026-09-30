' BrowserForWP — the page this phone serves to a computer, so a device token can be
' pasted into a box instead of typed on a 2014 keyboard.
'
' WHAT IT OWNS, and nothing else: a listener, the small amount of HTTP a browser
' will actually send it, and the HTML of the form. Every RULE -- what a token looks
' like, which slot a word names, whether the code matches, whether the address can
' be stored -- is TokenInbox's, and every SENTENCE comes from the catalogue. That
' split is the same one the rest of this project uses, and it is what makes the
' rules testable off the phone.
'
' FOUR THINGS IT DELIBERATELY DOES NOT DO:
'
'   * It does not listen when nobody is looking. Start() comes from the button on
'     the settings screen, and Stop() comes from that button, from the screen
'     closing, and from five wrong codes. The window is the minute a person spends
'     reading the address off the screen, not a port left open all afternoon.
'   * It does not answer a submission without the code. See TokenInbox.Review: the
'     code is checked first, before the token is even looked at.
'   * It does not echo the token back. The page travels in the clear inside the
'     local network -- stated in the docs rather than pretended away here -- so the
'     secret goes over it once, from the computer, and the reply shows four
'     characters of it and no more.
'   * It does not draw the form over anything but a GET. A POST to a path that is
'     not the form is a refusal, not a second copy of the page.

Imports System
Imports System.Collections.Generic
Imports System.Text
Imports System.Threading.Tasks
Imports BrowserForWP.Core.Remote
Imports BrowserForWP.Localization
Imports Windows.Networking
Imports Windows.Networking.Connectivity
Imports Windows.Networking.Sockets
Imports Windows.Security.Cryptography
Imports Windows.Storage.Streams

Namespace Engine

    ''' <summary>
    ''' The listener and the HTML behind the token page. Start it, show the address
    ''' and the code, and a computer on the same network can paste a token in.
    ''' </summary>
    Public NotInheritable Class TokenPage

        ''' <summary>Raised when a submission was accepted. This is a socket thread.</summary>
        Public Event Saved As EventHandler(Of TokenVerdict)

        ''' <summary>
        ''' Raised when the listener stopped by itself -- five wrong codes -- so the
        ''' screen can say why instead of showing an address that no longer answers.
        ''' </summary>
        Public Event Stopped As EventHandler

        Private _listener As StreamSocketListener
        Private _primaryUrl As String
        Private _secondaryUrl As String
        Private _failures As Integer

        Public Sub New()
            _code = NewCode()
            _primaryUrl = String.Empty
            _secondaryUrl = String.Empty
        End Sub

        Private ReadOnly _code As String

        ''' <summary>The four digits a person types on the computer.</summary>
        Public ReadOnly Property Code As String
            Get
                Return _code
            End Get
        End Property

        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _listener IsNot Nothing
            End Get
        End Property

        ''' <summary>The port that actually bound, or 0 when nothing is listening.</summary>
        Public ReadOnly Property Port As Integer
            Get
                Return _port
            End Get
        End Property

        Private _port As Integer

        ''' <summary>
        ''' The address to type into the computer, or the empty string when there is
        ''' nothing to type: not running, or running on a phone with no address a
        ''' computer could reach.
        ''' </summary>
        Public ReadOnly Property Address As String
            Get
                If Not IsRunning Then Return String.Empty
                Dim host As String = LocalAddress()
                If host.Length = 0 Then Return String.Empty
                Return "http://" & host & ":" & _port.ToString() & "/"
            End Get
        End Property

        ''' <summary>
        ''' Open the port and start answering. Returns a resource key: TokenInboxRunning
        ''' when the page is up, TokenInboxNoAddress when the phone has no address a
        ''' computer can reach, TokenInboxNoPort when every port it tried was taken.
        ''' </summary>
        Public Async Function StartAsync(primaryUrl As String, secondaryUrl As String) As Task(Of String)
            If _listener IsNot Nothing Then Return TokenRunning

            _primaryUrl = If(primaryUrl, String.Empty)
            _secondaryUrl = If(secondaryUrl, String.Empty)
            _failures = 0

            ' Asked BEFORE the port is opened, because a listener with no address to
            ' advertise is a listener nobody can reach: it would hold a port on a
            ' phone whose screen shows nothing to type.
            If LocalAddress().Length = 0 Then Return TokenNoAddress

            For offset As Integer = 0 To TokenInbox.PortsToTry - 1
                Dim candidate As Integer = TokenInbox.DefaultPort + offset
                Dim listener As StreamSocketListener = Nothing
                Try
                    ' INSIDE the guard, and that is a fix rather than a tidy-up:
                    ' activating a WinRT class is one more thing that can fail on a
                    ' phone (a class the platform will not hand out at all answers
                    ' with an exception rather than Nothing), and a constructor
                    ' written outside this Try would escape StartAsync entirely --
                    ' through the caller's own Try, which has no Catch -- and end the
                    ' process. A port this listener cannot have is the next port; a
                    ' listener that cannot exist is the same answer.
                    listener = New StreamSocketListener()
                    Await listener.BindServiceNameAsync(candidate.ToString())
                    ' And the subscription is inside the same guard: it is the other
                    ' half of "this listener is usable", and subscribing is one more
                    ' WinRT call that can answer with an exception. A port that bound
                    ' but cannot be listened on is a port this page cannot use, and
                    ' the next one is the answer.
                    AddHandler listener.ConnectionReceived, AddressOf OnConnectionReceived
                Catch
                    ' EADDRINUSE from another app, or an address the platform will not
                    ' let a listener have. The next port is the answer, and the address
                    ' shown is the one that actually bound -- never the one requested.
                    If listener IsNot Nothing Then listener.Dispose()
                    Continue For
                End Try

                _listener = listener
                _port = candidate
                Return TokenRunning
            Next

            _listener = Nothing
            _port = 0
            Return TokenNoPort
        End Function

        ''' <summary>
        ''' Close the listener. Safe to call when nothing is listening, and safe to
        ''' call twice: the button, the screen closing and the failure count all reach
        ''' here, and none of them knows about the others.
        '''
        ''' Named Close rather than Stop because Stop is a VB statement, and this
        ''' project's checker refuses declarations that shadow keywords.
        ''' </summary>
        Public Sub Close()
            Dim closing As StreamSocketListener = _listener
            _listener = Nothing
            _port = 0
            If closing Is Nothing Then Return
            Try
                RemoveHandler closing.ConnectionReceived, AddressOf OnConnectionReceived
                closing.Dispose()
            Catch
                ' A listener that is already gone cannot be unsubscribed from or
                ' disposed again, and nothing here can act on the difference: Close
                ' is called to reach the state this catch describes.
            End Try
        End Sub

        ''' <summary>
        ''' The one address of this phone's own that a computer on the network can
        ''' open, chosen by TokenInbox's rule from what the platform reports.
        ''' </summary>
        Private Shared Function LocalAddress() As String
            Dim candidates As New List(Of String)()
            Try
                For Each hostName As HostName In NetworkInformation.GetHostNames()
                    If hostName.Type <> HostNameType.Ipv4 Then Continue For
                    candidates.Add(hostName.CanonicalName)
                Next
            Catch
                ' A phone with no network has no names; an exception here would take
                ' the listener down with it, and "no address" is the honest answer.
                Return String.Empty
            End Try
            Return TokenInbox.ChooseAddress(candidates)
        End Function

        ''' <summary>
        ''' Four digits, from a cryptographic source. Two bytes are 65536 values, and
        ''' the modulo is the usual small bias -- which is irrelevant here: the code is
        ''' not a secret, it is a speed bump in front of a listener that closes after
        ''' five wrong answers.
        ''' </summary>
        Private Shared Function NewCode() As String
            Dim random As IBuffer = CryptographicBuffer.GenerateRandom(2)
            Dim bytes As Byte() = Nothing
            CryptographicBuffer.CopyToByteArray(random, bytes)
            Dim value As Integer = (CInt(bytes(0)) * 256 + CInt(bytes(1))) Mod 10000
            Return value.ToString("0000")
        End Function

        Private Async Sub OnConnectionReceived(sender As StreamSocketListener,
                                               args As StreamSocketListenerConnectionReceivedEventArgs)
            Dim socket As StreamSocket = args.Socket
            Try
                Dim request As String = Await ReadRequestAsync(socket)
                Dim response As String = Answer(request)
                Await WriteAsync(socket, response)
            Catch
                ' A browser that hung up mid-request is not an error worth a message:
                ' there is nobody left to read it.
            Finally
                Try
                    socket.Dispose()
                Catch
                    ' The socket is being closed, not used: a failure to close one is
                    ' not news, and the reason above (if any) is the one worth keeping.
                End Try
            End Try
        End Sub

        ''' <summary>
        ''' Read a browser's request: the head, and then as much of the body as the
        ''' head declared. There is no chunked-body support on purpose -- a browser
        ''' posting a form does not use it, and a listener that guesses at what it does
        ''' not implement is a listener that can be fed nonsense.
        ''' </summary>
        Private Shared Async Function ReadRequestAsync(socket As StreamSocket) As Task(Of String)
            Dim reader As New DataReader(socket.InputStream)
            Dim text As New StringBuilder()
            Try
                reader.InputStreamOptions = InputStreamOptions.Partial
                While text.Length <= TokenInbox.MaxBodyBytes
                    Dim loaded As UInteger = Await reader.LoadAsync(1024)
                    If loaded = 0 Then Exit While
                    text.Append(reader.ReadString(loaded))
                    If RequestIsComplete(text.ToString()) Then Exit While
                End While
            Finally
                reader.DetachStream()
            End Try
            Return text.ToString()
        End Function

        ''' <summary>Whether the head is finished and the declared body has arrived.</summary>
        Private Shared Function RequestIsComplete(request As String) As Boolean
            Dim headerEnd As Integer = request.IndexOf(BlankLine, StringComparison.Ordinal)
            If headerEnd < 0 Then Return False

            Dim declared As Integer = ContentLength(request)
            If declared <= 0 Then Return True
            ' In BYTES, not characters: Content-Length counts bytes, and a token that
            ' is pure ASCII would hide the difference until the day it is not.
            Dim submitted As String = request.Substring(headerEnd + BlankLine.Length)
            Return Encoding.UTF8.GetByteCount(submitted) >= declared
        End Function

        ''' <summary>The Content-Length header, or 0 when it is absent or nonsense.</summary>
        Private Shared Function ContentLength(request As String) As Integer
            For Each line As String In request.Split(New String() {CrLf}, StringSplitOptions.None)
                If line.Length = 0 Then Return 0
                Dim colonAt As Integer = line.IndexOf(":"c)
                If colonAt <= 0 Then Continue For
                Dim name As String = line.Substring(0, colonAt).Trim()
                If String.Compare(name, "Content-Length", StringComparison.OrdinalIgnoreCase) <> 0 Then
                    Continue For
                End If
                Dim value As Integer = 0
                If Integer.TryParse(line.Substring(colonAt + 1).Trim(), value) Then Return value
                Return 0
            Next
            Return 0
        End Function

        ''' <summary>
        ''' What to answer. Everything a browser sends that is not a GET or a POST of
        ''' the form is refused, and the refusal says which one it was -- a page that
        ''' answers 200 to anything is a page that cannot be told apart from a bug.
        ''' </summary>
        Private Function Answer(request As String) As String
            Dim lines As String() = request.Split(New String() {CrLf}, StringSplitOptions.None)
            If lines.Length = 0 OrElse lines(0).Length = 0 Then
                Return Response(400, Body("TokenPageBadRequest", String.Empty))
            End If

            Dim parts As String() = lines(0).Split(" "c)
            If parts.Length < 2 Then
                Return Response(400, Body("TokenPageBadRequest", String.Empty))
            End If
            Dim method As String = parts(0).ToUpperInvariant()
            Dim target As String = parts(1)

            If target <> "/" Then
                Return Response(404, Body("TokenPageNotFound", String.Empty))
            End If

            If method = "GET" Then
                Return Response(200, FormHtml())
            End If
            If method <> "POST" Then
                Return Response(405, Body("TokenPageBadRequest", String.Empty))
            End If

            Dim headerEnd As Integer = request.IndexOf(BlankLine, StringComparison.Ordinal)
            ' NOT named `body`: VB is case-insensitive, so a local called body shadows
            ' the Body method for the WHOLE of this method, and every Body(...) call
            ' above it becomes a reference to a variable declared later -- BC32000,
            ' followed by a BC30057 about Chars on every one of them. The compiler
            ' found this; a checker that reads scopes did not exist until this file
            ' asked for one.
            Dim submitted As String = If(headerEnd < 0, String.Empty, request.Substring(headerEnd + BlankLine.Length))
            Dim verdict As TokenVerdict = TokenInbox.Review(
                TokenInbox.ParseForm(submitted), Code, _primaryUrl, _secondaryUrl)

            If verdict.Ok Then
                ' Masked, and this is the only place the token is written at all: the
                ' reply says WHICH slot took it, and four characters of what it took.
                '
                ' An assignment rather than an If() expression inside the chain: the
                ' block counter in tools/check-vb.mjs reads a line that STARTS with If(
                ' as an If statement, and a file written to look like something other
                ' than what it is, to suit a checker, is the wrong direction. The
                ' checker stays as it is; this reads better anyway.
                Dim slotLabel As String = Localizer.Get("TokenPageSlotPrimary")
                If verdict.Slot = RemoteServers.Secondary Then
                    slotLabel = Localizer.Get("TokenPageSlotSecondary")
                End If
                Dim sentence As String = Localizer.Get("TokenPageSaved") & " " & slotLabel & " " &
                                         TokenInbox.Masked(verdict.Token)
                RaiseEvent Saved(Me, verdict)
                Return Response(200, Body("TokenPageSaved", sentence))
            End If

            ' The refusal, and the count that closes the listener. A wrong code is the
            ' only refusal that is somebody guessing; the others are a form filled in
            ' wrong, and counting those would close the page on a person who mistyped a
            ' token once.
            If verdict.ReasonKey = TokenInbox.ReasonCode Then
                _failures += 1
                If _failures >= TokenInbox.MaxCodeFailures Then
                    Close()
                    RaiseEvent Stopped(Me, EventArgs.Empty)
                    Return Response(403, Body("TokenPageRefused", Localizer.Get("TokenInboxStoppedFailures")))
                End If
            End If

            Return Response(403, Body("TokenPageRefused", Localizer.Get(verdict.ReasonKey)))
        End Function

        ''' <summary>The form, with the address the settings already hold filled in.</summary>
        Private Function FormHtml() As String
            Dim slot As StringBuilder = New StringBuilder()
            slot.Append("<select id=""" & TokenInbox.FieldSlot & """ name=""" & TokenInbox.FieldSlot & """>")
            slot.Append("<option value=""1"">").Append(EscapeHtml(Localizer.Get("TokenPageSlotPrimary"))).Append("</option>")
            slot.Append("<option value=""2"">").Append(EscapeHtml(Localizer.Get("TokenPageSlotSecondary"))).Append("</option>")
            slot.Append("</select>")

            ' Named `content` and not `body`, for the same reason Answer's posted text is
            ' not: this class has a Body method, and a local with that name hides it.
            Dim content As New StringBuilder()
            content.Append("<h1>").Append(EscapeHtml(Localizer.Get("TokenPageTitle"))).Append("</h1>")
            content.Append("<p>").Append(EscapeHtml(Localizer.Get("TokenPageIntro"))).Append("</p>")
            content.Append("<form method=""post"" action=""/"">")
            content.Append("<label for=""").Append(TokenInbox.FieldToken).Append(""">")
            content.Append(EscapeHtml(Localizer.Get("TokenPageTokenLabel"))).Append("</label>")
            content.Append("<input type=""text"" id=""").Append(TokenInbox.FieldToken)
            content.Append(""" name=""").Append(TokenInbox.FieldToken).Append(""" autocomplete=""off"" required>")
            content.Append("<label for=""").Append(TokenInbox.FieldSlot).Append(""">")
            content.Append(EscapeHtml(Localizer.Get("TokenPageSlotLabel"))).Append("</label>")
            content.Append(slot.ToString())
            content.Append("<label for=""").Append(TokenInbox.FieldAddress).Append(""">")
            content.Append(EscapeHtml(Localizer.Get("TokenPageAddressLabel"))).Append("</label>")
            content.Append("<input type=""text"" id=""").Append(TokenInbox.FieldAddress)
            content.Append(""" name=""").Append(TokenInbox.FieldAddress)
            content.Append(""" value=""").Append(EscapeHtml(_primaryUrl)).Append(""" inputmode=""url"">")
            content.Append("<label for=""").Append(TokenInbox.FieldCode).Append(""">")
            content.Append(EscapeHtml(Localizer.Get("TokenPageCodeLabel"))).Append("</label>")
            content.Append("<input type=""text"" id=""").Append(TokenInbox.FieldCode)
            content.Append(""" name=""").Append(TokenInbox.FieldCode)
            content.Append(""" inputmode=""numeric"" autocomplete=""off"" required>")
            content.Append("<button type=""submit"">")
            content.Append(EscapeHtml(Localizer.Get("TokenPageSubmit"))).Append("</button>")
            content.Append("</form>")
            Return Page(Localizer.Get("TokenPageTitle"), content.ToString())
        End Function

        ''' <summary>A one-sentence page: the result, or a refusal.</summary>
        Private Shared Function Body(headingKey As String, sentence As String) As String
            Dim text As String = If(sentence.Length = 0, Localizer.Get(headingKey), sentence)
            Return Page(headingKey, "<h1>" & EscapeHtml(Localizer.Get(headingKey)) & "</h1><p>" &
                        EscapeHtml(text) & "</p>")
        End Function

        Private Shared Function Page(title As String, content As String) As String
            Dim html As New StringBuilder()
            html.Append("<!doctype html><html><head><meta charset=""utf-8"">")
            html.Append("<meta name=""viewport"" content=""width=device-width, initial-scale=1"">")
            html.Append("<meta name=""robots"" content=""noindex"">")
            html.Append("<title>").Append(EscapeHtml(title)).Append("</title>")
            html.Append("<style>").Append(Style).Append("</style>")
            html.Append("</head><body><main>").Append(content).Append("</main></body></html>")
            Return html.ToString()
        End Function

        Private Shared Function Response(status As Integer, html As String) As String
            Dim payload As Byte() = Encoding.UTF8.GetBytes(html)
            Dim head As New StringBuilder()
            head.Append("HTTP/1.1 ").Append(status.ToString()).Append(" ").Append(StatusText(status)).Append(CrLf)
            head.Append("Content-Type: text/html; charset=utf-8").Append(CrLf)
            head.Append("Content-Length: ").Append(payload.Length.ToString()).Append(CrLf)
            ' A page that hands over a credential is the last page to want in a cache.
            head.Append("Cache-Control: no-store").Append(CrLf)
            head.Append("Referrer-Policy: no-referrer").Append(CrLf)
            head.Append("X-Content-Type-Options: nosniff").Append(CrLf)
            ' No script, and nothing external to fetch: the page is a form and a
            ' sentence, and it says so to the browser as well as to a reader.
            head.Append("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'").Append(CrLf)
            head.Append("Connection: close").Append(CrLf)
            head.Append(CrLf)
            Return head.ToString() & html
        End Function

        Private Shared Function StatusText(status As Integer) As String
            Select Case status
                Case 200
                    Return "OK"
                Case 400
                    Return "Bad Request"
                Case 403
                    Return "Forbidden"
                Case 404
                    Return "Not Found"
                Case 405
                    Return "Method Not Allowed"
                Case Else
                    Return "Error"
            End Select
        End Function

        Private Shared Async Function WriteAsync(socket As StreamSocket, response As String) As Task
            Dim writer As New DataWriter(socket.OutputStream)
            Try
                writer.WriteString(response)
                Await writer.StoreAsync()
                Await writer.FlushAsync()
            Finally
                writer.DetachStream()
            End Try
        End Function

        ''' <summary>
        ''' The form's text is typed by a person and the page is somebody else's
        ''' browser, so every value that is not a literal in this file is escaped.
        ''' </summary>
        Private Shared Function EscapeHtml(text As String) As String
            If String.IsNullOrEmpty(text) Then Return String.Empty
            Dim escaped As New StringBuilder(text.Length)
            For Each ch As Char In text
                Select Case ch
                    Case "&"c
                        escaped.Append("&amp;")
                    Case "<"c
                        escaped.Append("&lt;")
                    Case ">"c
                        escaped.Append("&gt;")
                    Case """"c
                        escaped.Append("&quot;")
                    Case "'"c
                        escaped.Append("&#39;")
                    Case Else
                        escaped.Append(ch)
                End Select
            Next
            Return escaped.ToString()
        End Function

        Private Const CrLf As String = vbCrLf
        Private Const BlankLine As String = vbCrLf & vbCrLf

        ' The status keys, as constants: the shell resolves them and this file is the
        ' only thing that decides which one it is.
        Public Const TokenRunning As String = "TokenInboxRunning"
        Public Const TokenNoAddress As String = "TokenInboxNoAddress"
        Public Const TokenNoPort As String = "TokenInboxNoPort"

        ''' <summary>
        ''' Dark and light, a form that fits a phone-shaped window, and no script: the
        ''' page is served to somebody's desktop browser and has nothing to run.
        ''' </summary>
        Private Const Style As String =
            ":root { color-scheme: light dark; }" &
            "body { font: 16px/1.5 system-ui, sans-serif; margin: 0; padding: 2rem 1rem; }" &
            "main { max-width: 34rem; margin: 0 auto; }" &
            "h1 { font-size: 1.4rem; margin-top: 0; }" &
            "label { display: block; margin: 1rem 0 .25rem; font-weight: 600; }" &
            "input[type=text], select { width: 100%; box-sizing: border-box; padding: .5rem; font: inherit; }" &
            "button { margin-top: 1.25rem; padding: .6rem 1rem; font: inherit; font-weight: 600; }"

    End Class

End Namespace
