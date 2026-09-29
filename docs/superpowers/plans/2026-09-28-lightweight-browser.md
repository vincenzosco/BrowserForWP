# Lightweight WP8.1 browser — Plan

**Goal:** Make BrowserForWP a genuinely good lightweight WP8.1 browser: lite-first defaults, reading/night modes, find+share, private mode, tracker blocking, session restore — while slimming settings, search options, stored data, and Release weight. Speed + memory wins every trade-off.

**Global Constraints (verbatim from the request and the platform):**
- Target: `TargetPlatformVersion 8.1`, `WindowsPhoneApp`, VB 12 / VS2013. Windows Phone 8.1 cannot host Chromium or Firefox, cannot replace Trident, and cannot exceed TLS 1.2 through the OS.
- No backend. Every byte of logic runs on-device. No `127.0.0.1` proxy feeding the WebView.
- API surface is ".NET for Windows Store apps": no `SHA256`/`HMACSHA256`/`RNGCryptoServiceProvider`, no `Encoding.ASCII`, no `RegexOptions.Compiled`, `VerifySignature` never `Verify`.
- VB 12: no implicit line continuation after `.`; use a `With` block. VB is case-insensitive: no local shadows a type, member, keyword, or `Page.Tag`.
- Never hand-edit `X25519.vb` or `BrowserForWP.Net/Tls13/` (untouched this round; the WebView cannot intercept subresources, so blocking covers top-level navigations only — stated in code, not oversold).
- `en-US` is the default and fallback language: every new key goes in both files.
- Layer discipline: pure strings/logic in Core; `InvokeScriptAsync` runners in `TridentEngine`; persistence calls and Share UI in the app.

## File Structure

| File | Why |
|---|---|
| `tools/proto/trackerblock.mjs` NEW | Failing-first mirror of `ShouldBlock` + caps. Must FAIL pre-fix, PASS post-fix. |
| `tools/proto/lightweight.mjs` NEW | Content guards: lite defaults, no google/bing templates, caps, overlay split, Debug-only tests. FAIL pre, PASS post. |
| `BrowserForWP.Core/Storage/AppSettings.vb` | Lite defaults, `NightMode`/`BlockTrackers=True`/`RestoreSession=False`/`LastSessionTabs`, legacy google/bing migration, `Get/SetSessionTabs` (cap 10, truncate 300). |
| `BrowserForWP.Core/Storage/HistoryStore.vb` | `MaxEntries` 200 → 100. |
| `BrowserForWP.Core/Storage/FavoritesStore.vb` | Cap 100 entries (`Add` returns False when full). |
| `BrowserForWP.Core/Browser/BrowserSession.vb` | `PrivateMode As Boolean = False` (session-only, never persisted). |
| `BrowserForWP.Core/Browser/TrackerBlocklist.vb` NEW | ~24-entry suffix list + `ShouldBlock(host)`. Pure. |
| `BrowserForWP.Core/Browser/ReadingMode.vb` NEW | ES5 extraction script constant (same inline precedent as `CompatibilityProbe.ProbeScript`). |
| `BrowserForWP.Core/Browser/NightMode.vb` NEW | CSS constant + `BuildScript(enabled)` (add/remove `bfwp-night` style). Pure. |
| `BrowserForWP.Core/Engine/TridentEngine.vb` | `FindInPageAsync`, `EnterReadingModeAsync`, `SetNightModeAsync`, private `EscapeJsString`. |
| `BrowserForWP.Net/Tls13/PinStore.vb` | Cap 50 pins (`Add` throws when full; `Parse` trims). Net/Tls13 untouched otherwise (prototype rule). |
| `BrowserForWP/MainPage.xaml` | Scrollable command bar + Find/Reading/Share buttons; FindBar overlay; 4 toggles; `DiagnosticsOverlay` split. |
| `BrowserForWP/MainPage.xaml.vb` | Share, find, reading, toggles, tracker block, night apply, history skip, session save/restore. |
| `BrowserForWP/Strings/*/Resources.resw` | 10 new keys, both languages: `ReadingMode NightMode Find Share PrivateMode BlockTrackers RestoreSession Diagnostics FindNoMatch BlockedTracker`. |
| `BrowserForWP.sln` | Drop `Release.*.Build.0` for both test GUIDs (tests build in Debug only). |

## Task 1 — failing checks first (commit: test)

`node tools/proto/trackerblock.mjs` → FAIL pre-fix (`TrackerBlocklist.vb` absent). Mirror cases: `doubleclick.net` blocked, `sub.doubleclick.net` blocked, `notdoubleclick.net` allowed, `example.com` allowed, empty allowed.
`node tools/proto/lightweight.mjs` → FAIL pre-fix (lite defaults absent, google/bing present, caps at old values, single overlay, Release test builds present).

## Task 2 — Core: defaults, caps, blocklist, scripts (commit: feat core)

