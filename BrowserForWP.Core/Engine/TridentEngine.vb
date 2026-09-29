' BrowserForWP — Windows Phone 8.1's only available engine.
'
' This class is the honest answer to "why not Chromium or Firefox": the platform
' provides one rendering engine and no mechanism to substitute it. What it CAN
' do is report its limits precisely, so the user gets an explanation rather than
' a mystery.

Imports System.Threading.Tasks
Imports BrowserForWP.Core.Browser
Imports BrowserForWP.Core.Storage
Imports Windows.Storage
Imports Windows.UI.Xaml.Controls

Namespace Engine

    ''' <summary>
    ''' Trident (IE11) hosted in the system WebView.
    ''' </summary>
    Public NotInheritable Class TridentEngine
        Implements IBrowserEngine

        Private ReadOnly _view As WebView

        Public Sub New()
            _view = New WebView()
        End Sub

        ''' <summary>The WebView itself, for the view layer to host.</summary>
        Public ReadOnly Property View As WebView
            Get
                Return _view
            End Get
        End Property

        ''' <summary>
        ''' Reported as measured on Windows Phone 8.1, not as desired:
        '''   SupportsTls13          - Schannel on WP8.1 tops out at TLS 1.2, and the
        '''                            WebView goes through Schannel. The app's own
        '''                            transport (BrowserForWP.Net) does speak TLS 1.3,
        '''                            but this engine's traffic never uses it.
        '''   SupportsModernJavaScript - IE11 has no ES6+, no modules, no async/await.
        '''   SupportsFetch          - IE11 has no fetch.
        ''' </summary>
        Public ReadOnly Property Capabilities As EngineCapabilities Implements IBrowserEngine.Capabilities
            Get
                Return New EngineCapabilities With {
                    .Name = "System WebView",
                    .RenderingEngine = "Trident (IE11)",
                    .SupportsTls13 = False,
                    .SupportsScripting = True,
                    .SupportsModernJavaScript = False,
                    .SupportsWebSocket = True,
                    .SupportsFetch = False
                }
            End Get
        End Property

        Public ReadOnly Property Source As Object Implements IBrowserEngine.Source
            Get
                Return _view
            End Get
        End Property

        Public Sub Navigate(url As String) Implements IBrowserEngine.Navigate
            If String.IsNullOrEmpty(url) Then Return
            Dim uri As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, uri) Then Return
            _view.Navigate(uri)
        End Sub

        Public Sub GoBack() Implements IBrowserEngine.GoBack
            If _view.CanGoBack Then _view.GoBack()
        End Sub

        Public Sub GoForward() Implements IBrowserEngine.GoForward
            If _view.CanGoForward Then _view.GoForward()
        End Sub

        Public Sub Reload() Implements IBrowserEngine.Reload
            _view.Refresh()
        End Sub

        Public Sub [Stop]() Implements IBrowserEngine.Stop
            ' WP8.1's WebView exposes no cancellation primitive. Stopping is
            ' expressed by no longer acting on the in-flight navigation, which the
            ' view layer handles by checking the navigation token before applying
            ' progress updates. Recorded here so the behaviour is not mistaken for
            ' an oversight.
        End Sub

        Public Function InvokeScriptAsync(script As String) As Task(Of String) Implements IBrowserEngine.InvokeScriptAsync
            Return _view.InvokeScriptAsync("eval", New String() {script}).AsTask()
        End Function

        ''' <summary>
        ''' Load the packaged compat.js and eval it in the current document.
        ''' Returns True when the timing-hook marker reads back; False when there
        ''' is no document yet or scripting is unavailable. Never throws.
        ''' </summary>
        Public Async Function InjectPolyfillAsync() As Task(Of Boolean)
            Try
                Dim polyUri As New Uri("ms-appx:///Polyfill/compat.js")
                Dim polyFile As StorageFile = Await StorageFile.GetFileFromApplicationUriAsync(polyUri)
                Dim polyText As String = Await FileIO.ReadTextAsync(polyFile)
                If String.IsNullOrEmpty(polyText) Then
                    Return False
                End If
                Await InvokeScriptAsync(polyText)
                Dim markerText As String = Await InvokeScriptAsync("window.__browserForWPCompat?1:0")
                If String.IsNullOrEmpty(markerText) Then
                    Return False
                End If
                Return markerText.Trim() = "1"
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Find text in the page via window.find. Never throws.</summary>
        Public Async Function FindInPageAsync(searchTerm As String) As Task(Of Boolean)
            Try
                If String.IsNullOrEmpty(searchTerm) Then
                    Return False
                End If
                Dim foundText As String = Await InvokeScriptAsync("window.find(""" & EscapeJsString(searchTerm) & """)")
                If String.IsNullOrEmpty(foundText) Then
                    Return False
                End If
                Return foundText.Trim().ToLowerInvariant() = "true"
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Swap the page for its article text. Reload exits. Never throws.</summary>
        Public Async Function EnterReadingModeAsync() As Task(Of Boolean)
            Try
                Dim markerText As String = Await InvokeScriptAsync(ReadingMode.Script)
                If String.IsNullOrEmpty(markerText) Then
                    Return False
                End If
                Return markerText.Trim() = "1"
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Install or remove the night stylesheet. Never throws.</summary>
        Public Async Function SetNightModeAsync(enabled As Boolean) As Task(Of Boolean)
            Try
                Await InvokeScriptAsync(NightMode.BuildScript(enabled))
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Article title + text, capped in-script. Never throws.</summary>
        Public Async Function ExtractArticleTextAsync() As Task(Of String)
            Try
                Dim rawText As String = Await InvokeScriptAsync(
                    "(function(){try{" &
                    "var t=document.title||'';" &
                    "var b=document.body?document.body.innerText:'';" &
                    "if(b&&b.length>16000){b=b.substring(0,16000);}" &
                    "return t+'\n'+b;" &
                    "}catch(e){return '';}})()")
                Return If(rawText, String.Empty)
            Catch ex As Exception
                Return String.Empty
            End Try
        End Function

        ''' <summary>Body font size as a page-default percentage. Never throws.</summary>
        Public Async Function SetTextSizeAsync(pct As Integer) As Task(Of Boolean)
            Try
                Dim clamped As Integer = SiteSettings.ClampTextSize(pct)
                Dim markerText As String = Await InvokeScriptAsync(
                    "(function(){try{document.body.style.fontSize='" & clamped & "%';return '1';}catch(e){return '0';}})()")
                If String.IsNullOrEmpty(markerText) Then
                    Return False
                End If
                Return markerText.Trim() = "1"
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Show or hide the page's images. Never throws.</summary>
        Public Async Function SetImagesEnabledAsync(enabled As Boolean) As Task(Of Boolean)
            Try
                Dim displayValue As String = If(enabled, "", "none")
                Await InvokeScriptAsync(
                    "(function(){try{" &
                    "var imgs=document.images;" &
                    "for(var i=0;i<imgs.length;i++){imgs[i].style.display='" & displayValue & "';}" &
                    "return '1';}catch(e){return '0';}})()")
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        Private Shared Function EscapeJsString(rawText As String) As String
            If rawText Is Nothing Then
                Return String.Empty
            End If
            Dim cleanText As String = rawText.Replace(vbCr, " ").Replace(vbLf, " ")
            cleanText = cleanText.Replace("\", "\\")
            cleanText = cleanText.Replace("""", "\""")
            Return cleanText
        End Function
    End Class

End Namespace
