# Native Engine Phase 2a — Layout and XAML Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native engine draw a real page: measured text, block and inline layout, and XAML output, reachable from Diagnostics on a URL fetched over the app's own TLS 1.3 transport.

**Architecture:** Phase 1 (`2026-09-29-native-engine-pipeline.md`) produces a `BoxNode` tree and stops. This plan adds the two things it deliberately left out. A text-measurement seam (`ITextMeasurer`) lives in Core and is answered by the app, because measuring text is a XAML operation and Core must not know about XAML — the same split `IBrowserEngine` already uses. On top of it, `BlockLayout` and `InlineLayout` turn the box tree into positioned `LayoutBox` nodes, and `XamlBoxRenderer` draws those into a `Canvas` inside a `ScrollViewer`.

**Tech Stack:** VB.NET 12 / .NET for Windows Store apps (WP8.1), WinRT XAML, MSBuild 12 in the ARM64 guest, Node.js prototypes as executable referees.

## Global Constraints

- Visual Studio 2013 / VB 12. **No leading-dot implicit line continuation** (VB 14+ feature); use `With` blocks. Enforced by `tools/check-vb.mjs` group 11.
- Core projects compile with **`Option Strict On`**. Every declaration needs an explicit type; no late binding.
- **VB is case-insensitive.** No local may share a name with a type, a member or a keyword in scope (`size`, `color`, `line`, `region`, `tag` are all traps). Name locals after what they hold.
- Absent from the .NET for Windows Store apps profile, so unusable: `List(Of T).AsReadOnly()`, `Encoding.ASCII`, `RegexOptions.Compiled`, `System.Security.Cryptography`, `ControlChars`.
- Every new `.vb` file **must** be declared in its `.vbproj` with `<Compile Include>`; `tools/check-vb.mjs` group 3 fails otherwise.
- `BrowserForWP/Strings/{en-US,it-IT}/Resources.resw` must define **exactly the same keys** (group 6).
- No user-visible English may be hardcoded in VB; every word comes from `Localizer.Get`.
- Never edit `tests/BrowserForWP.Crypto.Tests/Vectors.generated.vb` or `tools/wp81-theme-keys.txt` by hand.
- Never hand-edit `BrowserForWP.Crypto/X25519.vb` or `BrowserForWP.Net/Tls13/*` — change the prototype first.
- All geometry is in **device-independent pixels**, and so is the viewport width.
- The word `LayoutKind` is a `System.Runtime.InteropServices` type name; this plan uses `LayoutBoxKind` to avoid it.
- Windows Store profile has no CSS: this plan implements **content-box** sizing only. `box-sizing` is not read.

## Scope

**In scope:** text measurement; block width/height/position for static, non-floated, non-positioned blocks; inline flow with line breaking on whitespace; `text-align` left/centre/right; `line-height`; XAML output of blocks (background + border), text runs, and their colours; a Diagnostics entry point that renders the current tab's page.

**Explicitly out of scope, and named so nobody assumes it is coming:** JavaScript or any script execution; `float`/`position`; auto-margin centring; margin collapsing; `img`; tables; flexbox; grid; `min-height`; `box-sizing`; text baselines with mixed font sizes on one line (a line's height comes from its container's font).

**Why this stops where it stops:** Phases are plans, and a plan that carries layout *and* engine switching *and* images is too large to execute reliably. This plan ends with a page visible on the device, which is the first point at which the work can be judged.

## File Structure

| File | Create/Modify | Responsibility |
| --- | --- | --- |
| `BrowserForWP.Core/Engine/Native/ITextMeasurer.vb` | Create | The measuring question Core cannot answer itself. Two members, no implementation. |
| `BrowserForWP.Core/Engine/Native/FixedAdvanceTextMeasurer.vb` | Create | Deterministic measurer: 0.5 em per character. The referee's measurer, so every number the Node prototype asserts is predictable. |
| `BrowserForWP.Core/Engine/Native/LayoutBox.vb` | Create | One positioned node plus `LayoutBoxKind`. Knows its own content box. |
| `BrowserForWP.Core/Engine/Native/BlockLayout.vb` | Create | Box tree → positioned tree. Blocks stack; inline runs are delegated. |
| `BrowserForWP.Core/Engine/Native/InlineLayout.vb` | Create | Words → line boxes → alignment. |
| `BrowserForWP/Rendering/XamlTextMeasurer.vb` | Create | The real measurer: a `TextBlock` and `Measure`. |
| `BrowserForWP/Rendering/XamlBoxRenderer.vb` | Create | Positioned tree → `Canvas` of `TextBlock` / `Rectangle`, wrapped in a `ScrollViewer`. |
| `BrowserForWP.Core/BrowserForWP.Core.vbproj` | Modify | Declare the five new Core files. |
| `BrowserForWP/BrowserForWP.vbproj` | Modify | Declare the two new app files. |
| `BrowserForWP/MainPage.xaml` | Modify | The button and the host `Border` in the diagnostics panel. |
| `BrowserForWP/MainPage.xaml.vb` | Modify | Button label wiring and the handler that fetches, lays out and renders. |
| `BrowserForWP/Strings/en-US/Resources.resw`, `it-IT/Resources.resw` | Modify | One new key: the button label. |
| `tools/proto/textmeasure.mjs` | Create | Referee for the measurer. |
| `tools/proto/boxlayout.mjs` | Create | Referee for block and inline layout. |
| `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb` | Modify | Mirror the measurer's arithmetic so the compiled-in assertion exists too. |
| `tools/proto/core-logic.mjs` | Modify | The executable half of the assertion above. |
| `docs/MAINTAINING.md` | Modify | Round 7, the two new prototype commands, the deferred list. |
| `.agents/skills/browserforwp/SKILL.md` | Modify | Verification rows and the file list. |

---

### Task 1: The text-measurement seam

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/ITextMeasurer.vb`
- Create: `BrowserForWP.Core/Engine/Native/FixedAdvanceTextMeasurer.vb`
- Create: `BrowserForWP/Rendering/XamlTextMeasurer.vb`
- Create: `tools/proto/textmeasure.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Modify: `BrowserForWP/BrowserForWP.vbproj`
- Test: `tools/proto/textmeasure.mjs`

**Interfaces:**
- Consumes: `ComputedStyle` (its `FontSizePx`, `LineHeightPx`, `FontFamily`, `FontWeight`, `FontStyle` members) from `BrowserForWP.Core/Engine/Native/NodeTypes.vb`.
- Produces: `ITextMeasurer` with `Function MeasureWidth(text As String, style As ComputedStyle) As Double` and `Function LineHeight(style As ComputedStyle) As Double`; `FixedAdvanceTextMeasurer` implementing both, with `Public Const AdvanceFactor As Double = 0.5` and `Public Const NormalLineHeightFactor As Double = 1.2`; `BrowserForWP.Rendering.XamlTextMeasurer` implementing both. Every later task takes an `ITextMeasurer` parameter and never measures text itself.

- [ ] **Step 1: Write the failing prototype**

Create `tools/proto/textmeasure.mjs`:

```js
#!/usr/bin/env node
// Prototype and referee for the text-measurement seam.
//
// Why a prototype for five lines of arithmetic: every number the layout referee
// asserts is a multiple of a character advance, so the advance has to be a
// constant that a Node script and the VB can both state. FixedAdvanceTextMeasurer
// is that constant. If this file and the VB disagree, layout is wrong on the
// device and right in Node, which is the hardest kind of wrong to find.
//
// Usage: node tools/proto/textmeasure.mjs

import fs from 'node:fs';

let passed = 0;
let failed = 0;
function check(label, condition) {
  if (condition) { passed++; console.log(`  ok   ${label}`); }
  else { failed++; console.log(`  FAIL ${label}`); }
}
function near(actual, expected) { return Math.abs(actual - expected) < 1e-9; }
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

// ── The prototype ──────────────────────────────────────────────────────────
const ADVANCE_FACTOR = 0.5;          // ems per character
const NORMAL_LINE_HEIGHT = 1.2;      // CSS's "normal"

function measureWidth(text, style) {
  if (!text) { return 0; }
  return text.length * style.fontSizePx * ADVANCE_FACTOR;
}
function lineHeight(style) {
  if (style.lineHeightPx > 0) { return style.lineHeightPx; }
  return style.fontSizePx * NORMAL_LINE_HEIGHT;
}
function styleOf(fontSizePx, lineHeightPx) {
  return { fontSizePx: fontSizePx, lineHeightPx: lineHeightPx === undefined ? -1 : lineHeightPx };
}

check('empty text has zero width', measureWidth('', styleOf(16)) === 0);
check('width is half an em per character', near(measureWidth('abcd', styleOf(16)), 32));
check('width scales with font size', near(measureWidth('abcd', styleOf(32)), 64));
check('normal line height is 1.2 x font size', near(lineHeight(styleOf(16)), 19.2));
check('a resolved line height wins over normal', near(lineHeight(styleOf(16, 22.4)), 22.4));

// ── Parity with the VB port ────────────────────────────────────────────────
const iface = readIfPresent('BrowserForWP.Core/Engine/Native/ITextMeasurer.vb');
const fixedMeasurer = readIfPresent('BrowserForWP.Core/Engine/Native/FixedAdvanceTextMeasurer.vb');
const xamlMeasurer = readIfPresent('BrowserForWP/Rendering/XamlTextMeasurer.vb');

check('ITextMeasurer.vb exists', iface.length > 0);
check('FixedAdvanceTextMeasurer.vb exists', fixedMeasurer.length > 0);
check('XamlTextMeasurer.vb exists', xamlMeasurer.length > 0);
check('the VB uses the same 0.5 advance',
  fixedMeasurer.includes('AdvanceFactor As Double = 0.5'));
check('the VB uses the same 1.2 normal line height',
  fixedMeasurer.includes('NormalLineHeightFactor As Double = 1.2'));

console.log(`\n${passed}/${passed + failed} checks passed.`);
if (failed > 0) { process.exit(1); }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/textmeasure.mjs`
Expected: exit 1, `5/10 checks passed.` — the five arithmetic checks pass (the algorithm is proven), and exactly **5** failures, one per parity check, because none of the three VB files exists yet.

- [ ] **Step 3: Write the interface**

Create `BrowserForWP.Core/Engine/Native/ITextMeasurer.vb`:

```vb
' BrowserForWP — the text-measurement seam.
'
' Layout needs to know how wide a string is before it can break a line, and only
' the view layer can answer that: measuring text is a XAML operation. So Core asks
' the question and the app answers it, exactly as IBrowserEngine does for the
' engine itself.
'
' Two implementations exist on purpose:
'   * FixedAdvanceTextMeasurer -- arithmetic, so the numbers layout produces are
'     reproducible in tools/proto/boxlayout.mjs off-device.
'   * BrowserForWP.Rendering.XamlTextMeasurer -- the real one, a TextBlock probe.

Namespace Engine.Native

    ''' <summary>Measures text. The one thing layout cannot compute by itself.</summary>
    Public Interface ITextMeasurer

        ''' <summary>
        ''' Width of text in device-independent pixels when it is not broken: one
        ''' line, no wrapping.
        ''' </summary>
        Function MeasureWidth(text As String, style As ComputedStyle) As Double

        ''' <summary>
        ''' Height of one line box for this style: the resolved line-height when the
        ''' page set one, and 1.2 x font-size when it did not (CSS's "normal").
        ''' </summary>
        Function LineHeight(style As ComputedStyle) As Double

    End Interface

End Namespace
```

