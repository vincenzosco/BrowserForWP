# Native Engine — Document Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the front half of a from-scratch rendering engine for Windows Phone 8.1 — fetch a real URL over the app's own TLS 1.3 transport, parse its HTML and CSS, resolve the cascade, and produce a box tree — then make it visible on the device through a diagnostics view.

**Architecture:** A pipeline of pure functions in `BrowserForWP.Core`, each transliterated from an executable Node prototype in `tools/proto/`, because that mirror is the only harness that actually runs here. HTML tokenizer → tree builder → CSS parser → style resolver → box tree. Fetching is the one impure step: Core declares `IDocumentFetcher` and the app layer implements it over `BrowserForWP.Net`'s `HttpClient13`/`DohResolver`, so a page load finally travels over the TLS 1.3 stack this project already ships — which the `WebView` can never do, because it goes through Schannel and stops at TLS 1.2. Layout and XAML rendering are Phase 2, deliberately a separate plan.

**Tech Stack:** VB 12 / VS2013, Windows Phone 8.1 (`TargetPlatformVersion 8.1`), WinRT, `StreamSocket`, `Windows.Security.Cryptography.Core`; Node.js for the prototypes and mirrors.

## Global Constraints

Copied verbatim from the platform, the repository's own laws, and the standing request. Every task below implicitly includes all of them.

- Target: `TargetPlatformVersion 8.1`, `WindowsPhoneApp`, **VB 12 / VS2013**. No leading-dot fluent chains (VB 14+ only); use a `With` block. VB is case-insensitive: no local may shadow a type, member, keyword, or `Page.Tag`.
- **The three platform laws.** Windows Phone 8.1 cannot host Chromium or Firefox, cannot replace Trident, and cannot exceed **TLS 1.2 through the OS**. Anything in this plan that assumes otherwise is a plan error.
- **No backend.** Every byte of logic runs on-device. No `127.0.0.1` proxy feeding the `WebView` (AppContainers block loopback).
- **API surface:** ".NET for Windows Store apps". No `SHA256` / `HMACSHA256` / `RNGCryptoServiceProvider`, no `Encoding.ASCII`, no `RegexOptions.Compiled`, no `List(Of T).AsReadOnly()` (`ReadOnlyCollection(Of T)` is absent — `BC30456`). `CryptographicEngine.VerifySignature`, never `Verify`.
- **Layer discipline:** Crypto knows nothing about TLS; TLS knows nothing about the UI; **Core knows nothing about crypto or `BrowserForWP.Net`**. This is why fetching is an interface here and an implementation in the app project.
- `en-US` is the default and fallback language. **Every** user-visible string goes in both `BrowserForWP/Strings/en-US/Resources.resw` and `it-IT/Resources.resw`. No hardcoded English in `.vb`.
- Only one cipher suite ships: `TLS_AES_128_GCM_SHA256`. `HttpClient13` speaks HTTP/1.1 with `Content-Length` or chunked. **It does not send `Accept-Encoding` and cannot decompress `gzip`** — do not add the header until a decompressor exists, or every page will arrive as binary.
- Never hand-edit `X25519.vb` or `BrowserForWP.Net/Tls13/` without changing the prototype first.
- `tools/check-vb.mjs` enforces that **every `.vb` on disk is declared in its `.vbproj`** and that resw keys have en/it parity. It is a static checker, not a compiler: the guest build is the arbiter.
- The two `tests/` projects **compile but nothing executes them.** The executable referee for logic is the Node mirror. Keep mirror and VB in step.

### Two rules about writing these checks

Both were learned while validating this plan by running every prototype in it, before writing a line of VB:

1. **A negative assertion on a missing file passes vacuously.** `!/Accept-Encoding/.test('')` is `true`, so "the fetcher sends no Accept-Encoding" went green against a file that did not exist. Every script here therefore asserts that the file exists **and is non-empty** before asserting anything *about* it. Delete that guard and the check becomes decoration.
2. **The referee must be able to fail for the right reason.** Every script's expected state before implementation is listed explicitly, with the exact failing lines, so an implementer can tell "not written yet" apart from "written wrong".

---

## Scope: what this engine is, and what it will never be

This must be stated before any code, because the honest answer is the whole point of this repository.

**A from-scratch engine cannot be a modern browser engine.** Chromium, Gecko and WebKit are millions of lines and two decades of work. What *is* buildable, useful, and truthful on this platform is a **document renderer**: it fetches a page, parses a declared subset of HTML and CSS, and lays out readable text with correct box geometry. No JavaScript. That is a real capability — it is the only way this app can load *any* page over TLS 1.3, and it renders documents that Trident chokes on.

Declared subset for this plan — the plan is where the contract lives:

- **HTML:** `html head title meta body p div span h1..h6 a ul ol li blockquote pre code em strong b i br hr img article section header footer nav main aside figure figcaption noscript`. `script` and `style` are recognised and skipped (raw text, never executed, never document text). Tables, forms and frames are **out of scope and reported as such**.
- **CSS:** type, `.class`, `#id` selectors; descendant and child combinators; specificity; cascade; inheritance. Properties: `color background-color font-size font-weight font-style font-family line-height text-align text-decoration margin* padding* border* width height max-width display list-style-type`. Units `px em rem pt %` plus `auto`.
- **Never:** JavaScript execution of any kind, ES6 syntax, `Proxy`, `Intl`, CSS grid/flexbox, webfonts, `position:fixed/sticky`, animations. The engine reports each as unsupported rather than silently producing a wrong page.

**"Adapt another engine, like Internet Explorer"** is Task 1: closed with evidence, not with an opinion. Trident *is* the engine the platform provides, the app already injects a compatibility layer into it, and there is no API on WP8.1 to change its document mode, enable or disable its features, or reach MSHTML from a WinRT app. Task 1 ships the instrument that lets a handset confirm or refute that, because this repository does not accept "cannot be done" without having tried it.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `BrowserForWP.Core/Engine/Native/NodeTypes.vb` | Shared value types: `HtmlToken`, `HtmlAttribute`, `HtmlElement`, `CssSimpleSelector`, `CssSelectorPart`, `CssSelector`, `StyleDeclaration`, `CssRule`, `Stylesheet`, `ComputedStyle`, `BoxKind`, `BoxNode`. Types only, no logic — the contract the pipeline stages agree on. |
| `BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb` | Bytes of HTML → flat token list. |
| `BrowserForWP.Core/Engine/Native/HtmlTreeBuilder.vb` | Token list → `HtmlElement` tree. |
| `BrowserForWP.Core/Engine/Native/CssParser.vb` | CSS text → `Stylesheet` of `CssRule`, each with parsed selectors and specificity. |
| `BrowserForWP.Core/Engine/Native/SelectorMatcher.vb` | Does a `CssSelector` match an `HtmlElement`? |
| `BrowserForWP.Core/Engine/Native/UserAgentStylesheet.vb` | The engine's own default CSS, as a string constant. |
| `BrowserForWP.Core/Engine/Native/StyleResolver.vb` | Cascade + inheritance + shorthand expansion + unit resolution → `ComputedStyle`. |
| `BrowserForWP.Core/Engine/Native/BoxTreeBuilder.vb` | Styled element tree → box tree (block/inline/anonymous boxes). |
| `BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb` | The seam that keeps Core free of `BrowserForWP.Net`. |
| `BrowserForWP/Diagnostics/NetDocumentFetcher.vb` | `IDocumentFetcher` over `DohResolver` + `HttpClient13`. App layer, so Core stays clean. |
| `BrowserForWP.Core/Diagnostics/DocumentDumper.vb` | Box tree → indented text, for the diagnostics view. Pure. |
| `BrowserForWP.Core/Diagnostics/IeModeProbe.vb` | Reports how the hosted engine is configured (Task 1). |
| `tools/proto/ie-adapt.mjs` | Decision record for the IE-adaptation branch. |
| `tools/proto/probe-verdict.mjs` | Referee for `ProbeReport.CouldRun`. |
| `tools/proto/fetch-rules.mjs` | Referee for the fetch rules (content-type, charset, header casing). |
| `tools/proto/htmlparse.mjs` | Prototype + referee for tokenizer and tree builder. |
| `tools/proto/csscascade.mjs` | Prototype + referee for the CSS parser, matcher, cascade and inheritance. |
| `tools/proto/boxtree.mjs` | Prototype + referee for the box tree builder and the dumper. |
| `BrowserForWP.Core/BrowserForWP.Core.vbproj` | Declare every new `.vb` (check-vb fails otherwise). |
| `BrowserForWP/BrowserForWP.vbproj` | Declare `Diagnostics/NetDocumentFetcher.vb`. |
| `BrowserForWP/MainPage.xaml`, `.vb` | The "Parse current page" diagnostics control and its handler. |
| `BrowserForWP/Strings/{en-US,it-IT}/Resources.resw` | Keys for the new button and its status lines, and `ProbeNotRun`. |
| `BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb` | Fix the false "fully compatible" verdict (Task 2). |
| `docs/MAINTAINING.md`, `.agents/skills/browserforwp/SKILL.md`, `README.md`, `README.it.md` | Record the new tools, the new constraints and the honest capability claim. |

---

### Task 1: Close the "adapt Internet Explorer" branch with evidence

**Why this is a task and not a sentence:** the repository's own rule is that "cannot be done" must not be asserted without having tried it. This task produces the instrument that decides it on a handset, and records the API-surface evidence that exists off-device.

**Files:**
- Create: `BrowserForWP.Core/Diagnostics/IeModeProbe.vb`
- Create: `tools/proto/ie-adapt.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Modify: `docs/MAINTAINING.md`

**Interfaces:**
- Consumes: `Engine.IBrowserEngine.InvokeScriptAsync(script As String) As Task(Of String)`.
- Produces: `BrowserForWP.Core.Diagnostics.IeModeProbe.RunAsync(engine As IBrowserEngine) As Task(Of IeModeReport)`, returning `Nothing` when the document cannot answer; `IeModeReport` with `Public Property DocumentMode As Integer` and `Public Property RawJson As String`.

- [ ] **Step 1: Write the failing check first**

Create `tools/proto/ie-adapt.mjs`:

```js
#!/usr/bin/env node
// Decision record for "adapt Internet Explorer instead of writing an engine".
// It encodes what must be TRUE for that branch to be viable and FAILS while it
// is unmeasured. The on-device result is recorded separately and is NOT asserted
// here: no handset is available in this environment.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

// A negative assertion on an empty string passes vacuously, so existence is
// asserted separately from every property.
const probe = readIfPresent('BrowserForWP.Core/Diagnostics/IeModeProbe.vb');
check('IeModeProbe.vb exists', probe.length > 0);
check('the instrument reads documentMode', probe.includes('documentMode'));
check('the instrument reads X-UA-Compatible', probe.includes('X-UA-Compatible'));
check('the instrument is ES5 only (no let/const/arrow)',
  probe.length > 0 && !/\blet\s+\w|\bconst\s+\w|=>/.test(probe));

const recorded = readIfPresent('docs/MAINTAINING.md');
check('the branch is recorded as closed', recorded.includes('IE-adaptation is closed'));
check('document-mode forcing is recorded as unavailable',
  recorded.includes('no API to set the WebView document mode'));
check('MSHTML access from WinRT is recorded as unavailable',
  recorded.includes('no MSHTML surface is reachable from a WinRT app'));
check('feature-flag control is recorded as unavailable',
  recorded.includes('no API to toggle IE11 feature flags'));
check('a newer Trident is recorded as never shipping',
  recorded.includes('no Trident newer than IE11 ever shipped for this OS'));

console.log(`\n${checks - failures}/${checks} ie-adapt checks passed.`);
if (failures > 0) {
  console.log(`${failures} unmeasured/unrecorded item(s). The IE-adaptation branch is NOT closed.`);
  process.exit(1);
}
console.log('The IE-adaptation branch is closed with evidence.');
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/ie-adapt.mjs`
Expected: FAIL, exit 1, all **9** checks failing — the instrument and the record do not exist yet.

- [ ] **Step 3: Write the probe**

Create `BrowserForWP.Core/Diagnostics/IeModeProbe.vb`:

```vb
' BrowserForWP — reports how the hosted engine is actually configured.
'
' This exists to settle a question with measurement instead of assertion: can
' Trident be "adapted" into behaving like a newer engine? The four levers such a
' plan would need are (1) force a document mode, (2) replace or upgrade the
' engine binary, (3) toggle IE11 feature flags, (4) reach MSHTML directly. The
' API surface says all four are unavailable on WP8.1; this probe is how a handset
' confirms that rather than taking our word for it.
'
' ES5 only, deliberately: it runs on the engine under test.

Imports System.Threading.Tasks