AppSettings changes: `DefaultHomepage = "https://lite.duckduckgo.com/lite/"`, `DefaultSearchTemplate = "https://lite.duckduckgo.com/lite/?q={q}"`; new auto-properties `NightMode As Boolean`, `BlockTrackers As Boolean`, `RestoreSession As Boolean`, `LastSessionTabs As String`; constructor sets `BlockTrackers = True`; `LoadFromMap` migrates any stored template containing `google.` or `bing.` to the default and maps `homepage`/`searchTemplate`/`dohUrl` empties to defaults; new helpers:

```vb
Public Const MaxSessionTabs As Integer = 10

Public Function GetSessionTabs() As List(Of String)
    Dim result As New List(Of String)()
    If String.IsNullOrEmpty(LastSessionTabs) Then
        Return result
    End If
    Dim rawLines As String() = LastSessionTabs.Split(New String() {vbLf}, StringSplitOptions.None)
    For Each rawLine In rawLines
        Dim cleanLine As String = rawLine.Trim()
        If cleanLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
            result.Add(cleanLine)
        End If
        If result.Count >= MaxSessionTabs Then
            Exit For
        End If
    Next
    Return result
End Function

Public Sub SetSessionTabs(pageUrls As IList(Of String))
    Dim kept As New List(Of String)()
    If pageUrls IsNot Nothing Then
        For Each pageUrl In pageUrls
            If String.IsNullOrEmpty(pageUrl) Then
                Continue For
            End If
            Dim cleanUrl As String = pageUrl.Trim()
            If cleanUrl.Length > 300 Then
                cleanUrl = cleanUrl.Substring(0, 300)
            End If
            kept.Add(cleanUrl)
            If kept.Count >= MaxSessionTabs Then
                Exit For
            End If
        Next
    End If
    LastSessionTabs = String.Join(vbLf, kept.ToArray())
End Sub
```

`SaveToMap`/`LoadFromMap` gain `nightMode` (`1`/`0`), `blockTrackers`, `restoreSession`, `lastSessionTabs` keys with the same empty-guard style as existing keys. `SearchUrlFor` unchanged.

TrackerBlocklist.vb (full file):

```vb
' BrowserForWP — tiny on-device tracker host blocklist (pure, no WinRT).
' Covers top-level navigations only: WP8.1 WebView exposes no subresource
' filter API, so embedded trackers still load. Stated, not oversold.

Namespace Browser

    Public NotInheritable Class TrackerBlocklist

        Private Sub New()
        End Sub

        Private Shared ReadOnly BlockedSuffixes As String() = {
            "doubleclick.net", "google-analytics.com", "googlesyndication.com",
            "googletagmanager.com", "googletagservices.com", "googleadservices.com",
            "connect.facebook.net", "analytics.twitter.com", "static.ads-twitter.com",
            "ads.yahoo.com", "amazon-adsystem.com", "criteo.com", "criteo.net",
            "outbrain.com", "taboola.com", "scorecardresearch.com", "quantserve.com",
            "hotjar.com", "mixpanel.com", "segment.io", "amplitude.com",
            "newrelic.com", "moatads.com", "doubleverify.com", "adsrvr.org"
        }

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
```

ReadingMode.vb (full file): `Namespace Browser`, `Public NotInheritable Class ReadingMode` with `Public Const Script As String` = one VB string-concatenated ES5 IIFE: picks `document.querySelector('article')` else the `<div>/<section>/<main>` with the most `<p>` text, strips `script/style/nav/header/footer` descendants, swaps `document.body` to a 700px serif container, sets `document.__bfwpReading = 1`. Exits early (sets 0) when nothing article-like found.

NightMode.vb (full file): `Public Const StyleId As String = "bfwp-night"`, `Public Const Css As String` (dark rules), `Public Shared Function BuildScript(enabled As Boolean) As String` returning an eval string that adds (or removes) the style element.

HistoryStore: `MaxEntries` 200 → 100. FavoritesStore.Add: `If _items.Count >= 100 Then Return False` before insert. PinStore: `Public Const MaxPins As Integer = 50`; `Add` throws `InvalidOperationException("pin store full")` past cap; `Parse` stops adding past cap. BrowserSession: `Public Property PrivateMode As Boolean = False`.

Core vbproj: add `Browser\TrackerBlocklist.vb`, `Browser\ReadingMode.vb`, `Browser\NightMode.vb`.

Verify: `node tools/proto/trackerblock.mjs` → `trackerblock checks, 0 failure(s)`; `node tools/check-vb.mjs` → `0 finding(s)`.

## Task 3 — Engine runners (commit: feat engine)

TridentEngine additions (VB12-safe, never throw):