- [ ] **Step 4: Write the deterministic measurer**

Create `BrowserForWP.Core/Engine/Native/FixedAdvanceTextMeasurer.vb`:

```vb
' BrowserForWP — text measurement by arithmetic.
'
' Not the renderer's measurer: the app uses XamlTextMeasurer. This one exists so
' that layout is testable off-device, and so that any caller which needs the same
' answer twice gets it. Its two constants are asserted by
' tools/proto/textmeasure.mjs, which hardcodes the same values.

Namespace Engine.Native

    ''' <summary>Measures text as 0.5 em per character. Deterministic.</summary>
    Public NotInheritable Class FixedAdvanceTextMeasurer
        Implements ITextMeasurer

        ''' <summary>Ems per character. tools/proto/boxlayout.mjs assumes the same.</summary>
        Public Const AdvanceFactor As Double = 0.5

        ''' <summary>CSS's "normal" line-height, as a multiple of the font size.</summary>
        Public Const NormalLineHeightFactor As Double = 1.2

        Public Function MeasureWidth(text As String, style As ComputedStyle) As Double Implements ITextMeasurer.MeasureWidth
            If String.IsNullOrEmpty(text) OrElse style Is Nothing Then Return 0
            Return text.Length * style.FontSizePx * AdvanceFactor
        End Function

        Public Function LineHeight(style As ComputedStyle) As Double Implements ITextMeasurer.LineHeight
            If style Is Nothing Then Return 0
            If style.LineHeightPx > 0 Then Return style.LineHeightPx
            Return style.FontSizePx * NormalLineHeightFactor
        End Function

    End Class

End Namespace
```

- [ ] **Step 5: Write the XAML measurer**

Create `BrowserForWP/Rendering/XamlTextMeasurer.vb`:

```vb
' BrowserForWP — the real text measurer: XAML's own.
'
' Core declares ITextMeasurer and cannot implement it, because measuring text is a
' XAML operation and Core does not reference XAML. This is the app's answer, and it
' uses the same TextBlock the renderer will use, so the measurement and the drawing
' cannot disagree about a font.
'
' Must be called on the UI thread. Laying out a page is a UI-thread operation
' anyway, so no caller has to arrange that.

Imports BrowserForWP.Core.Engine.Native
Imports Windows.UI.Text
Imports Windows.UI.Xaml.Controls
Imports Windows.UI.Xaml.Media

Namespace Rendering

    ''' <summary>Measures by asking a TextBlock, which is what will draw the text.</summary>
    Public NotInheritable Class XamlTextMeasurer
        Implements ITextMeasurer

        Public Function MeasureWidth(text As String, style As ComputedStyle) As Double Implements ITextMeasurer.MeasureWidth
            If String.IsNullOrEmpty(text) OrElse style Is Nothing Then Return 0
            Dim probe As TextBlock = BuildProbe(text, style)
            probe.Measure(New Windows.Foundation.Size(Double.PositiveInfinity, Double.PositiveInfinity))
            Return probe.DesiredSize.Width
        End Function

        Public Function LineHeight(style As ComputedStyle) As Double Implements ITextMeasurer.LineHeight
            If style Is Nothing Then Return 0
            If style.LineHeightPx > 0 Then Return style.LineHeightPx
            ' "normal" means "whatever this font needs", so ask the font rather than
            ' assuming 1.2 -- the constant is only a fallback for the fixed measurer.
            Dim probe As TextBlock = BuildProbe("Mg", style)
            probe.Measure(New Windows.Foundation.Size(Double.PositiveInfinity, Double.PositiveInfinity))
            Return probe.DesiredSize.Height
        End Function

        Private Shared Function BuildProbe(text As String, style As ComputedStyle) As TextBlock
            Dim probe As New TextBlock()
            probe.Text = text
            probe.FontFamily = New FontFamily(CleanFontFamily(style.FontFamily))
            probe.FontSize = style.FontSizePx
            If style.FontWeight >= 600 Then
                probe.FontWeight = FontWeights.Bold
            Else
                probe.FontWeight = FontWeights.Normal
            End If
            If style.FontStyle = "italic" Then
                probe.FontStyle = FontStyles.Italic
            Else
                probe.FontStyle = FontStyles.Normal
            End If
            Return probe
        End Function

        ''' <summary>
        ''' CSS font stacks are quoted and comma separated ("'Segoe UI',Arial"); XAML
        ''' takes one family name. Unquoted, first entry, or a safe default.
        ''' </summary>
        Private Shared Function CleanFontFamily(cssFamily As String) As String
            If String.IsNullOrEmpty(cssFamily) Then Return "Segoe UI"
            Dim firstName As String = cssFamily.Split(","c)(0).Trim()
            firstName = firstName.Replace("'"c, " "c)
            firstName = firstName.Replace(ChrW(34), " "c)
            firstName = firstName.Trim()
            If firstName.Length = 0 Then Return "Segoe UI"
            Return firstName
        End Function

    End Class

End Namespace
```

- [ ] **Step 6: Declare all three files in their projects**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`, inside the `<ItemGroup>` that holds the `<Compile>` items and immediately after the line that reads exactly:

```xml
    <Compile Include="Engine\Native\BoxTreeBuilder.vb" />
```

insert:

```xml
    <Compile Include="Engine\Native\ITextMeasurer.vb" />
    <Compile Include="Engine\Native\FixedAdvanceTextMeasurer.vb" />
```

In `BrowserForWP/BrowserForWP.vbproj`, inside the `<ItemGroup>` that holds the `<Compile>` items and immediately after the line that reads exactly:

```xml
    <Compile Include="Diagnostics\NetDocumentFetcher.vb" />
```

insert:

```xml
    <Compile Include="Rendering\XamlTextMeasurer.vb" />
```

- [ ] **Step 7: Run the prototype and the checker**

Run: `node tools/proto/textmeasure.mjs`
Expected: exit 0, `10/10 checks passed.`

Run: `node tools/check-vb.mjs`
Expected: `0 finding(s)`, exit 0. If it reports `file exists on disk but is not in <Compile Include>`, Step 6 was skipped or the name is misspelled.

- [ ] **Step 8: Commit**

```bash
git add tools/proto/textmeasure.mjs \
        BrowserForWP.Core/Engine/Native/ITextMeasurer.vb \
        BrowserForWP.Core/Engine/Native/FixedAdvanceTextMeasurer.vb \
        BrowserForWP/Rendering/XamlTextMeasurer.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj BrowserForWP/BrowserForWP.vbproj
git commit -m "feat(engine): add the text-measurement seam layout needs"
```

---

### Task 2: Block layout

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/LayoutBox.vb`
- Create: `BrowserForWP.Core/Engine/Native/BlockLayout.vb`
- Create: `tools/proto/boxlayout.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Test: `tools/proto/boxlayout.mjs`

**Interfaces:**
- Consumes: `ITextMeasurer` from Task 1; `BoxNode`, `BoxKind`, `ComputedStyle` from Phase 1.
- Produces: `LayoutBoxKind` (`Block`, `Line`, `TextRun`); `LayoutBox` with `Kind`, `TagName`, `Text`, `Style`, `Children`, `Parent`, `XPx`, `YPx`, `WidthPx`, `HeightPx` and read-only `ContentLeftPx`, `ContentTopPx`, `ContentWidthPx`; `BlockLayout.Layout(root As BoxNode, viewportWidthPx As Double, measurer As ITextMeasurer) As LayoutBox`. **Coordinate contract, relied on by Task 3 and Task 4: `XPx`/`YPx` are the border-box's top-left, margins excluded; `WidthPx`/`HeightPx` are the border box; the content box is `Content*`.**

- [ ] **Step 1: Write the failing prototype**

Create `tools/proto/boxlayout.mjs`:

```js
#!/usr/bin/env node
// Prototype and referee for BrowserForWP.Core/Engine/Native/BlockLayout.vb.
//
// Transliteration rule for this repository: the VB is a hand port of the
// functions below, so when a check here disagrees with the device, first prove
// the check is right before touching the VB.
//
// Numbers are reproducible because the measurer is FixedAdvanceTextMeasurer:
// width = characters x font-size x 0.5, line height = 1.2 x font-size unless the
// page resolved one.
//
// Usage: node tools/proto/boxlayout.mjs

import fs from 'node:fs';

let passed = 0;
let failed = 0;
function check(label, condition) {
  if (condition) { passed++; console.log(`  ok   ${label}`); }
  else { failed++; console.log(`  FAIL ${label}`); }
}
function near(actual, expected) { return Math.abs(actual - expected) < 1e-9; }
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

// ── The prototype ──────────────────────────────────────────────────────────
const ADVANCE_FACTOR = 0.5;
const NORMAL_LINE_HEIGHT = 1.2;

function measureWidth(text, style) {
  if (!text) { return 0; }
  return text.length * style.fontSizePx * ADVANCE_FACTOR;
}
function lineHeight(style) {
  return style.lineHeightPx > 0 ? style.lineHeightPx : style.fontSizePx * NORMAL_LINE_HEIGHT;
}

// A ComputedStyle with only the members block layout reads.
function style(options) {
  const base = {
    fontSizePx: 16, lineHeightPx: -1, widthPx: -1, heightPx: -1, maxWidthPx: -1,
    marginTopPx: 0, marginRightPx: 0, marginBottomPx: 0, marginLeftPx: 0,
    paddingTopPx: 0, paddingRightPx: 0, paddingBottomPx: 0, paddingLeftPx: 0,
    borderTopWidthPx: 0, borderRightWidthPx: 0, borderBottomWidthPx: 0, borderLeftWidthPx: 0
  };
  return Object.assign(base, options || {});
}
function block(tagName, nodeStyle, children) {
  return { kind: 'block', tagName, style: nodeStyle, children: children || [] };
}
function text(content, nodeStyle) {
  return { kind: 'text', tagName: '#text', text: content, style: nodeStyle, children: [] };
}

function layOutChildren(children, contentLeft, contentTop, contentWidth) {
  let used = 0;
  for (const child of children) {
    const childStyle = child.style;
    if (child.kind === 'block') {
      used += childStyle.marginTopPx;
      const laid = layOutBlock(child, contentLeft, contentTop + used, contentWidth);
      used += laid.heightPx + childStyle.marginBottomPx;
    } else {
      const height = lineHeight(childStyle);
      used += height;
    }
  }
  return used;
}

