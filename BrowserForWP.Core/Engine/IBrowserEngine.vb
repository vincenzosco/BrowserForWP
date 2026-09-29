' BrowserForWP — the engine seam.
'
' Windows Phone 8.1 gives an app no way to replace Trident. Rather than scatter
' that fact through the UI as special cases, the engine is an interface and its
' limits are data. A platform with a real modern engine implements this
' interface and nothing above it changes.

Imports System.Threading.Tasks

Namespace Engine

    ''' <summary>
    ''' What a rendering engine can actually do, reported truthfully by that engine.
    '''
    ''' These flags are not decoration: the UI reads them to warn the user
    ''' accurately. An engine that overstates its capabilities turns into a lie the
    ''' UI repeats.
    ''' </summary>
    Public NotInheritable Class EngineCapabilities

        Public Property Name As String
        Public Property RenderingEngine As String
        Public Property SupportsTls13 As Boolean

        ''' <summary>
        ''' True when the engine can execute script at all. False for an engine
        ''' that renders markup and nothing else.
        ''' </summary>
        Public Property SupportsScripting As Boolean

        Public Property SupportsModernJavaScript As Boolean
        Public Property SupportsWebSocket As Boolean
        Public Property SupportsFetch As Boolean

        ''' <summary>
        ''' True when the injected compatibility layer is load-bearing.
        '''
        ''' The scripting test is not redundant with the modern-JavaScript one: an
        ''' engine with no script host does not support modern JavaScript either,
        ''' and injecting compat.js into it is meaningless rather than merely
        ''' pointless. Without this, the native engine would claim it needed a
        ''' layer it cannot possibly accept.
        ''' </summary>
        Public ReadOnly Property NeedsPolyfillLayer As Boolean
            Get
                Return SupportsScripting AndAlso Not SupportsModernJavaScript
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return Name & " (" & RenderingEngine & ")"
        End Function
    End Class

    ''' <summary>
    ''' The engine seam.
    '''
    ''' Implementations: TridentEngine on Windows Phone 8.1. A WebView2Engine
    ''' (Chromium) or GeckoViewEngine (Firefox) would implement this on a platform
    ''' where those engines exist; the transport and compatibility layers in
    ''' BrowserForWP.Net and BrowserForWP.Polyfill come along unchanged.
    ''' </summary>
    Public Interface IBrowserEngine

        ReadOnly Property Capabilities As EngineCapabilities

        ''' <summary>
        ''' The engine's host control, for the view to place in its visual tree.
        ''' Typed as Object so this library need not reference XAML types.
        ''' </summary>
        ReadOnly Property Source As Object

        Sub Navigate(url As String)
        Sub GoBack()
        Sub GoForward()
        Sub Reload()

        ''' <summary>Best-effort on engines with no cancellation primitive.</summary>
        Sub [Stop]()

        ''' <summary>
        ''' Evaluate a script in the current document and return its value. Used by
        ''' the compatibility probe and by polyfill injection.
        ''' </summary>
        Function InvokeScriptAsync(script As String) As Task(Of String)
    End Interface

End Namespace
