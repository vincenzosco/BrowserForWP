# The keyboard and form-input path, end to end

**Goal.** A person taps a field on the remote page, their phone's own keyboard
comes up, what they type lands in that field, and rotating the phone keeps both
the keyboard and the mapping from a finger to the page correct.

**The request, verbatim.** *"Add the keyboard and form-input path end to end,
including a hidden text field that survives rotation."*

## What "end to end" means here

```
  tap on the picture ──▶ TAP ──▶ server: page.mouse.click(x, y)
        │                                    │
        └── focus a 1x1 transparent TextBox ─┘  (this is what raises the SIP)
                    │
   the SIP types ──▶ TEXT ──▶ server: keyboard.insertText(text)
   Enter/Esc/... ──▶ KEY  ──▶ server: keyboard.press(name)
                    │
   the page redraws ──▶ FRAME ──▶ the picture shows the text
```

**A rotation is part of the path, not a separate feature.** The viewport the
server draws at and the viewport the finger is mapped through are the same two
numbers, and both are wrong after a rotation unless the client says so. So a
rotation re-measures, re-maps, and sends `RESIZE`.

## What it does NOT mean, and why

**The page never tells the phone that a field took focus.** The protocol has no
message for it (23 types, and none of them is "focus moved"), so the phone cannot
know whether a tap landed on a text box, a button or nothing at all. The
consequence is honest and worth stating plainly: **the keyboard comes up on every
tap**, because the client would otherwise have to guess. Adding a `FOCUS` message
would be a change in the protocol, in `Docker-BrowserForWP` and in the vectors,
and it is deferred rather than smuggled in (deferred item 17).

**There is no local field to mirror.** The hidden `TextBox` is not a model of the
page's field: it is a keyboard with a text buffer that empties itself on every
keystroke. The phone therefore cannot show a caret, a selection or the page's own
text in the field, and it does not try.

## Two facts the path depends on, both measured rather than assumed

1. **The server dispatches messages strictly in order, one at a time.**
   `src/server.js` chains them (`queue = queue.then(() => session.onFrame(frame))`)
   and `Session.onFrame` awaits `_dispatch`. So the `TAP` that focuses a field is
   *finished* before the `TEXT` that follows it is handled — which is the entire
   reason typing after a tap is not a race.
2. **The client did not have that property.** `RemoteEngine.Send` is
   fire-and-forget, and `Tls13Client.WriteAsync` does not serialise its callers:
   two concurrent writes can interleave record encryption, and worse, two
   concurrent `SealedChannel.Seal` calls can read the same sequence number before
   either increments it — an AEAD nonce reuse, on a stream the server then
   rejects as garbage. Nothing had exercised it because nothing typed. **Task 1
   is this, and it is the part of the feature nobody can see.**

## Files

| File | Change |
| --- | --- |
| `BrowserForWP/Engine/RemoteChannel.vb` | A write gate over Seal AND write, including the HELLO. |
| `BrowserForWP/Rendering/RemoteScreen.vb` | Owns the viewport; `SetViewport`; keeps the one hidden field. |
| `BrowserForWP/Engine/RemoteEngine.vb` | `TypeKey`, `Window.Current.SizeChanged`, `RESIZE`. |
| `BrowserForWP/MainPage.xaml` | The keys bar, and its toolbar button. |
| `BrowserForWP/MainPage.xaml.vb` | The nine handlers and their labels. |
| `BrowserForWP/Strings/en-US/Resources.resw`, `it-IT/Resources.resw` | Ten labels. |
| `tools/proto/remote-input.mjs` | New referee: the source contracts this path needs. |
| `docs/MAINTAINING.md`, `docs/ARCHITECTURE.md`, `.agents/skills/browserforwp/SKILL.md` | The record. |

---

## Task 1 — The write gate

**Step 1: the referee, first (RED).** `tools/proto/remote-input.mjs` asserts that
`RemoteChannel.vb` gates its writes: a `SemaphoreSlim`, a `WaitAsync` inside
`SendAsync` and inside `WriteFrameAsync`, and a `Release` in a `Finally` in both.
It is red against the current file, and that is the point of writing it first.

**Step 2: `BrowserForWP/Engine/RemoteChannel.vb`.**

```vb
        ''' <summary>
        ''' One writer at a time, and it covers the SEAL as well as the write.
        '''
        ''' Both halves matter and neither is obvious. Sealing is what allocates the
        ''' sequence number, so two threads inside Seal at once can read the same
        ''' number and encrypt two different records with the same nonce -- which is
        ''' not a garbled screen but a broken channel, and one the server answers by
        ''' closing it. And Tls13Client.WriteAsync does not serialise its callers:
        ''' two concurrent writes interleave their records on one socket.
        '''
        ''' It went unnoticed until the keyboard arrived, because until then every
        ''' message came from one place at a time. Typing is many small messages from
        ''' two: the UI thread sends keystrokes while the read loop sends the frame
        ''' acknowledgement.
        ''' </summary>
        Private ReadOnly _writeGate As New System.Threading.SemaphoreSlim(1, 1)
```