function layOutBlock(node, parentContentLeft, flowTop, containingWidth) {
  const s = node.style;
  const marginLeft = Math.max(0, s.marginLeftPx);
  const marginRight = Math.max(0, s.marginRightPx);
  const horizontalInsets =
    Math.max(0, s.borderLeftWidthPx) + Math.max(0, s.borderRightWidthPx) +
    Math.max(0, s.paddingLeftPx) + Math.max(0, s.paddingRightPx);

  let borderBoxWidth = s.widthPx >= 0 ? s.widthPx : containingWidth - marginLeft - marginRight;
  if (s.maxWidthPx >= 0 && borderBoxWidth > s.maxWidthPx) { borderBoxWidth = s.maxWidthPx; }
  const available = containingWidth - marginLeft - marginRight;
  if (borderBoxWidth > available) { borderBoxWidth = available; }
  if (borderBoxWidth < 0) { borderBoxWidth = 0; }

  const xPx = parentContentLeft + marginLeft;
  const yPx = flowTop + Math.max(0, s.marginTopPx);
  let contentWidthPx = borderBoxWidth - horizontalInsets;
  if (contentWidthPx < 0) { contentWidthPx = 0; }

  let contentHeightPx = 0;
  if (contentWidthPx > 0) {
    contentHeightPx = layOutChildren(node.children, xPx + Math.max(0, s.borderLeftWidthPx) + Math.max(0, s.paddingLeftPx),
      yPx + Math.max(0, s.borderTopWidthPx) + Math.max(0, s.paddingTopPx), contentWidthPx);
  }

  let borderBoxHeight = Math.max(0, s.paddingTopPx) + contentHeightPx + Math.max(0, s.paddingBottomPx) +
    Math.max(0, s.borderTopWidthPx) + Math.max(0, s.borderBottomWidthPx);
  if (s.heightPx >= 0 && s.heightPx > borderBoxHeight) { borderBoxHeight = s.heightPx; }

  return { xPx, yPx, widthPx: borderBoxWidth, heightPx: borderBoxHeight, contentWidthPx };
}

const viewportPx = 360;

// 1. Filling the viewport, margins excluded.
const filling = layOutBlock(block('div', style({ marginLeftPx: 8, marginRightPx: 8 })), 0, 0, viewportPx);
check('a block fills its container minus its margins',
  near(filling.widthPx, 344) && near(filling.xPx, 8));

// 2. An explicit width wins over filling.
const sized = layOutBlock(block('div', style({ widthPx: 100 })), 0, 0, viewportPx);
check('an explicit width wins over filling the container', near(sized.widthPx, 100));

// 3. max-width caps an explicit width.
const capped = layOutBlock(block('div', style({ widthPx: 400, maxWidthPx: 300 })), 0, 0, viewportPx);
check('max-width caps the width', near(capped.widthPx, 300));

// 4. Siblings stack, and a sibling's margin pushes the next one down.
const stack = block('div', style({}), [
  block('p', style({ heightPx: 20, marginBottomPx: 10 }), []),
  block('p', style({ heightPx: 30 }), [])
]);
const stacked = layOutBlock(stack, 0, 0, viewportPx);
const secondChildTop = layOutBlock(block('p', style({ heightPx: 30 })), 0, 0 + 20 + 10, viewportPx).yPx;
check('a sibling is placed below the previous one plus its margin',
  near(stacked.heightPx, 60) && near(secondChildTop, 30));

// 5. Height accumulates padding and borders.
const padded = layOutBlock(block('div', style({ paddingTopPx: 10, paddingBottomPx: 4, borderTopWidthPx: 2 }), [
  block('p', style({ heightPx: 50 }), [])
]), 0, 0, viewportPx);
check('height accumulates padding and borders', near(padded.heightPx, 66));

// 6. A text run is measured, not guessed.
const withText = layOutBlock(block('div', style({}), [text('0123456789', style({}))]), 0, 0, viewportPx);
check('a text run contributes its measured line height',
  near(withText.heightPx, 19.2) && near(measureWidth('0123456789', style({})), 80));

// ── Parity with the VB port ────────────────────────────────────────────────
const layoutBox = readIfPresent('BrowserForWP.Core/Engine/Native/LayoutBox.vb');
const blockLayout = readIfPresent('BrowserForWP.Core/Engine/Native/BlockLayout.vb');

check('LayoutBox.vb exists', layoutBox.length > 0);
check('BlockLayout.vb exists', blockLayout.length > 0);
check('BlockLayout exposes Layout(root, viewportWidthPx, measurer)',
  blockLayout.includes('Function Layout(root As BoxNode, viewportWidthPx As Double, measurer As ITextMeasurer) As LayoutBox'));
check('the layout files contain no VB 14 fluent chain',
  layoutBox.length > 0 && blockLayout.length > 0 &&
  !/\)\.\s*\n\s*\w+\(/.test(layoutBox) && !/\)\.\s*\n\s*\w+\(/.test(blockLayout));

console.log(`\n${passed}/${passed + failed} checks passed.`);
if (failed > 0) { process.exit(1); }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/boxlayout.mjs`
Expected: exit 1, `6/10 checks passed.` — the six block-layout checks pass and exactly **4** parity checks fail, because neither VB file exists yet.

- [ ] **Step 3: Write `LayoutBox`**

Create `BrowserForWP.Core/Engine/Native/LayoutBox.vb`:

```vb
' BrowserForWP — one positioned node.
'
' Coordinate contract, relied on by the renderer and by InlineLayout:
'   * XPx / YPx            the BORDER BOX's top-left. Margins are outside it.
'   * WidthPx / HeightPx   the border box, including border and padding.
'   * ContentLeftPx / ContentTopPx / ContentWidthPx   where children live.
'
' Margins are therefore never inside a rectangle: they are space, and BlockLayout
' applies them by moving the border box and by advancing the flow cursor.

Namespace Engine.Native

    ''' <summary>What a laid-out node is.</summary>
    Public Enum LayoutBoxKind
        ''' <summary>A block box: background, border, and children.</summary>
        Block
        ''' <summary>A line box inside a block. Children are its text runs.</summary>
        Line
        ''' <summary>A run of text to draw at its own position.</summary>
        TextRun
    End Enum

    ''' <summary>A node with geometry. All values are device-independent pixels.</summary>
    Public NotInheritable Class LayoutBox

        Public Property Kind As LayoutBoxKind
        Public Property TagName As String = String.Empty
        Public Property Text As String = String.Empty
        Public Property Style As ComputedStyle
        Public Property Children As New List(Of LayoutBox)()
        Public Property Parent As LayoutBox

        ''' <summary>Left edge of the border box.</summary>
        Public Property XPx As Double
        ''' <summary>Top edge of the border box.</summary>
        Public Property YPx As Double
        ''' <summary>Border-box width.</summary>
        Public Property WidthPx As Double
        ''' <summary>Border-box height.</summary>
        Public Property HeightPx As Double

        ''' <summary>Left edge of the content box.</summary>
        Public ReadOnly Property ContentLeftPx As Double
            Get
                If Style Is Nothing Then Return XPx
                Return XPx + Math.Max(0, Style.BorderLeftWidthPx) + Math.Max(0, Style.PaddingLeftPx)
            End Get
        End Property

        ''' <summary>Top edge of the content box.</summary>
        Public ReadOnly Property ContentTopPx As Double
            Get
                If Style Is Nothing Then Return YPx
                Return YPx + Math.Max(0, Style.BorderTopWidthPx) + Math.Max(0, Style.PaddingTopPx)
            End Get
        End Property

        ''' <summary>Width available to children.</summary>
        Public ReadOnly Property ContentWidthPx As Double
            Get
                If Style Is Nothing Then Return WidthPx
                Dim inset As Double = Math.Max(0, Style.BorderLeftWidthPx) + Math.Max(0, Style.BorderRightWidthPx) +
                                      Math.Max(0, Style.PaddingLeftPx) + Math.Max(0, Style.PaddingRightPx)
                Dim usable As Double = WidthPx - inset
                If usable < 0 Then Return 0
                Return usable
            End Get
        End Property

        ''' <summary>Depth-first count, used by the diagnostics readout.</summary>
        Public Function DescendantCount() As Integer
            Dim total As Integer = Children.Count
            For Each childItem In Children
                total += childItem.DescendantCount()
            Next
            Return total
        End Function

    End Class

End Namespace
```

- [ ] **Step 4: Write `BlockLayout`**

Create `BrowserForWP.Core/Engine/Native/BlockLayout.vb`:

```vb
' BrowserForWP — block layout: a box tree in, positioned boxes out.
'
' It answers three questions per box -- how wide, how tall, where -- for static,
' non-floated, non-positioned blocks that stack vertically. Inline flow is handed
' to InlineLayout; this class never measures text itself.
'
' Deliberately absent, each for a stated reason:
'   * float and position      -- outside the subset this repository declares
'   * auto margins            -- centring waits until a real page needs it
'   * margin collapsing       -- a refinement; without it spacing is slightly
'                                wide, which is visible but not wrong
'   * box-sizing / min-height -- content box only
'
' The node the page is laid out in is synthetic: Layout returns a #page box whose
' own style is Nothing, so its content box is the whole viewport and the html box
' is laid out inside it like any other child.

Namespace Engine.Native

    ''' <summary>Turns a box tree into a positioned tree. Never throws.</summary>
    Public NotInheritable Class BlockLayout

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Lay root out inside a viewport viewportWidthPx wide. Returns Nothing when
        ''' there is nothing to lay out or the viewport has no width.
        ''' </summary>
        Public Shared Function Layout(root As BoxNode, viewportWidthPx As Double, measurer As ITextMeasurer) As LayoutBox
            If root Is Nothing OrElse measurer Is Nothing Then Return Nothing
            If viewportWidthPx <= 0 Then Return Nothing

            Dim page As New LayoutBox()
            page.Kind = LayoutBoxKind.Block
            page.TagName = "#page"
            page.Style = Nothing
            page.XPx = 0
            page.YPx = 0
            page.WidthPx = viewportWidthPx

            Dim consumed As Double = LayOutChildren(root, page, viewportWidthPx, measurer)
            page.HeightPx = consumed
            Return page
        End Function

        ''' <summary>
        ''' Walk one node's children inside its content box and return the height
        ''' they consumed. Consecutive inline children form one flow, because a run of
        ''' text and a span between two paragraphs is one line-breaking problem, not
        ''' one problem each.
        ''' </summary>
        Private Shared Function LayOutChildren(parentNode As BoxNode, parentBox As LayoutBox,
                                              contentWidthPx As Double, measurer As ITextMeasurer) As Double
            Dim flowTopPx As Double = parentBox.ContentTopPx
            Dim consumed As Double = 0
            Dim runGroup As New List(Of BoxNode)()

            For Each childNode In parentNode.Children
                If childNode Is Nothing Then Continue For
                If childNode.Kind = BoxKind.Block Then
                    If runGroup.Count > 0 Then
                        consumed += LayOutInlineRun(runGroup, parentBox, flowTopPx + consumed, contentWidthPx, measurer)
                        runGroup.Clear()
                    End If
                    Dim childBox As LayoutBox = LayOutBlock(childNode, parentBox.ContentLeftPx,
                                                           flowTopPx + consumed, contentWidthPx, measurer)
                    If childBox IsNot Nothing AndAlso childBox.Style IsNot Nothing Then
                        parentBox.Children.Add(childBox)
                        consumed += Math.Max(0, childBox.Style.MarginTopPx) + childBox.HeightPx +
                                    Math.Max(0, childBox.Style.MarginBottomPx)
                    End If
                Else
                    runGroup.Add(childNode)
                End If
            Next

            If runGroup.Count > 0 Then
                consumed += LayOutInlineRun(runGroup, parentBox, flowTopPx + consumed, contentWidthPx, measurer)
            End If

            Return consumed
        End Function

        ''' <summary>Position one block box and lay out its children.</summary>
        Private Shared Function LayOutBlock(node As BoxNode, parentContentLeftPx As Double,
                                            flowTopPx As Double, containingWidthPx As Double,
                                            measurer As ITextMeasurer) As LayoutBox
            Dim nodeStyle As ComputedStyle = node.Style
            If nodeStyle Is Nothing Then Return Nothing

            Dim marginLeft As Double = Math.Max(0, nodeStyle.MarginLeftPx)
            Dim marginRight As Double = Math.Max(0, nodeStyle.MarginRightPx)
            Dim available As Double = containingWidthPx - marginLeft - marginRight

            Dim borderBoxWidth As Double
            If nodeStyle.WidthPx >= 0 Then
                borderBoxWidth = nodeStyle.WidthPx
            Else
                borderBoxWidth = available
            End If
            If nodeStyle.MaxWidthPx >= 0 AndAlso borderBoxWidth > nodeStyle.MaxWidthPx Then
                borderBoxWidth = nodeStyle.MaxWidthPx
            End If
            If borderBoxWidth > available Then borderBoxWidth = available
            If borderBoxWidth < 0 Then borderBoxWidth = 0

            Dim box As New LayoutBox()
            box.Kind = LayoutBoxKind.Block
            box.TagName = node.TagName
            box.Style = nodeStyle
            box.XPx = parentContentLeftPx + marginLeft
            box.YPx = flowTopPx + Math.Max(0, nodeStyle.MarginTopPx)
            box.WidthPx = borderBoxWidth

            ' A box with no content width cannot hold children, and recursing would
            ' still produce correctly positioned children at width zero. Skip it: on a
            ' 480px screen a stack of zero-width boxes is noise, not a layout.
            Dim contentHeightPx As Double = 0
            If box.ContentWidthPx > 0 Then
                contentHeightPx = LayOutChildren(node, box, box.ContentWidthPx, measurer)
            End If

            Dim borderBoxHeight As Double = Math.Max(0, nodeStyle.BorderTopWidthPx) +
                                            Math.Max(0, nodeStyle.PaddingTopPx) + contentHeightPx +
                                            Math.Max(0, nodeStyle.PaddingBottomPx) +
                                            Math.Max(0, nodeStyle.BorderBottomWidthPx)
            If nodeStyle.HeightPx >= 0 AndAlso nodeStyle.HeightPx > borderBoxHeight Then
                borderBoxHeight = nodeStyle.HeightPx
            End If
            box.HeightPx = borderBoxHeight
            Return box
        End Function

        ''' <summary>
        ''' Inline flow, first version: one line per run, measured, no wrapping. Task 3
        ''' replaces the body of this function with a call to InlineLayout.BuildLines,
        ''' which is why it is one function: the caller does not change.
        ''' </summary>
        Private Shared Function LayOutInlineRun(runs As IList(Of BoxNode), parentBox As LayoutBox,
                                               flowTopPx As Double, contentWidthPx As Double,
                                               measurer As ITextMeasurer) As Double
            Dim usedPx As Double = 0
            For Each runNode In runs
                If runNode Is Nothing OrElse runNode.Style Is Nothing Then Continue For
                Dim runStyle As ComputedStyle = runNode.Style
                Dim runWidthPx As Double = measurer.MeasureWidth(runNode.Text, runStyle)
                If runWidthPx > contentWidthPx Then runWidthPx = contentWidthPx

                Dim lineBox As New LayoutBox()
                lineBox.Kind = LayoutBoxKind.Line
                lineBox.Style = runStyle
                lineBox.XPx = parentBox.ContentLeftPx
                lineBox.YPx = flowTopPx + usedPx
                lineBox.WidthPx = runWidthPx
                lineBox.HeightPx = measurer.LineHeight(runStyle)
                lineBox.Parent = parentBox

                Dim runBox As New LayoutBox()
                runBox.Kind = LayoutBoxKind.TextRun
                runBox.TagName = runNode.TagName
                runBox.Text = runNode.Text
                runBox.Style = runStyle
                runBox.XPx = lineBox.XPx
                runBox.YPx = lineBox.YPx
                runBox.WidthPx = lineBox.WidthPx
                runBox.HeightPx = lineBox.HeightPx
                runBox.Parent = lineBox
                lineBox.Children.Add(runBox)

                parentBox.Children.Add(lineBox)
                usedPx += lineBox.HeightPx
            Next
            Return usedPx
        End Function

    End Class

