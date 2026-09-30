' BrowserForWP — the set of languages we ship, and the rules for choosing one.
'
' Pure logic, no WinRT dependency, so it is unit-testable off-device. The WinRT
' plumbing lives in Localizer.

Imports System.Collections.Generic
Imports System.Globalization

Namespace Localization

    ''' <summary>
    ''' The languages BrowserForWP ships and how a requested language maps onto
    ''' them. Adding a language is a data change here plus one resource folder.
    ''' </summary>
    Public NotInheritable Class LanguageCatalog

        ''' <summary>Supported tags. English first: it is the default and the fallback.</summary>
        Private Shared ReadOnly SupportedTags As String() = {"en-US", "it-IT"}

        Private Sub New()
        End Sub

        ''' <summary>
        ''' The language used when nothing else applies. Never remove it.
        '''
        ''' Named DefaultTag and not Default: Default is a VB keyword, and a member
        ''' called Default produces BC30183 "invalid keyword as identifier" at the
        ''' declaration and BC30456 "'Default' is not a member" at every use site.
        ''' </summary>
        Public Shared ReadOnly Property DefaultTag As String
            Get
                Return "en-US"
            End Get
        End Property

        ''' <summary>All supported tags, most-default first.</summary>
        Public Shared ReadOnly Property Supported As String()
            Get
                Return SupportedTags
            End Get
        End Property

        ''' <summary>
        ''' Pick the best supported language from an ordered list of the user's
        ''' preferences. The caller's order is authoritative — on Windows Phone that
        ''' order is the user's own display-language ranking, so an Italian speaker
        ''' with English second gets Italian.
        ''' </summary>
        Public Shared Function Match(requested As IEnumerable(Of String)) As String
            If requested IsNot Nothing Then
                For Each candidate In requested
                    Dim tag = Normalize(candidate)
                    If tag IsNot Nothing Then Return tag
                Next
            End If
            Return DefaultTag
        End Function

        ''' <summary>
        ''' Display name rendered in the language itself, which is what a language
        ''' picker should show.
        ''' </summary>
        Public Shared Function DisplayName(tag As String) As String
            Select Case Normalize(tag)
                Case "it-IT"
                    Return "Italiano"
                Case "en-US"
                    Return "English"
                Case Else
                    Return "English"
            End Select
        End Function

        ''' <summary>
        ''' Map a requested tag onto a supported one, matching on the primary
        ''' subtag so "it", "it-IT" and "IT-it" all resolve to Italian, and an
        ''' unshipped script or region suffix does not defeat the match.
        ''' Returns Nothing when nothing supported matches.
        ''' </summary>
        Private Shared Function Normalize(candidate As String) As String
            If String.IsNullOrEmpty(candidate) Then Return Nothing

            Dim primary As String
            Try
                primary = New CultureInfo(candidate).TwoLetterISOLanguageName
            Catch ex As ArgumentException
                ' CultureInfo rejects some well-formed BCP-47 tags; fall back to a
                ' plain split so weird-but-harmless tags still resolve. The instance is
                ' deliberately not used: the fallback is the same for every
                ' ArgumentException, and the TYPE is the whole point of this clause --
                ' a name of another type is a bug here, not a tag to guess at.
                primary = candidate.Split("-"c)(0)
            End Try

            If String.IsNullOrEmpty(primary) OrElse primary = "iv" Then Return Nothing
            primary = primary.ToLowerInvariant()

            ' The loop variable must not be named `supported`: VB is case-insensitive,
            ' so it collides with the Supported property and is rejected with BC30039
            ' ("the loop control variable cannot be a property").
            For Each supportedTag In SupportedTags
                If supportedTag.Split("-"c)(0).ToLowerInvariant() = primary Then Return supportedTag
            Next
            Return Nothing
        End Function
    End Class

End Namespace
