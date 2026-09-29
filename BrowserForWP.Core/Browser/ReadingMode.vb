' BrowserForWP — reading-mode extraction script (pure string, no WinRT).
'
' Runs inside the document via TridentEngine.EnterReadingModeAsync. ES5 only,
' same discipline as compat.js. Exiting is a Reload: the original DOM is gone
' by design, and re-fetching is cheaper than caching whole pages in RAM.

Namespace Browser

    ''' <summary>Article extractor for reading mode.</summary>
    Public NotInheritable Class ReadingMode

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Finds the article node (article element, else the div/section/main
        ''' with the most paragraph text), strips scripts/styles/nav, and swaps
        ''' the body for a narrow serif container. Reports 1/0 via the marker.
        ''' </summary>
        Public Const Script As String =
            "(function(){" &
            "try{" &
            "var best=null,bestLen=0;" &
            "var scoped=document.querySelector('article');" &
            "if(scoped&&scoped.innerText&&scoped.innerText.length>200){best=scoped;}" &
            "if(!best){" &
            "var nodes=document.querySelectorAll('div,section,main');" &
            "for(var i=0;i<nodes.length;i++){" &
            "var ps=nodes[i].querySelectorAll('p'),len=0;" &
            "for(var j=0;j<ps.length;j++){len+=(ps[j].innerText||'').length;}" &
            "if(len>bestLen){bestLen=len;best=nodes[i];}" &
            "}" &
            "}" &
            "if(!best||bestLen<200){document.__bfwpReading=0;return '0';}" &
            "var clone=best.cloneNode(true);" &
            "var kill=clone.querySelectorAll('script,style,nav,header,footer,iframe,form');" &
            "for(var k=kill.length-1;k>=0;k--){kill[k].parentNode.removeChild(kill[k]);}" &
            "var head=document.querySelector('h1');" &
            "var title=head?head.innerText:document.title;" &
            "document.body.innerHTML='<div style=""max-width:700px;margin:0 auto;padding:16px;font-family:Georgia,serif;font-size:18px;line-height:1.6;color:#222""><h1>'+title.replace(/</g,""&lt;"")+'</h1></div>';" &
            "document.body.firstChild.appendChild(clone);" &
            "document.__bfwpReading=1;return '1';" &
            "}catch(e){try{document.__bfwpReading=0;}catch(e2){}return '0';}" &
            "})()"
    End Class

End Namespace