End Namespace
```

- [ ] **Step 5: Declare both files**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`, immediately after the newly added line:

```xml
    <Compile Include="Engine\Native\FixedAdvanceTextMeasurer.vb" />
```

insert:

```xml
    <Compile Include="Engine\Native\LayoutBox.vb" />
    <Compile Include="Engine\Native\BlockLayout.vb" />
```

- [ ] **Step 6: Run the prototype and the checker**

Run: `node tools/proto/boxlayout.mjs`
Expected: exit 0, `10/10 checks passed.`

Run: `node tools/check-vb.mjs`
Expected: `0 finding(s)`, exit 0.

Run: `node tools/proto/textmeasure.mjs`
Expected: `10/10 checks passed.` — Task 1 must not have regressed.

- [ ] **Step 7: Commit**

```bash
git add tools/proto/boxlayout.mjs \
        BrowserForWP.Core/Engine/Native/LayoutBox.vb \
        BrowserForWP.Core/Engine/Native/BlockLayout.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj
git commit -m "feat(engine): lay out block boxes with measured text"
```

---

### Task 3: Inline flow and line breaking

**Files:**
- Create: `BrowserForWP.Core/Engine/Native/InlineLayout.vb`
- Modify: `BrowserForWP.Core/Engine/Native/BlockLayout.vb`
- Modify: `tools/proto/boxlayout.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Test: `tools/proto/boxlayout.mjs`

**Interfaces:**
- Consumes: `LayoutBox`, `LayoutBoxKind` from Task 2; `ITextMeasurer` from Task 1.
- Produces: `InlineLayout.BuildLines(runs As IList(Of BoxNode), contentLeftPx As Double, flowTopPx As Double, contentWidthPx As Double, containerStyle As ComputedStyle, measurer As ITextMeasurer) As IList(Of LayoutBox)`, where every returned box has `Kind = LayoutBoxKind.Line`, one `TextRun` child per word, and absolute positions. Task 4 draws a `TextRun` as one `TextBlock` at `XPx`/`YPx`.

- [ ] **Step 1: Extend the prototype with the inline cases**

Append to `tools/proto/boxlayout.mjs`, immediately **before** the `// ── Parity with the VB port` comment:

```js
// ── Inline flow ────────────────────────────────────────────────────────────
// One TextRun child per word: we break the lines ourselves, so the renderer draws
// words, not lines. Gaps between words are the space's own measured width.

function splitWords(text) {
  if (!text) { return []; }
  return text.split(/\s+/).filter(Boolean);
}

function buildLines(runs, contentLeftPx, flowTopPx, contentWidthPx, containerStyle) {
  const lines = [];
  if (!runs.length || contentWidthPx <= 0) { return lines; }

  const words = [];
  for (const run of runs) {
    if (!run.style) { continue; }
    for (const piece of splitWords(run.text)) {
      words.push({ text: piece, style: run.style });
    }
  }
  if (!words.length) { return lines; }

  const lineHeightPx = lineHeight(containerStyle || words[0].style);
  const groups = [];
  let current = { words: [], widthPx: 0 };

  for (const word of words) {
    const wordWidthPx = measureWidth(word.text, word.style);
    const spaceWidthPx = current.words.length > 0 ? measureWidth(' ', word.style) : 0;
    if (current.words.length > 0 && current.widthPx + spaceWidthPx + wordWidthPx > contentWidthPx) {
      groups.push(current);
      current = { words: [], widthPx: 0 };
      current.words.push(word);
      current.widthPx = wordWidthPx;
    } else {
      current.widthPx += spaceWidthPx + wordWidthPx;
      current.words.push(word);
    }
  }
  if (current.words.length) { groups.push(current); }

  groups.forEach((group, index) => {
    const lineTopPx = flowTopPx + index * lineHeightPx;
    const align = (containerStyle && containerStyle.textAlign) || 'left';
    let offsetPx = 0;
    if (align === 'center') { offsetPx = (contentWidthPx - group.widthPx) / 2; }
    else if (align === 'right') { offsetPx = contentWidthPx - group.widthPx; }
    if (offsetPx < 0) { offsetPx = 0; }

    let cursorX = contentLeftPx + offsetPx;
    const children = [];
    group.words.forEach((word, wordIndex) => {
      if (wordIndex > 0) { cursorX += measureWidth(' ', word.style); }
      const wordWidthPx = measureWidth(word.text, word.style);
      children.push({ xPx: cursorX, yPx: lineTopPx, widthPx: wordWidthPx, heightPx: lineHeightPx, text: word.text });
      cursorX += wordWidthPx;
    });
    lines.push({ xPx: contentLeftPx, yPx: lineTopPx, widthPx: contentWidthPx, heightPx: lineHeightPx, groupWidthPx: group.widthPx, children });
  });
  return lines;
}

// 7. A short run stays on one line.
const shortRun = buildLines([text('hello world', style({}))], 0, 0, 360, style({}));
check('a run that fits stays on one line',
  shortRun.length === 1 && shortRun[0].children.length === 2 && near(shortRun[0].heightPx, 19.2));

// 8. A long run wraps, and the line count is the arithmetic one.
//    20 words of 5 characters at 16px = 40px each, plus a space of 8px.
//    Line capacity: floor((360 + 8) / 48) = 7 words.
const longWords = [];
for (let i = 0; i < 20; i++) { longWords.push('abcde'); }
const wrapped = buildLines([text(longWords.join(' '), style({}))], 0, 0, 360, style({}));
check('a run that does not fit wraps into lines',
  wrapped.length === 3 && wrapped[2].children.length === 6);

// 9. Breaking happens at a space, never inside a word.
const singleLongWord = buildLines([text('abcdefghijklmnopqrstuvwxyz', style({}))], 0, 0, 100, style({}));
check('a word wider than the line is not split',
  singleLongWord.length === 1 && singleLongWord[0].children.length === 1 &&
  near(singleLongWord[0].children[0].widthPx, 208));

// 10. text-align centres and right-aligns by offsetting the line's own width.
const centred = buildLines([text('hello world', style({}))], 0, 0, 360, style({ textAlign: 'center' }));
check('text-align center offsets the words', near(centred[0].children[0].xPx, (360 - 88) / 2));
const rightAligned = buildLines([text('hello world', style({}))], 0, 0, 360, style({ textAlign: 'right' }));
check('text-align right offsets the words', near(rightAligned[0].children[0].xPx, 360 - 88));

// 11. A resolved line-height sets the line box, not the 1.2 default.
const tallLines = buildLines([text('hello', style({ fontSizePx: 20 }))], 0, 0, 360, style({ fontSizePx: 20, lineHeightPx: 28 }));
check('a resolved line-height wins over the default',
  tallLines.length === 1 && near(tallLines[0].heightPx, 28) && near(tallLines[0].children[0].yPx, 0));
```

