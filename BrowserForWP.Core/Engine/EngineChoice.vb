' BrowserForWP — which engine renders the next page.
'
' Three questions, one answer: what the user asked for, whether the hosted
' renderer is usable at all, and what a measurement of the page actually says.
' The rule lives here, in Core, because it is pure — no XAML, no prose the user
' reads, no I/O — so tools/proto/engine-choice.mjs can execute it off-device and
' tests/BrowserForWP.Core.Tests can compile it.
'
' AN EXPLICIT CHOICE IS NOT A PREFERENCE THAT LOSES TO AN ERROR. Two of the three
' answers can put a page on the server, and they are not the same statement. A
' person who picks Remote has said WHERE pages come from, so an unusable server
' leaves them with the server engine and a reason on screen -- never with a page
' drawn by the very engine they did not choose, which was a silent change of
' renderer under a setting that said otherwise. Auto is the setting for somebody
' who would rather let a measurement decide, and it is the only one that may hand
' a page to the device because the server could not take it.
'
' What is still asked before a page goes to the server is whether the server is
' usable at all: an address, a device token and its switch on. RemoteServers.Ready
' answers it, Auto consults it, and that is where "a default is not a promise that
' the server exists" is enforced. See ARCHITECTURE.md Law 5.
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
        ''' The engine to use. An explicit Trident choice wins over everything, and
        ''' an explicit Remote choice is honoured as written: this function does NOT
        ''' fall back to the device for it. Handing a page to the on-device engine
        ''' when somebody has chosen the server would be a silent change of renderer
        ''' under a setting that says otherwise, and what it hides is exactly what
        ''' the status line exists to report. The engine itself still refuses to draw
        ''' anything it cannot: RemoteEngine checks its own readiness on every
        ''' navigation and raises the reason, so a misconfigured install gets the
        ''' reason and no page rather than a page from the wrong engine.
        '''
        ''' Auto is the setting that may use either, and `hostedReady` -- whether the
        ''' hosted renderer has an address, a device token and its switch on, the
        ''' answer RemoteServers.Ready gives -- is consulted on that path, before a
        ''' page is handed to a server that is not there.
        ''' </summary>
        Public Shared Function Decide(setting As String, hostedReady As Boolean,
                                      probeMeasured As Boolean, missingFeatureCount As Integer) As String
            Dim wanted As String = Normalize(setting)
            If wanted = Trident Then Return Trident
            If wanted = Remote Then Return Remote

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

        ''' <summary>
        ''' Whether a page the hosted engine could not draw may be handed to the
        ''' on-device engine. False for an explicit Remote: the shell asks this
        ''' before its announced fallback, so a page is never drawn by an engine its
        ''' reader did not choose. True otherwise, because Auto asks for whichever
        ''' engine works and Trident never needs a fallback at all.
        ''' </summary>
        Public Shared Function MayFallBackToDevice(setting As String) As Boolean
            Return Normalize(setting) <> Remote
        End Function

    End Class

End Namespace
