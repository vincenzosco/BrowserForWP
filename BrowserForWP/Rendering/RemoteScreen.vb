' BrowserForWP — the remote engine's screen: the only control in this shell that is
' a picture of somebody else's window, and the only one that has to turn fingers
' into protocol messages.
'
' It is a Canvas of Images. Every frame the server sends is a list of rectangles,
' and each one is drawn at its own offset, so a partial frame costs nothing extra
' to display and a whole one is the same code path with one tile.
'
' WHAT IT DECODES. The tiles are JPEG, decoded by the platform. Nothing in
' BrowserForWP.Crypto is involved, and no XamlBoxRenderer exists to help: the
' renderer that drew boxes on this device was deleted with the native engine.
'
' THE SCALE, WHICH IS NOT ONE. The server was asked for a viewport of
' (width, height) CSS pixels at a device pixel ratio, and Playwright's frame then
' comes back width*dpr pixels wide. The canvas therefore carries a scale of
' 1/dpr, and the picture lands at the size the page actually has. The same factor
' in reverse turns a touch point on the glass into the CSS coordinate the server
' clicks at -- and the failure mode of getting it wrong is a browser that looks
' perfect and taps the wrong link, which is why the arithmetic is spelled out
' rather than folded into one number.
'
' WHY THERE IS A HIDDEN TextBox. A Canvas has no keyboard. A 1x1 transparent
' TextBox is what makes the platform raise the soft keyboard, and what the person
' types is forwarded as TEXT.
'
' WHEN IT RISES IS THE SERVER'S ANSWER, NOT OURS. A Canvas cannot tell a text
' field from a link, and the first version of this file focused the field on EVERY
' tap -- so the keyboard covered half the page somebody was trying to read, on
' every tap, forever. SetPageFocus is where the FOCUS message (0x27) lands: the
' server looks at document.activeElement and says whether it takes text. True
' focuses the field; False DISABLES it, because WinRT has no Unfocus() and
' disabling the focused control is the one state change that both drops the focus
' and closes the keyboard.
'
' This is still the part of the file that has never run on a handset: see the
' verification table in docs/MAINTAINING.md, where its rows are left blank rather
' than marked as passing.

Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices.WindowsRuntime
Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine.Remote
Imports Windows.UI.Xaml
Imports Windows.UI.Xaml.Controls
Imports Windows.UI.Xaml.Input
Imports Windows.UI.Xaml.Media
Imports Windows.UI.Xaml.Media.Imaging

