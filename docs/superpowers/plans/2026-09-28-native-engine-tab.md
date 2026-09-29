# The native engine as a real tab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make this repository's own rendering engine reachable the way every
other page is — type a URL in the address bar, press Go, and see the page
rendered by `BlockLayout`/`XamlBoxRenderer` in `ContentHost` — instead of a
320-pixel preview behind a Diagnostics button.

**Architecture:** `NativeEngine` implements the existing `IBrowserEngine` seam in
the app project, so the address bar, the tab list, the history store and the
hardware Back button all drive it unchanged. Engine selection is a Settings
choice with an automatic fallback, decided by a pure function in Core so it can
be asserted off-device. The shell branches on `EngineCapabilities`, never on
engine identity, except at the one place which must: event wiring.

**Tech Stack:** VB.NET 12 / VS2013, WinRT XAML, Windows Phone 8.1; Node `.mjs`
referees under `tools/proto/`; the ARM64 Parallels guest for the real build.

## Global Constraints

- `Option Strict On` in every library project; the app project is `OptionStrict
  Off`. Core code must compile under `Option Strict On`.
- No leading-dot fluent chaining (VB 12). No local may shadow a type, member or
  keyword. No `.AsReadOnly()`, `Encoding.ASCII`, `RegexOptions.Compiled`,
  `ControlChars`, `FontStyles` in Core/app code (`tools/check-vb.mjs` groups 12
  and 15).
- No English sentence may be produced by Core: user-facing text is a resource
  key resolved through `Localizer`, in both `en-US` and `it-IT`.
- Every `.vb` added to a project ends with a guest rebuild, not merely a
  prototype run (`docs/MAINTAINING.md`, Round 7).
- Anything under `BrowserForWP.Net/Tls13/` or `X25519.vb` is changed in
  `tools/proto/` first. This plan touches neither.

## Two design decisions, and why

**1. `EngineCapabilities` gains `SupportsScripting`, and `NeedsPolyfillLayer`
stops being `Not SupportsModernJavaScript`.**

That expression is wrong for an engine with no script host at all, which is
exactly what the native engine is. Today it would answer `True` and the shell
would try to inject `compat.js` into an engine that has no `window` — a silent
no-op at best. The honest model has three shapes:

| Engine | `SupportsScripting` | `SupportsModernJavaScript` | `NeedsPolyfillLayer` |
| --- | --- | --- | --- |
| Trident (IE11) | True | False | **True** |
| A modern engine | True | True | False |
| The native engine | False | False | **False** |

This is what lets the shell gate the polyfill, night mode and reading mode on
capability data rather than on `If engineIsNative`, which is the whole point of
having a seam.

**2. Engine lifecycle stays off the interface for this round.**

`IBrowserEngine` has no events, and today `MainPage` obtains them by casting:
`DirectCast(_engine, TridentEngine).View.NavigationStarting`. Clean would be to
put `NavigationStarting`/`NavigationCompleted` on the interface and re-raise them
in `TridentEngine`. That is a larger diff across every call site for no
user-visible gain in this round, so it is **deferred and recorded** rather than
done: the native engine exposes its own events and `MainPage` wires whichever
engine it built. The cost is stated honestly — the shell knows two engine types
at exactly one site, and `docs/ARCHITECTURE.md`'s claim that the engine is "a
configuration detail instead of an assumption baked into every call site" is
still true of *behaviour* and newly false of *construction*. Recorded in Task 4.

## File Structure

