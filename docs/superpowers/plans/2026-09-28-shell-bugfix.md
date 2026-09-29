# Shell bugfix round — Plan

**Goal:** Fix the four bugs found by the post-completion audit, all in code written last session, with a failing check first for each.

**Global Constraints (verbatim from the request and the platform):**
- Target: `TargetPlatformVersion 8.1`, `WindowsPhoneApp`, VB 12 / VS2013. Windows Phone 8.1 cannot host Chromium or Firefox, cannot replace Trident, and cannot exceed TLS 1.2 through the OS.
- No backend. Every byte of logic runs on-device. No `127.0.0.1` proxy feeding the WebView.
- API surface is ".NET for Windows Store apps": no `SHA256`/`HMACSHA256`/`RNGCryptoServiceProvider`, no `Encoding.ASCII`, no `RegexOptions.Compiled`, `VerifySignature` never `Verify`.
- VB 12: no implicit line continuation after `.`; use a `With` block. VB is case-insensitive: no local shadows a type, member, keyword, or `Page.Tag`.
- Never hand-edit `X25519.vb` or `BrowserForWP.Net/Tls13/` (none of these bugs live there; no prototype change needed).
- `en-US` is the default and fallback language. No new user-visible strings in this round, so no resw change.
- Layer discipline holds: all fixes are in `BrowserForWP/` (app) and `BrowserForWP.sln`.

## File Structure

| File | Why |
|---|---|
| `tools/proto/shell-guards.mjs` (NEW) | Failing-first check: simulates the picker/tabs event-loop model and asserts the VB guards exist. Must FAIL before the fix, PASS after. |
| `BrowserForWP/MainPage.xaml.vb` | Bugs 1-3 and 5: `e.Uri` for the completed URL; `_populatingLanguage` / `_refreshingTabs` re-entrancy guards; clear stale history/favorites selection. |
| `BrowserForWP.sln` | Bug 4: register both test projects so the guest build compiles them instead of silently ignoring them. |

## Diagnosis (evidence, not speculation)

- **Bug 1:** `OnNavigationCompleted` reads `Dim pageUrl As String = _engine.Source.ToString()`. `Source` returns the hosted `WebView` control object, so `ToString()` yields the type name, never the URL. Address bar, history store, and tab list all record garbage. The handler already receives `e As WebViewNavigationCompletedEventArgs`, which carries `e.Uri`.
- **Bug 2:** `PopulateLanguagePicker` calls `Items.Clear()` then sets `SelectedIndex`, both of which raise `SelectionChanged`. The handler calls `Override` + `ApplyLocalizedStrings` + `PopulateLanguagePicker` again: re-entrant, wipes a stored override with `Override(Nothing)` on the transient `-1` selection.
- **Bug 3:** `RefreshTabsList` sets `TabsList.SelectedIndex`, which raises `TabsList_SelectionChanged`, which calls `ActivateTab` + `_engine.Navigate` + `RefreshTabsList`: spurious navigations and a refresh/navigate loop.
- **Bug 4:** `tests/BrowserForWP.Core.Tests/` and `tests/BrowserForWP.Crypto.Tests/` have `.vbproj` files but are absent from `BrowserForWP.sln`, so `tools\vm-build.cmd` never compiles them.
- **Bug 5 (minor):** `HistoryList`/`FavoritesList` keep their selection, so tapping the same entry twice navigates only once. Reset to `-1` after handling; `-1` re-fires the handler, which returns early.

## Task 1 — failing checks first

Create `tools/proto/shell-guards.mjs`. It asserts on file content (the only executable signal available off-device for XAML-bound code):
1. `MainPage.xaml.vb` contains `_populatingLanguage` and `_refreshingTabs`.
2. `OnNavigationCompleted` uses `e.Uri` and no longer calls `_engine.Source.ToString()`.
3. A simulation of the unguarded populate-then-select loop exceeds 1 handler invocation; the guarded model performs exactly 1.
4. `BrowserForWP.sln` contains both `BrowserForWP.Core.Tests` and `BrowserForWP.Crypto.Tests`.

```bash
node tools/proto/shell-guards.mjs
# Expected BEFORE the fix: FAIL — missing guard: _populatingLanguage (and 3 more)
```

## Task 2 — fix the completed-URL source (Bug 1)

In `BrowserForWP/MainPage.xaml.vb`, `OnNavigationCompleted`:

```vb
Private Async Sub OnNavigationCompleted(sender As WebView, e As WebViewNavigationCompletedEventArgs)
    If _navigationToken Is Nothing Then Return
    _navigationToken = Nothing

    LoadProgress.Value = 100
    Dim pageUrl As String = e.Uri.ToString()
    _session.ActiveTab.ReplaceCurrent(pageUrl)
    AddressBox.Text = _session.ActiveTab.Url
```

Expected: address bar, history, and tab list record the navigated URL. Manual handset step: navigate to `https://example.com/`, confirm the address bar shows it.

## Task 3 — guard the picker and tab list (Bugs 2, 3, 5)

Add two fields next to `_navigationToken`:

```vb
''' <summary>True while pickers/lists are repopulated, so programmatic selection is ignored.</summary>
Private _populatingLanguage As Boolean = False
Private _refreshingTabs As Boolean = False
```

Guard `PopulateLanguagePicker`:

```vb
Private Sub PopulateLanguagePicker()
    _populatingLanguage = True
    Try
        LanguagePicker.Items.Clear()
        LanguagePicker.Items.Add(Localizer.Get("LanguageAutomatic"))
        For Each supportedTag As String In LanguageCatalog.Supported
            LanguagePicker.Items.Add(LanguageCatalog.DisplayName(supportedTag))
        Next
        LanguagePicker.SelectedIndex = If(Localizer.IsOverridden, 1, 0)
    Finally
        _populatingLanguage = False
    End Try
End Sub
```

Early-return in `LanguagePicker_SelectionChanged`:

```vb
Private Sub LanguagePicker_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
    If _populatingLanguage Then
        Return
    End If
```

Guard `RefreshTabsList` and `TabsList_SelectionChanged` identically with `_refreshingTabs`, and reset `HistoryList.SelectedIndex = -1` / `FavoritesList.SelectedIndex = -1` at the end of their handlers (the resulting `-1` event returns early).

Expected: opening Settings no longer resets the language; refreshing tabs performs zero navigations. Handset step: open/close Settings, confirm language persists and no reload occurs.

## Task 4 — register the test projects (Bug 4)

In `BrowserForWP.sln`, after the `BrowserForWP.Net` project line:

```
Project("{BC8A1FFA-BEE3-4634-8014-F334798102B3}") = "BrowserForWP.Core.Tests", "tests\BrowserForWP.Core.Tests\BrowserForWP.Core.Tests.vbproj", "{C0DE0004-0004-4A2B-9C3D-1B2C3D4E5F04}"
EndProject
Project("{BC8A1FFA-BEE3-4634-8014-F334798102B3}") = "BrowserForWP.Crypto.Tests", "tests\BrowserForWP.Crypto.Tests\BrowserForWP.Crypto.Tests.vbproj", "{C0DE0005-0005-4A2B-9C3D-1B2C3D4E5F05}"
EndProject
```

And in `GlobalSection(ProjectConfigurationPlatforms)`, after the Net block, one `ActiveCfg` + `Build.0` line per solution configuration per new GUID (6 configs each, same shape as the other library projects, no `Deploy` lines):

```
{C0DE0004-0004-4A2B-9C3D-1B2C3D4E5F04}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
{C0DE0004-0004-4A2B-9C3D-1B2C3D4E5F04}.Debug|Any CPU.Build.0 = Debug|Any CPU
...
```

Expected: guest solution build compiles both test libraries. Off-device signal: `node tools/check-vb.mjs` still `0 finding(s)` and the `.sln` parses (VS opens it).

## Verification matrix

- `node tools/proto/shell-guards.mjs` → `shell-guard checks, 0 failure(s)` (was FAIL before).
- `node tools/check-vb.mjs` → `0 finding(s)`, exit 0 (covers edited `.vb`, `.sln` comment scan, XAML wiring).
- `node tools/gen-vectors.mjs --check` → `52 assertions, 0 failure(s)` (untouched, regression floor).
- Guest `tools\vm-build.cmd /t:Rebuild` → `BUILD_EXIT=0` (cannot run here; must be reported honestly, not claimed).
- Handset steps in Tasks 2-3 (cannot run here; must be reported honestly, not claimed).

## Commits

1. `test(shell): add failing guards for picker/tabs loops, completed URL and sln` (plan + script, script fails).
2. `fix(shell): use completed navigation URI, guard picker/tabs re-entrancy` (MainPage fix, script passes).
3. `chore(sln): build both test projects in the solution` (sln fix, script passes).
Then `git push origin HEAD` and confirm `git ls-remote origin refs/heads/main` matches `git rev-parse HEAD`.