Namespace Rendering

    Public NotInheritable Class RemoteScreen

        ''' <summary>The largest a scroll delta may be before the wire truncates.</summary>
        Private Const MaxScrollDelta As Integer = 32767

        Private ReadOnly _canvas As New Canvas()
        Private ReadOnly _images As New Dictionary(Of Integer, Image)()
        Private ReadOnly _ime As New TextBox()
        Private ReadOnly _transform As New CompositeTransform()

        ' Integer, not Byte: a tap on a 1080p phone is at y = 1900, and the wire
        ' carries u16 for exactly that reason. Narrowing here would put every tap
        ' past the first 255 pixels in the wrong place, and the screen would still
        ' look right.
        Private ReadOnly _raiseInput As Action(Of Integer, Integer)

        ''' <summary>Page coordinates, then a CSS-pixel scroll delta.</summary>
        Private ReadOnly _raiseScroll As Action(Of Integer, Integer, Integer, Integer)

        ''' <summary>A Playwright key name, its modifiers, and text to insert instead.</summary>
        Private ReadOnly _raiseKey As Action(Of String, Integer, String)

        ' Not ReadOnly: a rotation changes it, and the scale has to follow.
        Private _pixelRatio As Double
        Private _viewportWidth As Integer
        Private _viewportHeight As Integer

        ' What the server last said about the page's focus. False until it says
        ' otherwise, which is the safe direction: a keyboard that stays down is a
        ' nuisance, one that rises over a page with no field in it is a bug.
        Private _pageFocusEditable As Boolean

        ' Set while the box is being cleared on purpose, so that clearing it does
        ' not look like the person deleting a character.
        Private _clearing As Boolean

        Public Sub New(raiseInput As Action(Of Integer, Integer),
                       raiseScroll As Action(Of Integer, Integer, Integer, Integer),
                       raiseKey As Action(Of String, Integer, String),
                       devicePixelRatio As Integer)
            _raiseInput = raiseInput
            _raiseScroll = raiseScroll
            _raiseKey = raiseKey
            SetScale(devicePixelRatio)

            _canvas.RenderTransform = _transform
            _canvas.Background = New SolidColorBrush(Windows.UI.Colors.White)
            _canvas.IsTapEnabled = True

            ' Both axes, and nothing else: System would hand the movement to the
            ' OS, which has no page to scroll.
            _canvas.ManipulationMode = ManipulationModes.TranslateX Or ManipulationModes.TranslateY
            AddHandler _canvas.Tapped, AddressOf OnTapped
            AddHandler _canvas.ManipulationCompleted, AddressOf OnManipulationCompleted

            PrepareKeyboardProxy()
        End Sub

        ''' <summary>The control the shell puts in its visual tree.</summary>
        Public ReadOnly Property Source As Object
            Get
                Return _canvas
            End Get
        End Property

        ''' <summary>
        ''' The viewport the server was asked for, in CSS pixels, and the ratio its
        ''' frames come back at. Called on connect and again after a rotation.
        '''
        ''' A FRAME DOES NOT CARRY THIS. The tiles are rectangles in FRAME pixels
        ''' (CSS x ratio) and nothing in the message says how wide the viewport was,
        ''' so a screen that inferred it from a tile would be inferring from the
        ''' picture. The engine knows it, and this is where it is said -- once.
        ''' </summary>
        Public Sub SetViewport(width As Integer, height As Integer, devicePixelRatio As Integer)
            _viewportWidth = Math.Max(1, width)
            _viewportHeight = Math.Max(1, height)
            SetScale(devicePixelRatio)
        End Sub

        ''' <summary>
        ''' True when the page's focused element takes text, and therefore when the
        ''' soft keyboard belongs on screen.
        '''
        ''' IT IS NOT "the field has focus". After a rotation the tree is
        ''' re-arranged and the field reports FocusState.Unfocused even though the
        ''' person was typing a moment ago -- so asking the control would close a
        ''' keyboard nobody dismissed. The page's answer is the durable fact; the
        ''' field's focus is one of its consequences.
        ''' </summary>
        Public ReadOnly Property WantsKeyboard As Boolean
            Get
                Return _pageFocusEditable
            End Get
        End Property

        ''' <summary>
        ''' What the server said about where the page's focus is.
        '''
        ''' Called for every FOCUS message, on the change the server reports rather
        ''' than on a schedule. True re-enables the field before focusing it, because
        ''' a previous message may have disabled it; False disables it, which is how
        ''' a focused TextBox in WinRT is made to let go.
        '''
        ''' A desktop page with nothing editable in it therefore leaves the keyboard
        ''' down, and the keys bar still works: Tab and Escape are KEY messages, not
        ''' the soft keyboard.
        ''' </summary>
        Public Sub SetPageFocus(editable As Boolean)
            _pageFocusEditable = editable
            Try
                If editable Then
                    ' Enabled first: focusing a disabled control is a no-op, and the
                    ' message that asks for focus is often the one after the message
                    ' that took it away.
                    _ime.IsEnabled = True
                    _ime.Focus(FocusState.Programmatic)
                    Return
                End If
                _ime.IsEnabled = False
            Catch
                ' A phone that refuses focus is a phone whose keyboard is not shown,
                ' which is the same outcome as a refusal to hide one -- and the tap
                ' that asked for it is still forwarded.
            End Try
        End Sub

        ''' <summary>
        ''' Raises the soft keyboard, or leaves it up. Called after a rotation,
        ''' because a re-arranged tree can drop focus and a keyboard that closes
        ''' itself when the phone turns is a keyboard the person did not dismiss.
        '''
        ''' A no-op unless the page's focused element takes text: the keys bar,
        ''' scrolling and every tap must keep working on a page with nothing to type
        ''' into, and this is the one place that could put a keyboard back over one.
        ''' </summary>
        Public Sub FocusKeyboard()
            If Not _pageFocusEditable Then Return
            Try
                _ime.IsEnabled = True
                _ime.Focus(FocusState.Programmatic)
            Catch
                ' A phone that refuses focus still gets the tap that asked for it.
            End Try
        End Sub

        ''' <summary>
        ''' The scale that turns frame pixels into layout pixels. A ratio of 2 means
        ''' the server draws twice as many pixels as this screen has, so the canvas
        ''' is scaled by a half and the picture lands at the size the page has.
        ''' </summary>
        Private Sub SetScale(devicePixelRatio As Integer)
            Dim ratio As Double = If(devicePixelRatio < 1, 1.0, CDbl(devicePixelRatio))
            _pixelRatio = ratio
            _transform.ScaleX = 1.0 / ratio
            _transform.ScaleY = 1.0 / ratio
        End Sub

        ''' <summary>
        ''' Draws the tiles. A rectangle that has been seen before gets its Image
        ''' reused, which is what keeps a twenty-frame-per-second page from
        ''' allocating twenty elements a second. A frame flagged as complete also
        ''' drops the images of tiles it does not mention, because a full frame is a
        ''' statement about the whole picture and not about its own rectangle list.
        ''' </summary>
        Public Async Function ShowFrameAsync(tiles As IList(Of RemoteFrameTile), full As Boolean) As Task
            If tiles Is Nothing Then Return

            Dim live As New HashSet(Of Integer)()
            For index As Integer = 0 To tiles.Count - 1
                Dim tile As RemoteFrameTile = tiles(index)
                Dim key As Integer = tile.X * 65536 + tile.Y
                live.Add(key)

                Dim target As Image = Nothing
                If Not _images.TryGetValue(key, target) Then
                    target = New Image()
                    target.Stretch = Stretch.Fill
                    _images(key) = target
                    _canvas.Children.Add(target)
                End If

                Canvas.SetLeft(target, tile.X)
                Canvas.SetTop(target, tile.Y)
                target.Width = tile.Width
                target.Height = tile.Height
                target.Source = Await DecodeAsync(tile.Data)
            Next

            If Not full Then Return
            Dim stale As New List(Of Integer)()
            For Each known As Integer In _images.Keys
                If Not live.Contains(known) Then stale.Add(known)
            Next
            For Each known As Integer In stale
                Dim gone As Image = _images(known)
                _images.Remove(known)
                _canvas.Children.Remove(gone)
            Next
        End Function

        ''' <summary>
        ''' One JPEG into one ImageSource.
        ''' </summary>
        ''' <remarks>
        ''' BitmapImage.SetSourceAsync, and not the decoder this started as. The
        ''' first version read BitmapDecoder.GetSoftwareBitmapAsync, which hands
        ''' back a SoftwareBitmap -- and a SoftwareBitmap is not a BitmapSource, so
        ''' assigning it to Image.Source needs a SoftwareBitmapSource round trip
        ''' and one more asynchronous step. SetSourceAsync returns the BitmapSource
        ''' itself, and the platform still does the decoding.
        ''' </remarks>
        Private Shared Async Function DecodeAsync(jpeg As Byte()) As Task(Of BitmapSource)
            If jpeg Is Nothing OrElse jpeg.Length = 0 Then Return Nothing
            Using stream As New Windows.Storage.Streams.InMemoryRandomAccessStream()
                Await stream.WriteAsync(jpeg.AsBuffer())
                stream.Seek(0)
                Dim picture As New BitmapImage()
                Await picture.SetSourceAsync(stream)
                Return picture
            End Using
        End Function

        Private Sub OnTapped(sender As Object, e As TappedRoutedEventArgs)
            If _raiseInput Is Nothing Then Return

            ' A tap is a tap and NOT a request to type. Focusing the hidden field
            ' here is what raised the keyboard on links, buttons and empty space --
            ' and the only thing that can tell those apart is the page, which is on
            ' the server: SetPageFocus is where its answer arrives.
            Dim point As Windows.Foundation.Point = e.GetPosition(_canvas)
            Dim mapped As Windows.Foundation.Point = ToPage(point)
            _raiseInput(Clamp(mapped.X), Clamp(mapped.Y))
        End Sub

        Private Sub OnManipulationCompleted(sender As Object, e As ManipulationCompletedRoutedEventArgs)
            If _raiseScroll Is Nothing Then Return

            ' Cumulative rather than per-delta: one message per gesture instead of
            ' one per frame of the gesture, and the sign is the part that is easy to
            ' get backwards. Dragging the finger UP moves the page's content up,
            ' which is a scroll DOWN, and Playwright's wheel takes positive deltaY
            ' as scrolling down. Hence the minus.
            Dim moved As Windows.Foundation.Point = e.Cumulative.Translation
            Dim deltaX As Integer = CInt(-moved.X)
            Dim deltaY As Integer = CInt(-moved.Y)
            If deltaX = 0 AndAlso deltaY = 0 Then Return

            Dim mapped As Windows.Foundation.Point = ToPage(e.Position)
            _raiseScroll(Clamp(mapped.X), Clamp(mapped.Y),
                         ClampDelta(deltaX), ClampDelta(deltaY))
        End Sub

        Private Sub OnKeyboardTextChanged(sender As Object, e As TextChangedEventArgs)
            If _clearing OrElse _raiseKey Is Nothing Then Return
            Dim typed As String = _ime.Text
            If String.IsNullOrEmpty(typed) Then Return

            ' An empty key name with text in it means "insert this", which is the
            ' shape the server's own decoder expects: a non-empty text INSERTS
            ' rather than presses.
            _raiseKey(String.Empty, 0, typed)

            _clearing = True
            Try
                _ime.Text = String.Empty
            Finally
                _clearing = False
            End Try
        End Sub

        Private Sub OnKeyboardKeyDown(sender As Object, e As KeyRoutedEventArgs)
            If _raiseKey Is Nothing Then Return
            Select Case e.Key
                Case Windows.System.VirtualKey.Enter
                    _raiseKey("Enter", 0, String.Empty)
                Case Windows.System.VirtualKey.Back
                    _raiseKey("Backspace", 0, String.Empty)
                Case Else
                    Return
            End Select
            e.Handled = True
        End Sub

        ''' <summary>
        ''' The 1x1 transparent field that owns the soft keyboard. Transparent and
        ''' one pixel across on purpose: it must be focusable, and it must never be
        ''' visible or get in the way of the picture.
        ''' </summary>
        Private Sub PrepareKeyboardProxy()
            _ime.Opacity = 0
            _ime.Width = 1
            _ime.Height = 1
            _ime.IsSpellCheckEnabled = False
            _ime.IsTextPredictionEnabled = False
            AddHandler _ime.TextChanged, AddressOf OnKeyboardTextChanged
            AddHandler _ime.KeyDown, AddressOf OnKeyboardKeyDown
            Canvas.SetLeft(_ime, 0)
            Canvas.SetTop(_ime, 0)
            _canvas.Children.Add(_ime)
        End Sub

        ''' <summary>
        ''' A point on the glass as a point on the page. The divisor is guarded
        ''' because a canvas that has not been arranged yet reports a size of zero,
        ''' and dividing by it would put every tap at infinity.
        ''' </summary>
        Private Function ToPage(point As Windows.Foundation.Point) As Windows.Foundation.Point
            Dim onScreenX As Double = Math.Max(1.0, _canvas.ActualWidth)
            Dim onScreenY As Double = Math.Max(1.0, _canvas.ActualHeight)
            Dim pageX As Double = point.X * _viewportWidth / onScreenX
            Dim pageY As Double = point.Y * _viewportHeight / onScreenY
            Return New Windows.Foundation.Point(pageX, pageY)
        End Function

        ''' <summary>The wire carries u16 for a touch coordinate.</summary>
        Private Shared Function Clamp(value As Double) As Integer
            If value < 0 Then Return 0
            If value > 65535 Then Return 65535
            Return CInt(value)
        End Function

        ''' <summary>
        ''' The wire carries i16 for a scroll delta, and this CLAMPS rather than
        ''' truncates. Wrapping a fast flick in the i16 range turns it into a scroll
        ''' the other way, which reads as a page that fights the finger.
        ''' </summary>
        Private Shared Function ClampDelta(value As Integer) As Integer
            If value > MaxScrollDelta Then Return MaxScrollDelta
            If value < -MaxScrollDelta Then Return -MaxScrollDelta
            Return value
        End Function

    End Class

End Namespace
