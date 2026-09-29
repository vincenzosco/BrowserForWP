' BrowserForWP — the compatibility probe.
'
' Why this exists: on Windows Phone 8.1 the rendering engine has real limits, and
' the user deserves to know which one they hit. "This site does not work" is a
' dead end; "this site needs Intl and IE11 has no Intl" is actionable.
'
' The probe runs INSIDE the document, in the page's own engine, which is the only
' place the question can be answered truthfully. A desktop-side feature test
' would just measure the desktop.

Imports System.Collections.Generic
Imports System.Threading.Tasks

Namespace Diagnostics

    ''' <summary>Which modern web features the active document is missing.</summary>
    Public NotInheritable Class ProbeReport

        Public Property EngineName As String = String.Empty

        Public Property MissingFeatures As New List(Of String)()

        ''' <summary>
        ''' True only when the probe actually ran and returned an answer. An empty
        ''' feature list from a probe that never ran is not evidence of anything,
        ''' and conflating the two made the UI claim "no missing web features" for
        ''' a document it had not measured -- including on an engine with no
        ''' scripting host at all.
        ''' </summary>
        Public Property CouldRun As Boolean = False

        Public ReadOnly Property IsFullyCompatible As Boolean
            Get
                Return CouldRun AndAlso MissingFeatures.Count = 0
            End Get
        End Property
    End Class

    ''' <summary>Asks the engine which features are absent, from inside the page.</summary>
    Public NotInheritable Class CompatibilityProbe

        ''' <summary>
        ''' ES5-only probe expression. Each check is a feature test, never a
        ''' version sniff: version sniffing is how compatibility shims get wrong
        ''' answers when a browser is updated.
        '''
        ''' The async/await test uses eval in a try/catch because a syntax error in
        ''' a direct expression would fail the whole script on IE11 — which is
        ''' precisely the engine whose answer we need.
        ''' </summary>
        Private Const ProbeScript As String =
            "(function(){" &
            "var missing=[];" &
            "if(!window.fetch)missing.push('fetch');" &
            "if(!window.Promise)missing.push('Promise');" &
            "if(typeof Symbol==='undefined')missing.push('Symbol');" &
            "if(typeof Intl==='undefined')missing.push('Intl');" &
            "if(typeof WeakMap==='undefined')missing.push('WeakMap');" &
            "if(typeof Proxy==='undefined')missing.push('Proxy');" &
            "if(typeof Map==='undefined')missing.push('Map');" &
            "if(typeof Set==='undefined')missing.push('Set');" &
            "if(!Object.assign)missing.push('Object.assign');" &
            "if(!Array.from)missing.push('Array.from');" &
            "if(!window.WebSocket)missing.push('WebSocket');" &
            "try{var g=document.documentElement.style;" &
            "if(!('grid' in g)&&!('gridTemplateColumns' in g))missing.push('CSS grid');" &
            "}catch(e){}" &
            "try{eval('(async function(){})');}catch(e){missing.push('async/await');}" &
            "try{eval('(function(){let x=1;})');}catch(e){missing.push('let/const');}" &
            "try{eval('(function(){return ()=>1;})');}catch(e){missing.push('arrow functions');}" &
            "return JSON.stringify(missing);" &
            "})()"

        Public Async Function RunAsync(engine As Engine.IBrowserEngine) As Task(Of ProbeReport)
            Dim report As New ProbeReport()

            If engine Is Nothing Then Return report
            report.EngineName = engine.Capabilities.Name

            Dim raw As String = Nothing
            Try
                raw = Await engine.InvokeScriptAsync(ProbeScript).ConfigureAwait(False)
            Catch ex As Exception
                ' A document that cannot be scripted (still loading, cross-origin at
                ' the top level) yields no data rather than an exception in the UI.
                Return report
            End Try

            If String.IsNullOrEmpty(raw) Then Return report
            raw = raw.Trim()
            If raw.Length < 2 Then Return report

            ' From here the engine answered, so the verdict is meaningful even if
            ' the answer lists gaps.
            report.CouldRun = True

            ' The engine returns a JSON array literal; parse the quoted elements
            ' directly. A JSON parser dependency would be overkill for this shape.
            Dim inner = raw.Substring(1, raw.Length - 2)
            If inner.Length = 0 Then Return report

            For Each part In inner.Split(","c)
                Dim feature = part.Trim().Trim(""""c)
                If feature.Length > 0 Then report.MissingFeatures.Add(feature)
            Next

            Return report
        End Function
    End Class

End Namespace
