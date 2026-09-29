' BrowserForWP — night-mode stylesheet (pure strings, no WinRT).
'
' Applied by TridentEngine.SetNightModeAsync via a single style element, so
' toggling off removes exactly what toggling on added. Kept tiny on purpose:
' every rule here ships to every page when enabled.

Namespace Browser

    ''' <summary>Dark stylesheet builder for night mode.</summary>
    Public NotInheritable Class NightMode

        Private Sub New()
        End Sub

        Public Const StyleId As String = "bfwp-night"

        Public Const Css As String =
            "html,body{background:#111111 !important;color:#dddddd !important;}" &
            "a,a:link,a:visited{color:#7aa7ff !important;}" &
            "p,li,span,div,td{color:#dddddd !important;}" &
            "img,video{opacity:0.85 !important;}"

        ''' <summary>Eval script: install the style when on, remove it when off.</summary>
        Public Shared Function BuildScript(enabled As Boolean) As String
            If enabled Then
                Return "(function(){try{" &
                    "var old=document.getElementById('" & StyleId & "');" &
                    "if(old){old.parentNode.removeChild(old);}" &
                    "var st=document.createElement('style');" &
                    "st.id='" & StyleId & "';st.type='text/css';" &
                    "st.appendChild(document.createTextNode('" & Css & "'));" &
                    "document.getElementsByTagName('head')[0].appendChild(st);" &
                    "return '1';}catch(e){return '0';}})()"
            End If
            Return "(function(){try{" &
                "var old=document.getElementById('" & StyleId & "');" &
                "if(old){old.parentNode.removeChild(old);}" &
                "return '1';}catch(e){return '0';}})()"
        End Function
    End Class

End Namespace