Then extend the parity block, immediately before the `console.log` line:

```js
const inlineLayout = readIfPresent('BrowserForWP.Core/Engine/Native/InlineLayout.vb');

check('InlineLayout.vb exists', inlineLayout.length > 0);
check('InlineLayout exposes BuildLines',
  inlineLayout.includes('Function BuildLines(runs As IList(Of BoxNode), contentLeftPx As Double, flowTopPx As Double, contentWidthPx As Double, containerStyle As ComputedStyle, measurer As ITextMeasurer) As IList(Of LayoutBox)'));
check('InlineLayout contains no VB 14 fluent chain', inlineLayout.length > 0 && !/\)\.\s*\n\s*\w+\(/.test(inlineLayout));
```

Also change check 6's label and expectation to reflect that a block containing text now wraps rather than contributing one fixed line. Replace this line:

```js
check('a text run contributes its measured line height',
  near(withText.heightPx, 19.2) && near(measureWidth('0123456789', style({})), 80));
```

with:

```js
check('a text run is measured, not guessed',
  near(withText.heightPx, 19.2) && near(measureWidth('0123456789', style({})), 80));
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/proto/boxlayout.mjs`
Expected: exit 1, `16/19 checks passed.` — the five new inline checks pass against the prototype and exactly **3** parity checks fail, because `InlineLayout.vb` does not exist yet.

- [ ] **Step 3: Write `InlineLayout`**

Create `BrowserForWP.Core/Engine/Native/InlineLayout.vb`:

```vb
' BrowserForWP — inline flow: words into line boxes, then alignment.
'
' A line is broken at whitespace and nowhere else, which is what CSS does by
' default and what keeps a long URL readable instead of split. A word wider than
' the line therefore overflows rather than breaking: overflow is honest, a broken
' word is a lie about the text.
'
' One TextRun per word, not one per line. The renderer draws words at absolute
' positions, so a line box is a container with no visual of its own, and the space
' between two words is simply the gap between their positions.

Namespace Engine.Native

    ''' <summary>Turns inline runs into line boxes. Never throws.</summary>
    Public NotInheritable Class InlineLayout

        Private Sub New()
        End Sub

        ''' <summary>One word and the style it must be drawn in.</summary>
        Private NotInheritable Class WordRun
            Public Property Text As String = String.Empty
            Public Property Style As ComputedStyle
        End Class

        ''' <summary>
        ''' Break runs into line boxes, positioned absolutely. Returns an empty list
        ''' when there is nothing to break or the content box has no width.
        ''' </summary>
        Public Shared Function BuildLines(runs As IList(Of BoxNode), contentLeftPx As Double, flowTopPx As Double, contentWidthPx As Double, containerStyle As ComputedStyle, measurer As ITextMeasurer) As IList(Of LayoutBox)
            Dim lines As New List(Of LayoutBox)()
            If runs Is Nothing OrElse measurer Is Nothing Then Return lines
            If contentWidthPx <= 0 Then Return lines

            Dim words As New List(Of WordRun)()
            For Each runNode In runs
                If runNode Is Nothing OrElse runNode.Style Is Nothing Then Continue For
                For Each wordText In SplitWords(runNode.Text)
                    Dim word As New WordRun()
                    word.Text = wordText
                    word.Style = runNode.Style
                    words.Add(word)
                Next
            Next
            If words.Count = 0 Then Return lines

            ' A line box is as tall as the block that holds it: that is what a
            ' resolved line-height on the container already means.
            Dim lineStyle As ComputedStyle = containerStyle
            If lineStyle Is Nothing Then lineStyle = words(0).Style
            Dim lineHeightPx As Double = measurer.LineHeight(lineStyle)

            Dim lineWords As New List(Of WordRun)()
            Dim lineWidthPx As Double = 0
            Dim lineTopPx As Double = flowTopPx

            For Each word In words
                Dim wordWidthPx As Double = measurer.MeasureWidth(word.Text, word.Style)
                Dim spaceWidthPx As Double = 0
                If lineWords.Count > 0 Then spaceWidthPx = measurer.MeasureWidth(" ", word.Style)

                If lineWords.Count > 0 AndAlso lineWidthPx + spaceWidthPx + wordWidthPx > contentWidthPx Then
                    lines.Add(BuildLine(lineWords, lineWidthPx, lineHeightPx, contentLeftPx, lineTopPx, contentWidthPx, lineStyle, measurer))
                    lineTopPx += lineHeightPx
                    lineWords = New List(Of WordRun)()
                    lineWidthPx = 0
                Else
                    lineWidthPx += spaceWidthPx
                End If

                lineWords.Add(word)
                lineWidthPx += wordWidthPx
            Next

            If lineWords.Count > 0 Then
                lines.Add(BuildLine(lineWords, lineWidthPx, lineHeightPx, contentLeftPx, lineTopPx, contentWidthPx, lineStyle, measurer))
            End If

            Return lines
        End Function

        ''' <summary>Position one line's words, honouring text-align.</summary>
        Private Shared Function BuildLine(lineWords As IList(Of WordRun), lineWidthPx As Double,
                                         lineHeightPx As Double, contentLeftPx As Double,
                                         lineTopPx As Double, contentWidthPx As Double,
                                         lineStyle As ComputedStyle, measurer As ITextMeasurer) As LayoutBox
            Dim lineBox As New LayoutBox()
            lineBox.Kind = LayoutBoxKind.Line
            lineBox.Style = lineStyle
            lineBox.XPx = contentLeftPx
            lineBox.YPx = lineTopPx
            lineBox.WidthPx = contentWidthPx
            lineBox.HeightPx = lineHeightPx

            Dim offsetPx As Double = 0
            If lineStyle IsNot Nothing Then
                If lineStyle.TextAlign = "center" Then
                    offsetPx = (contentWidthPx - lineWidthPx) / 2
                ElseIf lineStyle.TextAlign = "right" Then
                    offsetPx = contentWidthPx - lineWidthPx
                End If
            End If
            If offsetPx < 0 Then offsetPx = 0

            Dim cursorX As Double = contentLeftPx + offsetPx
            Dim isFirstWord As Boolean = True
            For Each word In lineWords
                If Not isFirstWord Then cursorX += measurer.MeasureWidth(" ", word.Style)
                isFirstWord = False

                Dim wordWidthPx As Double = measurer.MeasureWidth(word.Text, word.Style)
                Dim runBox As New LayoutBox()
                runBox.Kind = LayoutBoxKind.TextRun
                runBox.Text = word.Text
                runBox.Style = word.Style
                runBox.XPx = cursorX
                runBox.YPx = lineTopPx
                runBox.WidthPx = wordWidthPx
                runBox.HeightPx = lineHeightPx
                runBox.Parent = lineBox
                lineBox.Children.Add(runBox)

                cursorX += wordWidthPx
            Next

            Return lineBox
        End Function

        ''' <summary>Words of a text run: split on whitespace, empties dropped.</summary>
        Private Shared Function SplitWords(text As String) As IList(Of String)
            Dim words As New List(Of String)()
            If String.IsNullOrEmpty(text) Then Return words

            Dim currentWord As New System.Text.StringBuilder()
            For Each character In text
                If Char.IsWhiteSpace(character) Then
                    If currentWord.Length > 0 Then
                        words.Add(currentWord.ToString())
                        currentWord.Length = 0
                    End If
                Else
                    currentWord.Append(character)
                End If
            Next
            If currentWord.Length > 0 Then words.Add(currentWord.ToString())
            Return words
        End Function

    End Class

End Namespace
```

- [ ] **Step 4: Replace the inline path in `BlockLayout`**

In `BrowserForWP.Core/Engine/Native/BlockLayout.vb`, replace the entire body of `LayOutInlineRun` — from `Dim usedPx As Double = 0` down to `Return usedPx`, inclusive — with:

```vb
            Dim lines As IList(Of LayoutBox) = InlineLayout.BuildLines(runs, parentBox.ContentLeftPx,
                                                                      flowTopPx, contentWidthPx,
                                                                      parentBox.Style, measurer)
            Dim usedPx As Double = 0
            For Each lineBox In lines
                lineBox.Parent = parentBox
                parentBox.Children.Add(lineBox)
                usedPx += lineBox.HeightPx
            Next
            Return usedPx
```

Also update that function's summary comment, which currently says it is the first version with no wrapping. Replace:

```vb
        ''' <summary>
        ''' Inline flow, first version: one line per run, measured, no wrapping. Task 3
        ''' replaces the body of this function with a call to InlineLayout.BuildLines,
        ''' which is why it is one function: the caller does not change.
        ''' </summary>
```

with:

```vb
        ''' <summary>
        ''' Inline flow: delegate the run group to InlineLayout and add the lines to
        ''' the parent. Kept as one function so the block path never learns how lines
        ''' are built.
        ''' </summary>
```

One consequence to be aware of when reading the diff: `parentBox.Style` is passed as the container style, and for the synthetic `#page` box that is `Nothing`, so `InlineLayout` falls back to the first word's style. That is the intended behaviour for a document whose text sits directly under `<html>`.

- [ ] **Step 5: Declare the new file**

In `BrowserForWP.Core/BrowserForWP.Core.vbproj`, immediately after the newly added line:

```xml
    <Compile Include="Engine\Native\BlockLayout.vb" />
```

insert:

```xml
    <Compile Include="Engine\Native\InlineLayout.vb" />
```

- [ ] **Step 6: Run the prototype and the checker**

Run: `node tools/proto/boxlayout.mjs`
Expected: exit 0, `19/19 checks passed.`

Run: `node tools/proto/textmeasure.mjs`
Expected: `10/10 checks passed.`

Run: `node tools/check-vb.mjs`
Expected: `0 finding(s)`, exit 0.

- [ ] **Step 7: Commit**

```bash
git add tools/proto/boxlayout.mjs \
        BrowserForWP.Core/Engine/Native/InlineLayout.vb \
        BrowserForWP.Core/Engine/Native/BlockLayout.vb \
        BrowserForWP.Core/BrowserForWP.Core.vbproj
git commit -m "feat(engine): break inline text into lines and align them"
```

---

### Task 4: Draw the laid-out page in XAML