Namespace Diagnostics

    ''' <summary>What the engine reports about its own configuration.</summary>
    Public NotInheritable Class IeModeReport

        Public Property DocumentMode As Integer
        Public Property RawJson As String = String.Empty

    End Class

    ''' <summary>Asks the live document how it is configured. Never throws.</summary>
    Public NotInheritable Class IeModeProbe

        ''' <summary>
        ''' Reads documentMode and the X-UA-Compatible value the engine honoured,
        ''' then appends an IE=edge meta tag and reads documentMode again. If a
        ''' host-side injection could raise the mode, modeAfter would exceed
        ''' modeBefore. Appending after load is the strongest form of the test,
        ''' because the meta tag would have been honoured had it been in the head.
        ''' </summary>
        Private Const ProbeScript As String =
            "(function(){" &
            "var compat='(none)';" &
            "try{" &
            "var m=document.querySelector('meta[http-equiv=""X-UA-Compatible""]');" &
            "if(m){compat=m.getAttribute('content');}" &
            "}catch(e){}" &
            "var before=document.documentMode||0;" &
            "try{" &
            "var t=document.createElement('meta');" &
            "t.setAttribute('http-equiv','X-UA-Compatible');" &
            "t.setAttribute('content','IE=edge');" &
            "document.getElementsByTagName('head')[0].appendChild(t);" &
            "}catch(e){}" &
            "return JSON.stringify({" &
            "modeBefore:before," &
            "modeAfter:document.documentMode||0," &
            "compatMeta:compat," &
            "ua:navigator.userAgent" &
            "});" &
            "})()"

        ''' <summary>Probe a live document. Returns Nothing when it cannot answer.</summary>
        Public Shared Async Function RunAsync(engine As Engine.IBrowserEngine) As Task(Of IeModeReport)
            If engine Is Nothing Then Return Nothing

            Dim raw As String = Nothing
            Try
                raw = Await engine.InvokeScriptAsync(ProbeScript).ConfigureAwait(False)
            Catch ex As Exception
                Return Nothing
            End Try

            If String.IsNullOrEmpty(raw) Then Return Nothing
            raw = raw.Trim()
            If raw.Length < 2 OrElse raw(0) <> "{"c Then Return Nothing

            Dim reportResult As New IeModeReport()
            reportResult.RawJson = raw
            reportResult.DocumentMode = ExtractInt(raw, "modeBefore")
            Return reportResult
        End Function

        ''' <summary>
        ''' Pull an integer member out of the flat JSON the probe returns. A JSON
        ''' parser dependency for three fields would be overkill, and the probe
        ''' controls the exact shape.
        ''' </summary>
        Private Shared Function ExtractInt(json As String, memberName As String) As Integer
            Dim needle As String = """" & memberName & """:"
            Dim at As Integer = json.IndexOf(needle, StringComparison.Ordinal)
            If at < 0 Then Return 0
            Dim startPos As Integer = at + needle.Length
            Dim endPos As Integer = startPos
            While endPos < json.Length AndAlso Char.IsDigit(json(endPos))
                endPos += 1
            End While
            If endPos = startPos Then Return 0
            Dim parsed As Integer = 0
            If Not Integer.TryParse(json.Substring(startPos, endPos - startPos), parsed) Then Return 0
            Return parsed
        End Function
    End Class

End Namespace
```

- [ ] **Step 4: Declare the new file**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`, inside the `<ItemGroup>` that lists `<Compile Include="Diagnostics\CompatibilityProbe.vb" />`, add directly after it:

```xml
    <Compile Include="Diagnostics\IeModeProbe.vb" />
```

- [ ] **Step 5: Record the verdict in the maintenance notes**

In `docs/MAINTAINING.md`, add a subsection at the end of `### Still open`:

```markdown
### IE-adaptation is closed

"Adapt Internet Explorer instead of writing an engine" was examined and closed.
Trident **is** the platform engine, and an app cannot re-configure it. The four
levers such a plan needs, and why each is absent:

- **no API to set the WebView document mode.** `WebView` exposes no document-mode
  property, and the hosted engine is already the newest available.
- **no Trident newer than IE11 ever shipped for this OS.** There is nothing to
  move up to; `X-UA-Compatible: IE=edge` selects the engine that is already
  running.
- **no API to toggle IE11 feature flags.** WP8.1 gives an app no switch over
  which CSS/JS features Trident honours.
- **no MSHTML surface is reachable from a WinRT app.** No COM activation of
  `mshtml`, no `IWebBrowser2`, no document-mode control.

What an app *can* do is what this repository already does: inject an ES5
compatibility layer into the document (`TridentEngine.InjectPolyfillAsync`) and
report the engine's limits truthfully (`CompatibilityProbe`).

`BrowserForWP.Core/Diagnostics/IeModeProbe.vb` plus a **Diagnostics → IE mode**
tap is how a handset turns that from an argument into a measurement. Until
someone runs it, the probe is the instrument and this section is the claim — keep
the two distinct, and record the measured `documentMode` here when it happens.
```

- [ ] **Step 6: Run the check to verify it passes**

Run: `node tools/proto/ie-adapt.mjs`
Expected: `9/9 ie-adapt checks passed.` then `The IE-adaptation branch is closed with evidence.` Exit 0.

- [ ] **Step 7: Verify nothing else broke**

Run: `node tools/check-vb.mjs`
Expected: `0 finding(s)`, exit 0. It fails on an undeclared `.vb`, so this is also the check that the vbproj edit is correct.

- [ ] **Step 8: Commit**

```bash
git add BrowserForWP.Core/Diagnostics/IeModeProbe.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj \
        tools/proto/ie-adapt.mjs docs/MAINTAINING.md
git commit -m "docs(engine): close the IE-adaptation branch with an instrument, not an opinion"
```

---

### Task 2: Stop `CompatibilityProbe` reporting success it did not measure

**Why:** this is a real defect found while planning. `CompatibilityProbe.RunAsync` returns a report with an empty `MissingFeatures` list when the document cannot be scripted (still loading, or an engine with no scripting host). `IsFullyCompatible` is `MissingFeatures.Count = 0`, so **"no answer" currently renders in the UI as "No missing web features detected."** A native engine that cannot evaluate scripts would make that lie permanent.

**Files:**
- Modify: `BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb`
- Modify: `BrowserForWP/MainPage.xaml.vb` (the `CompatProbeButton_Click` branch reading `IsFullyCompatible`)
- Modify: `BrowserForWP/Strings/en-US/Resources.resw`, `BrowserForWP/Strings/it-IT/Resources.resw`
- Create: `tools/proto/probe-verdict.mjs`

**Interfaces:**
- Produces: `ProbeReport.CouldRun As Boolean` (default `False`); `ProbeReport.IsFullyCompatible` now additionally requires `CouldRun`.

- [ ] **Step 1: Write the failing check first**

Create `tools/proto/probe-verdict.mjs`:

```js
#!/usr/bin/env node
// A report that could not run must never read as "fully compatible".
// Mirrors ProbeReport.IsFullyCompatible in
// BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

// The rule under test: compatible iff the probe actually ran AND found nothing.
function isFullyCompatible(sawAnswer, missingFeatures) {
  return sawAnswer && missingFeatures.length === 0;
}
check('no answer is not compatibility', isFullyCompatible(false, []) === false);
check('answer with gaps is not compatibility', isFullyCompatible(true, ['fetch']) === false);
check('answer with no gaps is compatibility', isFullyCompatible(true, []) === true);

const probe = readIfPresent('BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb');
check('CompatibilityProbe.vb exists', probe.length > 0);
check('ProbeReport carries CouldRun', probe.includes('CouldRun'));
check('IsFullyCompatible requires CouldRun', /IsFullyCompatible[\s\S]{0,120}CouldRun/.test(probe));

const main = readIfPresent('BrowserForWP/MainPage.xaml.vb');
check('the UI distinguishes an unanswered probe', main.length > 0 && main.includes('ProbeNotRun'));

const en = readIfPresent('BrowserForWP/Strings/en-US/Resources.resw');
const it = readIfPresent('BrowserForWP/Strings/it-IT/Resources.resw');
check('ProbeNotRun exists in en-US', en.includes('name="ProbeNotRun"'));
check('ProbeNotRun exists in it-IT', it.includes('name="ProbeNotRun"'));

console.log(`\n${checks - failures}/${checks} probe-verdict checks passed.`);
if (failures > 0) {
  console.log(`${failures} probe-verdict failure(s).`);
  process.exit(1);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/probe-verdict.mjs`
Expected: FAIL, exit 1 — **5** failures, exactly: `ProbeReport carries CouldRun`, `IsFullyCompatible requires CouldRun`, `the UI distinguishes an unanswered probe`, `ProbeNotRun exists in en-US`, `ProbeNotRun exists in it-IT`. The three logic assertions already pass, because they test the rule rather than the code.

- [ ] **Step 3: Fix the report**

In `BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb`, replace the `ProbeReport` class with:

```vb
    ''' <summary>Which modern web features the active document is missing.</summary>
    Public NotInheritable Class ProbeReport

        Public Property EngineName As String = String.Empty

        Public Property MissingFeatures As New List(Of String)()

        ''' <summary>
        ''' True only when the probe actually ran and returned an answer. An empty
        ''' feature list from a probe that never ran is not evidence of anything,
        ''' and conflating the two made the UI claim "no missing web features" for
        ''' a document it had not measured -- including on an engine with no
        ''' scripting host at all.
        ''' </summary>
        Public Property CouldRun As Boolean = False

        Public ReadOnly Property IsFullyCompatible As Boolean
            Get
                Return CouldRun AndAlso MissingFeatures.Count = 0
            End Get
        End Property
    End Class
```

and in `RunAsync` set the flag at the single point where an answer arrives. Replace from `If String.IsNullOrEmpty(raw) Then Return report` to the end of that guard with:

```vb
            If String.IsNullOrEmpty(raw) Then Return report
            raw = raw.Trim()
            If raw.Length < 2 Then Return report

            ' From here the engine answered, so the verdict is meaningful even if
            ' the answer lists gaps.
            report.CouldRun = True
```

- [ ] **Step 4: Add the resource key in both languages**

In `BrowserForWP/Strings/en-US/Resources.resw`, after the `DiagnosticsNoMissingFeatures` entry:

```xml
  <data name="ProbeNotRun" xml:space="preserve">
    <value>No answer from the page: it is still loading, or this engine cannot run scripts.</value>
  </data>
```

In `BrowserForWP/Strings/it-IT/Resources.resw`, in the same position:

```xml
  <data name="ProbeNotRun" xml:space="preserve">
    <value>Nessuna risposta dalla pagina: è ancora in caricamento, oppure questo motore non esegue script.</value>
  </data>
```

- [ ] **Step 5: Stop the UI printing an unmeasured verdict**

In `BrowserForWP/MainPage.xaml.vb`, in `CompatProbeButton_Click`, replace the branch that reads `report.IsFullyCompatible` with:

```vb
            If Not report.CouldRun Then
                ' Nothing was measured, so say that. Reporting "no missing web
                ' features" here was a claim the probe had not earned, and it is
                ' the shape of lie this project exists to avoid.
                CompatProbeResult.Text = Localizer.Get("ProbeNotRun")
            ElseIf report.IsFullyCompatible Then
                CompatProbeResult.Text = Localizer.Get("DiagnosticsNoMissingFeatures")
            Else
                CompatProbeResult.Text = String.Join(", ", report.MissingFeatures)
            End If
```

- [ ] **Step 6: Run the check to verify it passes**

Run: `node tools/proto/probe-verdict.mjs`
Expected: `9/9 probe-verdict checks passed.` Exit 0.

- [ ] **Step 7: Build for real**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== Real compiler errors ===` / `none`, then `=== BUILD_EXIT=0 ===`. Warnings: only the two deliberate `ResourceLoader` ones. Count warnings from `/t:Rebuild`: an incremental build skips unchanged projects and reports a cleaner log than the tree deserves.

- [ ] **Step 8: Commit**

```bash
git add BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb \
        BrowserForWP/MainPage.xaml.vb \
        BrowserForWP/Strings/en-US/Resources.resw BrowserForWP/Strings/it-IT/Resources.resw \
        tools/proto/probe-verdict.mjs
git commit -m "fix(diagnostics): stop reporting an unmeasured probe as fully compatible"
```

---

### Task 3: Fetch a document over the app's own TLS 1.3 transport

**Why:** this is the point of the whole plan. Everything the engine displays arrives through here, and it is the first code path in the product where a **page load** travels over `Tls13Client` instead of Schannel. `BrowserForWP.Net` has been compiled and shipped but reachable only from the probe since it was written.

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb`
- Create: `BrowserForWP/Diagnostics/NetDocumentFetcher.vb`
- Create: `tools/proto/fetch-rules.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`, `BrowserForWP/BrowserForWP.vbproj`

**Interfaces:**
- Consumes: `BrowserForWP.Net.Dns.DohResolver` (`Sub New(Optional serverUrl As String = Nothing)`, `Async Function ResolveAsync(host As String) As Task(Of IList(Of DnsAnswer))`, `Shared ReadOnly Property DefaultServerUrl`); `BrowserForWP.Net.Http.HttpClient13` (`Sub New(host As String, Optional port As Integer = 443)`, `Async Function GetAsync(url As String) As Task(Of HttpResponse)`, `Implements IDisposable`); `BrowserForWP.Net.Http.HttpResponse` (`StatusCode As Integer`, `ReasonPhrase As String`, `Headers As Dictionary(Of String, String)`, `Body As Byte()`, `FinalUrl As String`, `Function BodyAsText() As String`).
- Produces: `Engine.Native.DocumentResponse` with `StatusCode`, `FinalUrl`, `ContentType`, `Charset`, `Text`, `Error`, `IsHtml`, `EffectiveCharset`; `Engine.Native.IDocumentFetcher.FetchAsync(url As String, dohUrl As String) As Task(Of DocumentResponse)`.

- [ ] **Step 1: Write the failing check first**

Create `tools/proto/fetch-rules.mjs`. The impure fetch cannot run in Node, but the *rules around it* can, and those are where the mistakes live: charset, content-type, header casing, and the `Accept-Encoding` trap.

```js
#!/usr/bin/env node
// Rules for turning an HTTP response into document text, mirrored from
// BrowserForWP/Diagnostics/NetDocumentFetcher.vb and
// BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

// Content-Type: "text/html; charset=UTF-8" -> media type + charset.
function parseContentType(header) {
  if (!header) return { type: '', charset: '' };
  const parts = String(header).split(';');
  const type = parts[0].trim().toLowerCase();
  let charset = '';
  for (let i = 1; i < parts.length; i += 1) {
    const p = parts[i].trim();
    if (p.toLowerCase().startsWith('charset=')) charset = p.slice(8).trim().replace(/^"|"$/g, '');
  }
  return { type, charset: charset.toLowerCase() };
}
check('plain html', parseContentType('text/html').type === 'text/html');
check('html with charset', (() => {
  const r = parseContentType('text/html; charset=UTF-8');
  return r.type === 'text/html' && r.charset === 'utf-8';
})());
check('quoted charset', parseContentType('text/html; charset="ISO-8859-1"').charset === 'iso-8859-1');
check('empty header is empty', parseContentType('').type === '');
check('missing header does not throw', parseContentType(undefined).charset === '');

// Only these two media types are documents for this engine.
function isHtmlType(type) {
  return type === 'text/html' || type === 'application/xhtml+xml';
}
check('text/html is a document', isHtmlType('text/html') === true);
check('application/xhtml+xml is a document', isHtmlType('application/xhtml+xml') === true);
check('image/png is not', isHtmlType('image/png') === false);
check('empty is not', isHtmlType('') === false);

// A header lookup must be case-insensitive: servers send any casing they like.
function headerValue(headers, name) {
  if (!headers) return '';
  const wanted = name.toLowerCase();
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() === wanted) return headers[key];
  }
  return '';
}
check('header lookup is case-insensitive', headerValue({ 'content-type': 'text/html' }, 'Content-Type') === 'text/html');
check('missing header yields empty', headerValue({}, 'Content-Type') === '');

const fetcher = readIfPresent('BrowserForWP/Diagnostics/NetDocumentFetcher.vb');
check('NetDocumentFetcher.vb exists', fetcher.length > 0);
check('the fetcher sends no Accept-Encoding', fetcher.length > 0 && !/Accept-Encoding/i.test(fetcher));
check('the fetcher resolves through DoH', fetcher.includes('DohResolver'));
check('the fetcher uses the TLS 1.3 client', fetcher.includes('HttpClient13'));
check('the fetcher returns errors instead of throwing', fetcher.includes('Error ='));

const seam = readIfPresent('BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb');
check('IDocumentFetcher.vb exists', seam.length > 0);
check('the seam does not reference Net', seam.length > 0 && !seam.includes('BrowserForWP.Net'));

console.log(`\n${checks - failures}/${checks} fetch-rule checks passed.`);
if (failures > 0) {
  console.log(`${failures} fetch-rule failure(s).`);
  process.exit(1);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/fetch-rules.mjs`
Expected: FAIL, exit 1 — **7** failures: `NetDocumentFetcher.vb exists`, `the fetcher sends no Accept-Encoding`, `the fetcher resolves through DoH`, `the fetcher uses the TLS 1.3 client`, `the fetcher returns errors instead of throwing`, `IDocumentFetcher.vb exists`, `the seam does not reference Net`. The 11 rules-of-thumb assertions pass; they are the specification for the VB below.

- [ ] **Step 3: Write the seam**

Create `BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb`:

```vb
' BrowserForWP — how the native engine gets bytes, without knowing how.
'
' Core must not reference BrowserForWP.Net (layer discipline), so the engine
' depends on this interface and the app layer implements it over the TLS 1.3
' client. That is also what makes the engine testable: a fetcher returning fixed
' bytes exercises the whole pipeline with no network.

Imports System.Threading.Tasks

Namespace Engine.Native

    ''' <summary>One fetched document, or the reason there is not one.</summary>
    Public NotInheritable Class DocumentResponse

        Public Property StatusCode As Integer
        Public Property FinalUrl As String = String.Empty

        ''' <summary>Lowercased media type with parameters stripped, e.g. "text/html".</summary>
        Public Property ContentType As String = String.Empty

        ''' <summary>Lowercased charset from Content-Type, or empty when absent.</summary>
        Public Property Charset As String = String.Empty

        ''' <summary>Decoded body. Empty when Error is set or the type is not a document.</summary>
        Public Property Text As String = String.Empty

        ''' <summary>Empty on success. A failed fetch is data, never an exception.</summary>
        Public Property Error As String = String.Empty

        Public ReadOnly Property IsHtml As Boolean
            Get
                Return ContentType = "text/html" OrElse ContentType = "application/xhtml+xml"
            End Get
        End Property

        ''' <summary>The charset to decode with. UTF-8 unless the server says otherwise.</summary>
        Public ReadOnly Property EffectiveCharset As String
            Get
                If String.IsNullOrEmpty(Charset) Then Return "utf-8"
                Return Charset
            End Get
        End Property
    End Class

    ''' <summary>The engine's only impure dependency.</summary>
    Public Interface IDocumentFetcher

        ''' <summary>
        ''' Fetch one URL. Implementations must not throw for an expected failure --
        ''' no network, bad host, TLS failure -- but return it in Error.
        ''' </summary>
        Function FetchAsync(url As String, dohUrl As String) As Task(Of DocumentResponse)

    End Interface