| File | Responsibility |
| --- | --- |
| `BrowserForWP.Core/Engine/EngineChoice.vb` (new) | Pure selection + fallback rule. No XAML, no strings, no I/O. |
| `BrowserForWP.Core/Engine/IBrowserEngine.vb` (modify) | `SupportsScripting`; corrected `NeedsPolyfillLayer`. |
| `BrowserForWP.Core/Engine/TridentEngine.vb` (modify) | Reports `SupportsScripting = True`. |
| `BrowserForWP/Engine/NativeEngine.vb` (new) | The engine: fetch over the app's TLS 1.3 transport, build, lay out, render, report. |
| `BrowserForWP/MainPage.xaml` (modify) | Engine picker in Settings; Diagnostics loses the preview host. |
| `BrowserForWP/MainPage.xaml.vb` (modify) | Mutable engine, per-engine wiring, capability-gated features. |
| `BrowserForWP/Strings/{en-US,it-IT}/Resources.resw` (modify) | The picker's copy and the reason keys. |
| `tools/proto/engine-choice.mjs` (new) | The executable referee for `EngineChoice`. |
| `tools/proto/core-logic.mjs`, `tests/.../CoreLogicTests.vb` (modify) | The compiled half of the same assertions, plus the three engine shapes. |

---

### Task 1: The engine-choice rule, with a referee

**Files:**
- Create: `BrowserForWP.Core/Engine/EngineChoice.vb`
- Modify: `BrowserForWP.Core/Engine/IBrowserEngine.vb`, `BrowserForWP.Core/Engine/TridentEngine.vb`
- Create: `tools/proto/engine-choice.mjs`
- Modify: `tools/proto/core-logic.mjs`, `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb`

**Interfaces:**
- Produces: `EngineChoice.Trident/Native/Auto` (String constants),
  `EngineChoice.AutomaticFallbackThreshold` (Integer = 8),
  `EngineChoice.Normalize(setting As String) As String`,
  `EngineChoice.Decide(setting As String, probeMeasured As Boolean,
  missingFeatureCount As Integer) As String`,
  `EngineChoice.Explain(choice As String) As String` (returns a **resource key**),
  `EngineCapabilities.SupportsScripting As Boolean`.

- [ ] **Step 1: Write the failing referee**

`tools/proto/engine-choice.mjs`, in the `check(name, ok)` idiom of
`tools/proto/ie-adapt.mjs`, asserting at least: `Decide("native", …) = "native"`
regardless of the probe; `Decide("trident", …) = "trident"`; `Decide("auto",
False, 0) = "trident"` (**an absent measurement is never grounds for switching**);
`Decide("auto", True, 7) = "trident"`; `Decide("auto", True, 8) = "native"`;
`Decide("garbage", True, 8) = "native"`; `Explain` returns a key, never a
sentence containing a space; and `Normalize("") = Auto`.

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/engine-choice.mjs`
Expected: every check fails, exit 1.

- [ ] **Step 3: Implement the rule and the capability flag**

`EngineChoice.vb` under `Option Strict On`, `Namespace Engine`, `Public
NotInheritable Class EngineChoice` with `Private Sub New()` — an
uninstantiable holder, like `LiteRedirects`. `Decide` reads in one direction
only: setting first, then the probe, and it treats an unmeasured probe as
Trident.

In `IBrowserEngine.vb`, add `SupportsScripting As Boolean` above the existing
`SupportsModernJavaScript`, and change `NeedsPolyfillLayer` to
`Return SupportsScripting AndAlso Not SupportsModernJavaScript`. In
`TridentEngine.vb`, add `.SupportsScripting = True` to the `EngineCapabilities`
initialiser.

- [ ] **Step 4: Run the referee to verify it passes**

Run: `node tools/proto/engine-choice.mjs`
Expected: `N/N engine-choice checks passed.`

- [ ] **Step 5: Mirror the assertions into the compiled and executable halves**

Add to `tools/proto/core-logic.mjs` (before the summary comment) and to
`CoreLogicTests.vb` (after the measurer checks) the same four decisions plus the
three engine shapes of the table above, asserting
`NeedsPolyfillLayer = False` for the no-scripting engine. Increment the VB `ran`
counter once for the block.

- [ ] **Step 6: Run both halves**

Run: `node tools/proto/core-logic.mjs && node tools/check-vb.mjs`
Expected: `0 failure(s)` with the count moved by four, and `0 finding(s)`.

- [ ] **Step 7: Commit**

```bash
git add BrowserForWP.Core/Engine/EngineChoice.vb BrowserForWP.Core/Engine/IBrowserEngine.vb \
        BrowserForWP.Core/Engine/TridentEngine.vb tools/proto/engine-choice.mjs \
        tools/proto/core-logic.mjs tests/BrowserForWP.Core.Tests/CoreLogicTests.vb
