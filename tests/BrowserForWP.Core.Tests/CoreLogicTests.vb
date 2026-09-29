' BrowserForWP — Core logic checks (build-safe, no MSTest dependency).
'
' Runs in the guest as a library: call CoreLogicTests.RunAll() from Test
' Explorer or a debug page. Every Check throws InvalidOperationException on
' failure so a regression is loud. Node mirrors cover the same behavior
' off-device (tools/proto/useragents.mjs, pinstore.mjs).

Imports BrowserForWP.Core.Browser
Imports BrowserForWP.Core.Engine
Imports BrowserForWP.Core.Remote
Imports BrowserForWP.Core.Storage
Imports BrowserForWP.Localization
Imports BrowserForWP.Net.Tls13

Namespace CoreTests

    ''' <summary>Pure-logic regression checks for Core + pin/hostname helpers.</summary>
    Public NotInheritable Class CoreLogicTests

        Private Sub New()
        End Sub

        Private Shared Sub Check(ok As Boolean, checkName As String)
            If Not ok Then
                Throw New InvalidOperationException("check failed: " & checkName)
            End If
        End Sub

        Public Shared Function RunAll() As Integer
            Dim ran As Integer = 0

            Dim searchTarget = AddressNormalizer.Normalize("hello world")
            Check(searchTarget.IsSearch, "search classified")
            ran += 1

            Dim urlTarget = AddressNormalizer.Normalize("example.com")
            Check(urlTarget.Url = "https://example.com", "bare host https")
            ran += 1

            Dim refusedTarget = AddressNormalizer.Normalize("javascript:alert(1)")
            Check(Not refusedTarget.IsValid, "javascript refused")
            ran += 1

            Dim tab As New TabModel()
            tab.PushHistory("https://a.example/")
            tab.PushHistory("https://b.example/")
            Check(tab.CanGoBack, "tab can go back")
            tab.Back()
            Check(tab.Url = "https://a.example/", "tab back url")
            ran += 1

            Dim session As New BrowserSession()
            session.NewTab()
            Check(session.ActiveTab IsNot Nothing, "session active tab")
            Check(session.EffectiveUserAgent = UserAgents.MobileDefault, "default UA mobile")
            session.DesktopMode = True
            Check(session.EffectiveUserAgent = UserAgents.DesktopWindows, "desktop UA")
            ran += 1

            Check(UserAgents.EffectiveUserAgent(False) = UserAgents.MobileDefault, "UA helper mobile")
            ran += 1

            ' The engine-choice rule. Refused exhaustively off-device by
            ' tools/proto/engine-choice.mjs; these four are the rows that also have
            ' to hold in the compiled half, and the third is the one that matters:
            ' an absent measurement is never grounds for switching engines.
            Check(EngineChoice.Decide(EngineChoice.Remote, False, 0) = EngineChoice.Remote,
                  "engine choice: explicit remote wins with no measurement")
            Check(EngineChoice.Decide(EngineChoice.Trident, True, 99) = EngineChoice.Trident,
                  "engine choice: explicit trident wins over a broken probe")
            Check(EngineChoice.Decide(EngineChoice.Auto, False, 99) = EngineChoice.Trident,
                  "engine choice: auto never switches on an absent measurement")
            Check(EngineChoice.Decide(EngineChoice.Auto, True, EngineChoice.AutomaticFallbackThreshold) = EngineChoice.Remote,
                  "engine choice: auto switches at the threshold")
            ran += 1

            ' The three engine shapes. The third is the defect this property had:
            ' an engine with no script host is not an engine that wants a polyfill
            ' layer, and saying it did would have had the shell injecting compat.js
            ' into something with no window to inject into.
            Dim scriptingEngine As New EngineCapabilities With {.SupportsScripting = True, .SupportsModernJavaScript = False}
            Dim modernEngine As New EngineCapabilities With {.SupportsScripting = True, .SupportsModernJavaScript = True}
            Dim markupOnlyEngine As New EngineCapabilities With {.SupportsScripting = False, .SupportsModernJavaScript = False}
            Check(scriptingEngine.NeedsPolyfillLayer, "capabilities: an IE11-shaped engine needs the polyfill layer")
            Check(Not modernEngine.NeedsPolyfillLayer, "capabilities: a modern engine does not")
            Check(Not markupOnlyEngine.NeedsPolyfillLayer, "capabilities: an engine with no script host does not")
            ran += 1

            Dim appSettings As New AppSettings()
            ' The default template is the LITE endpoint, so the expected value is the
            ' lite URL. This assertion previously named the heavy duckduckgo.com
            ' endpoint that the lite-first default replaced; it compiled, so only
            ' running it would have caught the drift.
            Check(appSettings.SearchUrlFor("hello world") = "https://lite.duckduckgo.com/lite/?q=hello%20world", "search url")
            Check(appSettings.SearchUrlFor("hello world").StartsWith("https://lite.duckduckgo.com/"), "search default is lite")
            Dim hostMap As Dictionary(Of String, String) = appSettings.SaveToMap()
            Dim reloaded As New AppSettings()
            reloaded.LoadFromMap(hostMap)
            Check(reloaded.Homepage = appSettings.Homepage, "settings roundtrip")
            ran += 1

            Dim history As New HistoryStore()
            history.Add("https://a.example/", "A")
            Dim savedHistory As String = history.Serialize()
            Dim history2 As New HistoryStore()
            history2.Parse(savedHistory)
            Check(history2.Count = 1, "history roundtrip")
            ran += 1

            Dim favorites As New FavoritesStore()
            Check(favorites.Add("https://a.example/", "A"), "fav add")
            Check(Not favorites.Add("https://a.example/", "A"), "fav dup rejected")
            Check(favorites.Contains("https://a.example/"), "fav contains")
            ran += 1

            Dim saved As New SavedPages()
            saved.Add("https://a.example/", "A", "hello world")
            Check(saved.Count = 1, "saved add")
            Dim savedAgain As New SavedPages()
            savedAgain.Parse(saved.Serialize())
            Check(savedAgain.Count = 1, "saved roundtrip")
            ran += 1

            Dim dial As New SpeedDial()
            Check(dial.Add("https://a.example/", "A"), "dial add")
            For dialIndex As Integer = 1 To 20
                dial.Add("https://s" & dialIndex.ToString() & ".example/", "S")
            Next
            Check(dial.Count = SpeedDial.MaxSlots, "dial capped")
            ran += 1

            Dim sitePrefs As New SiteSettings()
            Check(sitePrefs.GetSetting("Example.COM").TextSizePct = 100, "site default size")
            sitePrefs.SetSetting("example.com", 500, True)
            Dim gotBack = sitePrefs.GetSetting("example.com")
            Check(gotBack.TextSizePct = 200, "site size clamped")
            Check(gotBack.ImagesOff, "site images off")
            ran += 1

            Dim backupSections As New Dictionary(Of String, String)()
            backupSections("settings") = "a=1"
            Dim backupText As String = BackupManager.BuildBackup(backupSections)
            Dim parsedSections As Dictionary(Of String, String) = Nothing
            Check(BackupManager.TryParseBackup(backupText, parsedSections), "backup roundtrip")
            Check(parsedSections("settings") = "a=1", "backup payload")
            Check(Not BackupManager.TryParseBackup("garbage", parsedSections), "backup rejects garbage")
            ran += 1

            Check(PinStore.NormalizeHost("Example.COM:443") = "example.com", "pin host normalize")
            Dim pins As New PinStore()
            pins.Add("Example.com", "abc123")
            Check(pins.Verify("example.com", "abc123"), "pin match")
            Check(Not pins.Verify("example.com", "zzz"), "pin mismatch")
            Check(pins.Verify("other.com", Nothing), "no pin passes")
            ran += 1

            Dim hostNames As New List(Of String)()
            hostNames.Add("*.example.com")
            Dim wildHit = CertificateValidator.MatchHostname(hostNames, "a.example.com")
            Check(wildHit.IsMatch, "wildcard one label")
            Dim wildDeep = CertificateValidator.MatchHostname(hostNames, "a.b.example.com")
            Check(Not wildDeep.IsMatch, "wildcard not two labels")
            ran += 1

            Check(LanguageCatalog.Match(New String() {"it-IT"}) = "it-IT", "lang it")
            Check(LanguageCatalog.Match(New String() {"xx"}) = "en-US", "lang fallback")
            ran += 1

            Dim serverSettings As New RemoteServerSettings()
            serverSettings.PrimaryUrl = "render.example.com"
            serverSettings.SecondaryUrl = "https://backup.example.com/"
            serverSettings.PrimaryToken = "primary-token"
            Dim serverOrder As List(Of String) = RemoteServers.Order(serverSettings)
            Check(serverOrder.Count = 2, "server order: two configured")
            Check(serverOrder(0) = "https://render.example.com", "server order: primary normalized first")
            Check(serverOrder(1) = "https://backup.example.com", "server order: secondary normalized")
            Check(RemoteServers.Normalize("file:///tmp") = "", "server url: non-web scheme refused")
            ' A colon is not a scheme. Reading the host of "render.example.com:8443" as
            ' a scheme refused it, so a server a person had just typed VANISHED from
            ' the settings screen with no message -- worse than refusing the field,
            ' because there is nothing to correct.
            Check(RemoteServers.Normalize("render.example.com:8443") = "https://render.example.com:8443",
                  "server url: a bare host and port is a host and port")
            ' One field instead of two: the same device, registered on both servers.
            Check(RemoteServers.TokenFor(serverSettings, "https://backup.example.com") = "primary-token",
                  "server token: the secondary falls back to the primary's")
            ran += 1

            Return ran
        End Function
    End Class

End Namespace
