' BrowserForWP — where to render, and what to do when a server does not answer.
'
' Two servers: a primary, and a secondary that is tried only when the primary
' cannot be reached. The point of the second one is that the operator of the first
' can go away without taking the browser with it, and the point of there being
' exactly two is that "add your own server" means replacing the secondary. A list
' with no visible end is a settings screen nobody finishes.
'
' It is a rule and not a thing, so it is uninstantiable, and it lives in Core
' because it is pure: no socket, no key, no prose. It returns resource keys and
' the Localizer resolves them.

Imports System
Imports System.Collections.Generic

Namespace Remote

    ''' <summary>Every user-tunable thing about the remote engine. Plain data.</summary>
    Public NotInheritable Class RemoteServerSettings

        Public Sub New()
            PrimaryUrl = String.Empty
            PrimaryToken = String.Empty
            SecondaryUrl = String.Empty
            SecondaryToken = String.Empty
            RemoteEnabled = True
        End Sub

        Public Property PrimaryUrl As String
        Public Property PrimaryToken As String
        Public Property SecondaryUrl As String
        Public Property SecondaryToken As String

        ''' <summary>
        ''' Switched ON in the default settings, because the hosted engine is this
        ''' build's default engine -- which is also why this is the field the
        ''' Settings screen leads with, next to the note that the server's operator
        ''' can read every page sent to it. Turning it off is one toggle.
        ''' </summary>
        Public Property RemoteEnabled As Boolean

    End Class

    Public NotInheritable Class RemoteServers

        ''' <summary>The two roles, as resource keys rather than sentences.</summary>
        Public Const Primary As String = "primary"
        Public Const Secondary As String = "secondary"

        Public Const UrlScheme As String = "https://"
        Public Const SchemeHttps As String = "https"
        Public Const SchemeHttp As String = "http"

        ''' <summary>What separates a scheme from an authority. See HasAuthority.</summary>
        Public Const AuthorityMarker As String = "://"

        ''' <summary>A rule, not a thing: there is nothing here to hold.</summary>
        Private Sub New()
        End Sub

        ''' <summary>
        ''' Look at what people actually type, and refuse everything that is not a
        ''' web address.
        '''
        ''' TWO THINGS ARE CHECKED HERE RATHER THAN LEFT TO Uri, because this
        ''' function's contract is "nonsense becomes not configured" and Uri's
        ''' leniency is not part of that contract:
        '''
        '''   * A leading scheme is a scheme only when "://" follows it. The first
        '''     draft asked whether a colon appeared at all, and
        '''     "render.example.com:8443" has one: the host reads as scheme
        '''     "render.example.com" and the port as its path, the scheme is neither
        '''     https nor http, and the server a person just typed VANISHES from the
        '''     settings screen without saying so. A silent disappearance is worse
        '''     than a rejected field, because there is nothing to correct.
        '''   * The authority has to look like a host. Uri accepts spellings this
        '''     must not, and which ones it accepts can differ between profiles, so
        '''     the check is written out instead of delegated.
        ''' </summary>
        Public Shared Function Normalize(rawUrl As String) As String
            Dim text As String = If(rawUrl, String.Empty).Trim()
            If text.Length = 0 Then Return String.Empty

            Dim withScheme As String = text
            If Not HasAuthority(text) Then
                withScheme = UrlScheme & text
            End If

            If Not LooksLikeAHost(withScheme) Then Return String.Empty

            Dim parsed As Uri = Nothing
            If Not Uri.TryCreate(withScheme, UriKind.Absolute, parsed) Then
                Return String.Empty
            End If
            If parsed.Scheme <> SchemeHttps AndAlso parsed.Scheme <> SchemeHttp Then
                Return String.Empty
            End If

            Dim origin As String = parsed.Scheme & AuthorityMarker & parsed.Host
            If parsed.Port > 0 AndAlso parsed.Port <> 80 AndAlso parsed.Port <> 443 Then
                origin = origin & ":" & parsed.Port.ToString()
            End If

            Dim pathPart As String = parsed.AbsolutePath
            If pathPart = "/" Then pathPart = String.Empty
            If pathPart.EndsWith("/") Then
                pathPart = pathPart.Substring(0, pathPart.Length - 1)
            End If
            Return origin & pathPart
        End Function

        ''' <summary>
        ''' True when the text opens with "scheme://" and not merely with something
        ''' that has a colon in it. Separated out because the difference is the
        ''' entire bug described on Normalize, and because it is the part a reader
        ''' will want to check.
        ''' </summary>
        Private Shared Function HasAuthority(text As String) As Boolean
            Dim markerAt As Integer = text.IndexOf(AuthorityMarker, StringComparison.Ordinal)
            If markerAt <= 0 Then Return False

            Dim schemePart As String = text.Substring(0, markerAt)
            If Not Char.IsLetter(schemePart.Chars(0)) Then Return False
            For Each schemeChar As Char In schemePart
                If Char.IsLetterOrDigit(schemeChar) Then Continue For
                If schemeChar = "."c OrElse schemeChar = "+"c OrElse schemeChar = "-"c Then
                    Continue For
                End If
                Return False
            Next
            Return True
        End Function

        ''' <summary>
        ''' The authority between "://" and the first "/", and it must be a host:
        ''' letters, digits, dots, hyphens, one colon for a port, brackets for IPv6.
        ''' A space is not a hostname, whatever Uri happens to do with it.
        ''' </summary>
        Private Shared Function LooksLikeAHost(text As String) As Boolean
            Dim markerAt As Integer = text.IndexOf(AuthorityMarker, StringComparison.Ordinal)
            If markerAt <= 0 Then Return False

            Dim rest As String = text.Substring(markerAt + AuthorityMarker.Length)
            Dim slashAt As Integer = rest.IndexOf("/"c)
            Dim authority As String = If(slashAt < 0, rest, rest.Substring(0, slashAt))
            If authority.Length = 0 Then Return False

            ' A port is the only place a colon may appear, and it must be digits.
            ' Splitting at the LAST colon also leaves an IPv6 literal like [::1]
            ' intact, brackets and all.
            Dim hostPart As String = authority
            Dim colonAt As Integer = authority.LastIndexOf(":"c)
            If colonAt >= 0 Then
                Dim portPart As String = authority.Substring(colonAt + 1)
                If portPart.Length = 0 Then Return False
                For Each portChar As Char In portPart
                    If Not Char.IsDigit(portChar) Then Return False
                Next
                hostPart = authority.Substring(0, colonAt)
            End If
            If hostPart.Length = 0 Then Return False

            Dim brackets As Integer = 0
            For Each hostChar As Char In hostPart
                If Char.IsLetterOrDigit(hostChar) Then Continue For
                If hostChar = "."c OrElse hostChar = "-"c OrElse hostChar = "_"c Then Continue For
                If hostChar = "["c OrElse hostChar = "]"c Then
                    brackets += 1
                    Continue For
                End If
                Return False
            Next
            Return brackets = 0 OrElse brackets = 2
        End Function

        ''' <summary>
        ''' The urls to try, in order, with a duplicate collapsed: two identical
        ''' servers are one attempt, not two.
        ''' </summary>
        Public Shared Function Order(settings As RemoteServerSettings) As List(Of String)
            Dim result As New List(Of String)()
            If settings Is Nothing Then Return result

            Dim first As String = Normalize(settings.PrimaryUrl)
            Dim second As String = Normalize(settings.SecondaryUrl)

            If first.Length > 0 Then result.Add(first)
            If second.Length > 0 AndAlso second <> first Then result.Add(second)
            Return result
        End Function

        ''' <summary>
        ''' True when the hosted engine can be used at all: the switch is on AND at
        ''' least one server has both an address and a token.
        '''
        ''' A candidate with no token is not a configured server. The connection
        ''' would be refused by the server at the handshake for a reason the phone
        ''' cannot see, one round trip after everything looked fine -- and "the
        ''' servers are unreachable" is then the wrong thing to tell somebody whose
        ''' actual next step is to register this device. This is the answer
        ''' EngineChoice.Decide consults before it hands a page to the hosted engine.
        '''
        ''' The SWITCH is read both here and in RemoteEngine, deliberately: the engine
        ''' reads it on every navigation, because that is where the disclosure is
        ''' honoured, and this reads it so that a switched-off server is not even a
        ''' candidate.
        ''' </summary>
        Public Shared Function Ready(settings As RemoteServerSettings) As Boolean
            If settings Is Nothing Then Return False
            If Not settings.RemoteEnabled Then Return False
            For Each candidate As String In Order(settings)
                If TokenFor(settings, candidate).Length > 0 Then Return True
            Next
            Return False
        End Function

        ''' <summary>
        ''' The token for a url. The secondary falls back to the primary's token when
        ''' its own is empty, which is what makes "the same device, registered on
        ''' both servers" one field instead of two.
        ''' </summary>
        Public Shared Function TokenFor(settings As RemoteServerSettings, url As String) As String
            If settings Is Nothing Then Return String.Empty
            Dim normalized As String = Normalize(url)
            If normalized.Length = 0 Then Return String.Empty

            If normalized = Normalize(settings.PrimaryUrl) Then
                Return If(settings.PrimaryToken, String.Empty)
            End If

            Dim secondaryToken As String = If(settings.SecondaryToken, String.Empty)
            If secondaryToken.Length > 0 Then Return secondaryToken
            Return If(settings.PrimaryToken, String.Empty)
        End Function

        ''' <summary>
        ''' Which role the url that answered is. Never a sentence: Core has no
        ''' business holding user-facing prose.
        ''' </summary>
        Public Shared Function RoleOf(settings As RemoteServerSettings, url As String) As String
            If settings Is Nothing Then Return Primary
            Dim secondary As String = Normalize(settings.SecondaryUrl)
            If secondary.Length > 0 AndAlso Normalize(url) = secondary Then Return Secondary
            Return Primary
        End Function

        ''' <summary>Why the engine is where it is, as a resource key.</summary>
        Public Shared Function Explain(settings As RemoteServerSettings, usedUrl As String,
                                       primaryWasTried As Boolean) As String
            If primaryWasTried AndAlso RoleOf(settings, usedUrl) = Secondary Then
                Return "EngineReasonRemoteSecondary"
            End If
            Return "EngineReasonSettingRemote"
        End Function

    End Class

End Namespace