git commit -m "feat(engine): decide which engine renders, and fix the polyfill flag"
```

---

### Task 2: `NativeEngine` implements the seam

**Files:**
- Create: `BrowserForWP/Engine/NativeEngine.vb`
- Modify: `BrowserForWP/BrowserForWP.vbproj`
- Modify: `docs/MAINTAINING.md` (the `NativeEngine` deferred item closes)

**Interfaces:**
- Consumes: `IDocumentFetcher`, `BoxTreeBuilder.BuildPage`, `BlockLayout.Layout`,
  `XamlTextMeasurer`, `XamlBoxRenderer.Render`, `Localizer`.
- Produces: `NativeEngine As IBrowserEngine` with
  `Sub New(fetcher As IDocumentFetcher, dohUrl As String)`,
  `Event Navigated As EventHandler(Of NativeNavigationResult)`,
  `ReadOnly Property CurrentUrl As String`,
  `ReadOnly Property LastError As String`,
  `ReadOnly Property LastSize As String`, and
  `Async Function RenderAsync(url As String) As Task(Of Boolean)`.

- [ ] **Step 1: Write the engine

`Public NotInheritable Class NativeEngine Implements IBrowserEngine`, namespace
`BrowserForWP.Engine`. `Source` returns one `ScrollViewer` created once and
re-filled on each render, so the host control is stable across navigations.
`Capabilities` reports `.Name = "BrowserForWP native"`,
`.RenderingEngine = "BlockLayout + XamlBoxRenderer"`, `.SupportsTls13 = True`
(it is the only engine in this product that may claim it — the fetch goes
through `BrowserForWP.Net`), `.SupportsModernJavaScript = False`,
`.SupportsScripting = False`, `.SupportsWebSocket = False`, `.SupportsFetch =
False`. `InvokeScriptAsync` returns `String.Empty` and must never be called;
`GoBack`/`GoForward`/`Reload` re-render `CurrentUrl`; `[Stop]` is a no-op with a
comment saying why (there is no in-flight render to cancel — the fetch is not
cancellable either, and pretending otherwise would be the lie this repository
avoids). Viewport width comes from the `ScrollViewer`'s `ActualWidth` with a 360
fallback, exactly as the Diagnostics preview does today.

- [ ] **Step 2: Declare it in the project file**

Add `<Compile Include="Engine\NativeEngine.vb" />` immediately after
`<Compile Include="Diagnostics\NetDocumentFetcher.vb" />`.

- [ ] **Step 3: Rebuild — this task adds a `.vb` to a project**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
  "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild /p:Configuration=Debug /p:Platform=ARM"
```
Expected: `=== BUILD_EXIT=0 ===`, no `error BC`, only the two `ResourceLoader`
warnings.

- [ ] **Step 4: Commit**

```bash
git add BrowserForWP/Engine/NativeEngine.vb BrowserForWP/BrowserForWP.vbproj
git commit -m "feat(engine): a NativeEngine that answers the IBrowserEngine seam"
```

---

### Task 3: The shell drives it

**Files:**
- Modify: `BrowserForWP/MainPage.xaml`, `BrowserForWP/MainPage.xaml.vb`
- Modify: `BrowserForWP/Strings/{en-US,it-IT}/Resources.resw`

**Interfaces:**
- Consumes: `EngineChoice.Decide`/`Explain`, `NativeEngine`.
- Produces: `RefreshEngineUI()`, `ApplyEngineChoice()` —
  and no other page-visible surface.

