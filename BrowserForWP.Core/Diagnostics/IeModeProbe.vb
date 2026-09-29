' BrowserForWP — reports how the hosted engine is actually configured.
'
' This exists to settle a question with measurement instead of assertion: can
' Trident be "adapted" into behaving like a newer engine? The four levers such a
' plan would need are (1) force a document mode, (2) replace or upgrade the
' engine binary, (3) toggle IE11 feature flags, (4) reach MSHTML directly. The
' API surface says all four are unavailable on WP8.1; this probe is how a handset
' confirms that rather than taking our word for it.
'
' ES5 only, deliberately: it runs on the engine under test.

Imports System.Threading.Tasks

Namespace Diagnostics

    ''' <summary>What the engine reports about its own configuration.</summary>
    Public NotInheritable Class IeModeReport

        Public Property DocumentMode As Integer
        Public Property RawJson As String = String.Empty

    End Class

    ''' <summary>Asks the live document how it is configured. Never throws.</summary>
    Public NotInheritable Class IeModeProbe

        ''' <summary>
        ''' Reads documentMode and the X-UA-Compatible value the engine honoured,
        ''' then appends an IE=edge meta tag and reads documentMode again. If a
        ''' host-side injection could raise the mode, modeAfter would exceed
        ''' modeBefore. Appending after load is the strongest form of the test,
        ''' because the meta tag would have been honoured had it been in the head.
        ''' </summary>
        Private Const ProbeScript As String =
            "(function(){" &
            "var compat='(none)';" &
            "try{" &
            "var m=document.querySelector('meta[http-equiv=""X-UA-Compatible""]');" &
            "if(m){compat=m.getAttribute('content');}" &
            "}catch(e){}" &
            "var before=document.documentMode||0;" &
            "try{" &
            "var t=document.createElement('meta');" &
            "t.setAttribute('http-equiv','X-UA-Compatible');" &
            "t.setAttribute('content','IE=edge');" &
            "document.getElementsByTagName('head')[0].appendChild(t);" &
            "}catch(e){}" &
            "return JSON.stringify({" &
            "modeBefore:before," &
            "modeAfter:document.documentMode||0," &
            "compatMeta:compat," &
            "ua:navigator.userAgent" &
            "});" &
            "})()"

        ''' <summary>Probe a live document. Returns Nothing when it cannot answer.</summary>
        Public Shared Async Function RunAsync(engine As Engine.IBrowserEngine) As Task(Of IeModeReport)
            If engine Is Nothing Then Return Nothing

            Dim raw As String = Nothing
            Try
                raw = Await engine.InvokeScriptAsync(ProbeScript).ConfigureAwait(False)
            Catch ex As Exception
                Return Nothing
            End Try

            If String.IsNullOrEmpty(raw) Then Return Nothing
            raw = raw.Trim()
            If raw.Length < 2 OrElse raw(0) <> "{"c Then Return Nothing

            Dim reportResult As New IeModeReport()
            reportResult.RawJson = raw
            reportResult.DocumentMode = ExtractInt(raw, "modeBefore")
            Return reportResult
        End Function

        ''' <summary>
        ''' Pull an integer member out of the flat JSON the probe returns. A JSON
        ''' parser dependency for three fields would be overkill, and the probe
        ''' controls the exact shape.
        ''' </summary>
        Private Shared Function ExtractInt(json As String, memberName As String) As Integer
            Dim needle As String = """" & memberName & """:"
            Dim at As Integer = json.IndexOf(needle, StringComparison.Ordinal)
            If at < 0 Then Return 0
            Dim startPos As Integer = at + needle.Length
            Dim endPos As Integer = startPos
            While endPos < json.Length AndAlso Char.IsDigit(json(endPos))
                endPos += 1
            End While
            If endPos = startPos Then Return 0
            Dim parsed As Integer = 0
            If Not Integer.TryParse(json.Substring(startPos, endPos - startPos), parsed) Then Return 0
            Return parsed
        End Function
    End Class

End Namespace