**Files:**
- Create: `BrowserForWP/Rendering/XamlBoxRenderer.vb`
- Modify: `BrowserForWP/BrowserForWP.vbproj`
- Modify: `BrowserForWP/MainPage.xaml`
- Modify: `BrowserForWP/MainPage.xaml.vb`
- Modify: `BrowserForWP/Strings/en-US/Resources.resw`
- Modify: `BrowserForWP/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `BlockLayout.Layout` and `LayoutBox` from Task 2; `InlineLayout`'s line boxes from Task 3; `XamlTextMeasurer` from Task 1; `BrowserForWP.Diagnostics.NetDocumentFetcher`, `BoxTreeBuilder.BuildPage`, `DocumentResponse` from Phase 1; `Localizer.Get`, and `MainPage`'s existing private `InlineStyleText(html As String) As String` and `_pinTable` / `_appSettings` / `_session` fields.
- Produces: `BrowserForWP.Rendering.XamlBoxRenderer.Render(root As LayoutBox) As ScrollViewer`. Task 5 documents it; nothing else consumes it.

- [ ] **Step 1: Add the two new strings**

In `BrowserForWP/Strings/en-US/Resources.resw`, immediately before the line:

```xml
  <data name="LiteRedirects" xml:space="preserve">
```

insert:

```xml
  <data name="RenderNatively" xml:space="preserve">
    <value>Render current page natively</value>
  </data>
```

In `BrowserForWP/Strings/it-IT/Resources.resw`, immediately before the same anchor line:

```xml
  <data name="LiteRedirects" xml:space="preserve">
```

insert:

```xml
  <data name="RenderNatively" xml:space="preserve">
    <value>Disegna la pagina con il motore nativo</value>
  </data>
```

- [ ] **Step 2: Add the button and its host to the diagnostics panel**

In `BrowserForWP/MainPage.xaml`, immediately after this existing element:

```xml
                    <TextBlock x:Name="ParseResult" TextWrapping="Wrap"
                               FontFamily="Consolas" FontSize="12"
                               MaxHeight="220" Margin="0,0,0,8" Opacity="0.85"/>
```

insert:

```xml
                    <Button x:Name="RenderNativeButton" Margin="0,0,0,6"
                            Click="RenderNativeButton_Click"/>
                    <Border x:Name="NativePreviewHost" Height="320"
                            BorderThickness="1" Margin="0,0,0,8"/>
```

- [ ] **Step 3: Write the renderer**

Create `BrowserForWP/Rendering/XamlBoxRenderer.vb`:

```vb
' BrowserForWP — draws a laid-out page into XAML.
'
' Output only: no script runs, nothing here reads back from what it drew. That is
' the whole difference between this engine and the WebView, and it is why this
' renderer is allowed to be simple.
'
' Start and end tags are the page's own; every element it creates is positioned
' absolutely inside one Canvas, so a mistake here shows up as a box in the wrong
' place rather than as a reflow of everything after it.
'
' Draws: block backgrounds, one border outline per block, and every text run. Does
' not draw: images, shadows, radii, gradients, multiple border styles on one box
' (the widest visible edge wins), text selection, hover states.

Imports System.Globalization
Imports BrowserForWP.Core.Engine.Native
Imports Windows.UI.Text
Imports Windows.UI.Xaml.Controls
Imports Windows.UI.Xaml.Media
Imports Windows.UI.Xaml.Shapes

Namespace Rendering

    ''' <summary>A laid-out page as a scrollable XAML element.</summary>
    Public NotInheritable Class XamlBoxRenderer

        Private Const FallbackTextColor As String = "#000000"
        Private Const TransparentColor As String = "transparent"
        Private Const VisibleBorderStyle As String = "none"

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Draw the laid-out tree. Returns Nothing for Nothing, so a caller can assign
        ''' the result to a host without a null check of its own.
        ''' </summary>
        Public Shared Function Render(root As LayoutBox) As ScrollViewer
            If root Is Nothing Then Return Nothing

            Dim canvas As New Canvas()
            canvas.Width = Math.Max(1, root.WidthPx)
            canvas.Height = Math.Max(1, root.HeightPx)
            AppendBox(canvas, root)

            Dim host As New ScrollViewer()
            host.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            host.VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            host.Content = canvas
            Return host
        End Function

        Private Shared Sub AppendBox(canvas As Canvas, box As LayoutBox)
            If canvas Is Nothing OrElse box Is Nothing Then Return

            AppendBoxDecoration(canvas, box)
            If box.Kind = LayoutBoxKind.TextRun Then
                AppendTextRun(canvas, box)
                Return
            End If
            For Each childBox In box.Children
                AppendBox(canvas, childBox)
            Next
        End Sub

        ''' <summary>Background fill, then a border outline when the page asked for one.</summary>
        Private Shared Sub AppendBoxDecoration(canvas As Canvas, box As LayoutBox)
            If box.Style Is Nothing Then Return
            If box.Kind <> LayoutBoxKind.Block Then Return
            If box.WidthPx <= 0 OrElse box.HeightPx <= 0 Then Return

            Dim backgroundCss As String = box.Style.BackgroundColor
            If Not String.IsNullOrEmpty(backgroundCss) AndAlso backgroundCss <> TransparentColor Then
                Dim fillShape As New Rectangle()
                fillShape.Width = box.WidthPx
                fillShape.Height = box.HeightPx
                fillShape.Fill = New SolidColorBrush(ParseCssColor(backgroundCss, TransparentBrushColor()))
                Canvas.SetLeft(fillShape, box.XPx)
                Canvas.SetTop(fillShape, box.YPx)
                canvas.Children.Add(fillShape)
            End If

            ' One outline for the box, not four edges: the subset this engine claims
            ' does not include differing per-edge styles, and a box with four
            ' rectangles looks identical to a box with one outline when they agree.
            Dim edgeWidth As Double = Math.Max(Math.Max(box.Style.BorderLeftWidthPx, box.Style.BorderRightWidthPx),
                                              Math.Max(box.Style.BorderTopWidthPx, box.Style.BorderBottomWidthPx))
            If edgeWidth <= 0 Then Return
            If box.Style.BorderTopStyle = VisibleBorderStyle AndAlso box.Style.BorderLeftStyle = VisibleBorderStyle AndAlso
               box.Style.BorderRightStyle = VisibleBorderStyle AndAlso box.Style.BorderBottomStyle = VisibleBorderStyle Then
                Return
            End If

            Dim outlineShape As New Rectangle()
            outlineShape.Width = box.WidthPx
            outlineShape.Height = box.HeightPx
            outlineShape.StrokeThickness = edgeWidth
            outlineShape.Stroke = New SolidColorBrush(ParseCssColor(box.Style.BorderTopColor, ParseCssColor(FallbackTextColor, TransparentBrushColor())))
            Canvas.SetLeft(outlineShape, box.XPx)
            Canvas.SetTop(outlineShape, box.YPx)
            canvas.Children.Add(outlineShape)
        End Sub

        Private Shared Sub AppendTextRun(canvas As Canvas, box As LayoutBox)
            If String.IsNullOrEmpty(box.Text) Then Return

            Dim run As New TextBlock()
            run.Text = box.Text
            run.TextWrapping = TextWrapping.NoWrap
            If box.Style IsNot Nothing Then
                run.FontSize = box.Style.FontSizePx
                If box.Style.FontWeight >= 600 Then
                    run.FontWeight = FontWeights.Bold
                Else
                    run.FontWeight = FontWeights.Normal
                End If
                If box.Style.FontStyle = "italic" Then
                    run.FontStyle = FontStyles.Italic
                Else
                    run.FontStyle = FontStyles.Normal
                End If
                run.Foreground = New SolidColorBrush(ParseCssColor(box.Style.Color, ParseCssColor(FallbackTextColor, TransparentBrushColor())))
            End If
            Canvas.SetLeft(run, box.XPx)
            Canvas.SetTop(run, box.YPx)
            canvas.Children.Add(run)
        End Sub

        Private Shared Function TransparentBrushColor() As Windows.UI.Color
            Return Windows.UI.Color.FromArgb(0, 0, 0, 0)
        End Function

        ''' <summary>
        ''' #rrggbb and #rgb only, plus "transparent". An unknown value returns the
        ''' fallback rather than throwing: a page with one odd colour must not cost the
        ''' whole render.
        ''' </summary>
        Private Shared Function ParseCssColor(value As String, fallback As Windows.UI.Color) As Windows.UI.Color
            If String.IsNullOrEmpty(value) Then Return fallback
            Dim trimmed As String = value.Trim()
            If trimmed = TransparentColor Then Return TransparentBrushColor()
            If Not trimmed.StartsWith("#") Then Return fallback

            Dim hex As String = trimmed.Substring(1)
            If hex.Length = 3 Then
                hex = hex.Substring(0, 1) & hex.Substring(0, 1) &
                      hex.Substring(1, 1) & hex.Substring(1, 1) &
                      hex.Substring(2, 1) & hex.Substring(2, 1)
            End If
            If hex.Length <> 6 Then Return fallback

            Dim redValue As Integer = 0
            Dim greenValue As Integer = 0
            Dim blueValue As Integer = 0
            If Not Integer.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, redValue) Then Return fallback
            If Not Integer.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, greenValue) Then Return fallback
            If Not Integer.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, blueValue) Then Return fallback

            Return Windows.UI.Color.FromArgb(255, CByte(redValue), CByte(greenValue), CByte(blueValue))
        End Function

    End Class

End Namespace
```

- [ ] **Step 4: Declare the renderer**

In `BrowserForWP/BrowserForWP.vbproj`, immediately after the newly added line:

```xml
    <Compile Include="Rendering\XamlTextMeasurer.vb" />
```

insert:

```xml
    <Compile Include="Rendering\XamlBoxRenderer.vb" />
```

- [ ] **Step 5: Wire the button label**

In `BrowserForWP/MainPage.xaml.vb`, immediately after the existing line:

```vb
        ParsePageButton.Content = Localizer.Get("ParseThisPage")
```

insert:

```vb
        RenderNativeButton.Content = Localizer.Get("RenderNatively")
```

- [ ] **Step 6: Write the handler**

In `BrowserForWP/MainPage.xaml.vb`, immediately after the end of `ParsePageButton_Click` — that is, after its closing `End Sub` and before the summary comment that begins `''' Ask the hosted engine how it is configured.` — insert:

```vb
    ''' <summary>
    ''' Fetch the current tab's page over the app's own TLS 1.3 transport and draw it
    ''' with the native engine. This is the first place where a page is RENDERED from
    ''' the code in BrowserForWP.Core: the Diagnostics parse button stops at a box
    ''' tree, and the WebView never involves Core at all.
    ''' Types are fully qualified because this file's Imports are for the Core engine
    ''' and the app's own diagnostics, not for Rendering.
    ''' </summary>
    Private Async Sub RenderNativeButton_Click(sender As Object, e As RoutedEventArgs)
        RenderNativeButton.IsEnabled = False
        Try
            Dim tabUrl As String = _session.ActiveTab.Url
            If String.IsNullOrEmpty(tabUrl) Then
                ParseResult.Text = Localizer.Get("ParseNoDocument")
                Return
            End If

            Dim fetcher As New BrowserForWP.Diagnostics.NetDocumentFetcher(_pinTable)
            Dim response As DocumentResponse = Await fetcher.FetchAsync(tabUrl, _appSettings.DohUrl)

            If Not String.IsNullOrEmpty(response.ErrorMessage) Then
                ParseResult.Text = Localizer.Get("ParseFailed") & " " & response.ErrorMessage
                Return
            End If
            If Not response.IsHtml Then
                ParseResult.Text = Localizer.Get("ParseNoDocument")
                Return
            End If

            ' The preview is laid out for the width the host actually has. Before the
            ' first layout pass that is 0, and the fallback keeps the button usable.
            Dim viewportPx As Double = NativePreviewHost.ActualWidth
            If viewportPx < 1 Then viewportPx = 360

            Dim boxTree As BoxNode = BoxTreeBuilder.BuildPage(response.Text, InlineStyleText(response.Text))
            Dim measurer As New BrowserForWP.Rendering.XamlTextMeasurer()
            Dim laidOut As BrowserForWP.Core.Engine.Native.LayoutBox =
                BrowserForWP.Core.Engine.Native.BlockLayout.Layout(boxTree, viewportPx, measurer)
            NativePreviewHost.Child = BrowserForWP.Rendering.XamlBoxRenderer.Render(laidOut)

            ParseResult.Text = CInt(laidOut.WidthPx).ToString() & " x " & CInt(laidOut.HeightPx).ToString() &
                               "  " & laidOut.DescendantCount().ToString()
        Catch ex As Exception
            ParseResult.Text = Localizer.Get("ParseFailed") & " " & ex.Message
        Finally
            RenderNativeButton.IsEnabled = True
        End Try
    End Sub