- [ ] **Step 1: The picker, and the preview's removal**

In `MainPage.xaml`, add to the Settings panel, immediately after the
`SearchEnginePicker`'s label block: `EngineLabel` (TextBlock) and `EnginePicker`
(ComboBox, `Width="280"`, `SelectionChanged="EnginePicker_SelectionChanged"`).
Remove `RenderNativeButton` and `NativePreviewHost` from the Diagnostics panel;
leave `ParsePageButton`/`ParseResult` alone, because parsing a page and rendering
one are different diagnostics and only the second is now a product path.

- [ ] **Step 2: Mutable engine, one wiring site**

Replace `Private ReadOnly _engine As IBrowserEngine = New TridentEngine()` with a
mutable `_engine`, a `_tridentEngine`, a `_nativeEngine`, and:

```vb
Private Sub UseEngine(choice As String)
    If choice = EngineChoice.Native Then
        If _nativeEngine Is Nothing Then
            _nativeEngine = New BrowserForWP.Engine.NativeEngine(
                New BrowserForWP.Diagnostics.NetDocumentFetcher(_pinTable), _appSettings.DohUrl)
            AddHandler _nativeEngine.Navigated, AddressOf OnNativeNavigated
        End If
        _engine = _nativeEngine
    Else
        If _tridentEngine Is Nothing Then
            _tridentEngine = New TridentEngine()
        End If
        _engine = _tridentEngine
    End If
    ContentHost.Child = DirectCast(_engine.Source, UIElement)
End Sub
```

`OnNavigatedTo` calls `ApplyEngineChoice()` before the first `Navigate`. The
Trident event handlers are wired only when `_tridentEngine` is created, inside
this same `Sub` — one site, on purpose.

- [ ] **Step 3: Gate the Trident-only features on capability data**

`OnDOMContentLoaded`, the polyfill calls in `OnNavigationCompleted`, night mode,
the reading-mode fallback and the compatibility probe all become
`If _engine.Capabilities.SupportsScripting Then` bodies. `OnNativeNavigated`
reports progress, the final URL, the error text and the security glyph through
the *same* helpers the Trident path uses (`ReplaceCurrent`, `AddressBox`,
`StatusText`, `ErrorText`, `UpdateSecurityGlyph`), so a page rendered natively
leaves the shell in the same state a page rendered by Trident does.

- [ ] **Step 4: The copy, in both languages**

Add to both `.resw` files, in the same order: `EngineLabel`, `EngineAuto`,
`EngineTrident`, `EngineNative`, and one reason key per `EngineChoice.Explain`
return value (`EngineReasonSetting`, `EngineReasonAutoFits`,
`EngineReasonAutoNoMeasurement`). Italian and English both; `check-vb.mjs`
group 6 fails on any key that exists in one and not the other.

- [ ] **Step 5: Rebuild — this task adds no `.vb`, but changes one**

Run the four configurations plus the two `AnyCPU` app-project builds exactly as
`docs/MAINTAINING.md` step 4 documents them (the `Any CPU` pair cannot cross
`prlctl exec`, whose argument splitting eats the space).
Expected: `BUILD_EXIT=0` six times.

- [ ] **Step 6: Commit**

```bash
git add BrowserForWP/MainPage.xaml BrowserForWP/MainPage.xaml.vb \
        BrowserForWP/Strings/en-US/Resources.resw BrowserForWP/Strings/it-IT/Resources.resw
git commit -m "feat(shell): the native engine is an engine you can pick, not a preview"
```

---

### Task 4: Record it

**Files:**
- Modify: `docs/MAINTAINING.md`, `.agents/skills/browserforwp/SKILL.md`,
  `docs/superpowers/plans/2026-09-28-native-engine-tab.md` (this file),
  `docs/ARCHITECTURE.md`

- [ ] **Step 1: A Round 8 in `MAINTAINING.md`**, with the measured numbers, the
  two design decisions above, and the new deferred items: engine lifecycle not on
  the seam; no cancellation; `SupportsScripting` now meaningful and Trident's
  reading-mode fallback unchanged.
