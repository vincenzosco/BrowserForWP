' BrowserForWP — user-agent table (pure, no WinRT dependency).
'
' The engine cannot change Trident, but the request header can ask for desktop
' content. Kept here so it is unit-testable off-device; the Node mirror is
' tools/proto/useragents.mjs.

Namespace Browser

    ''' <summary>Mobile and desktop user-agent strings.</summary>
    Public NotInheritable Class UserAgents

        Private Sub New()
        End Sub

        ''' <summary>Default mobile identifier sent by the shell.</summary>
        Public Const MobileDefault As String = "Mozilla/5.0 (compatible; MSIE 10.0; Windows Phone 8.1; Trident/6.0; BrowserForWP/1.0 Mobile)"

        ''' <summary>Desktop identifier sent when DesktopMode is on.</summary>
        Public Const DesktopWindows As String = "Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko"

        ''' <summary>Effective UA for the given mode. Pure function for testability.</summary>
        Public Shared Function EffectiveUserAgent(desktopMode As Boolean) As String
            If desktopMode Then
                Return DesktopWindows
            End If
            Return MobileDefault
        End Function
    End Class

End Namespace
