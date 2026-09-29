' BrowserForWP — the engine's own default stylesheet.
'
' Without this, every page renders as undifferentiated inline text: block
' elements would not stack and headings would not differ from paragraphs. It is a
' constant rather than a resource because it is engine input, not user-facing
' copy -- and there is nothing to localize in `display: block`.

Namespace Engine.Native

    ''' <summary>The default presentational rules of the native engine.</summary>
    Public NotInheritable Class UserAgentStylesheet

        Private Sub New()
        End Sub

        Public Const Css As String =
            "html,body,div,p,article,section,header,footer,nav,main,aside,figure,blockquote,pre,ul,ol,li,h1,h2,h3,h4,h5,h6{display:block}" &
            "body{margin:8px;font-size:16px;font-family:'Segoe UI';line-height:1.4;color:#000000}" &
            "h1{font-size:2em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h2{font-size:1.5em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h3{font-size:1.25em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h4{font-size:1em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h5{font-size:1em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h6{font-size:1em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "p{margin-top:1em;margin-bottom:1em}" &
            "a{color:#0066cc;text-decoration:underline}" &
            "ul{margin-top:1em;margin-bottom:1em;padding-left:2em}" &
            "ol{margin-top:1em;margin-bottom:1em;padding-left:2em}" &
            "li{display:block;list-style-type:disc}" &
            "blockquote{margin-top:1em;margin-bottom:1em;margin-left:2em;margin-right:2em}" &
            "pre{font-family:Consolas;margin-top:1em;margin-bottom:1em}" &
            "em{font-style:italic}" &
            "strong{font-weight:700}" &
            "b{font-weight:700}" &
            "img{display:block}" &
            "script,style,head,title,meta{display:none}"

    End Class

End Namespace