- [ ] **Step 2: Correct `docs/ARCHITECTURE.md`.** Its claim that the engine is a
  configuration detail is true of behaviour and false of construction from this
  round on. Say so where the claim is made, rather than leaving a sentence the
  code no longer earns.
- [ ] **Step 3: The verification table in `SKILL.md`** gains the
  `engine-choice.mjs` row, and the file listing gains the engine and the proto.
- [ ] **Step 4: Append this plan's Outcome section** — the check counts, the
  first `BUILD_EXIT` lines, and every place this plan was wrong. Append; do not
  rewrite.
- [ ] **Step 5: Commit and push**

```bash
git add docs/MAINTAINING.md docs/ARCHITECTURE.md .agents/skills/browserforwp/SKILL.md \
        docs/superpowers/plans/2026-09-28-native-engine-tab.md
git commit -m "docs: record the round that gave the native engine a tab"
git push origin HEAD
```

---

## Verification matrix

| Layer touched | Command | Expected |
| --- | --- | --- |
| Engine choice | `node tools/proto/engine-choice.mjs` | all checks passed, exit 0 |
| Capability shapes | `node tools/proto/core-logic.mjs` | `0 failure(s)`, four more assertions |
| Any `.vb` / `.xaml` / `.resw` | `node tools/check-vb.mjs` | `0 finding(s)`, exit 0 |
| Crypto, layout, probes | `gen-vectors.mjs`, `boxlayout.mjs`, `textmeasure.mjs`, `sandbox-escape.mjs`, `ie-adapt.mjs`, `probe-verdict.mjs` | unchanged counts |
| Every `.vb` | `tools\vm-build.cmd /t:Rebuild`, six configurations | `BUILD_EXIT=0`, no `error BC` |
| On a handset (cannot run here) | Settings → engine picker → *native*, then a real URL | a page drawn in `ContentHost`; record the surprises in `MAINTAINING.md` |

**What this plan does not do**, stated so it cannot be mistaken for a regression:
no JavaScript of any kind, no `float`/`position`, no collapsed margins, no
images, no tables, no `EngineCapabilities` claim that is not measured, and no
change to `BrowserForWP.Net` or the polyfill. The native engine renders what
`BlockLayout` can lay out, and this round makes that reachable — it does not make
it a browser.

## Self-Review

**Spec coverage.** The request has three parts and each has a task: *a real tab*
(Task 3, `ContentHost` and the tab list, not a Border in Diagnostics), *with an
address bar* (Task 3, `AddressBox`/`GoButton` through `_engine.Navigate`), *not a
preview* (Task 3 Step 1 removes `NativePreviewHost` and `RenderNativeButton`).
Task 2 is what makes the engine exist in the first place; Task 1 is the rule that
decides when anyone sees it.

**Placeholder scan.** No `TBD`, no "add error handling", no "similar to Task N".
Task 3 states its new members as complete code and its edits as exact anchors by
name, which is this repository's convention for edits to a 1030-line file whose
line numbers have moved twice already.

**Type consistency.** `EngineChoice.Decide` has one signature, quoted verbatim by
`engine-choice.mjs` and by `CoreLogicTests.vb`; `SupportsScripting` is added to
`EngineCapabilities` in Task 1 and read in Task 3 but written nowhere else;
`NativeEngine.Navigated`'s payload type is `NativeNavigationResult`, named once;
`EngineChoice.Explain` returns resource keys, and Task 3 Step 4 defines exactly
the keys that Task 1's `Explain` can return.

---

## Outcome — what executing this plan actually produced

Appended, not rewritten: the plan above is what was planned, and this is what it
cost. Every number below was measured.

### Per task, the counts