```

- [ ] **Step 7: Run the local checks**

Run: `node tools/check-vb.mjs`
Expected: `0 finding(s)`, exit 0. The two most likely findings, and what each means:
- `[xaml-handler] MainPage.xaml … RenderNativeButton_Click` — the handler name in XAML does not match the `Sub` in VB. VB is case-insensitive but not spelling-insensitive.
- `[resources] … RenderNatively` — the key exists in one `.resw` and not the other.

Run: `node tools/check-polyfill.mjs`
Expected: `is valid ES5` — unchanged, listed because the full suite is cheap.

- [ ] **Step 8: Build in the guest**

Run:

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```

Expected: `=== BUILD_EXIT=0 ===`, no `error BC`, and only the two deliberate `BC40000` `ResourceLoader` warnings. A `BC30456` naming `Layout`, `Render` or `BuildLines` means a type from a task is not declared in its `.vbproj`, or a file declares a namespace other than `Engine.Native` / `Rendering`.

- [ ] **Step 9: Commit**

```bash
git add BrowserForWP/Rendering/XamlBoxRenderer.vb \
        BrowserForWP/BrowserForWP.vbproj \
        BrowserForWP/MainPage.xaml BrowserForWP/MainPage.xaml.vb \
        BrowserForWP/Strings/en-US/Resources.resw BrowserForWP/Strings/it-IT/Resources.resw
git commit -m "feat(engine): draw the laid-out page in XAML from Diagnostics"
```

---

### Task 5: Record it, and prove nothing regressed

