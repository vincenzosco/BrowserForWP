' BrowserForWP — lite-version redirects (pure, no WinRT dependency).
'
' A few high-traffic hosts still serve working light endpoints that Trident can
' render. Rules are conservative: known hosts only, path and query preserved,
' and already-light hosts never rewritten (no redirect loops).

Namespace Browser

    ''' <summary>Host-to-lite-URL rules.</summary>
    Public NotInheritable Class LiteRedirects

        Private Sub New()
        End Sub

        ''' <summary>Lite URL for a page URL, or Nothing to leave it alone.</summary>
        Public Shared Function RedirectUrl(pageUrl As String) As String
            If String.IsNullOrEmpty(pageUrl) Then
                Return Nothing
            End If
            Dim parsedUri As Uri = Nothing
            If Not Uri.TryCreate(pageUrl, UriKind.Absolute, parsedUri) Then
                Return Nothing
            End If
            Dim hostKey As String = parsedUri.Host.Trim().ToLowerInvariant()
            Dim pathAndQuery As String = parsedUri.PathAndQuery
            Dim hostLabels As String() = hostKey.Split("."c)
            Dim alreadyMobile As Boolean = False
            For Each hostLabel In hostLabels
                If hostLabel = "m" Then
                    alreadyMobile = True
                    Exit For
                End If
            Next
            If hostKey.EndsWith(".wikipedia.org") AndAlso Not alreadyMobile Then
                Return "https://" & hostKey.Insert(hostKey.IndexOf("."c), ".m") & pathAndQuery
            End If
            If hostKey = "facebook.com" OrElse hostKey = "www.facebook.com" OrElse hostKey = "m.facebook.com" Then
                Return "https://mbasic.facebook.com" & pathAndQuery
            End If
            If hostKey = "reddit.com" OrElse hostKey = "www.reddit.com" Then
                Return "https://old.reddit.com" & pathAndQuery
            End If
            Return Nothing
        End Function
    End Class

End Namespace