| Task | Referee | RED | GREEN | Commit |
| --- | --- | --- | --- | --- |
| 1 — the choice rule | `node tools/proto/engine-choice.mjs` | 16/21 | 21/21 | `659bdcf` |
| 2 — the engine | `node tools/proto/boxtree.mjs` | 45/48 | 48/48 | `b4f1fcb` |
| 3 — the shell | the guest build, six configurations | `BUILD_EXIT=1` | `BUILD_EXIT=0` ×6 | `ae99341` |
| 4 — the record | this section | — | — | *this commit* |

`core-logic.mjs` moved 55 → 62 assertions, `check-vb.mjs` 73 → 75 groups with 0
findings throughout, and the guest build ran after **every** task, which is the
rule Round 7 earned and the reason Task 1's two red commits never existed here.

### Where the plan was wrong

**1. It sent the engine a DoH URL instead of the settings object.** The plan's
interface line reads `Sub New(fetcher As IDocumentFetcher, dohUrl As String)`.
`NativeEngine` takes `AppSettings` and reads `DohUrl` at fetch time, so a DoH
server typed into Settings applies to the next fetch instead of to the next app
launch. Flattening it to a string would have made the setting appear broken.

**2. `Explain(choice)` could not explain.** A reason computed from the chosen
engine alone cannot tell "you chose this" from "Automatic decided this", which
are different sentences. It takes the same three inputs as `Decide`. The plan's
Self-Review claimed the Task 1 and Task 3 keys matched; the signature had to
change for them to match for the right reason.

**3. The plan's own roadmap, and the plan, disagreed — and the roadmap was
wrong.** `2026-09-29-native-engine-pipeline.md` says the automatic fallback fires
when `CompatibilityProbe.CouldRun` is `False`. `EngineChoice` exists precisely to
refuse that: an absent measurement must never move an engine. The implemented
rule is `CouldRun = True AndAlso MissingFeatures.Count >= 8`, and the roadmap is
corrected in `docs/MAINTAINING.md` rather than left to be re-derived.

**4. The plan's file list was incomplete.** It never names
`AppSettings.vb`, but the setting has to persist somewhere and that is where
`SaveToMap`/`LoadFromMap` live. Task 3's list should have carried it. It also
missed that the property cannot be called `EngineChoice` — VB is
case-insensitive, so a member of that name shadows the type and every
`EngineChoice.Auto` under it would silently become a String.

**5. Two things only the compiler could see.** `InlineStyleText` had a *second*
call site, in the parse handler, which the plan did not know about and grep found
only because the function had just been deleted. And `ApplyLocalizedStrings`
still set the `Content` of the button whose XAML was deleted in Step 1:
`BC30451`, reported against the page rather than the handler. Four subsystems had
to agree for this round — Core, the engine, the XAML and two resource files — and
`check-vb.mjs` passed on all four before the build failed.

**6. Moving a helper broke a referee, which is the referee working.**
`boxtree.mjs` asserted `main.includes('InlineStyleText')`. That assertion follows
the move to `BoxTreeBuilder.PageCss` rather than being deleted, and the plan did
not anticipate either the move or the fix.

### What was verified, and what was not

Verified: `engine-choice.mjs` 21/21; `boxtree.mjs` 48/48 including six new
`PageCss` cases; `core-logic.mjs` 62 assertions, 0 failures; `check-vb.mjs` 75
groups, 0 findings; the guest build at `BUILD_EXIT=0` for `Debug|ARM`,
`Debug|x86`, `Release|ARM`, `Release|x86` and both app-project `AnyCPU` builds,
with only the two deliberate `ResourceLoader` warnings.

**Not verified: everything a user would see.** Nothing in this round has run on a
handset, and the engine picker has never been rendered. The choice rule is
executed, the fetch and layout path was exercised by pre-existing protos, and the
integration is asserted by *nothing at all* — no check predicts that picking
"BrowserForWP native" and typing a URL produces a page. The first handset session
is the beginning of that verification, and its surprises belong in
`docs/MAINTAINING.md`, deferred items 11–14 being the ones already known.