**Files:**
- Modify: `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb`
- Modify: `tools/proto/core-logic.mjs`
- Modify: `docs/MAINTAINING.md`
- Modify: `.agents/skills/browserforwp/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-28-native-engine-layout.md` (this file's Outcome section)

**Interfaces:**
- Consumes: `FixedAdvanceTextMeasurer` from Task 1; everything else for the regression run.
- Produces: no code. This task exists because a repository that keeps the compiled-in assertions and the Node mirrors in step cannot let them drift, and because the next session needs the numbers this plan measured.

- [ ] **Step 1: Mirror the measurer assertion into the compiled suite**

`tests/BrowserForWP.Core.Tests/CoreLogicTests.vb` currently imports `BrowserForWP.Core.Browser`, `.Storage`, `BrowserForWP.Localization` and `BrowserForWP.Net.Tls13` — **not** the native engine. Add it, immediately after the first of those lines, which reads exactly:

```vb
Imports BrowserForWP.Core.Browser
```

so that it becomes:

```vb
Imports BrowserForWP.Core.Browser
Imports BrowserForWP.Core.Engine.Native
Imports BrowserForWP.Core.Storage
```

Then insert the new checks immediately after the existing line:

```vb
            Check(UserAgents.EffectiveUserAgent(False) = UserAgents.MobileDefault, "UA helper mobile")
            ran += 1
```

```vb

            ' The measurer's arithmetic is what makes layout numbers reproducible, so
            ' it is asserted here (compiled on the guest) AND in
            ' tools/proto/textmeasure.mjs (executed on any machine). Keep the two in
            ' step: a drift between them makes Node right and the device wrong.
            Dim measureStyle As New ComputedStyle()
            measureStyle.FontSizePx = 16
            Dim fixedMeasurer As New FixedAdvanceTextMeasurer()
            Check(fixedMeasurer.MeasureWidth("abcd", measureStyle) = 32, "measurer advance")
            Check(fixedMeasurer.LineHeight(measureStyle) = 19.2, "measurer normal line height")
            ran += 1
```

`ran` is the file's own count of executed blocks, incremented once per block; that is why it is incremented once for these two checks and not twice.

- [ ] **Step 2: Mirror the same assertion in the executable mirror**

In `tools/proto/core-logic.mjs`, insert immediately **before** the summary comment, which reads exactly:

```js
// ── Summary ───────────────────────────────────────────────────────────────
```

the following block:

```js
// ── The measurement seam (mirror of CoreLogicTests.vb's measurer checks) ──
// Same two numbers as the VB. This file is what executes them; the VB project
// compiles and is never run off-device.
const ADVANCE_FACTOR = 0.5;
const NORMAL_LINE_HEIGHT = 1.2;
check('measurer advance', 4 * 16 * ADVANCE_FACTOR === 32);
check('measurer normal line height', 16 * NORMAL_LINE_HEIGHT === 19.2);

```

- [ ] **Step 3: Run the whole local suite**

Run:

```bash
node tools/gen-vectors.mjs
node tools/proto/core-logic.mjs
node tools/proto/textmeasure.mjs
node tools/proto/boxlayout.mjs
node tools/check-polyfill.mjs
node tools/check-vb.mjs
python3 tools/make_logo.py
git status --short
```

Expected, in order: `53 assertions passed`; `0 failure(s)` with the assertion count increased by exactly 2 (from 53 to 55); `10/10 checks passed`; `19/19 checks passed`; `is valid ES5`; `64 check group(s) run, 0 finding(s)`; twelve PNG lines and no `Assets` entry in `git status`.

- [ ] **Step 4: Rebuild every configuration in the guest**

Run:

```bash
for cfg in Debug Release; do for plat in ARM x86; do
  echo "##### $cfg / $plat"
  prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild /p:Configuration=$cfg /p:Platform=$plat" 2>&1 \
    | grep -E "BUILD_EXIT|error BC|error MSB|warning BC"
done; done
```

Then the two `Any CPU` ones, which cannot be selected through `prlctl exec` because that platform's name contains a space:

```bash
for cfg in Debug Release; do
  echo "##### $cfg / AnyCPU"
  prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "cd /d C:\Mac\Home\Documents\BrowserForWP && \"C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe\" BrowserForWP\BrowserForWP.vbproj /nologo /v:minimal /t:Rebuild /p:Configuration=$cfg /p:Platform=AnyCPU & echo BUILD_EXIT=%ERRORLEVEL%" 2>&1 \
    | grep -E "BUILD_EXIT|error BC|warning BC"
done
```

Expected: `BUILD_EXIT=0` six times, and only the two `BC40000` `ResourceLoader` warnings each time.

- [ ] **Step 5: Record the round in `docs/MAINTAINING.md`**

Add to the verification-commands block, after the `node tools/proto/core-logic.mjs` entry:

```bash
# Text measurement: the seam layout depends on. Must print "10/10 checks passed".
node tools/proto/textmeasure.mjs

# Block and inline layout. Must print "19/19 checks passed". This is the
# transliteration source for LayoutBox.vb / BlockLayout.vb / InlineLayout.vb, and
# it is the only way those numbers are verified off-device: nothing else in this
# repository predicts where a box lands.
node tools/proto/boxlayout.mjs
```

Add to the tools table, after the `tools/proto/core-logic.mjs` row:

```markdown
| `tools/proto/textmeasure.mjs` | The measurer's arithmetic, plus parity with the VB that implements it. | `node`, on any machine. |
| `tools/proto/boxlayout.mjs` | Block widths and heights, line breaking, alignment. The referee for `BlockLayout.vb` / `InlineLayout.vb`. | `node`, on any machine. |
```

Add a new round section after Round 6 (before `## The loop`):

```markdown
### Round 7 — the native engine lays out and draws a page

Phase 1 stopped at a box tree on purpose; this round added the two things it left
out, and the boundary between them is one interface:

- **Text measurement** is a XAML operation, so Core declares `ITextMeasurer` and
  the app answers it — the same split `IBrowserEngine` already uses for the engine
  itself. `FixedAdvanceTextMeasurer` (0.5 em per character) exists so the numbers
  layout produces are reproducible in `tools/proto/boxlayout.mjs`.
- **Layout** is `BlockLayout` (blocks stack, widths fill, `max-width` caps) plus
  `InlineLayout` (words into lines, breaking at whitespace, `text-align`).
- **Drawing** is `XamlBoxRenderer`: one `Canvas`, a `TextBlock` per word, a
  `Rectangle` per background and border, inside a `ScrollViewer`.
- **Reachable** from Diagnostics → *Render current page natively*, which fetches
  the current tab over the app's own TLS 1.3 transport. This is the first code
  path in the product where a page is **rendered** by this repository's own engine
  rather than by the WebView.

What it does **not** do, stated here so it cannot be mistaken for a regression:
no JavaScript, no `float`/`position`, no auto-margin centring, no margin
collapsing, no images, no tables, no flexbox, no grid, and one border outline per
box instead of four independently styled edges. The declared subset is a reader,
not a browser.

**Verified:** `node tools/proto/boxlayout.mjs` 19/19, `textmeasure.mjs` 10/10,
`check-vb.mjs` 0 finding(s), six configurations `BUILD_EXIT=0`.
**Not verified:** the on-device output. Nothing in this round has been drawn on a
handset; the geometry is asserted off-device and the rendering is not asserted at
all. Record the first real screenshot's surprises here when someone runs it.
```

Add to the `### Deferred from the native-engine phase` list:

```markdown
8. **Layout has no collapsed-margin model and no auto-margin centring.** Spacing
   between blocks is therefore slightly wider than a browser's, and a centred
   `max-width` page (`margin: 0 auto`) will be left-aligned. Both are refinements
   of `BlockLayout`, and both are visible only once there is layout.
9. **`text-align` reads the container, not the line's own runs.** A line whose runs
   disagree about alignment follows the block that holds it. The subset this engine
   claims has one alignment per block.
10. **A word wider than its line overflows instead of breaking.** Deliberate: a
    split URL is a lie about the text. It is also how a long unbroken token becomes
    a horizontal scrollbar.
```

- [ ] **Step 6: Update the skill**

In `.agents/skills/browserforwp/SKILL.md`, add to the verification table, after the `core-logic.mjs` row:

```markdown
| Text measurement (`ITextMeasurer`, either implementation) | `node tools/proto/textmeasure.mjs` | `10/10 checks passed` |
| `BlockLayout.vb` / `InlineLayout.vb` / `LayoutBox.vb`, or anything that positions a box | `node tools/proto/boxlayout.mjs` | `19/19 checks passed` |
```

And add to the file listing:

```markdown
tools/proto/textmeasure.mjs   ← the measurer's arithmetic (referee)
tools/proto/boxlayout.mjs     ← block/inline layout numbers (referee)
```

- [ ] **Step 7: Record the outcome in this plan**

Append to this file:

```markdown
## Outcome — what executing this plan actually produced

(filled in during execution: the exact check counts, the first `BUILD_EXIT` lines,
and every place the plan was wrong. Do not rewrite the plan to match; append.)
```

- [ ] **Step 8: Commit and push**

```bash
git add tests/BrowserForWP.Core.Tests/CoreLogicTests.vb tools/proto/core-logic.mjs \
        docs/MAINTAINING.md .agents/skills/browserforwp/SKILL.md \
        docs/superpowers/plans/2026-09-28-native-engine-layout.md
git commit -m "docs: record the round that drew a page with the native engine"
git push
```

---

## Verification matrix

| Layer touched | Command | Expected |
| --- | --- | --- |
| Measurer | `node tools/proto/textmeasure.mjs` | `10/10 checks passed` |
| Layout | `node tools/proto/boxlayout.mjs` | `19/19 checks passed` |
| Core logic (incl. the new measurer mirror) | `node tools/proto/core-logic.mjs` | `0 failure(s)` |
| Crypto | `node tools/gen-vectors.mjs` | `53 assertions, 0 failure(s)` |
| Any `.vb` / `.xaml` / `.vbproj` / `.resw` | `node tools/check-vb.mjs` | `0 finding(s)`, exit 0 |
| Polyfill | `node tools/check-polyfill.mjs` | `is valid ES5` |
| Assets | `python3 tools/make_logo.py` | 12 PNGs, no git diff |
| Every `.vb` | `tools\vm-build.cmd /t:Rebuild` in the guest, six configurations | `BUILD_EXIT=0`, no `error BC`, only the two `ResourceLoader` warnings |
| On a handset | Diagnostics → **Render current page natively** on a real URL | a page drawn by this engine; record the surprises in `docs/MAINTAINING.md` |

**"Compiled" is not "tested".** The measurer and the layout arithmetic *are*
tested, off-device, by executed prototypes. The rendering is **not tested at all**:
no check in this repository predicts what `XamlBoxRenderer` looks like. Treat the
first on-device render as the beginning of that verification, not as a formality.

## Self-Review

**Spec coverage.** The request this plan came from — "our own engine, like
Chromium" — has three parts, and each is answered where it belongs. *Own engine:*
Tasks 1–4, which is the layout and rendering Phase 1 deliberately deferred.
*Like Chromium:* impossible on this platform, and `docs/ARCHITECTURE.md` Law 1
plus `docs/MAINTAINING.md` § "IE-adaptation is closed" already record why with
evidence — no JIT, no arbitrary native binaries, no MSHTML surface. *"Pretending to
be IE to inherit its permissions":* there is nothing to inherit. The AppContainer
grant is per package, declared in `Package.appxmanifest`, and Trident inside the
`WebView` runs in the same container as the app. No task exists for it because no
mechanism exists for it; this paragraph is the record, so the next session does not
re-derive it.

**Placeholder scan.** No `TBD`, no "add error handling", no "similar to Task N".
Every code step carries the whole file or the whole function.

**Type consistency.** `LayoutBoxKind` (not `LayoutKind`, which is a BCL name) with
`Block`/`Line`/`TextRun`; `LayoutBox.XPx`/`YPx` documented once as the border box
and read that way by both `InlineLayout` and `XamlBoxRenderer`; `ITextMeasurer`
has exactly `MeasureWidth` and `LineHeight`, both taking a `ComputedStyle`;
`FixedAdvanceTextMeasurer.AdvanceFactor`/`NormalLineHeightFactor` are asserted by
name in `textmeasure.mjs` and referenced by name in `CoreLogicTests.vb`;
`InlineLayout.BuildLines` has one signature, quoted verbatim in `boxlayout.mjs`'s
parity check and used verbatim in `BlockLayout.LayOutInlineRun`.

**Anchors are text, never line numbers.** Every insertion point in this plan names
the exact line it goes after (`<Compile Include="Engine\Native\BoxTreeBuilder.vb" />`,
`ParsePageButton.Content = Localizer.Get("ParseThisPage")`, the closing `End Sub` of
`ParsePageButton_Click`, `// ── Summary ───...`). Line numbers in `MainPage.xaml.vb`,
`CoreLogicTests.vb` and both `.vbproj` files moved during this repository's last two
rounds, and a plan that cites a line number is wrong the moment it is read after the
first task is applied.

---

## Outcome — what executing this plan actually produced

Executed 2026-09-28, all five tasks, `c079267` → the commit that carries this
section. Red-first held throughout: every prototype failed before it passed, and
every failure was a specific wrong number rather than a missing file.

### Per task, the counts

| Task | Referee | RED | GREEN | Commit |
| --- | --- | --- | --- | --- |
| 1 — text-measurement seam | `node tools/proto/textmeasure.mjs` | 5/10 | 10/10 | `c079267` |
| 2 — block layout | `node tools/proto/boxlayout.mjs` | 6/10 | 10/10 | `3464081` |
| 3 — inline flow | `node tools/proto/boxlayout.mjs` | 16/19 | 19/19 | `0b233f5` |
| 4 — XAML rendering | none; this task has no referee | — | — | `020cde6` |
| 5 — record and regress | `node tools/proto/core-logic.mjs` | 53 assertions | 55 assertions, 0 failures | *this commit* |

Task 4 having no referee is not an oversight in the table: the plan scheduled no
check for the renderer because none is possible here, and the Verification matrix
below says so in advance. The consequence is real and is restated under "Not
verified".

### Where the plan was wrong

**1. `FontStyles` — four `BC30451` errors, and this plan wrote every one of them.**

Task 1's `XamlTextMeasurer.vb` (this plan, lines 280 and 282) and Task 4's
`XamlBoxRenderer.vb` (lines 1405 and 1407) are written with
`FontStyles.Italic` / `FontStyles.Normal`. That type is `System.Windows` — WPF. The
WinRT profile has no `FontStyles` helper at all, and the guest build reported
**four** `error BC30451: 'FontStyles' non dichiarato`, one per site. The fix names
`Windows.UI.Text.FontStyle.Italic` / `.Normal`, and the hazard is now `check-vb.mjs`
group 12's newest entry, paid for with a negative control in `/tmp/bfwp-neg`:
**4 findings with `FontStyles` restored, 0 without**.

What makes this worth recording rather than just fixing: **XAML markup cannot
produce this error.** `FontStyle="Italic"` inside a `<TextBlock>` resolves through
the enum and never names the WPF helper, so the surrounding `.xaml` files gave no
scent of it. Neither did any analogue in the repository. Only the compiler knew.

**2. Task 1's commit does not build, and this plan's own granularity rule says it
must.**

`c079267` declares `BrowserForWP/Rendering/XamlTextMeasurer.vb` in
`BrowserForWP.vbproj`, with `FontStyles` at its lines 50 and 52. It first compiles
in `020cde6`, where that same file was changed as a side effect of fixing Task 4.
So a task the plan describes as "ends with an independently testable deliverable"
shipped three commits earlier as a non-building tree.

Nothing caught it, and **nothing could have**: the plan scheduled a Node prototype
run per task and a guest build only once, at the end. A Node prototype cannot know
a profile hazard, and a guest build — run once — cannot say which commit introduced
one. The rule this round earned is therefore stronger than the plan's:

> **A task that adds a `.vb` file to a `.vbproj` ends with a rebuild, not merely a
> prototype run.**

Recorded in `docs/MAINTAINING.md` Round 7 and in the loop in
`.agents/skills/browserforwp/SKILL.md`.

**3. Step 4's command, as written, prints nothing.**

The loop ends `| grep -E "BUILD_EXIT|error BC|error MSB|warning BC" | sort -u`. On
this host `sort` rejects MSBuild's console output with `Illegal byte sequence` and
emits nothing, so the command *as planned* would have shown a blank line under each
of the six configuration headers — with all six builds actually at `BUILD_EXIT=0`.
A false negative wearing the costume of a failure. The working form writes each raw
log to a file and filters it afterwards with `LC_ALL=C grep -a`.

**4. The group count was stale the day the plan was written.**

Step 3 expects `64 check group(s) run, 0 finding(s)`. The true number is **71**:
group 14 (project flavour) landed in `b09de8b` earlier the same session, after this
plan was written and before it was executed. Everything else in Step 3's expected
output was exact, including `53 assertions` → `55`.

### What was verified, and what was not

Verified for real, on the guest, this round:

- **Six configurations, `BUILD_EXIT=0` each** — `Debug|ARM`, `Debug|x86`,
  `Release|ARM`, `Release|x86`, and `Debug`/`Release` of the app project at
  `AnyCPU` (the platform name cannot cross `prlctl exec` because it contains a
  space). Only the two deliberate `BC40000` `ResourceLoader` warnings.
- **The test project compiled**, which is the point of running it after Task 5:
  `BrowserForWP.sln` line 15 registers `BrowserForWP.Core.Tests`, and
  `vm-build.cmd` line 44 builds the solution, so the new
  `Imports BrowserForWP.Core.Engine.Native` and both measurer checks were compiled
  rather than assumed.
- `core-logic.mjs` 55 assertions / 0 failures; `textmeasure.mjs` 10/10;
  `boxlayout.mjs` 19/19; `gen-vectors.mjs` 53 assertions; `check-vb.mjs` 71 groups
  / 0 findings; `check-polyfill.mjs` ES5; `make_logo.py` regenerates all 12 PNGs
  with no `git status` change.

**Not verified: everything the user would actually see.** No check in this
repository predicts what `XamlBoxRenderer` draws, and nothing in this round has been
run on a handset. Page geometry is asserted by executed prototypes; the rendering is
asserted by nothing at all. This is the clearest case of "compiled is not tested" in
the project so far, and the first on-device render should be treated as the start of
the renderer's verification rather than as a formality. Still open from Phase 1 and
untouched here: `NetDocumentFetcher`'s redirect loop and latin1 branch against live
servers, `line-height` inherited as an already-resolved px value, and `http://` URLs
that speak TLS to port 80.

### The lesson about checkers

Group 12's entries are now all paid for by a build that failed. A static checker
cannot discover a platform hazard it does not already know, so only a build can
teach it one; the two are not redundant and neither can be skipped. Section "Where
the plan was wrong" item 2 is the converse and the more expensive half: a build
that runs once at the end of five tasks can be green while three of those commits
were red.