and both write paths:

```vb
        Public Async Function SendAsync(messageType As Byte, payload As Byte()) As Task
            If _channel Is Nothing Then Throw New RemoteChannelException("not connected")
            If _closed Then Throw New RemoteChannelException("the connection is closed")
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
```

`WriteFrameAsync` (the unsealed HELLO) takes the same gate around its single
write.

- [ ] Referee red, then green.
- [ ] `node tools/check-vb.mjs` → 0 findings.

---

## Task 2 — The screen owns its viewport

**`RemoteScreen.vb`.** `ShowFrameAsync(tiles, full, width, height)` loses its last
two parameters: a frame does not carry the viewport in CSS pixels, and taking it
from the frame's tiles would mean inferring it. The engine knows it, and now says
it once:

```vb
        ''' <summary>
        ''' The viewport the server was asked for, in CSS pixels, and the ratio its
        ''' frames come back at. Called on connect and again after a rotation.
        ''' </summary>
        Public Sub SetViewport(width As Integer, height As Integer, devicePixelRatio As Integer)
            _viewportWidth = Math.Max(1, width)
            _viewportHeight = Math.Max(1, height)

            Dim ratio As Double = If(devicePixelRatio < 1, 1.0, CDbl(devicePixelRatio))
            If Math.Abs(ratio - _pixelRatio) > 0.001 Then
                _pixelRatio = ratio
                _transform.ScaleX = 1.0 / ratio
                _transform.ScaleY = 1.0 / ratio
            End If
        End Sub

        ''' <summary>True while the hidden field owns the soft keyboard.</summary>
        Public ReadOnly Property HasKeyboardFocus As Boolean
            Get
                Return _ime.FocusState <> FocusState.Unfocused
            End Get
        End Property

        ''' <summary>Raises the soft keyboard. Safe to call when it is already up.</summary>
        Public Sub FocusKeyboard()
            Try
                _ime.Focus(FocusState.Programmatic)
            Catch
                ' A phone that refuses focus still gets the tap that asked for it.
            End Try
        End Sub
```

`ShowFrameAsync` then uses the stored viewport for nothing and simply drops the
parameters.

- [ ] Build green, checker green.

---

## Task 3 — A rotation reaches the server

**`RemoteEngine.vb`.** `RefreshViewport` becomes `ApplyViewport` (it still runs on
every navigation, because settings can change the viewport in other ways and
because the bounds are not to be trusted before the page is arranged):

```vb
        Private Sub ApplyViewport()
            Dim bounds As Windows.Foundation.Rect = Windows.UI.Xaml.Window.Current.Bounds
            _viewportWidth = Math.Max(1, CInt(bounds.Width))
            _viewportHeight = Math.Max(1, CInt(bounds.Height))
            _devicePixelRatio = MeasurePixelRatio()
            _screen.SetViewport(_viewportWidth, _viewportHeight, _devicePixelRatio)
        End Sub

        ''' <summary>
        ''' The phone turned. Two numbers have to move together: the viewport the
        ''' server draws at, and the one this device maps a finger through. If only
        ''' the second moved, every tap would be offset by the difference; if only
        ''' the first, the picture would be letterboxed against a stale mapping.
        ''' </summary>
        Private Sub OnWindowSizeChanged(sender As Object, e As Windows.UI.Xaml.WindowSizeChangedEventArgs)
            If _channel Is Nothing OrElse Not _channel.IsOpen Then Return

            Dim hadKeyboard As Boolean = _screen.HasKeyboardFocus
            ApplyViewport()
            If hadKeyboard Then _screen.FocusKeyboard()

            Send(RemoteMessageType.Resize,
                 RemoteMessages.EncodeResize(_viewportWidth, _viewportHeight, _devicePixelRatio))
        End Sub
```

wired once, in the constructor: `AddHandler Windows.UI.Xaml.Window.Current.SizeChanged, AddressOf OnWindowSizeChanged`.
The hidden field itself is untouched by this: one instance, created in the
constructor, never rebuilt — which is the "survives rotation" half of the request,
and is asserted by the referee rather than asserted here.

- [ ] Build green.

---

## Task 4 — The keys a soft keyboard cannot send

**`MainPage.xaml`**, inside the content grid next to `FindBar`, plus a toolbar
button:

```xml
                <Border x:Name="RemoteKeysBar" Visibility="Collapsed"
                        VerticalAlignment="Bottom" Margin="8"
                        Background="{ThemeResource ApplicationPageBackgroundThemeBrush}"
                        CornerRadius="4" Padding="6">
                    <StackPanel Orientation="Horizontal">
                        <Button x:Name="KeyTabButton" Click="KeyTabButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyEnterButton" Click="KeyEnterButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyEscapeButton" Click="KeyEscapeButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyBackspaceButton" Click="KeyBackspaceButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyLeftButton" Click="KeyLeftButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyUpButton" Click="KeyUpButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyDownButton" Click="KeyDownButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyRightButton" Click="KeyRightButton_Click" Margin="0,0,4,0"/>
                        <Button x:Name="KeyBarCloseButton" Click="KeyBarCloseButton_Click"/>
                    </StackPanel>
                </Border>
```

**`RemoteEngine.vb`** — the one public door the bar needs:

```vb
        ''' <summary>
        ''' Presses a named key on the page. The names are Playwright's, and a name
        ''' the server does not recognise is a key that does nothing rather than a
        ''' failure, which is why the nine this shell offers are asserted against the
        ''' list in tools/proto/remote-input.mjs.
        ''' </summary>
        Public Sub TypeKey(keyName As String)
            If String.IsNullOrEmpty(keyName) Then Return
            Send(RemoteMessageType.Key, RemoteMessages.EncodeKey(keyName, 0, String.Empty))
        End Sub
```

**`MainPage.xaml.vb`** — one handler per button, all through one helper:

```vb
    Private Sub SendRemoteKey(keyName As String)
        Dim remote As BrowserForWP.Engine.RemoteEngine = TryCast(_engine, BrowserForWP.Engine.RemoteEngine)
        If remote Is Nothing Then Return
        remote.TypeKey(keyName)
    End Sub

    Private Sub KeyTabButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("Tab")
    End Sub
```

and the other seven the same way, with `Enter`, `Escape`, `Backspace`,
`ArrowLeft`, `ArrowUp`, `ArrowDown`, `ArrowRight`.

`KeyBarCloseButton_Click` hides the bar. `RemoteKeysButton_Click` toggles it.
`UseEngine` disables the button and hides the bar for the other engine, next to
the three buttons Law 5 already disables.

**Ten resource keys** in both languages: `RemoteKeys` (the toolbar button),
`KeyTab`, `KeyEnter`, `KeyEscape`, `KeyBackspace`, `KeyLeft`, `KeyUp`, `KeyDown`,
`KeyRight`, `KeyBarClose`.

**Not offered, deliberately: Shift+Tab and Ctrl+anything.** The protocol carries a
modifier byte and the server's `key` handler ignores it (`browser.js` reads
`{ key, text }`), so a button that sent a modifier would be a button that does
nothing. Recorded as deferred item 18 rather than shipped.

---

## Task 5 — The referee

`tools/proto/remote-input.mjs`, in the house style (each check names the defect it
would have caught):

1. `RemoteScreen.vb` constructs exactly ONE `TextBox` — a field rebuilt per
   navigation or per rotation would lose the person's half-typed word.
2. `PrepareKeyboardProxy` is called once, from the constructor.
3. `RemoteChannel.vb` gates its writes: the semaphore, and `WaitAsync`/`Release`
   in BOTH `SendAsync` and `WriteFrameAsync`.
4. `RemoteEngine.vb` subscribes to `Window.Current.SizeChanged`, and its handler
   both calls `_screen.SetViewport` and sends `RemoteMessageType.Resize`.
5. Every key name passed to `SendRemoteKey` in `MainPage.xaml.vb` is a Playwright
   key name (a list, and the four the protocol cannot express are named as
   absent).
6. Every `Localizer.Get("Key...")` in `MainPage.xaml.vb` has a `data name` in BOTH
   `.resw` files — a label with no key is an empty button, and `check-vb.mjs`
   group 6 only compares the two files with each other.
7. Every `Key*Button` named in the XAML exists in the code-behind (the existing
   handler check covers the handlers; this covers the names).

**Negative control.** Each of 1–6 is run against an edited copy in `--probe` mode
that plants the defect, and the check must fail. A green check over a rule nobody
has seen refuse is decoration, and this repository has paid for that twice.

---

## Task 6 — The record

- `docs/MAINTAINING.md`: the new referee in the tool table and the command list;
  deferred item 14 (rotation) is **closed** by Task 3, so it moves out of the
  list; three new deferred items (17 focus mirroring, 18 the modifier byte, 19
  the keyboard coming up on every tap); the verification table's row 7 keeps its
  blank result, with the keys bar named as what is still unrun.
- `docs/ARCHITECTURE.md`: Law 5 gains the sentence about which half of the input
  path is local (the keyboard) and which half is the server's (the field).
- `.agents/skills/browserforwp/SKILL.md`: a row for `remote-input.mjs`.
- Six configurations in the guest, then commit and push.

**Not verifiable here, and left blank:** anything that needs the SIP. The referee
can prove the field is created once and that the key names are real; it cannot
prove the platform raises a keyboard, that a keystroke arrives after it, or that
a rotation keeps it up.
