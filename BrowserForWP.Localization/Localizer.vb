' BrowserForWP — the WinRT-backed half of localization.
'
' Resolution order, in full:
'   1. An explicit per-app override (the user picked a language in Settings).
'   2. Windows.Globalization.ApplicationLanguages.Languages — the phone's own
'      ordered display-language list.
'   3. en-US.
'
' The actual string lookup is delegated to the platform resource qualifier
' system: we tell it which language to resolve, and it handles per-string
' fallback. That is why the .resw files live under Strings/<tag>/Resources.resw.

Imports System.Collections.Generic
Imports Windows.ApplicationModel.Resources
Imports Windows.ApplicationModel.Resources.Core

Namespace Localization

    ''' <summary>String lookup for the UI, following the phone's language.</summary>
    Public NotInheritable Class Localizer

        ''' <summary>
        ''' Resource map name. The .resw files are at
        ''' BrowserForWP/Strings/&lt;tag&gt;/Resources.resw, and the map the build
        ''' makes for them is named after the FILE -- "Resources" -- with the
        ''' language folder acting as a qualifier rather than as part of a path.
        '''
        ''' MEASURED, not assumed, 2026-09-29: the resource maps inside the built
        ''' resources.pri are exactly 'Resources', 'Files', 'Polyfill' and 'Assets',
        ''' on Debug/AnyCPU as well as x86/Debug and ARM/Debug. This constant used to
        ''' read "Strings/Resources", which is a map that does not exist: every
        ''' lookup threw ResourceMap Not Found, the error is swallowed by design, so
        ''' the app ran with raw keys as labels -- "EngineLabel", "KeyBarClose" --
        ''' and about fifty first-chance exceptions per launch in the debugger.
        ''' tools/check-vb.mjs now compares this name against the names the .resw
        ''' files become, which is the question parity was not asking.
        '''
        ''' The angle brackets must be escaped: a doc comment is parsed as XML, so an
        ''' unescaped &lt;tag&gt; opens an element and the closing &lt;/summary&gt;
        ''' then mismatches (BC42304).
        ''' </summary>
        Private Const ResourceMap As String = "Resources"

        Private Shared _override As String
        Private Shared _current As String
        Private Shared _loader As ResourceLoader

        Private Sub New()
        End Sub

        ''' <summary>The language currently in use.</summary>
        Public Shared ReadOnly Property CurrentLanguage As String
            Get
                If _current Is Nothing Then Return LanguageCatalog.DefaultTag
                Return _current
            End Get
        End Property

        ''' <summary>
        ''' Resolve the language from the phone and apply it. Call once, early in
        ''' App.OnLaunched, before any UI string is read.
        ''' </summary>
        Public Shared Sub Initialize()
            _current = LanguageCatalog.Match(Windows.Globalization.ApplicationLanguages.Languages)
            ApplyLanguageQualifier(_current)
        End Sub

        ''' <summary>
        ''' Force a specific language, ignoring the phone. Passing Nothing or an
        ''' unsupported tag returns to automatic resolution.
        ''' </summary>
        Public Shared Sub Override(tag As String)
            If String.IsNullOrEmpty(tag) Then
                _override = Nothing
                Initialize()
                Return
            End If

            Dim matched = LanguageCatalog.Match(New String() {tag})
            _override = matched
            _current = matched
            ApplyLanguageQualifier(matched)
        End Sub

        ''' <summary>True when the user has pinned a language rather than following the phone.</summary>
        Public Shared ReadOnly Property IsOverridden As Boolean
            Get
                Return _override IsNot Nothing
            End Get
        End Property

        ''' <summary>
        ''' A localized string. Never returns Nothing: a missing key yields the key
        ''' itself, so a gap shows up visibly in the UI instead of blanking a label.
        ''' </summary>
        Public Shared Function [Get](key As String) As String
            If String.IsNullOrEmpty(key) Then Return String.Empty
            Try
                Dim value = Loader.GetString(key)
                If value IsNot Nothing Then Return value
            Catch ex As Exception
                ' A missing resource map or a malformed key must not take the UI
                ' down; the key itself is a better diagnostic than a crash.
            End Try
            Return key
        End Function

        ''' <summary>
        ''' Format a template. Uses the current culture so numbers and dates follow
        ''' the phone, which is separate from which language the strings are in.
        ''' </summary>
        Public Shared Function Format(key As String, ParamArray args As Object()) As String
            Return String.Format(System.Globalization.CultureInfo.CurrentCulture, [Get](key), args)
        End Function

        ''' <summary>
        ''' Created lazily, and rebuilt whenever the language qualifier changes, so
        ''' a runtime language switch is reflected without an app restart.
        ''' </summary>
        Private Shared ReadOnly Property Loader As ResourceLoader
            Get
                If _loader Is Nothing Then _loader = New ResourceLoader(ResourceMap)
                Return _loader
            End Get
        End Property

        ''' <summary>
        ''' Tell the resource system which language to resolve. Setting the
        ''' qualifier is what makes a user-chosen language take effect at runtime;
        ''' the manifest alone only covers the launch language.
        ''' </summary>
        Private Shared Sub ApplyLanguageQualifier(tag As String)
            Try
                Dim context = ResourceContext.GetForCurrentView()
                context.QualifierValues("Language") = tag
                _loader = New ResourceLoader(ResourceMap)   ' discard the old resolution
            Catch ex As Exception
                ' If the qualifier cannot be set we keep whatever the manifest
                ' resolved, which is still a valid language rather than a failure.
            End Try
        End Sub
    End Class

End Namespace