```vb
Public Async Function FindInPageAsync(searchTerm As String) As Task(Of Boolean)
    Try
        If String.IsNullOrEmpty(searchTerm) Then
            Return False
        End If
        Dim foundText As String = Await InvokeScriptAsync("window.find(""" & EscapeJsString(searchTerm) & """)")
        If String.IsNullOrEmpty(foundText) Then
            Return False
        End If
        Return foundText.Trim().ToLowerInvariant() = "true"
    Catch ex As Exception
        Return False
    End Try
End Function

Public Async Function EnterReadingModeAsync() As Task(Of Boolean)
    Try
        Dim markerText As String = Await InvokeScriptAsync(Browser.ReadingMode.Script)
        If String.IsNullOrEmpty(markerText) Then
            Return False
        End If
        Return markerText.Trim() = "1"
    Catch ex As Exception
        Return False
    End Try
End Function

Public Async Function SetNightModeAsync(enabled As Boolean) As Task(Of Boolean)
    Try
        Await InvokeScriptAsync(Browser.NightMode.BuildScript(enabled))
        Return True
    Catch ex As Exception
        Return False
    End Try
End Function

Private Shared Function EscapeJsString(rawText As String) As String
    If rawText Is Nothing Then
        Return String.Empty
    End If
    Dim cleanText As String = rawText.Replace(vbCr, " ").Replace(vbLf, " ")
    cleanText = cleanText.Replace("\", "\\")
    cleanText = cleanText.Replace("""", "\""")
    Return cleanText
End Function
```

(`Browser.ReadingMode` resolves via `BrowserForWP.Core.Browser` — TridentEngine has no such import; use the fully qualified `Core.Browser.ReadingMode.Script` since RootNamespace is `BrowserForWP.Core` and the file sits in `Namespace Engine`. Same for NightMode.)

Verify: `node tools/check-vb.mjs` → `0 finding(s)`. Handset: find highlights, reading/article swap, night toggle.

## Task 4 — Shell wiring + split (commit: feat shell)

MainPage.xaml: command-bar `StackPanel` wrapped in a horizontal `ScrollViewer`; add `FindButton`/`ReadingButton`/`ShareButton` (Click handlers); content `Border` wrapped with a `FindBar` Grid (`FindBox` with `KeyDown="FindBox_KeyDown"`, `FindNextButton`, `FindCloseButton`, `FindResult`); Settings overlay gains `PrivateModeToggle`/`NightModeToggle`/`RestoreSessionToggle`/`BlockTrackersToggle` (Checked+Unchecked) and `DiagnosticsButton`; new `DiagnosticsOverlay` grid hosts the moved engine/probe/DoH/pin section plus `DiagnosticsBackButton`. No control is renamed, so existing code-behind keeps working.

MainPage.xaml.vb: `Imports Windows.ApplicationModel.DataTransfer`; hook/unhook `DataTransferManager.GetForCurrentView().DataRequested` in `OnNavigatedTo/From`; launch block uses `GetSessionTabs()` when `RestoreSession` else homepage; `NavigateFromAddressBar` + `OnNavigationStarting` cancel blocked tracker hosts with the `BlockedTracker` string; `OnNavigationCompleted` skips history + session save when `PrivateMode`, applies night mode, saves capped session tabs; new handlers for find/reading/share/toggles/overlays; `ApplyLocalizedStrings` covers the 10 new keys; `_searchTemplates` becomes DDG html + DDG Lite.

resw: 10 keys in both files (en-US / it-IT): ReadingMode/Modalità lettura, NightMode/Modalità notte, Find/Trova, Share/Condividi, PrivateMode/Modalità privata, BlockTrackers/Blocca tracker, RestoreSession/Ripristina schede all'avvio, Diagnostics/Diagnostica, FindNoMatch/Non trovato, BlockedTracker/Tracker bloccato.

Verify: `node tools/proto/lightweight.mjs` → `0 failure(s)`; `node tools/check-vb.mjs` → `0 finding(s)`. Handset steps: lite homepage on fresh install, reading/night/find/share/private/block/restore each toggled once.

## Task 5 — Tests Debug-only (commit: chore)

`BrowserForWP.sln`: delete the six `Release.*.Build.0` lines for `{C0DE0004…}` and `{C0DE0005…}` (keep all `ActiveCfg` lines). Release solution builds skip the test libraries; Debug still compiles them.

Verify: `node tools/check-vb.mjs` → `0 finding(s)`; guest `tools\vm-build.cmd` Debug + Release → `BUILD_EXIT=0` (guest unavailable here — report honestly).

## Commits + push

1. `test(lightweight): add failing tracker and lite-default checks`
2. `feat(core): lite defaults, store caps, tracker blocklist, reading/night scripts`
3. `feat(engine): find, reading-mode and night-mode runners`
4. `feat(shell): private mode, session restore, find/share/reading, split diagnostics`
5. `chore(sln): build test projects in Debug only`
Then `git push origin HEAD` + `ls-remote` match check.
