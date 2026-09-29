' BrowserForWP — which engine renders the next page.
'
' Three questions, one answer: what the user asked for, whether the hosted
' renderer is usable at all, and what a measurement of the page actually says.
' The rule lives here, in Core, because it is pure — no XAML, no prose the user
' reads, no I/O — so tools/proto/engine-choice.mjs can execute it off-device and
' tests/BrowserForWP.Core.Tests can compile it.
'
' The hosted engine is the default this build ships with, and the reason the
' usable question comes first is that a default is not a promise that the server
' exists: an install that has not been registered against one renders on the
' device and says so, rather than sending a page nowhere or showing nothing at
' all. See ARCHITECTURE.md Law 5.
'
' The row of the table that matters most is the one about an ABSENT measurement.
' ProbeReport.CouldRun = False means nothing was measured, and this project has
' already shipped one lie of that exact shape: a probe that never ran was
' reported in the UI as "no missing web features detected". An unmeasured page
' therefore stays on the engine that exists, and never switches to the one that
' does not.

Namespace Engine

    ''' <summary>
    ''' The engine-selection and automatic-fallback rule, as data. Uninstantiable:
    ''' it is a rule, not a thing.
    ''' </summary>
    Public NotInheritable Class EngineChoice

        Public Const Trident As String = "trident"

        ''' <summary>
        ''' Pages are drawn by a server running Chromium.
        '''
        ''' This keyword used to be "native", for the on-device renderer this
        ''' repository built and then deleted. The VALUE changed with the meaning
        ''' on purpose: an upgraded install that stored "native" must NOT silently
        ''' start sending every page it reads through a server. Normalize turns the
        ''' old keyword into Auto, so the worst a stale setting can do is nothing.
        ''' </summary>
        Public Const Remote As String = "remote"

        Public Const Auto As String = "auto"

        ''' <summary>
        ''' The CompatibilityProbe count at which an automatic choice gives up on
        ''' Trident. Eight is deliberately the same number the shell's reader
        ''' fallback already uses, so the two thresholds cannot come to disagree
        ''' about what "too broken to read" means.
        ''' </summary>
        Public Const AutomaticFallbackThreshold As Integer = 8

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Anything unrecognised is Auto: never an error, and never Remote by
        ''' accident. A corrupt setting must not change which engine renders.
        ''' </summary>
        Public Shared Function Normalize(setting As String) As String
            If setting = Trident Then Return Trident
            If setting = Remote Then Return Remote
            Return Auto
        End Function

        ''' <summary>
        ''' The engine to use. The HOSTED engine is what this build is set to, and
        ''' that is the first thing this function has to be careful about: wanting it
        ''' is not the same as being able to use it. `hostedReady` says whether the
        ''' hosted renderer has an address, a device token and its switch on -- what
        ''' RemoteServers.Ready answers -- and it is consulted BEFORE the choice is
        ''' returned, so an install that has not been registered against a server
        ''' renders on the device instead of sending pages nowhere.
        '''
        ''' An explicit Trident choice still wins over everything: a person who asked
        ''' for the system engine keeps it. An explicit Auto consults the
        ''' measurement, and the measurement can only move a page onto the hosted
        ''' engine when that engine is usable at all.
        ''' </summary>
        Public Shared Function Decide(setting As String, hostedReady As Boolean,
                                      probeMeasured As Boolean, missingFeatureCount As Integer) As String
            Dim wanted As String = Normalize(setting)
            If wanted = Trident Then Return Trident

            If wanted = Remote Then
                If hostedReady Then Return Remote
                Return Trident
            End If

            If Not probeMeasured Then Return Trident
            If missingFeatureCount < AutomaticFallbackThreshold Then Return Trident
            If hostedReady Then Return Remote
            Return Trident
        End Function

        ''' <summary>
        ''' Why Decide returned what it did, as a resource key. Never a sentence:
        ''' Core has no business holding user-facing prose, and the device has to
        ''' say this in two languages. The view layer resolves it through Localizer.
        '''
        ''' "Wanted but not usable" is one key, not two: whether the address is
        ''' missing, the token is missing or the switch is off, the person reading
        ''' the status line has the same next step, which is to finish configuring
        ''' the server. The settings screen is where the difference is spelled out.
        ''' </summary>
        Public Shared Function Explain(setting As String, hostedReady As Boolean,
                                       probeMeasured As Boolean, missingFeatureCount As Integer) As String
            Dim wanted As String = Normalize(setting)
            If wanted = Trident Then Return "EngineReasonSetting"

            If wanted = Remote Then
                If hostedReady Then Return "EngineReasonSettingRemote"
                Return "EngineReasonRemoteNotConfigured"
            End If

            If Not probeMeasured Then Return "EngineReasonAutoNoMeasurement"
            If missingFeatureCount < AutomaticFallbackThreshold Then Return "EngineReasonAutoFits"
            If hostedReady Then Return "EngineReasonAutoTooManyMissingFeatures"
            Return "EngineReasonRemoteNotConfigured"
        End Function

    End Class

End Namespace
