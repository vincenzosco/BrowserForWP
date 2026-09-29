' BrowserForWP — tiny on-device tracker host blocklist (pure, no WinRT).
'
' LIMIT, STATED HONESTLY: WP8.1 WebView exposes no subresource filter API, so
' this covers top-level navigations only (typed URLs, tapped links, redirects).
' Embedded third-party pixels still load. The list is intentionally short: the
' worst offender networks, matched by exact host or subdomain suffix.

Namespace Browser

    ''' <summary>Top-level tracker navigation blocklist.</summary>
    Public NotInheritable Class TrackerBlocklist

        Private Sub New()
        End Sub

        Private Shared ReadOnly BlockedSuffixes As String() = {
            "doubleclick.net",
            "google-analytics.com",
            "googlesyndication.com",
            "googletagmanager.com",
            "googletagservices.com",
            "googleadservices.com",
            "connect.facebook.net",
            "analytics.twitter.com",
            "static.ads-twitter.com",
            "ads.yahoo.com",
            "amazon-adsystem.com",
            "criteo.com",
            "criteo.net",
            "outbrain.com",
            "taboola.com",
            "scorecardresearch.com",
            "quantserve.com",
            "hotjar.com",
            "mixpanel.com",
            "segment.io",
            "amplitude.com",
            "newrelic.com",
            "moatads.com",
            "doubleverify.com",
            "adsrvr.org"
        }

        ''' <summary>True when the host is a known tracker (exact or subdomain).</summary>
        Public Shared Function ShouldBlock(hostName As String) As Boolean
            If String.IsNullOrEmpty(hostName) Then
                Return False
            End If
            Dim cleanHost As String = hostName.Trim().ToLowerInvariant()
            Dim colonPos As Integer = cleanHost.IndexOf(":"c)
            If colonPos >= 0 Then
                cleanHost = cleanHost.Substring(0, colonPos)
            End If
            For Each blockedSuffix In BlockedSuffixes
                If cleanHost = blockedSuffix Then
                    Return True
                End If
                If cleanHost.EndsWith("." & blockedSuffix) Then
                    Return True
                End If
            Next
            Return False
        End Function
    End Class

End Namespace