End Namespace
```

- [ ] **Step 4: Implement it over TLS 1.3**

Create `BrowserForWP/Diagnostics/NetDocumentFetcher.vb`:

```vb
' BrowserForWP — IDocumentFetcher over the app's own TLS 1.3 transport.
'
' App layer on purpose: Core must not know about BrowserForWP.Net. This is the
' first code path where a PAGE LOAD travels over Tls13Client rather than through
' Schannel, which is the only way anything in this product can load a page above
' TLS 1.2.
'
' Two deliberate limits: no Accept-Encoding is sent, because HttpClient13 cannot
' decompress gzip and a compressed body would arrive as binary; and redirects are
' followed explicitly, because GetAsync returns whatever the socket returned.

Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine.Native
Imports BrowserForWP.Net.Dns
Imports BrowserForWP.Net.Http

Namespace Diagnostics

    ''' <summary>Fetches documents over DoH + TLS 1.3. Never throws.</summary>
    Public NotInheritable Class NetDocumentFetcher
        Implements IDocumentFetcher

        Private Const MaxRedirects As Integer = 3

        Public Async Function FetchAsync(url As String, dohUrl As String) As Task(Of DocumentResponse) Implements IDocumentFetcher.FetchAsync
            Dim parsedUri As Uri = Nothing
            If String.IsNullOrEmpty(url) OrElse Not Uri.TryCreate(url, UriKind.Absolute, parsedUri) Then
                Dim invalidResult As New DocumentResponse()
                invalidResult.Error = "invalid url"
                Return invalidResult
            End If
            If parsedUri.Scheme <> "https" AndAlso parsedUri.Scheme <> "http" Then
                Dim schemeResult As New DocumentResponse()
                schemeResult.Error = "unsupported scheme"
                Return schemeResult
            End If

            Dim dohEndpoint As String = If(String.IsNullOrEmpty(dohUrl), DohResolver.DefaultServerUrl, dohUrl)
            Try
                Using doh As New DohResolver(dohEndpoint)
                    Try
                        Await doh.ResolveAsync(parsedUri.Host)
                    Catch ex As Exception
                        ' Resolution failure is not fatal: the handshake below still
                        ' tries the OS path and reports a real error if it fails.
                    End Try
                End Using

                Return Await ReadWithRedirectsAsync(parsedUri.ToString(), 0)
            Catch ex As Exception
                Dim failureResult As New DocumentResponse()
                failureResult.Error = ex.Message
                Return failureResult
            End Try
        End Function

        Private Async Function ReadWithRedirectsAsync(url As String, depth As Integer) As Task(Of DocumentResponse)
            Dim result As New DocumentResponse()
            If depth > MaxRedirects Then
                result.Error = "too many redirects"
                Return result
            End If

            Dim parsedUri As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, parsedUri) Then
                result.Error = "invalid url"
                Return result
            End If

            Dim port As Integer = parsedUri.Port
            If port <= 0 Then port = 443

            Using client As New HttpClient13(parsedUri.Host, port)
                Dim httpResponse As HttpResponse = Await client.GetAsync(url)
                result.StatusCode = httpResponse.StatusCode
                result.FinalUrl = httpResponse.FinalUrl

                Dim locationHeader As String = HeaderValue(httpResponse, "Location")
                If httpResponse.StatusCode >= 300 AndAlso httpResponse.StatusCode < 400 AndAlso Not String.IsNullOrEmpty(locationHeader) Then
                    Dim nextUrl As String = ResolveRelative(url, locationHeader)
                    Return Await ReadWithRedirectsAsync(nextUrl, depth + 1)
                End If

                Dim contentType As String = HeaderValue(httpResponse, "Content-Type")
                result.ContentType = MediaTypeOf(contentType)
                result.Charset = CharsetOf(contentType)

                If result.StatusCode <> 200 Then
                    result.Error = "HTTP " & result.StatusCode
                    Return result
                End If
                If Not result.IsHtml Then
                    result.Error = "not a document: " & If(String.IsNullOrEmpty(result.ContentType), "(no content-type)", result.ContentType)
                    Return result
                End If

                result.Text = DecodeBody(httpResponse.Body, result.Charset)
                Return result
            End Using
        End Function

        ''' <summary>Case-insensitive header read: servers choose their own casing.</summary>
        Private Shared Function HeaderValue(response As HttpResponse, headerName As String) As String
            If response Is Nothing OrElse response.Headers Is Nothing Then Return String.Empty
            For Each pairItem In response.Headers
                If String.Equals(pairItem.Key, headerName, StringComparison.OrdinalIgnoreCase) Then
                    Return pairItem.Value
                End If
            Next
            Return String.Empty
        End Function

        Private Shared Function MediaTypeOf(contentType As String) As String
            If String.IsNullOrEmpty(contentType) Then Return String.Empty
            Dim semicolon As Integer = contentType.IndexOf(";"c)
            Dim mediaType As String = If(semicolon < 0, contentType, contentType.Substring(0, semicolon))
            Return mediaType.Trim().ToLowerInvariant()
        End Function

        Private Shared Function CharsetOf(contentType As String) As String
            If String.IsNullOrEmpty(contentType) Then Return String.Empty
            Dim lowered As String = contentType.ToLowerInvariant()
            Dim at As Integer = lowered.IndexOf("charset=", StringComparison.Ordinal)
            If at < 0 Then Return String.Empty
            Dim rest As String = contentType.Substring(at + 8).Trim()
            Dim semicolon As Integer = rest.IndexOf(";"c)
            If semicolon >= 0 Then rest = rest.Substring(0, semicolon)
            Return rest.Trim().Trim(""""c).ToLowerInvariant()
        End Function

        ''' <summary>
        ''' Decode the body. UTF-8 is the default; only a charset the platform can
        ''' actually decode is honoured, and anything else falls back to UTF-8
        ''' rather than guessing.
        ''' </summary>
        Private Shared Function DecodeBody(body As Byte(), charset As String) As String
            If body Is Nothing OrElse body.Length = 0 Then Return String.Empty
            If charset = "iso-8859-1" OrElse charset = "latin1" OrElse charset = "windows-1252" Then
                Dim latinEncoding As System.Text.Encoding = System.Text.Encoding.GetEncoding("ISO-8859-1")
                Return latinEncoding.GetString(body, 0, body.Length)
            End If
            Return System.Text.Encoding.UTF8.GetString(body, 0, body.Length)
        End Function

        Private Shared Function ResolveRelative(baseUrl As String, reference As String) As String
            Dim absoluteUri As Uri = Nothing
            If Uri.TryCreate(New Uri(baseUrl), reference, absoluteUri) Then Return absoluteUri.ToString()
            Return reference
        End Function
    End Class

End Namespace
```

- [ ] **Step 5: Declare both new files**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`, inside the `<ItemGroup>` holding the other `Engine\` entries, add after `<Compile Include="Engine\TridentEngine.vb" />`:

```xml
    <Compile Include="Engine\Native\IDocumentFetcher.vb" />
```

In `BrowserForWP/BrowserForWP.vbproj`, after `<Compile Include="Diagnostics\TlsProbeRunner.vb" />`:

```xml
    <Compile Include="Diagnostics\NetDocumentFetcher.vb" />
```

- [ ] **Step 6: Run the check to verify it passes**

Run: `node tools/proto/fetch-rules.mjs`
Expected: `18/18 fetch-rule checks passed.` Exit 0.

- [ ] **Step 7: Build for real**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== BUILD_EXIT=0 ===`, no `BC` errors, only the two `ResourceLoader` warnings. If `System.Text.Encoding.GetEncoding("ISO-8859-1")` reports `BC30456`, that is a **profile gap, not a typo**: drop the latin1 branch, keep UTF-8 only, and add the fact to `docs/MAINTAINING.md`'s error taxonomy. Do not weaken Option Strict to make it compile.

- [ ] **Step 8: Commit**

```bash
git add BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb \
        BrowserForWP/Diagnostics/NetDocumentFetcher.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj BrowserForWP/BrowserForWP.vbproj \
        tools/proto/fetch-rules.mjs
git commit -m "feat(engine): fetch documents over the app's own TLS 1.3 transport"
```

---

### Task 4: HTML tokenizer and tree builder

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/NodeTypes.vb`
- Create: `BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb`
- Create: `BrowserForWP.Core/Engine/Native/HtmlTreeBuilder.vb`
- Create: `tools/proto/htmlparse.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`

**Interfaces:**
- Produces: `HtmlTokenKind` (`Text`, `StartTag`, `EndTag`); `HtmlAttribute(Name As String, Value As String)`; `HtmlToken(Kind, Name, Text, Attributes, SelfClosing)`; `HtmlElement(TagName, Attributes, Children, Text, Parent)` with `Function Attribute(name As String) As String` and `ReadOnly Property IsText As Boolean`; `HtmlTokenizer.Tokenize(html As String) As IList(Of HtmlToken)`; `HtmlTreeBuilder.Build(tokens As IList(Of HtmlToken)) As HtmlElement` (returns the `<html>` root).

- [ ] **Step 1: Write the failing check first**

Create `tools/proto/htmlparse.mjs`. **This is the prototype**: the VB in steps 3–5 is its transliteration, so the algorithm is proven here before it is written in a language this environment cannot execute.

```js
#!/usr/bin/env node
// Prototype and referee for BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb and
// HtmlTreeBuilder.vb. Change this first, watch it pass, then port.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

const VOID = ['area','base','br','col','embed','hr','img','input','link','meta','param','source','track','wbr'];
const RAW_TEXT = ['script','style'];
const HEAD_CONTENT = ['title','meta','link','base','style','script','noscript'];

function tokenize(html) {
  const tokens = [];
  let pos = 0;
  let text = '';
  const flush = () => { if (text.length) { tokens.push({ kind: 'text', text }); text = ''; } };

  while (pos < html.length) {
    const ch = html[pos];
    if (ch !== '<') { text += ch; pos += 1; continue; }

    if (html.startsWith('<!--', pos)) {
      const end = html.indexOf('-->', pos + 4);
      pos = end < 0 ? html.length : end + 3;
      continue;
    }
    if (html.startsWith('<!', pos)) {
      const end = html.indexOf('>', pos);
      if (end < 0) break;
      pos = end + 1;
      continue;
    }
    if (html.startsWith('</', pos)) {
      const end = html.indexOf('>', pos);
      if (end < 0) break;
      flush();
      const name = html.slice(pos + 2, end).trim().toLowerCase();
      if (name) tokens.push({ kind: 'endtag', name });
      pos = end + 1;
      continue;
    }
    if (pos + 1 < html.length && /[A-Za-z]/.test(html[pos + 1])) {
      flush();
      let scan = pos + 1;
      while (scan < html.length && html[scan] !== '>') scan += 1;
      const inside = html.slice(pos + 1, scan);
      pos = scan < html.length ? scan + 1 : html.length;
      const tag = parseStartTag(inside);
      if (!tag) continue;
      tokens.push(tag);
      if (!tag.selfClosing && RAW_TEXT.includes(tag.name)) {
        const closeAt = html.toLowerCase().indexOf(`</${tag.name}`, pos);
        const rawEnd = closeAt < 0 ? html.length : closeAt;
        const raw = html.slice(pos, rawEnd);
        if (raw.length) tokens.push({ kind: 'text', text: raw, raw: true });
        pos = rawEnd;
      }
      continue;
    }
    text += ch;
    pos += 1;
  }
  flush();
  return tokens;
}

function parseStartTag(inside) {
  let s = inside;
  let selfClosing = false;
  if (s.endsWith('/')) { selfClosing = true; s = s.slice(0, -1); }
  const m = /^\s*([A-Za-z][A-Za-z0-9-]*)/.exec(s);
  if (!m) return null;
  const name = m[1].toLowerCase();
  const attributes = [];
  const rest = s.slice(m[0].length);
  const ATTR = /\s*([A-Za-z_:][A-Za-z0-9_:.-]*)(?:\s*=\s*("([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g;
  let a;
  while ((a = ATTR.exec(rest)) !== null) {
    if (a[1] === undefined) break;
    const value = a[3] !== undefined ? a[3] : a[4] !== undefined ? a[4] : a[5] !== undefined ? a[5] : '';
    attributes.push({ name: a[1].toLowerCase(), value });
    if (ATTR.lastIndex === a.index) break;
  }
  return { kind: 'starttag', name, attributes, selfClosing };
}

function makeNode(tag) {
  return { tagName: tag, attributes: {}, children: [], text: '', parent: null };
}

function tree(tokens) {
  const root = makeNode('html');
  let head = null;
  let body = null;
  let inHead = false;
  const stack = [root];
  const top = () => stack[stack.length - 1];
  const ensureHead = () => {
    if (!head) { head = makeNode('head'); head.parent = root; root.children.unshift(head); }
    return head;
  };
  const ensureBody = () => {
    if (!body) { body = makeNode('body'); body.parent = root; root.children.push(body); }
    return body;
  };

  for (const t of tokens) {
    if (t.kind === 'text') {
      // Text directly under <html> belongs to neither section and is dropped.
      if (top() === root) continue;
      // Script and style bodies are data, not document text: the box tree must
      // never see them, or a stray "<div>" inside a string would appear as text.
      if (RAW_TEXT.includes(top().tagName)) continue;
      if (!t.text.trim()) continue;
      const node = makeNode('#text');
      node.text = t.text.replace(/\s+/g, ' ');
      node.parent = top();
      top().children.push(node);
      continue;
    }
    if (t.kind === 'endtag') {
      for (let i = stack.length - 1; i >= 1; i -= 1) {
        if (stack[i].tagName === t.name) { stack.length = i; break; }
      }
      continue;
    }
    if (t.name === 'html') continue;
    if (t.name === 'head') { ensureHead(); inHead = true; stack.length = 1; stack.push(head); continue; }
    if (t.name === 'body') { ensureBody(); inHead = false; stack.length = 1; stack.push(body); continue; }

    if (!body && HEAD_CONTENT.includes(t.name)) {
      const headParent = ensureHead();
      const el = makeNode(t.name);
      el.parent = headParent;
      for (const a of t.attributes) el.attributes[a.name] = a.value;
      headParent.children.push(el);
      if (!VOID.includes(t.name) && !t.selfClosing) stack.push(el);
      continue;
    }

    ensureBody();
    // Leaving the head section: the insertion point moves to the body, whatever
    // was still open in the head.
    if (inHead || top() === root) { inHead = false; stack.length = 1; stack.push(body); }

    const parent = top();
    const el = makeNode(t.name);
    el.parent = parent;
    for (const a of t.attributes) el.attributes[a.name] = a.value;
    parent.children.push(el);
    if (!VOID.includes(t.name) && !t.selfClosing) stack.push(el);
  }
  return root;
}

function textOf(root) {
  let out = '';
  const walk = (n) => { if (n.tagName === '#text') { out += n.text; return; } for (const c of n.children) walk(c); };
  walk(root);
  return out;
}
function find(root, tag) {
  if (!root) return null;
  if (root.tagName === tag) return root;
  for (const c of root.children) { const hit = find(c, tag); if (hit) return hit; }
  return null;
}

// ── tokenizer ───────────────────────────────────────────────────────────────
check('plain text is one token', tokenize('hello').length === 1);
check('comment is skipped', tokenize('<!-- x -->').length === 0);
check('doctype is skipped', tokenize('<!DOCTYPE html>').length === 0);
check('start tag', tokenize('<p>')[0].name === 'p');
check('end tag', tokenize('</p>')[0].kind === 'endtag');
check('attributes lowercase', tokenize('<A HREF="x" ID=y>')[0].attributes[0].name === 'href');
check('quoted value', tokenize('<a href="http://e.com/">')[0].attributes[0].value === 'http://e.com/');
check('single-quoted value', tokenize("<a href='x'>")[0].attributes[0].value === 'x');
check('unquoted value', tokenize('<a href=x>')[0].attributes[0].value === 'x');
check('valueless attribute', tokenize('<input disabled>')[0].attributes[0].value === '');
check('self-closing flag', tokenize('<br/>')[0].selfClosing === true);
check('script body is raw text', tokenize('<script>if (a<b) {}</script>')[1].raw === true);
check('less-than in text survives', (() => {
  const t = tokenize('a < b');
  return t.length === 1 && t[0].text === 'a < b';
})());

// ── tree builder ────────────────────────────────────────────────────────────
check('implicit html/head/body', (() => {
  const t = tree(tokenize('<title>x</title><p>y</p>'));
  return t.tagName === 'html' && find(t, 'head') !== null && find(t, 'body') !== null;
})());
check('title goes to head, p to body', (() => {
  const t = tree(tokenize('<title>x</title><p>y</p>'));
  return find(find(t, 'head'), 'title') !== null && find(find(t, 'body'), 'p') !== null;
})());
check('head is created only once', (() => {
  const t = tree(tokenize('<title>a</title><meta charset="utf-8"><p>b</p>'));
  return t.children.filter((c) => c.tagName === 'head').length === 1;
})());
check('explicit head wins', (() => {
  const t = tree(tokenize('<head><title>a</title></head><body><p>b</p></body>'));
  return find(find(t, 'head'), 'title') !== null && find(find(t, 'body'), 'p') !== null;
})());
check('a block after head content lands in the body, not the head', (() => {
  const t = tree(tokenize('<title>a</title><p>b</p>'));
  return find(find(t, 'head'), 'p') === null;
})());
check('nesting by end tag', (() => {
  const t = tree(tokenize('<div><p>a</p></div>'));
  const d = find(t, 'div');
  return d.children.length === 1 && d.children[0].tagName === 'p';
})());
check('void element does not nest', (() => {
  const t = tree(tokenize('<p>a<br>b</p>'));
  const p = find(t, 'p');
  return p.children.length === 2 && p.children[0].tagName === '#text' && p.children[1].tagName === 'br';
})());
check('whitespace-only text is dropped', (() => {
  const t = tree(tokenize('<p>\n   \n</p>'));
  return find(t, 'p').children.length === 0;
})());
check('text whitespace collapses', (() => {
  const t = tree(tokenize('<p>a   b</p>'));
  return find(find(t, 'p'), '#text').text === 'a b';
})());
check('stray end tag is ignored', find(tree(tokenize('</div><p>a</p>')), 'p') !== null);
check('unclosed element is auto-closed at the end', (() => {
  const t = tree(tokenize('<div><p>a'));
  return find(t, 'p') !== null;
})());
check('declared subset is recognised end to end', (() => {
  const t = tree(tokenize('<article><h1>T</h1><p>a<em>b</em></p></article>'));
  return find(t, 'article') !== null && find(t, 'h1') !== null && find(t, 'em') !== null;
})());
check('script text is not document text', (() => {
  const t = tree(tokenize('<body><p>a</p><script>var x=1;</script>'));
  return !textOf(find(t, 'body')).includes('var x');
})());
check('style text is not document text', (() => {
  const t = tree(tokenize('<body><p>a</p><style>p{color:red}</style>'));
  return !textOf(find(t, 'body')).includes('color:red');
})());

// ── VB file parity ──────────────────────────────────────────────────────────
const tokenizer = readIfPresent('BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb');
check('HtmlTokenizer.vb exists', tokenizer.length > 0);
check('HtmlTokenizer.vb handles raw text', tokenizer.includes('script'));
check('HtmlTokenizer.vb has no VB14 fluent chain', tokenizer.length > 0 && !/\)\.\s*\n\s*\w+\(/.test(tokenizer));

console.log(`\n${checks - failures}/${checks} htmlparse checks passed.`);
if (failures > 0) {
  console.log(`${failures} htmlparse failure(s).`);
  process.exit(1);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/htmlparse.mjs`
Expected: FAIL, exit 1 — **3** failures, exactly the three `HtmlTokenizer.vb` parity checks. The 26 prototype assertions pass: the algorithm is proven, the port is not done.

**A real defect this verification already caught.** The first version of this prototype never created an implicit `<head>`, so `<title>x</title>` put `title` inside `body` and `find(root, 'head')` returned `null`. It also let `script` bodies become document text. Both were found by *running* the prototype, and both are asserted above — this is the entire reason the prototype comes first.

- [ ] **Step 3: Write the shared types**

Create `BrowserForWP.Core/Engine/Native/NodeTypes.vb`:

```vb
' BrowserForWP — the value types the native pipeline stages agree on.
'
' Types only, no logic: every stage converts one of these into the next, so this
' file is the interface contract between the prototype and the VB. The names here
' are matched by tools/proto/htmlparse.mjs, csscascade.mjs and boxtree.mjs --
' change one, change both.

Imports System.Collections.Generic

Namespace Engine.Native

    Public Enum HtmlTokenKind
        Text
        StartTag
        EndTag
    End Enum

    ''' <summary>One attribute, name lowercased.</summary>
    Public NotInheritable Class HtmlAttribute

        Public Sub New(name As String, value As String)
            Me.Name = name
            Me.Value = value
        End Sub

        Public ReadOnly Name As String
        Public ReadOnly Value As String
    End Class

    Public NotInheritable Class HtmlToken

        Public Property Kind As HtmlTokenKind
        Public Property Name As String = String.Empty
        Public Property Text As String = String.Empty
        Public Property Attributes As New List(Of HtmlAttribute)()
        Public Property SelfClosing As Boolean

    End Class

    ''' <summary>
    ''' One node of the document tree. A text node has TagName "#text" and its
    ''' content in Text; an element has attributes and children.
    ''' </summary>
    Public NotInheritable Class HtmlElement

        Public Property TagName As String = String.Empty
        Public Property Attributes As New Dictionary(Of String, String)()
        Public Property Children As New List(Of HtmlElement)()
        Public Property Text As String = String.Empty
        Public Property Parent As HtmlElement

        Public ReadOnly Property IsText As Boolean
            Get
                Return TagName = "#text"
            End Get
        End Property

        ''' <summary>Attribute value, or an empty string. Never Nothing.</summary>
        Public Function Attribute(name As String) As String
            If String.IsNullOrEmpty(name) Then Return String.Empty
            Dim foundValue As String = Nothing
            If Attributes.TryGetValue(name.ToLowerInvariant(), foundValue) Then
                Return If(foundValue, String.Empty)
            End If
            Return String.Empty
        End Function

        Public Overrides Function ToString() As String
            If IsText Then Return "#text"
            Return TagName
        End Function
    End Class

End Namespace
```

- [ ] **Step 4: Write the tokenizer**

Create `BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb` as the transliteration of `tokenize` and `parseStartTag` from Step 1. Required behaviour, every line asserted by the prototype: comments and declarations skipped; `</` produces `EndTag`; a `<` followed by a letter produces `StartTag`; attribute names lowercased; values from double quotes, single quotes, unquoted, or empty; a trailing `/` sets `SelfClosing`; `script` and `style` bodies consumed as one `Text` token up to their own end tag; any other `<` is literal text.

Public surface, exactly:

```vb
Public NotInheritable Class HtmlTokenizer
    Public Shared Function Tokenize(html As String) As IList(Of HtmlToken)
```

Private helpers the port needs, all `Shared`: `IsNameStartChar(ch As Char) As Boolean`, `FlushText(tokens As List(Of HtmlToken), buffer As StringBuilder)`, `ParseStartTag(inside As String) As HtmlToken`, `IsVoidElement(name As String) As Boolean`, `IsRawTextElement(name As String) As Boolean`, `IndexOfIgnoreCase(haystack As String, needle As String, startPos As Integer) As Integer`.

Two VB 12 rules that matter here: build the attribute buffer with a `StringBuilder` and a `While` loop rather than `Regex` if you prefer explicit indices (`Regex` is available, `RegexOptions.Compiled` is not), and **do not** chain calls across lines after a `.`.

- [ ] **Step 5: Write the tree builder**

Create `BrowserForWP.Core/Engine/Native/HtmlTreeBuilder.vb` as the transliteration of `tree` from Step 1:

```vb
Public NotInheritable Class HtmlTreeBuilder
    Public Shared Function Build(tokens As IList(Of HtmlToken)) As HtmlElement
```

Required behaviour, every line asserted: implicit `html`/`head`/`body`, created at most once; `title`, `meta`, `link`, `base`, `style`, `script` and `noscript` go to `head` while no `body` exists yet, and a block element after them goes to the **body** (leaving the head section, whatever was still open in it); explicit `head`/`body` win; text directly under `html` is dropped; whitespace-only text is dropped; other text whitespace collapses to single spaces; **text inside `script`/`style` is dropped, not treated as document text**; void elements are never pushed on the stack; an end tag pops to the matching open element; an unmatched end tag is ignored; unclosed elements remain open at the end and are simply part of the tree.

- [ ] **Step 6: Declare the three files**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`, inside the `<ItemGroup>` holding `<Compile Include="Engine\TridentEngine.vb" />`, add:

```xml
    <Compile Include="Engine\Native\NodeTypes.vb" />
    <Compile Include="Engine\Native\HtmlTokenizer.vb" />
    <Compile Include="Engine\Native\HtmlTreeBuilder.vb" />
```

- [ ] **Step 7: Run the check to verify it passes**

Run: `node tools/proto/htmlparse.mjs`
Expected: `29/29 htmlparse checks passed.` Exit 0.

- [ ] **Step 8: Build for real**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== BUILD_EXIT=0 ===`, no `BC` errors.

- [ ] **Step 9: Commit**

```bash
git add BrowserForWP.Core/Engine/Native/NodeTypes.vb \
        BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb \
        BrowserForWP.Core/Engine/Native/HtmlTreeBuilder.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj tools/proto/htmlparse.mjs
git commit -m "feat(engine): parse the declared HTML subset, prototype first"
```

---

### Task 5: CSS parser, selector matching and specificity

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/CssParser.vb`
- Create: `BrowserForWP.Core/Engine/Native/SelectorMatcher.vb`
- Create: `tools/proto/csscascade.mjs`
- Modify: `BrowserForWP.Core/Engine/Native/NodeTypes.vb` (add the CSS types)
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`

**Interfaces:**
- Consumes: `HtmlElement` from Task 4.
- Produces: `CssSimpleSelector(TypeName, ClassName, IdName)` with `Function Specificity() As Integer` (`id*100 + class*10 + type`); `CssSelectorPart(Combinator, Simple)`; `CssSelector(Parts)` with `Function Specificity() As Integer`; `StyleDeclaration(Name, Value)`; `CssRule(Selectors, Declarations)`; `Stylesheet(Rules)`; `CssParser.Parse(css As String) As Stylesheet`; `SelectorMatcher.Matches(selector As CssSelector, element As HtmlElement) As Boolean`; `SelectorMatcher.MatchesSimple(simple As CssSimpleSelector, element As HtmlElement) As Boolean`.

- [ ] **Step 1: Write the failing check first**

Create `tools/proto/csscascade.mjs`. Task 6 inserts a section into this file; the anchor line is `// ── VB file parity (CssParser) ──`, so keep it exactly as written.

```js
#!/usr/bin/env node
// Prototype and referee for CssParser.vb and SelectorMatcher.vb. Task 6 inserts
// the cascade section immediately before the parity marker below.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

function stripComments(css) {
  let out = '';
  let i = 0;
  while (i < css.length) {
    if (css.startsWith('/*', i)) {
      const end = css.indexOf('*/', i + 2);
      i = end < 0 ? css.length : end + 2;
      continue;
    }
    out += css[i];
    i += 1;
  }
  return out;
}

function parseSimple(text) {
  const simple = { type: '', cls: '', id: '' };
  const re = /([.#]?)([A-Za-z0-9_-]+)/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    if (m[1] === '.') simple.cls = m[2].toLowerCase();
    else if (m[1] === '#') simple.id = m[2].toLowerCase();
    else simple.type = m[2].toLowerCase();
  }
  return simple;
}

function parseSelector(text) {
  const parts = [];
  const tokens = text.trim().split(/\s*(>)\s*|\s+/).filter((t) => t !== undefined && t !== '');
  let combinator = '';
  for (const t of tokens) {
    if (t === '>') { combinator = '>'; continue; }
    parts.push({ combinator, simple: parseSimple(t) });
    combinator = ' ';
  }
  return parts;
}

function specificity(parts) {
  let total = 0;
  for (const p of parts) {
    if (p.simple.id) total += 100;
    if (p.simple.cls) total += 10;
    if (p.simple.type && p.simple.type !== '*') total += 1;
  }
  return total;
}

function parseCss(css) {
  const text = stripComments(css);
  const rules = [];
  let i = 0;
  while (i < text.length) {
    const braceAt = text.indexOf('{', i);
    if (braceAt < 0) break;
    const closeAt = text.indexOf('}', braceAt);
    if (closeAt < 0) break;
    const selectorText = text.slice(i, braceAt).trim();
    const body = text.slice(braceAt + 1, closeAt);
    i = closeAt + 1;
    if (!selectorText || selectorText.startsWith('@')) continue;
    const selectors = selectorText.split(',').map((s) => parseSelector(s)).filter((s) => s.length > 0);
    if (selectors.length === 0) continue;
    const declarations = [];
    for (const piece of body.split(';')) {
      const colon = piece.indexOf(':');
      if (colon <= 0) continue;
      declarations.push({ name: piece.slice(0, colon).trim().toLowerCase(), value: piece.slice(colon + 1).trim() });
    }
    if (declarations.length === 0) continue;
    rules.push({ selectors, declarations });
  }
  return rules;
}

const sheet = parseCss(`
  /* comment */
  p { color: #111; margin: 8px 4px }
  .lead { color: red }
  #main p { color: blue }
  div > p { font-size: 14px }
  h1, h2 { font-weight: bold }
  @media screen { p { color: green } }
`);

check('comment stripped', !JSON.stringify(sheet).includes('comment'));
check('five rules parsed, at-rule skipped', sheet.length === 5);
check('declarations split', sheet[0].declarations.length === 2);
check('declaration name lowercased', sheet[0].declarations[0].name === 'color');
check('declaration value preserved', sheet[0].declarations[1].value === '8px 4px');
check('selector list yields two selectors', sheet[4].selectors.length === 2);
check('type specificity', specificity(parseSelector('p')) === 1);
check('class specificity', specificity(parseSelector('.lead')) === 10);
check('id specificity', specificity(parseSelector('#main')) === 100);
check('id plus type', specificity(parseSelector('#main p')) === 101);
check('class plus type', specificity(parseSelector('p.lead')) === 11);
check('universal adds nothing', specificity(parseSelector('*')) === 0);
check('child combinator recorded', (() => {
  const parts = parseSelector('div > p');
  return parts.length === 2 && parts[1].combinator === '>';
})());
check('descendant combinator recorded', (() => {
  const parts = parseSelector('div p');
  return parts.length === 2 && parts[1].combinator === ' ';
})());

// ── matching, over a hand-built tree ────────────────────────────────────────
function el(tag, attrs = {}, children = []) {
  const node = { tagName: tag, attributes: attrs, children, text: '', parent: null };
  for (const c of children) c.parent = node;
  return node;
}
const tree = el('body', {}, [
  el('div', { id: 'main', class: 'wrap' }, [el('p', { class: 'lead' }, [el('em', {}, [])])]),
  el('p', { class: 'lead' }, []),
]);

function matchesSimple(simple, node) {
  if (!node || node.tagName === '#text') return false;
  if (simple.type && simple.type !== '*' && node.tagName !== simple.type) return false;
  if (simple.id && node.attributes.id !== simple.id) return false;
  if (simple.cls) {
    const classes = (node.attributes.class || '').split(/\s+/).filter(Boolean);
    if (!classes.includes(simple.cls)) return false;
  }
  return true;
}

function matches(selector, node) {
  if (!node || node.tagName === '#text') return false;
  let current = node;
  for (let i = selector.length - 1; i >= 0; i -= 1) {
    if (!matchesSimple(selector[i].simple, current)) return false;
    if (i === 0) return true;
    const wantChild = selector[i].combinator === '>';
    current = current.parent;
    if (wantChild) {
      if (!current) return false;
    } else {
      // Descendant: climb until an ancestor matches the part on the left.
      let climbed = current;
      while (climbed && !matchesSimple(selector[i - 1].simple, climbed)) climbed = climbed.parent;
      if (!climbed) return false;
      current = climbed;
      i -= 1;   // the climb consumed the left part, so the loop must skip it too
    }
  }
  return true;
}

const innerP = tree.children[0].children[0];
const outerP = tree.children[1];
check('type matches', matches(parseSelector('p'), innerP) === true);
check('type does not match other tag', matches(parseSelector('div'), innerP) === false);
check('descendant matches', matches(parseSelector('div p'), innerP) === true);
check('descendant does not match unrelated', matches(parseSelector('section p'), outerP) === false);
check('child matches', matches(parseSelector('body > div'), tree.children[0]) === true);
check('child does not match grandchild', matches(parseSelector('body > p'), innerP) === false);
check('id matches', matches(parseSelector('#main'), tree.children[0]) === true);
check('class matches', matches(parseSelector('.lead'), outerP) === true);
check('class does not match missing class', matches(parseSelector('.other'), outerP) === false);
check('id plus descendant', matches(parseSelector('#main p'), innerP) === true);
check('em is reachable by class from an ancestor', matches(parseSelector('.wrap em'), innerP.children[0]) === true);

// ── VB file parity (CssParser) ──────────────────────────────────────────────
const parser = readIfPresent('BrowserForWP.Core/Engine/Native/CssParser.vb');
check('CssParser.vb exists', parser.length > 0);
check('CssParser.vb parses into a Stylesheet', parser.includes('Stylesheet'));
check('CssParser.vb does not use RegexOptions.Compiled',
  parser.length > 0 && !parser.includes('RegexOptions.Compiled'));

console.log(`\n${checks - failures}/${checks} csscascade checks passed.`);
if (failures > 0) {
  console.log(`${failures} csscascade failure(s).`);
  process.exit(1);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/csscascade.mjs`
Expected: FAIL, exit 1 — **3** failures, exactly the three `CssParser.vb` parity checks. The 25 parsing and matching assertions pass.

**A real defect this verification already caught.** The first version of the cascade assertion read `w.decl.color === 'blue'` while a declaration is `{name, value}` — so it asserted against `undefined` and failed against a *correct* implementation. The lesson is in the plan deliberately: when a check fails, first prove the check is right. A referee that is wrong in the other direction is how a correct implementation gets "fixed" into a broken one.

- [ ] **Step 3: Add the CSS types**

Append to `BrowserForWP.Core/Engine/Native/NodeTypes.vb`, before `End Namespace`:

```vb
    ''' <summary>
    ''' One compound selector: at most one type, one class and one id. Attribute
    ''' selectors and pseudo-classes are deliberately absent -- the engine reports
    ''' them as unsupported instead of matching them wrongly.
    ''' </summary>
    Public NotInheritable Class CssSimpleSelector

        Public Property TypeName As String = String.Empty
        Public Property ClassName As String = String.Empty
        Public Property IdName As String = String.Empty

        ''' <summary>id counts 100, class 10, type 1. The universal selector adds nothing.</summary>
        Public Function Specificity() As Integer
            Dim total As Integer = 0
            If Not String.IsNullOrEmpty(IdName) Then total += 100
            If Not String.IsNullOrEmpty(ClassName) Then total += 10
            If Not String.IsNullOrEmpty(TypeName) AndAlso TypeName <> "*" Then total += 1
            Return total
        End Function

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return String.IsNullOrEmpty(TypeName) AndAlso String.IsNullOrEmpty(ClassName) AndAlso String.IsNullOrEmpty(IdName)
            End Get
        End Property
    End Class

    ''' <summary>A simple selector plus how it relates to the part on its right.</summary>
    Public NotInheritable Class CssSelectorPart

        Public Property Combinator As String = String.Empty
        Public Property Simple As CssSimpleSelector

    End Class

    Public NotInheritable Class CssSelector

        Public Property Parts As New List(Of CssSelectorPart)()

        Public Function Specificity() As Integer
            Dim total As Integer = 0
            For Each partItem In Parts
                If partItem.Simple IsNot Nothing Then total += partItem.Simple.Specificity()
            Next
            Return total
        End Function

    End Class

    Public NotInheritable Class StyleDeclaration

        Public Sub New(name As String, value As String)
            Me.Name = name
            Me.Value = value
        End Sub

        Public ReadOnly Name As String
        Public ReadOnly Value As String

    End Class

    Public NotInheritable Class CssRule

        Public Property Selectors As New List(Of CssSelector)()
        Public Property Declarations As New List(Of StyleDeclaration)()

    End Class

    Public NotInheritable Class Stylesheet

        Public Property Rules As New List(Of CssRule)()

    End Class
```

- [ ] **Step 4: Write the parser and the matcher**

Create `BrowserForWP.Core/Engine/Native/CssParser.vb` as the transliteration of `stripComments`, `parseSimple`, `parseSelector` and `parseCss`:

```vb
Public NotInheritable Class CssParser
    ''' <summary>Parse a stylesheet. Unknown at-rules are skipped, never guessed at.</summary>
    Public Shared Function Parse(css As String) As Stylesheet
```

Create `BrowserForWP.Core/Engine/Native/SelectorMatcher.vb`:

```vb
Public NotInheritable Class SelectorMatcher
    ''' <summary>Does this selector match this element? The rightmost part is the subject.</summary>
    Public Shared Function Matches(selector As CssSelector, element As HtmlElement) As Boolean
    ''' <summary>Type, class and id only, as declared in NodeTypes.</summary>
    Public Shared Function MatchesSimple(simple As CssSimpleSelector, element As HtmlElement) As Boolean
```

Write these two files in one pass with the prototype open beside them. The climbing loop in `Matches` is the one place where a transliteration error produces quietly wrong styling instead of a compile error: descending with `>` must check the parent and **not** climb, and the descendant case must climb until a match and then skip the part it just consumed.

- [ ] **Step 5: Declare the two files**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`:

```xml
    <Compile Include="Engine\Native\CssParser.vb" />
    <Compile Include="Engine\Native\SelectorMatcher.vb" />
```

- [ ] **Step 6: Run the check to verify it passes**

Run: `node tools/proto/csscascade.mjs`
Expected: `28/28 csscascade checks passed.` Exit 0.

- [ ] **Step 7: Build for real**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== BUILD_EXIT=0 ===`, no `BC` errors.

- [ ] **Step 8: Commit**

```bash
git add BrowserForWP.Core/Engine/Native/NodeTypes.vb \
        BrowserForWP.Core/Engine/Native/CssParser.vb \
        BrowserForWP.Core/Engine/Native/SelectorMatcher.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj tools/proto/csscascade.mjs
git commit -m "feat(engine): parse CSS and match selectors with real specificity"
```

---

### Task 6: The cascade, inheritance and unit resolution

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/UserAgentStylesheet.vb`
- Create: `BrowserForWP.Core/Engine/Native/StyleResolver.vb`
- Modify: `BrowserForWP.Core/Engine/Native/NodeTypes.vb` (add `ComputedStyle`)
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Modify: `tools/proto/csscascade.mjs`

**Interfaces:**
- Consumes: `Stylesheet`, `CssParser`, `SelectorMatcher`, `HtmlElement`.
- Produces: `ComputedStyle`; `StyleResolver.Resolve(element As HtmlElement, pageSheet As Stylesheet, parentStyle As ComputedStyle, rootFontSizePx As Double) As ComputedStyle`; `UserAgentStylesheet.Css As String`.

`StyleResolver.Resolve` is called once per element, top-down, with the parent's already-computed style. That ordering is the contract: inheritance is a parameter, never a second pass.

- [ ] **Step 1: Write the failing check first**

Insert this block into `tools/proto/csscascade.mjs` **immediately before** the line `// ── VB file parity (CssParser) ──`:

```js
// ── lengths ─────────────────────────────────────────────────────────────────
function parseLength(value, parentFontPx, rootFontPx) {
  if (value === undefined) return null;
  const v = String(value).trim().toLowerCase();
  if (v === 'auto') return -1;
  let m = /^(-?[\d.]+)px$/.exec(v); if (m) return parseFloat(m[1]);
  m = /^(-?[\d.]+)pt$/.exec(v); if (m) return parseFloat(m[1]) * 96 / 72;
  m = /^(-?[\d.]+)em$/.exec(v); if (m) return parseFloat(m[1]) * parentFontPx;
  m = /^(-?[\d.]+)rem$/.exec(v); if (m) return parseFloat(m[1]) * rootFontPx;
  if (v === '0') return 0;
  return null;
}
check('px parsed', parseLength('8px', 16, 16) === 8);
check('pt converted at 96/72', Math.abs(parseLength('12pt', 16, 16) - 16) < 0.001);
check('em is relative to the parent font', parseLength('2em', 20, 16) === 40);
check('rem is relative to the root font', parseLength('2rem', 20, 18) === 36);
check('auto is a sentinel, not a number', parseLength('auto', 16, 16) === -1);
check('bare zero is a length', parseLength('0', 16, 16) === 0);
check('percentage is not a length here', parseLength('50%', 16, 16) === null);
check('garbage is not a length', parseLength('thick', 16, 16) === null);

// ── shorthand expansion ─────────────────────────────────────────────────────
function expandShorthand(name, value, out) {
  if (name === 'margin' || name === 'padding') {
    const v = value.split(/\s+/);
    const sides = v.length === 1 ? [v[0], v[0], v[0], v[0]]
      : v.length === 2 ? [v[0], v[1], v[0], v[1]]
      : v.length === 3 ? [v[0], v[1], v[2], v[1]]
      : [v[0], v[1], v[2], v[3]];
    ['top', 'right', 'bottom', 'left'].forEach((s, i) => { out[`${name}-${s}`] = sides[i]; });
    return;
  }
  if (name === 'border') {
    for (const piece of value.split(/\s+/)) {
      if (/^\d/.test(piece)) out['border-width-all'] = piece;
      else if (['none', 'hidden', 'solid', 'dashed', 'dotted'].includes(piece)) out['border-style-all'] = piece;
      else out['border-color-all'] = piece;
    }
  }
}
check('margin: one value applies to four sides', (() => {
  const out = {}; expandShorthand('margin', '8px', out);
  return out['margin-top'] === '8px' && out['margin-left'] === '8px';
})());
check('margin: two values are vertical then horizontal', (() => {
  const out = {}; expandShorthand('margin', '8px 4px', out);
  return out['margin-top'] === '8px' && out['margin-right'] === '4px'
    && out['margin-bottom'] === '8px' && out['margin-left'] === '4px';
})());
check('margin: three values mirror the bottom', (() => {
  const out = {}; expandShorthand('margin', '1px 2px 3px', out);
  return out['margin-bottom'] === '3px' && out['margin-left'] === '2px';
})());
check('margin: four values map in order', (() => {
  const out = {}; expandShorthand('margin', '1px 2px 3px 4px', out);
  return out['margin-top'] === '1px' && out['margin-right'] === '2px'
    && out['margin-bottom'] === '3px' && out['margin-left'] === '4px';
})());
check('border shorthand sorts width, style, colour', (() => {
  const out = {}; expandShorthand('border', 'solid 2px #333', out);
  return out['border-style-all'] === 'solid' && out['border-width-all'] === '2px'
    && out['border-color-all'] === '#333';
})());

// ── the cascade ─────────────────────────────────────────────────────────────
// Declarations carry a source order so ties are resolvable; the winner is the
// highest specificity, and on a tie the later declaration.
function winningDeclaration(element, rules) {
  let winner = null;
  for (const rule of rules) {
    for (const selector of rule.selectors) {
      if (!matches(selector, element)) continue;
      const spec = specificity(selector);
      for (const decl of rule.declarations) {
        if (winner === null || spec > winner.spec
            || (spec === winner.spec && decl.order > winner.order)) {
          winner = { spec, order: decl.order, decl };
        }
      }
    }
  }
  return winner;
}
function withOrder(rules) {
  let order = 0;
  for (const rule of rules) { for (const d of rule.declarations) d.order = order++; }
  return rules;
}

check('specificity beats source order', (() => {
  const element = tree.children[0].children[0];   // div#main > p
  const rules = withOrder(parseCss('#main p { color: blue } p { color: red }'));
  const w = winningDeclaration(element, rules);
  return w !== null && w.decl.value === 'blue';
})());
check('equal specificity: the later declaration wins', (() => {
  const element = tree.children[1];               // p.lead
  const rules = withOrder(parseCss('p { color: red } .lead { color: green }'));
  const w = winningDeclaration(element, rules);
  return w !== null && w.decl.value === 'green';
})());
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/csscascade.mjs`
Expected: FAIL, exit 1 — the same **3** `CssParser.vb` parity failures plus **3** new parity failures (`StyleResolver.vb exists`, `the UA stylesheet is a VB constant`, `the UA stylesheet styles h1 and p`) once Step 5 adds them. All 15 new cascade assertions pass on the prototype.

- [ ] **Step 3: Write the UA stylesheet**

Create `BrowserForWP.Core/Engine/Native/UserAgentStylesheet.vb`:

```vb
' BrowserForWP — the engine's own default stylesheet.
'
' Without this, every page renders as undifferentiated inline text: block
' elements would not stack and headings would not differ from paragraphs. It is a
' constant rather than a resource because it is engine input, not user-facing
' copy -- and there is nothing to localize in `display: block`.

Namespace Engine.Native

    ''' <summary>The default presentational rules of the native engine.</summary>
    Public NotInheritable Class UserAgentStylesheet

        Private Sub New()
        End Sub

        Public Const Css As String =
            "html,body,div,p,article,section,header,footer,nav,main,aside,figure,blockquote,pre,ul,ol,li,h1,h2,h3,h4,h5,h6{display:block}" &
            "body{margin:8px;font-size:16px;font-family:'Segoe UI';line-height:1.4;color:#000000}" &
            "h1{font-size:2em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h2{font-size:1.5em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h3{font-size:1.25em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h4{font-size:1em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h5{font-size:1em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "h6{font-size:1em;font-weight:700;margin-top:0.6em;margin-bottom:0.6em}" &
            "p{margin-top:1em;margin-bottom:1em}" &
            "a{color:#0066cc;text-decoration:underline}" &
            "ul{margin-top:1em;margin-bottom:1em;padding-left:2em}" &
            "ol{margin-top:1em;margin-bottom:1em;padding-left:2em}" &
            "li{display:block;list-style-type:disc}" &
            "blockquote{margin-top:1em;margin-bottom:1em;margin-left:2em;margin-right:2em}" &
            "pre{font-family:Consolas;margin-top:1em;margin-bottom:1em}" &
            "em{font-style:italic}" &
            "strong{font-weight:700}" &
            "b{font-weight:700}" &
            "img{display:block}" &
            "script,style,head,title,meta{display:none}"

    End Class

End Namespace
```

Note the shorthands are written out as longhands. That is deliberate: the resolver's shorthand expansion is exercised by *page* CSS, and a stylesheet the engine controls should not depend on its own feature it is still proving.

- [ ] **Step 4: Add the computed style**

Append to `NodeTypes.vb` before `End Namespace`. `-1` is the "auto" sentinel for lengths; `LineHeightPx = -1` means `normal`:

```vb
    ''' <summary>
    ''' Used values for one box. Lengths are already resolved to pixels -- em/rem
    ''' need the parent font size, which is only available while walking the tree,
    ''' so resolution happens in the resolver and never in the parser.
    ''' </summary>
    Public NotInheritable Class ComputedStyle

        Public Property Display As String = "inline"
        Public Property Color As String = "#000000"
        Public Property BackgroundColor As String = "transparent"
        Public Property FontSizePx As Double = 16
        Public Property FontFamily As String = "'Segoe UI'"
        Public Property FontWeight As Integer = 400
        Public Property FontStyle As String = "normal"
        Public Property LineHeightPx As Double = -1
        Public Property TextAlign As String = "left"
        Public Property TextDecoration As String = "none"
        Public Property ListStyleType As String = "disc"
        Public Property MarginTopPx As Double
        Public Property MarginRightPx As Double
        Public Property MarginBottomPx As Double
        Public Property MarginLeftPx As Double
        Public Property PaddingTopPx As Double
        Public Property PaddingRightPx As Double
        Public Property PaddingBottomPx As Double
        Public Property PaddingLeftPx As Double
        Public Property BorderTopWidthPx As Double
        Public Property BorderRightWidthPx As Double
        Public Property BorderBottomWidthPx As Double
        Public Property BorderLeftWidthPx As Double
        Public Property BorderTopStyle As String = "none"
        Public Property BorderRightStyle As String = "none"
        Public Property BorderBottomStyle As String = "none"
        Public Property BorderLeftStyle As String = "none"
        Public Property BorderTopColor As String = "currentcolor"
        Public Property BorderRightColor As String = "currentcolor"
        Public Property BorderBottomColor As String = "currentcolor"
        Public Property BorderLeftColor As String = "currentcolor"
        Public Property WidthPx As Double = -1
        Public Property HeightPx As Double = -1
        Public Property MaxWidthPx As Double = -1

        Public ReadOnly Property IsBlock As Boolean
            Get
                Return Display = "block"
            End Get
        End Property

        Public ReadOnly Property IsHidden As Boolean
            Get
                Return Display = "none"
            End Get
        End Property
    End Class
```

- [ ] **Step 5: Write the resolver**

Create `BrowserForWP.Core/Engine/Native/StyleResolver.vb`:

```vb
Public NotInheritable Class StyleResolver

    ''' <summary>
    ''' The used style for one element. parentStyle carries inheritance;
    ''' rootFontSizePx is the base for rem. Pass the already-computed parent, or
    ''' Nothing at the root.
    ''' </summary>
    Public Shared Function Resolve(element As HtmlElement,
                                   pageSheet As Stylesheet,
                                   parentStyle As ComputedStyle,
                                   rootFontSizePx As Double) As ComputedStyle
```

Required behaviour, in this order:

1. Start from the parent's inherited properties when a parent exists, otherwise from the `body` defaults. Inherited set: `color font-size font-weight font-style font-family line-height text-align list-style-type`.
2. Merge the user-agent sheet and the page sheet, applying **the UA sheet first**, so a page rule of equal specificity still wins through source order and no `!important` is needed. Cache the parsed UA sheet in a `Private Shared` field: parsing it once per element would be thousands of redundant parses per page.
3. Collect winners: higher specificity always wins; on a tie the later declaration wins. Iterate in a fixed order — UA rules, then page rules, in source order — and keep a running winner per property name.
4. Expand shorthands before applying: `margin` and `padding` (1/2/3/4 values, mirroring the 2nd and 4th from the 3rd), and `border` (width/style/colour in any order, each piece classified by shape: leading digit is a width, a known style keyword is the style, anything else is the colour). Apply shorthand expansion **first**, so a later longhand in the same rule overrides it.
5. Resolve lengths with the `parseLength` rules asserted in Step 1: `px`, `pt` (×96/72), `em` (× parent font size), `rem` (× root font size), bare `0`, and `auto` → `-1`. An unparsable value leaves the inherited/default value alone rather than becoming zero — **a wrong colour is a wrong page, but a margin that silently becomes 0 collapses the layout**.

- [ ] **Step 6: Declare the two files and extend the parity checks**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`:

```xml
    <Compile Include="Engine\Native\UserAgentStylesheet.vb" />
    <Compile Include="Engine\Native\StyleResolver.vb" />
```

In `tools/proto/csscascade.mjs`, add these three assertions to the `VB file parity (CssParser)` section:

```js
const resolver = readIfPresent('BrowserForWP.Core/Engine/Native/StyleResolver.vb');
check('StyleResolver.vb exists', resolver.length > 0);
check('StyleResolver.vb exposes Resolve', resolver.includes('Function Resolve'));
const uaSheet = readIfPresent('BrowserForWP.Core/Engine/Native/UserAgentStylesheet.vb');
check('the UA stylesheet is a VB constant', uaSheet.includes('Const Css'));
check('the UA stylesheet styles h1 and p', uaSheet.includes("h1{") && uaSheet.includes("p{"));
```

- [ ] **Step 7: Run the check to verify it passes**

Run: `node tools/proto/csscascade.mjs`
Expected: `47/47 csscascade checks passed.` Exit 0.

- [ ] **Step 8: Build for real**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== BUILD_EXIT=0 ===`, no `BC` errors.

- [ ] **Step 9: Commit**

```bash
git add BrowserForWP.Core/Engine/Native/UserAgentStylesheet.vb \
        BrowserForWP.Core/Engine/Native/StyleResolver.vb \
        BrowserForWP.Core/Engine/Native/NodeTypes.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj tools/proto/csscascade.mjs
git commit -m "feat(engine): resolve the cascade, inheritance and lengths to px"
```

---

### Task 7: The box tree, and a dumper that makes the pipeline inspectable

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/BoxTreeBuilder.vb`
- Create: `BrowserForWP.Core/Diagnostics/DocumentDumper.vb`
- Create: `tools/proto/boxtree.mjs`
- Modify: `BrowserForWP.Core/Engine/Native/NodeTypes.vb` (add `BoxKind`, `BoxNode`)
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`

**Interfaces:**
- Consumes: `HtmlElement`, `ComputedStyle`, `StyleResolver`.
- Produces: `BoxKind` (`Block`, `Inline`, `Text`); `BoxNode(Kind, TagName, Text, Style, Children, Parent, Anonymous)` with `Function DescendantCount() As Integer`; `BoxTreeBuilder.Build(root As HtmlElement, pageSheet As Stylesheet) As BoxNode`; `BoxTreeBuilder.BuildPage(html As String, pageCss As String) As BoxNode`; `DocumentDumper.Dump(root As BoxNode) As String`.

- [ ] **Step 1: Write the failing check first**

Create `tools/proto/boxtree.mjs`. Task 8 inserts its wiring assertions before the marker `// ── VB file parity (BoxTreeBuilder) ──`, so keep that line exactly.

```js
#!/usr/bin/env node
// Prototype and referee for BoxTreeBuilder.vb and DocumentDumper.vb.
// The rule it exists to pin down: an inline run inside a block container gets its
// own anonymous block box, which is what lets Phase 2 lay out only blocks and
// inline runs and never worry about the mixture.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

// A hand-built, already-styled tree keeps this prototype about ONE rule: how
// elements and text become boxes. Styling has its own referee in csscascade.mjs.
function style(display) { return { display, isBlock: display === 'block', isHidden: display === 'none' }; }
function el(tag, st, children) { return { tagName: tag, children: children || [], style: st, text: '' }; }
function text(s) { return { tagName: '#text', text: s, children: [], style: style('inline') }; }

function build(node) {
  if (node.style && node.style.isHidden) return null;
  if (node.tagName === '#text') {
    if (!node.text.trim()) return null;   // whitespace-only text carries no box
    return { kind: 'text', tagName: '#text', text: node.text, style: node.style, children: [] };
  }
  const isBlock = node.style.isBlock;
  const children = [];
  for (const child of node.children) {
    const built = build(child);
    if (built === null) continue;
    if (!isBlock) { children.push(built); continue; }
    if (built.kind === 'block') children.push(built);
    else {
      const last = children[children.length - 1];
      if (last && last.anonymous) last.children.push(built);
      else children.push({ kind: 'block', tagName: '#anonymous', text: '', style: style('block'), children: [built], anonymous: true });
    }
  }
  return { kind: isBlock ? 'block' : 'inline', tagName: node.tagName, text: '', style: node.style, children };
}

check('hidden element is dropped entirely', build(el('script', style('none'), [text('var x')])) === null);
check('whitespace-only text produces no box',
  build(el('p', style('block'), [text('   ')])).children.length === 0);
check('a block root is a block box', (() => {
  const b = build(el('body', style('block'), [el('p', style('block'), [])]));
  return b.kind === 'block' && b.tagName === 'body';
})());
check('nested blocks stay nested', (() => {
  const b = build(el('body', style('block'), [el('div', style('block'), [el('p', style('block'), [])])]));
  return b.children[0].tagName === 'div' && b.children[0].children[0].tagName === 'p';
})());
check('a bare inline child is wrapped in an anonymous block', (() => {
  const b = build(el('body', style('block'), [text('hello')]));
  return b.children.length === 1 && b.children[0].tagName === '#anonymous'
    && b.children[0].kind === 'block' && b.children[0].children[0].kind === 'text';
})());
check('consecutive inline children share one anonymous block', (() => {
  const b = build(el('body', style('block'), [text('a'), el('em', style('inline'), [text('b')])]));
  return b.children.length === 1 && b.children[0].children.length === 2;
})());
check('an inline run is split around a block child', (() => {
  const b = build(el('body', style('block'), [
    text('before'), el('p', style('block'), [text('middle')]), text('after'),
  ]));
  return b.children.length === 3
    && b.children[0].kind === 'block' && b.children[0].anonymous === true
    && b.children[1].tagName === 'p'
    && b.children[2].anonymous === true;
})());
check('an inline container keeps inline children inline', (() => {
  const b = build(el('span', style('inline'), [text('x')]));
  return b.kind === 'inline' && b.children[0].kind === 'text' && b.children[0].anonymous === undefined;
})());
check('a document with only text still produces a block', (() => {
  const b = build(el('html', style('block'), [text('only text')]));
  return b.kind === 'block' && b.children.length === 1;
})());
check('a hidden child does not break the surrounding run', (() => {
  const b = build(el('div', style('block'), [text('a'), el('script', style('none'), []), text('b')]));
  return b.children.length === 1 && b.children[0].children.length === 2;
})());

function dump(node, indent = 0) {
  const pad = '  '.repeat(indent);
  const label = node.tagName === '#text' ? `#text "${node.text}"` : node.tagName;
  const lines = [`${pad}${node.kind[0]} ${label}`];
  for (const c of node.children) lines.push(dump(c, indent + 1));
  return lines.join('\n');
}
check('dump marks the box kind',
  dump(build(el('body', style('block'), [el('p', style('block'), [text('a')])]))).startsWith('b body'));
check('dump shows text content', dump(build(el('body', style('block'), [text('a')]))).includes('#text "a"'));
check('dump indents children',
  dump(build(el('body', style('block'), [el('p', style('block'), [])]))).includes('\n  b p'));

// ── VB file parity (BoxTreeBuilder) ─────────────────────────────────────────
const builder = readIfPresent('BrowserForWP.Core/Engine/Native/BoxTreeBuilder.vb');
check('BoxTreeBuilder.vb exists', builder.length > 0);
check('BoxTreeBuilder exposes BuildPage', builder.includes('BuildPage'));
check('DocumentDumper.vb exists', readIfPresent('BrowserForWP.Core/Diagnostics/DocumentDumper.vb').length > 0);

console.log(`\n${checks - failures}/${checks} boxtree checks passed.`);
if (failures > 0) {
  console.log(`${failures} boxtree failure(s).`);
  process.exit(1);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/boxtree.mjs`
Expected: FAIL, exit 1 — **3** failures, exactly the three parity checks. The 13 box and dumper assertions pass.

- [ ] **Step 3: Add the box types**

Append to `NodeTypes.vb` before `End Namespace`:

```vb
    Public Enum BoxKind
        Block
        Inline
        Text
    End Enum

    ''' <summary>
    ''' One node of the box tree: a block box, an inline box, or a text run.
    ''' Phase 2's layout consumes only this -- it never sees HTML again.
    ''' </summary>
    Public NotInheritable Class BoxNode

        Public Property Kind As BoxKind
        Public Property TagName As String = String.Empty
        Public Property Text As String = String.Empty
        Public Property Style As ComputedStyle
        Public Property Children As New List(Of BoxNode)()
        Public Property Parent As BoxNode

        ''' <summary>True for a block generated to hold a stray inline run.</summary>
        Public Property Anonymous As Boolean

        Public Function DescendantCount() As Integer
            Dim total As Integer = Children.Count
            For Each childItem In Children
                total += childItem.DescendantCount()
            Next
            Return total
        End Function
    End Class
```

- [ ] **Step 4: Write the builder**

Create `BrowserForWP.Core/Engine/Native/BoxTreeBuilder.vb`:

```vb
Public NotInheritable Class BoxTreeBuilder

    ''' <summary>Style and box an element tree that has already been parsed.</summary>
    Public Shared Function Build(root As HtmlElement, pageSheet As Stylesheet) As BoxNode

    ''' <summary>
    ''' The engine entry point: HTML text plus page CSS in, box tree out. Phase 2
    ''' calls this and nothing else.
    ''' </summary>
    Public Shared Function BuildPage(html As String, pageCss As String) As BoxNode
```

`BuildPage` is `HtmlTreeBuilder.Build(HtmlTokenizer.Tokenize(html))`, then `CssParser.Parse(pageCss)`, then `Build`. Keep it to four lines and let `Build` do the work — a second entry point that does its own parsing is how the two drift apart.

In `Build`, walk top-down passing the parent's `ComputedStyle` down (that is why the resolver takes it as a parameter), drop anything whose style is hidden, drop text boxes whose text is whitespace-only, and apply the anonymous-block rule exactly as the prototype does: inside a block container, consecutive inline and text boxes accumulate into a single generated block box; a real block box closes the run; every generated box gets `Anonymous = True`. A child that is dropped (hidden, or empty text) must **not** break the run — the prototype asserts that.

- [ ] **Step 5: Write the dumper**

Create `BrowserForWP.Core/Diagnostics/DocumentDumper.vb`:

```vb
' BrowserForWP — renders a box tree as indented text.
'
' This is how the pipeline becomes inspectable without a layout engine: on the
' device it shows exactly what the engine understood, which is the difference
' between "the page looks wrong" and "the engine dropped the article tag".

Imports System.Text
Imports BrowserForWP.Core.Engine.Native

Namespace Diagnostics

    ''' <summary>Indented, human-readable dump of a box tree.</summary>
    Public NotInheritable Class DocumentDumper

        Private Sub New()
        End Sub

        Public Shared Function Dump(root As BoxNode) As String
            Dim builder As New StringBuilder()
            Append(builder, root, 0)
            Return builder.ToString().TrimEnd()
        End Function

        Private Shared Sub Append(builder As StringBuilder, node As BoxNode, depth As Integer)
            If node Is Nothing OrElse builder Is Nothing Then Return
            ' Indentation is capped so a deeply nested page cannot produce a
            ' kilometre-wide line on a 480px screen.
            Dim cappedDepth As Integer = Math.Min(depth, 12)
            builder.Append(New String(" "c, cappedDepth * 2))

            builder.Append(BoxKindLetter(node.Kind))
            builder.Append(" "c)
            If node.Kind = BoxKind.Text Then
                builder.Append("#text """)
                builder.Append(Truncate(node.Text, 48))
                builder.Append(""""c)
            Else
                builder.Append(node.TagName)
                If node.Anonymous Then builder.Append(" (anonymous)")
            End If
            builder.Append(vbLf)

            For Each childItem In node.Children
                Append(builder, childItem, depth + 1)
            Next
        End Sub

        Private Shared Function BoxKindLetter(kind As BoxKind) As String
            Select Case kind
                Case BoxKind.Block
                    Return "b"
                Case BoxKind.Inline
                    Return "i"
                Case Else
                    Return "t"
            End Select
        End Function

        Private Shared Function Truncate(value As String, maxLength As Integer) As String
            Dim safeValue As String = If(value, String.Empty)
            If safeValue.Length <= maxLength Then Return safeValue
            Return safeValue.Substring(0, maxLength) & "..."
        End Function
    End Class

End Namespace
```

- [ ] **Step 6: Declare the two files**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`:

```xml
    <Compile Include="Engine\Native\BoxTreeBuilder.vb" />
    <Compile Include="Diagnostics\DocumentDumper.vb" />
```

- [ ] **Step 7: Run the check to verify it passes**

Run: `node tools/proto/boxtree.mjs`
Expected: `16/16 boxtree checks passed.` Exit 0.

- [ ] **Step 8: Build for real**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== BUILD_EXIT=0 ===`, no `BC` errors.

- [ ] **Step 9: Commit**

```bash
git add BrowserForWP.Core/Engine/Native/BoxTreeBuilder.vb \
        BrowserForWP.Core/Diagnostics/DocumentDumper.vb \
        BrowserForWP.Core/Engine/Native/NodeTypes.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj tools/proto/boxtree.mjs
git commit -m "feat(engine): build the box tree and dump it for inspection"
```

---

### Task 8: Make the whole pipeline visible on the device

**Files:**
- Modify: `BrowserForWP/MainPage.xaml`, `BrowserForWP/MainPage.xaml.vb`
- Modify: `BrowserForWP/Strings/en-US/Resources.resw`, `BrowserForWP/Strings/it-IT/Resources.resw`
- Modify: `tools/proto/boxtree.mjs`
- Modify: `docs/MAINTAINING.md`, `.agents/skills/browserforwp/SKILL.md`, `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `Diagnostics.NetDocumentFetcher`, `Engine.Native.BoxTreeBuilder.BuildPage`, `Diagnostics.DocumentDumper.Dump`, `Core.Storage.AppSettings.DohUrl`, `Core.Browser.BrowserSession.ActiveTab.Url`.
- Produces: three resource keys — `ParseThisPage`, `ParseNoDocument`, `ParseFailed`.

- [ ] **Step 1: Write the failing check first**

Insert this block into `tools/proto/boxtree.mjs` **immediately before** `// ── VB file parity (BoxTreeBuilder) ──`:

```js
// ── wiring parity (Task 8) ──────────────────────────────────────────────────
const xaml = readIfPresent('BrowserForWP/MainPage.xaml');
const main = readIfPresent('BrowserForWP/MainPage.xaml.vb');
const enRes = readIfPresent('BrowserForWP/Strings/en-US/Resources.resw');
const itRes = readIfPresent('BrowserForWP/Strings/it-IT/Resources.resw');

check('the diagnostics view has a parse button', xaml.includes('ParsePageButton'));
check('the parse handler exists', main.includes('ParsePageButton_Click'));
check('the result block exists', xaml.includes('ParseResult'));
check('the handler uses the TLS 1.3 fetcher', main.includes('NetDocumentFetcher'));
check('the handler runs the pipeline', main.includes('BuildPage'));
check('the handler dumps the tree', main.includes('DocumentDumper'));
check('the handler reads <style> text', main.includes('InlineStyleText'));
for (const key of ['ParseThisPage', 'ParseNoDocument', 'ParseFailed']) {
  check(`${key} in en-US`, enRes.includes(`name="${key}"`));
  check(`${key} in it-IT`, itRes.includes(`name="${key}"`));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/boxtree.mjs`
Expected: FAIL, exit 1 — **16** failures: the previous 3 parity checks plus all 13 wiring checks.

- [ ] **Step 3: Add the XAML**

In `BrowserForWP/MainPage.xaml`, inside `DiagnosticsOverlay`, after the `PinStatus` `TextBlock`:

```xml
                    <Button x:Name="ParsePageButton" Margin="0,8,0,6"
                            Click="ParsePageButton_Click"/>
                    <TextBlock x:Name="ParseResult" TextWrapping="Wrap"
                               FontFamily="Consolas" FontSize="12"
                               MaxHeight="220" Margin="0,0,0,8" Opacity="0.85"/>
```

- [ ] **Step 4: Add the three keys to both languages**

`en-US`:

```xml
  <data name="ParseThisPage" xml:space="preserve">
    <value>Parse current page</value>
  </data>
  <data name="ParseNoDocument" xml:space="preserve">
    <value>Nothing to parse: the tab has no address, or the response was not HTML.</value>
  </data>
  <data name="ParseFailed" xml:space="preserve">
    <value>Fetch failed.</value>
  </data>
```

`it-IT`:

```xml
  <data name="ParseThisPage" xml:space="preserve">
    <value>Analizza la pagina corrente</value>
  </data>
  <data name="ParseNoDocument" xml:space="preserve">
    <value>Niente da analizzare: la scheda non ha indirizzo, oppure la risposta non è HTML.</value>
  </data>
  <data name="ParseFailed" xml:space="preserve">
    <value>Recupero non riuscito.</value>
  </data>
```

- [ ] **Step 5: Wire the handler**

In `BrowserForWP/MainPage.xaml.vb`, add `Imports BrowserForWP.Core.Engine.Native` to the imports block, add the label in `ApplyLocalizedStrings` next to `PinRemoveButton.Content`:

```vb
        ParsePageButton.Content = Localizer.Get("ParseThisPage")
```

and the handler at the end of the class:

```vb
    ''' <summary>
    ''' Fetch the active tab's URL over the app's own TLS 1.3 transport and show
    ''' what the native pipeline understood. This is the demonstration that the
    ''' engine exists: it is the only place in the product where a page LOAD goes
    ''' over Tls13Client rather than through the WebView's Schannel path.
    ''' </summary>
    Private Async Sub ParsePageButton_Click(sender As Object, e As RoutedEventArgs)
        ParsePageButton.IsEnabled = False
        Try
            Dim tabUrl As String = _session.ActiveTab.Url
            If String.IsNullOrEmpty(tabUrl) Then
                ParseResult.Text = Localizer.Get("ParseNoDocument")
                Return
            End If

            Dim fetcher As New BrowserForWP.Diagnostics.NetDocumentFetcher()
            Dim response As DocumentResponse = Await fetcher.FetchAsync(tabUrl, _appSettings.DohUrl)

            If Not String.IsNullOrEmpty(response.Error) Then
                ParseResult.Text = Localizer.Get("ParseFailed") & " " & response.Error
                Return
            End If
            If Not response.IsHtml Then
                ParseResult.Text = Localizer.Get("ParseNoDocument")
                Return
            End If

            Dim boxTree As BoxNode = BoxTreeBuilder.BuildPage(response.Text, InlineStyleText(response.Text))
            Dim headerText As String = response.FinalUrl & "  [" & response.EffectiveCharset & "]  " &
                                       boxTree.DescendantCount() & " box(es)" & vbCrLf
            ParseResult.Text = headerText & BrowserForWP.Core.Diagnostics.DocumentDumper.Dump(boxTree)
        Catch ex As Exception
            ParseResult.Text = Localizer.Get("ParseFailed") & " " & ex.Message
        Finally
            ParsePageButton.IsEnabled = True
        End Try
    End Sub

    ''' <summary>
    ''' Collect the text of every inline style element, because the tree builder
    ''' drops script and style bodies. Anything more would need a real head parser.
    ''' </summary>
    Private Shared Function InlineStyleText(html As String) As String
        If String.IsNullOrEmpty(html) Then Return String.Empty
        Dim collected As New System.Text.StringBuilder()
        Dim lowered As String = html.ToLowerInvariant()
        Dim searchFrom As Integer = 0
        While True
            Dim openAt As Integer = lowered.IndexOf("<style", searchFrom, StringComparison.Ordinal)
            If openAt < 0 Then Exit While
            Dim bodyStart As Integer = lowered.IndexOf(">"c, openAt)
            If bodyStart < 0 Then Exit While
            Dim closeAt As Integer = lowered.IndexOf("</style", bodyStart)
            If closeAt < 0 Then Exit While
            collected.Append(html.Substring(bodyStart + 1, closeAt - bodyStart - 1))
            collected.Append(vbLf)
            searchFrom = closeAt + 1
        End While
        Return collected.ToString()
    End Function
```

- [ ] **Step 6: Run the parity check to verify it passes**

Run: `node tools/proto/boxtree.mjs`
Expected: `32/32 boxtree checks passed.` Exit 0.

- [ ] **Step 7: Build for real, then run the whole floor**

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== Real compiler errors ===` / `none` then `=== BUILD_EXIT=0 ===`.

Then, on the host:

```bash
node tools/check-vb.mjs
node tools/gen-vectors.mjs
node tools/check-polyfill.mjs
for s in ie-adapt probe-verdict fetch-rules htmlparse csscascade boxtree core-logic; do node tools/proto/$s.mjs; done
python3 tools/make_logo.py
```
Expected: `0 finding(s)`; `53 assertions, 0 failure(s)`; `is valid ES5`; every proto script reporting `checks passed` with exit 0; `done.` with no git diff. `check-vb.mjs` is the one that catches an undeclared `.vb`, an unwired XAML handler, or a resw key present in one language only — this task touches all three.

- [ ] **Step 8: Record the new capability honestly**

In `README.md`, add to the "Modern web pages" area and the disclosure list:

```markdown
- **A native document engine is being built** (`BrowserForWP.Core/Engine/Native`).
  It fetches a page over the app's own TLS 1.3 transport — the only path in this
  product that can load anything above TLS 1.2 — and parses a **declared subset**
  of HTML and CSS into a box tree. It does **not** execute JavaScript and never
  will; pages that need scripting are Trident's job, through the compatibility
  layer. Layout and rendering are the next phase; today the pipeline's output is
  visible under **Diagnostics → Parse current page**.
```

In `README.it.md`:

```markdown
- **Un motore di documenti nativo è in costruzione**
  (`BrowserForWP.Core/Engine/Native`). Recupera una pagina attraverso il
  trasporto TLS 1.3 dell'app — l'unico percorso di questo prodotto che può
  caricare qualcosa sopra TLS 1.2 — e analizza un **sottoinsieme dichiarato** di
  HTML e CSS producendo un albero di box. **Non esegue JavaScript** e mai lo
  farà; le pagine che richiedono script restano compito di Trident, tramite il
  livello di compatibilità. Il layout e il disegno sono la fase successiva; oggi
  l'output della pipeline è visibile in **Diagnostica → Analizza la pagina
  corrente**.
```

Add the six new scripts to the verification table in `.agents/skills/browserforwp/SKILL.md` and to `docs/MAINTAINING.md` § "The verification commands", and add `Engine/Native/` plus `tools/proto/{htmlparse,csscascade,boxtree}.mjs` to the repo-layout block in both.

- [ ] **Step 9: Commit**

```bash
git add BrowserForWP/MainPage.xaml BrowserForWP/MainPage.xaml.vb \
        BrowserForWP/Strings/en-US/Resources.resw BrowserForWP/Strings/it-IT/Resources.resw \
        tools/proto/boxtree.mjs README.md README.it.md \
        docs/MAINTAINING.md .agents/skills/browserforwp/SKILL.md
git commit -m "feat(engine): expose the document pipeline and load a page over TLS 1.3"
```

---

## Roadmap: Phase 2 (a separate plan, not written here)

This plan stops at a box tree on purpose. Phase 2 is a separate plan because it is a separate subsystem with its own referee, and because a plan carrying both would be too large to execute reliably. Its tasks, in order, with the interfaces this plan already fixed:

1. **Text measurement seam** — `Engine.Native.ITextMeasurer.Measure(text As String, style As ComputedStyle) As Double` in Core, `XamlTextMeasurer` in the app (a `TextBlock` plus `Measure`), and a `FixedAdvanceTextMeasurer` in Core so `tools/proto/boxlayout.mjs` stays deterministic.
2. **Block layout** — `BlockLayout.Layout(root As BoxNode, viewportWidthPx As Double, measurer As ITextMeasurer) As LaidOutBox`. Widths, `auto` margins, child offsets.
3. **Inline layout and line breaking** — line boxes, breaking at spaces, `text-align`, `line-height`.
4. **Margin collapsing** — adjacent sibling, parent/first-child, parent/last-child.
5. **XAML rendering** — the laid-out tree to a `Canvas` of `TextBlock`/`Rectangle`/`Image`, inside a `ScrollViewer`.
6. **`NativeEngine`** — `Implements IBrowserEngine`; `Source` returns the `ScrollViewer`, so `ContentHost.Child = DirectCast(_engine.Source, UIElement)` works unchanged; `InvokeScriptAsync` returns an empty string and `Capabilities.SupportsTls13 = True`, `SupportsModernJavaScript = False`.
7. **Engine switching** — a Settings choice, plus automatic fallback when `CompatibilityProbe.CouldRun` is `False` or `MissingFeatures.Count >= 8`.
8. **`img` and bare tables**, only if a real page needs them.

`EngineCapabilities.SupportsTls13 = True` for `NativeEngine` is the honest payoff of this line of work: it is the only engine in this product that may claim it, and after Task 3 it will be true of a page load rather than only of a probe.

---

## Verification matrix

Every task ends with its own gate. These are the commands, with the counts this plan produces.

| Layer touched | Command | Expected |
| --- | --- | --- |
| Any `.vb` / `.vbproj` / `.xaml` / `.resw` | `node tools/check-vb.mjs` | `0 finding(s)`, exit 0 |
| Any `.vb` at all | `tools\vm-build.cmd /t:Rebuild` in the guest | `BUILD_EXIT=0`, no `BC` errors, only the two `ResourceLoader` warnings |
| Crypto / vectors | `node tools/gen-vectors.mjs` | `53 assertions, 0 failure(s)` |
| `compat.js` | `node tools/check-polyfill.mjs` | `is valid ES5` |
| IE-adaptation record | `node tools/proto/ie-adapt.mjs` | `9/9 checks passed` |
| Probe verdict | `node tools/proto/probe-verdict.mjs` | `9/9 checks passed` |
| Fetch rules | `node tools/proto/fetch-rules.mjs` | `18/18 checks passed` |
| HTML pipeline | `node tools/proto/htmlparse.mjs` | `29/29 checks passed` |
| CSS pipeline | `node tools/proto/csscascade.mjs` | `47/47 checks passed` |
| Box tree and wiring | `node tools/proto/boxtree.mjs` | `32/32 checks passed` |
| Core `Browser` / storage logic | `node tools/proto/core-logic.mjs` | `0 failure(s)`, 53 assertions |
| Assets | `python3 tools/make_logo.py` | 12 PNGs, no git diff |
| On a handset (cannot run here) | Diagnostics → **Parse current page** on a real URL | box tree dumped; record the real output in `docs/MAINTAINING.md` |

---

## Self-Review

**Spec coverage.** "Build an engine from scratch": Tasks 4–7 are the tokenizer, tree builder, CSS parser, selector matcher, cascade and box tree; the Roadmap names the layout and rendering that complete it, as a separate plan. "Or adapt one like Internet Explorer": Task 1, closed with an instrument rather than an opinion. Both readings of the request have a task, and what genuinely cannot be done — JavaScript, ES6, `Proxy`, `Intl`, grid/flex — is declared out of scope instead of quietly omitted.

**Prototypes were executed, not imagined.** Every script in this plan was run during planning. That found three real defects, all fixed above:

1. The first `htmlparse.mjs` never created an implicit `<head>`, so `<title>` landed in the body and `find(root, 'head')` returned `null`; it also let `script` bodies become document text. Both are now asserted.
2. The first cascade assertion read `w.decl.color` when a declaration is `{name, value}`, so it rejected a correct implementation. Recorded in Task 5 because the failure mode — "fixing" correct code to satisfy a broken check — is worse than the bug it looked like.
3. Every negative assertion written against a missing file passed vacuously (`!/Accept-Encoding/.test('')` is `true`). All six scripts now assert existence and non-emptiness separately, and the rule is stated in Global Constraints.

**Placeholder scan.** No `TBD`, no "add error handling", no "similar to Task N". Every code step carries real code, except the two-step Tasks 4 and 5, where the plan names each helper the VB port needs, gives the exact public signature, and points at the prototype function it transliterates — that is this repository's prototype-first rule, and the prototype in the same task is its complete specification, asserted line by line.

**Type consistency.** `HtmlElement.Attribute`/`IsText`, `CssSelector.Specificity`, `ComputedStyle` property names, `BoxNode.Kind`/`Anonymous`/`DescendantCount`, `DocumentResponse.IsHtml`/`EffectiveCharset`, `IDocumentFetcher.FetchAsync(url, dohUrl)`, `BoxTreeBuilder.Build`/`BuildPage`, `DocumentDumper.Dump`, `ProbeReport.CouldRun`, `IeModeProbe.RunAsync` — each is defined once and used with the same name and shape everywhere it appears, including in the Node mirrors, which share the vocabulary deliberately.

**Counting the failures honestly.** Each task's Step 2 states the exact number and identity of the failures expected before implementation, so "not written yet" cannot be confused with "written wrong". Those numbers were measured, not estimated: 9, 5, 7, 3, 3, 3, and 16 respectively, and each script was confirmed to exit 1 in that state.

**Two things this plan cannot verify, stated rather than hidden.** The latin1 branch in `NetDocumentFetcher` may hit a profile gap (`BC30456`); Task 3 Step 7 says what to do when it does. And nothing here runs on a handset, so Task 8's on-device output is recorded as pending in the verification matrix and in Task 1's verdict.

---

## Outcome — what executing this plan actually produced

Appended rather than rewritten: the plan above is what was planned, and this is
what it cost. Every number below was measured.

**All eight tasks landed.** `BrowserForWP.Core/Engine/Native/` now holds the
`IDocumentFetcher` seam, the HTML tokenizer and tree builder, the CSS parser, the
selector matcher, the cascade, the box tree, and `Diagnostics/DocumentDumper.vb`,
and the diagnostics view exposes **Parse current page**. It also exposes **Check
IE mode**, which the plan did not ask for — Task 1 promises that an IE-mode tap is
how a handset turns the decision into a measurement, and no task built one, so the
instrument would have shipped unreachable.

**Eight defects came out of executing it, which reading it had not.**

1. `Public Property Error As String` is `BC30183`: `Error` is a reserved VB
   keyword. It also drags a spurious `BC42312` onto the correct doc comment above
   it, so the message you chase is the wrong one. Renamed to `ErrorMessage`.
2. `Microsoft.VisualBasic.ControlChars` is absent from the Store profile
   (`BC30451`) even though `Microsoft.VisualBasic.Strings` is present. Fourth
   profile gap on record; `tools/check-vb.mjs` now flags it.
3. `Encoding.GetEncoding("ISO-8859-1")` **compiles**, so the `BC30456` this plan's
   Task 3 Step 7 anticipated never arrived. Whether it *resolves* at run time is a
   question the compiler cannot answer, so the latin1 branch asks and falls back
   instead of assuming.
4. The prototype's void-element assertion expected two children for
   `<p>a<br>b</p>`, which has three — it rejected a correct implementation, the
   same class of error as the cascade assertion the plan already recorded.
5. `fetch-rules.mjs` asserted that `NetDocumentFetcher.vb` never names
   `Accept-Encoding` while reading the comments, so it forbade documenting the rule
   it enforced; `IDocumentFetcher.vb` failed the same way. Both strip VB comments
   now — never string literals — and carry negative controls.
6. The resolver's first draft kept cascade state in `Shared` dictionaries, which
   would have leaked one element's winners into the next. Caught before any build.
7. Task 8's own code contained `" box(es)"` inline: hardcoded English, which this
   plan's Global Constraints forbid. It is now `ParseBoxCount` in both languages.
8. Task 1's `IeModeProbe` had no caller anywhere in the plan, so the measurement
   the task's own text promises could not have been taken. See above.

**The verification matrix is wrong in three rows, and "Counting the failures
honestly" is wrong in five.** Measured with `tail -1` on each script:

| Script | This plan said | It is | Why |
| --- | --- | --- | --- |
| `ie-adapt.mjs` | 9 | 9 | — |
| `probe-verdict.mjs` | 9 | 9 | — |
| `fetch-rules.mjs` | 18 | **25** | two negative controls for the comment stripper, plus four charset-fallback rules |
| `htmlparse.mjs` | 29 | **30** | simply miscounted by one |
| `csscascade.mjs` | 47 | 47 | — |
| `boxtree.mjs` | 32 | **39** | the IE-mode wiring checks Task 1's ruling required |

The pre-implementation failure counts are not `9, 5, 7, 3, 3, 3, 16` either.
Tasks 6 and 8 each have a RED state whose identity depends on which earlier task
has already merged — after Task 5, `CssParser.vb` exists, so those three parity
checks pass — and Task 6 Step 6 adds four parity assertions where its Step 2 lists
three. Measured: 9, then 5, then 7, then 3, then 3, then 4, then 3, then 21.

**Still unverified, and it is the same gap as always.** Nothing here has run on a
handset. The prototypes prove the algorithms, the guest build proves the ports
compile, and neither proves the ports behave as their prototypes do — a gap this
plan's own Task 4 note states and cannot close. The matrix's last row, running
**Diagnostics → Parse current page** on a real URL, is still pending; so is the
`documentMode` reading that would close Task 1.
