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

        ''' <summary>
        ''' Set when the resource map could not be loaded, so that the cost of a
        ''' broken map is ONE exception instead of one per string.
        '''
        ''' Measured in the emulator on 2026-09-29: the wrong map name produced about
        ''' fifty ResourceMap Not Found first-chance exceptions for a single launch,
        ''' because constructing the loader was retried on every lookup and the
        ''' failure was swallowed. A swallowed error that repeats once per label is
        ''' not a diagnostic, it is a flood -- and it buried the one exception that
        ''' mattered among fifty identical ones.
        ''' </summary>
        Private Shared _loaderUnavailable As Boolean

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

            ' Nothing means the map could not be loaded, which has already been
            ' reported once and is not worth reporting again per string.
            '
            ' The local is `resolver` and NOT `loader`: VB is case-insensitive, so
            ' `Dim loader = Loader` declares a local that shadows the property it
            ' is reading, and the compiler reports BC30980 ("cannot infer the type
            ' of 'loader' from an expression containing 'loader'") followed by
            ' BC30574 and BC30512, because with Option Strict On the name is then
            ' late-bound Object. Measured on the first build of this change; the
            ' same trap is why X509Reader.DerReader.Element.Value() has a differently
            ' named local.
            Dim resolver As ResourceLoader = Loader
            If resolver IsNot Nothing Then
                Try
                    Dim value = resolver.GetString(key)
                    If value IsNot Nothing Then Return value
                Catch ex As Exception
                    ' A malformed key must not take the UI down; the key itself is a
                    ' better diagnostic than a crash.
                End Try
            End If
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
        ''' a runtime language switch is reflected without an app restart. Nothing
        ''' after a failure: the map either exists for the life of the process or it
        ''' does not, and asking again per string only repeats the exception.
        ''' </summary>
        Private Shared ReadOnly Property Loader As ResourceLoader
            Get
                If _loader Is Nothing AndAlso Not _loaderUnavailable Then
                    _loader = TryCreateLoader()
                End If
                Return _loader
            End Get
        End Property

        ''' <summary>
        ''' The only place a loader is constructed, and therefore the only place the
        ''' "unavailable" flag is set. Nothing means the map is not there.
        ''' </summary>
        Private Shared Function TryCreateLoader() As ResourceLoader
            Try
                _loaderUnavailable = False

                ' A DELIBERATE use of a deprecated API, and its BC40000 is silenced
                ' once for this project in BrowserForWP.Localization.vbproj rather than
                ' tolerated in the log on every build. VB 12 -- the VS2013 toolchain
                ' this project compiles on -- has no `#Disable Warning` directive
                ' (that arrived in VB 14), so a per-project NoWarn is the only
                ' supported form; the guest build is what proved that.
                '
                ' Why keep the constructor: the suggested replacement,
                ' GetForCurrentView(name), is tied to the view's cached
                ' ResourceContext, so re-creating the loader after
                ' ApplyLanguageQualifier changed the Language qualifier would hand back
                ' the same stale resolution and the runtime language switch would stop
                ' working. This constructor builds a NEW loader each call, which is what
                ' a rebuilt-on-every-switch loader needs. The deprecation note names a
                ' "TBD" release that never shipped for Windows Phone 8.1, so the API is
                ' permanent here. Recorded in docs/MAINTAINING.md Round 24; do not
                ' "fix" this without a handset to test the language switch on.
                Return New ResourceLoader(ResourceMap)
            Catch
                ' _loaderUnavailable IS the report: Get falls back to the key name and
                ' stops retrying a map the platform will not hand out. Kept rather than
                ' printed because this runs during static initialisation, before any
                ' screen exists to print it on.
                _loaderUnavailable = True
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Tell the resource system which language to resolve. Setting the
        ''' qualifier is what makes a user-chosen language take effect at runtime;
        ''' the manifest alone only covers the launch language.
        ''' </summary>
        Private Shared Sub ApplyLanguageQualifier(tag As String)
            Try
                Dim context = ResourceContext.GetForCurrentView()
                context.QualifierValues("Language") = tag
                _loader = TryCreateLoader()   ' discard the old resolution
            Catch ex As Exception
                ' If the qualifier cannot be set we keep whatever the manifest
                ' resolved, which is still a valid language rather than a failure.
            End Try
        End Sub
    End Class

End Namespace
