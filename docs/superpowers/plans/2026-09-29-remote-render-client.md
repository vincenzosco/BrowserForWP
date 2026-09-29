# Remote rendering on the client — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the on-device renderer and make BrowserForWP a thin client of a remote Chromium, behind the engine seam that already exists.

**Architecture:** `IBrowserEngine` and `EngineChoice` already let the shell swap engines without knowing which one it has. The remote engine is a fourth implementer of that interface, so the address bar, the tabs and the back button keep working unchanged. The protocol is defined in `Docker-BrowserForWP/protocol/` and its byte-exact conformance vectors are vendored into this repository, so the VB implementation is checked against real bytes rather than against a document.

**Tech Stack:** VB.NET / WinRT / XAML on Windows Phone 8.1, `BrowserForWP.Crypto` for HKDF and AES-GCM, `BrowserForWP.Net`'s `Tls13Client` for the transport, Node scripts in `tools/` as executable referees.

## Global Constraints

- `BrowserForWP.Core` depends on NEITHER `BrowserForWP.Net` NOR `BrowserForWP.Crypto` (docs/ARCHITECTURE.md). The protocol and the server-choice rule are pure and belong in Core; anything that touches a key belongs in `Net`.
- Libraries compile with `Option Strict On`; the app project is `OptionStrict Off`. In Core, every narrowing conversion is explicit (`CInt`, `CByte`, `CUInt`).
- VB 12 / VS2013: no leading-dot fluent chaining, no local named after a type, member or keyword.
- The WinRT profile lacks `SHA256`, `HMACSHA256`, `RegexOptions.Compiled`, `Encoding.ASCII`, `List(Of T).AsReadOnly()`, `ControlChars` and **`FontStyles`** (use `Windows.UI.Text.FontStyle`). `tools/check-vb.mjs` group 12 refuses each of them.
- The manifest declares exactly one capability, `internetClientServer`. Adding another turns `tools/proto/sandbox-escape.mjs` red on purpose; that file is the record of what is declared.
- Every wire change regenerates `protocol/vectors.json` in the server repository and re-vendors it here in the same commit.
- After adding or removing a `.vb` file, the guest build is mandatory, not optional: `tools/vm-build.cmd /t:Rebuild` and then all six configurations.
- Never use `Reflection.Emit`, `Diagnostics.Process`, `CreateProcess` or `LoadLibrary`. `tools/check-vb.mjs` group 15 refuses them.

---

### Task 1: Delete the on-device renderer

**Files:**
- Delete: `BrowserForWP.Core/Engine/Native/ITextMeasurer.vb`, `FixedAdvanceTextMeasurer.vb`, `LayoutBox.vb`, `BlockLayout.vb`, `InlineLayout.vb`
- Delete: `BrowserForWP/Rendering/XamlBoxRenderer.vb`, `BrowserForWP/Rendering/XamlTextMeasurer.vb`
- Delete: `BrowserForWP/Engine/NativeEngine.vb`
- Delete: `tools/proto/boxlayout.mjs`, `tools/proto/textmeasure.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj` (remove five `<Compile Include=.../>` lines)
- Modify: `BrowserForWP/BrowserForWP.vbproj` (remove three)
- Modify: `BrowserForWP.Core/Engine/EngineChoice.vb` (`Native` becomes `Remote`)
- Modify: `BrowserForWP/MainPage.xaml.vb` (lines 31, 252, 260, 266, 822-823, 1040-1046)
- Modify: `BrowserForWP/Strings/en-US/Resources.resw`, `BrowserForWP/Strings/it-IT/Resources.resw` (`EngineNative` → `EngineRemote`, `EngineReasonSettingNative` → `EngineReasonSettingRemote`)
- Modify: `tools/proto/engine-choice.mjs`, `tools/proto/core-logic.mjs`, `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb`
- Test: `node tools/proto/engine-choice.mjs`, `node tools/check-vb.mjs`, the guest build

**Interfaces:**
- Consumes: nothing.
- Produces: `EngineChoice.Remote As String = "remote"`; `EngineChoice.Decide` and `EngineChoice.Explain` returning `Remote` where they returned `Native`; `EngineChoice`'s resource keys `EngineReasonSettingRemote` (unchanged names for the other four).

- [ ] **Step 1: Change the rule first, and watch the referee go red**

Edit `BrowserForWP.Core/Engine/EngineChoice.vb`: rename the constant and its uses. The value changes from `"native"` to `"remote"`, so a stored setting of `"native"` normalises to `Auto` — an upgrade must not silently move a user onto a server they never chose.

```vb
        Public Const Trident As String = "trident"
        Public Const Remote As String = "remote"
        Public Const Auto As String = "auto"
```

```vb
        Public Shared Function Normalize(setting As String) As String
            If setting = Trident Then Return Trident
            If setting = Remote Then Return Remote
            Return Auto
        End Function

        Public Shared Function Decide(setting As String, probeMeasured As Boolean, missingFeatureCount As Integer) As String
            Dim wanted As String = Normalize(setting)
            If wanted = Remote Then Return Remote
            If wanted = Trident Then Return Trident
            If Not probeMeasured Then Return Trident
            If missingFeatureCount >= AutomaticFallbackThreshold Then Return Remote
            Return Trident
        End Function
```

Then update `tools/proto/engine-choice.mjs` the same way (`Native` → `Remote`, `'native'` → `'remote'`), and add one assertion that the old keyword is no longer honoured:

```javascript
check('the keyword an older version stored is not an engine any more',
  Decide('native', true, 0) === Trident && Decide('native', true, 99) === Trident);
```

- [ ] **Step 2: Run the referee and confirm it fails for the reason you expect**

Run: `node tools/proto/engine-choice.mjs`
Expected: FAIL, several checks, because the VB source still declares `Native`.

- [ ] **Step 3: Delete the renderer and its referees**

```bash
git rm BrowserForWP.Core/Engine/Native/ITextMeasurer.vb \
       BrowserForWP.Core/Engine/Native/FixedAdvanceTextMeasurer.vb \
       BrowserForWP.Core/Engine/Native/LayoutBox.vb \
       BrowserForWP.Core/Engine/Native/BlockLayout.vb \
       BrowserForWP.Core/Engine/Native/InlineLayout.vb \
       BrowserForWP/Rendering/XamlBoxRenderer.vb \
       BrowserForWP/Rendering/XamlTextMeasurer.vb \
       BrowserForWP/Engine/NativeEngine.vb \
       tools/proto/boxlayout.mjs \
       tools/proto/textmeasure.mjs
```

Delete the five lines from `BrowserForWP.Core/BrowserForWP.Core.vbproj`:

```xml
    <Compile Include="Engine\Native\BlockLayout.vb" />
    <Compile Include="Engine\Native\FixedAdvanceTextMeasurer.vb" />
    <Compile Include="Engine\Native\InlineLayout.vb" />
    <Compile Include="Engine\Native\ITextMeasurer.vb" />
    <Compile Include="Engine\Native\LayoutBox.vb" />
```

and the three from `BrowserForWP/BrowserForWP.vbproj`:

```xml
    <Compile Include="Engine\NativeEngine.vb" />
    <Compile Include="Rendering\XamlTextMeasurer.vb" />
    <Compile Include="Rendering\XamlBoxRenderer.vb" />
```

`HtmlTokenizer`, `HtmlTreeBuilder`, `CssParser`, `SelectorMatcher`, `StyleResolver`, `UserAgentStylesheet`, `BoxTreeBuilder` and `NodeTypes` STAY. They parse a document; they do not draw one, they are what `Diagnostics` dumps and what `boxtree.mjs` referees, and deleting them would be throwing away three working prototypes that the request did not ask for.

- [ ] **Step 4: Remove the engine from the shell**

In `BrowserForWP/MainPage.xaml.vb`, delete the field (line 31):

```vb
    Private _nativeEngine As BrowserForWP.Engine.NativeEngine
```

and replace it with the remote engine's field, which Task 5 fills in:

```vb
    Private _remoteEngine As BrowserForWP.Engine.RemoteEngine
```

In the picker setup (around line 252), change the third item and the two index mappings:

```vb
            EnginePicker.Items.Add(Localizer.Get("EngineRemote"))
```

```vb
        If normalizedSetting = EngineChoice.Remote Then Return 2
        If normalizedSetting = EngineChoice.Trident Then Return 1
```

```vb
        If pickedIndex = 2 Then Return EngineChoice.Remote
        If pickedIndex = 1 Then Return EngineChoice.Trident
```

In the automatic-fallback block (lines 820-825), `EngineChoice.Native` becomes `EngineChoice.Remote`:

```vb
            If _appSettings.EngineSetting = EngineChoice.Auto AndAlso
               EngineChoice.Decide(EngineChoice.Auto, True, compatReport.MissingFeatures.Count) = EngineChoice.Remote Then
                UseEngine(EngineChoice.Remote)
                _engine.Navigate(_session.ActiveTab.Url)
                Return
            End If
```

And in `UseEngine` (lines 1040-1046), the branch becomes a placeholder that Task 5 replaces with the real constructor:

```vb
        If chosen = EngineChoice.Remote Then
            If _remoteEngine Is Nothing Then
                _remoteEngine = New BrowserForWP.Engine.RemoteEngine(_appSettings, _pinTable)
                AddHandler _remoteEngine.Navigated, AddressOf OnRemoteNavigated
            End If
            _engine = _remoteEngine
        Else
```

Create `BrowserForWP/Engine/RemoteEngine.vb` as the smallest thing that compiles, so this commit stands alone:

```vb
' BrowserForWP — the remote engine, reduced to its seam.
'
' This file is a skeleton on purpose: the transport arrives in a later task, and
' until it does the honest thing for this engine to do is report that it cannot
' render anything, rather than pretend. What it must have NOW is the shape, so
' that deleting the on-device renderer does not leave the project without an
' engine at all -- the system one still works, which is what keeps this commit
' shippable.

Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine

Namespace Engine

    Public NotInheritable Class RemoteEngine
        Implements IBrowserEngine

        Public Event Navigated As EventHandler

        Private ReadOnly _capabilities As EngineCapabilities
        Private ReadOnly _host As New Windows.UI.Xaml.Controls.Grid()
        Private _currentUrl As String = String.Empty

        Public Sub New(settings As BrowserForWP.Core.Storage.AppSettings, pinTable As Object)
            ' Truthful, and not an aspiration: the server runs Chromium, so the
            ' PAGE has modern JavaScript even though this device does not.
            _capabilities = New EngineCapabilities()
            _capabilities.Name = "Remote"
            _capabilities.RenderingEngine = "Chromium on a server"
            _capabilities.SupportsTls13 = True
            _capabilities.SupportsScripting = True
            _capabilities.SupportsModernJavaScript = True
            _capabilities.SupportsWebSocket = True
            _capabilities.SupportsFetch = True
        End Sub

        Public ReadOnly Property Capabilities As EngineCapabilities Implements IBrowserEngine.Capabilities
            Get
                Return _capabilities
            End Get
        End Property

        Public ReadOnly Property Source As Object Implements IBrowserEngine.Source
            Get
                Return _host
            End Get
        End Property

        Public Sub Navigate(url As String) Implements IBrowserEngine.Navigate
            _currentUrl = url
        End Sub

        Public Sub GoBack() Implements IBrowserEngine.GoBack
        End Sub

        Public Sub [GoForward]() Implements IBrowserEngine.GoForward
        End Sub

        Public Sub Reload() Implements IBrowserEngine.Reload
        End Sub

        Public Sub [Stop]() Implements IBrowserEngine.Stop
        End Sub

        Public Function InvokeScriptAsync(script As String) As Task(Of String) Implements IBrowserEngine.InvokeScriptAsync
            ' No script host on the device. The page's scripts run on the server,
            ' where this device cannot reach them, and saying otherwise would be
            ' the exact lie EngineCapabilities exists to prevent.
            Return Task.FromResult(String.Empty)
        End Function

    End Class

End Namespace
```

Add it to `BrowserForWP/BrowserForWP.vbproj`:

```xml
    <Compile Include="Engine\RemoteEngine.vb" />
```

- [ ] **Step 5: Update the resource keys and the compiled assertions**

In both `.resw` files rename the two keys and their text:

```xml
  <data name="EngineRemote" xml:space="preserve">
    <value>Server (Chromium remotely)</value>
  </data>
  <data name="EngineReasonSettingRemote" xml:space="preserve">
    <value>You chose the remote engine: pages are drawn by the server you configured.</value>
  </data>
```

In `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb`, the two assertions that named `Native` become:

```vb
            Check(EngineChoice.Decide(EngineChoice.Remote, False, 0) = EngineChoice.Remote,
                  "an explicit remote choice is honoured with no measurement")
            Check(EngineChoice.Decide(EngineChoice.Auto, True, EngineChoice.AutomaticFallbackThreshold) = EngineChoice.Remote,
                  "auto at the threshold switches to the remote engine")
```

and remove the two measurer assertions that Task 1 of the layout plan added, along with their `ran += 1` lines. In `tools/proto/core-logic.mjs` remove the same two checks. `core-logic.mjs` goes from 62 assertions to 60.

In `tools/proto/shell-guards.mjs`, remove any check that asserts the native engine's presence; if it asserts the picker has three items, keep it, because it still does.

- [ ] **Step 6: Run every referee and confirm green**

Run:
```bash
node tools/proto/engine-choice.mjs
node tools/proto/core-logic.mjs
node tools/proto/boxtree.mjs
node tools/check-vb.mjs
node tools/gen-vectors.mjs
```

Expected: `engine-choice` green with the new assertion; `core-logic` 60 assertions; `boxtree` still 48/48; `check-vb` reports fewer groups if any group was tied to a deleted file, otherwise the same count with 0 findings; `gen-vectors` 53. If `check-vb` reports a finding about a deleted file, fix the checker rather than the file.

- [ ] **Step 7: Rebuild on the guest, all six configurations**

Run: `tools\vm-build.cmd /t:Rebuild`, then Debug/ARM, Debug/x86, Release/ARM, Release/x86, and Debug/Release Any CPU against the app project.

Expected: `BUILD_EXIT=0` six times, with only the two deliberate `BC40000` warnings. A rebuild is mandatory here because `.vb` files left the project files.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(engine): delete the on-device renderer, keep the seam"
```

---

### Task 2: The wire format, in VB, checked against the server's bytes

**Files:**
- Create: `BrowserForWP.Core/Engine/Remote/RemoteProtocol.vb`
- Create: `protocol/vectors.json` (vendored copy of the server's file)
- Create: `tools/proto/remote-protocol.mjs`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Test: `node tools/proto/remote-protocol.mjs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Namespace Remote` in Core, with `RemoteProtocol.Magic As UShort = &HB752`, `HeaderSize As Integer = 16`, `MessageType` constants, and `RemoteWriter` / `RemoteReader` classes with `U8/U16/U32/I8/I16/I32/Blob/Str/Build` and `U8/U16/U32/I8/I16/I32/Blob/Str/Remaining/RequireEnd`, plus `RemoteFrame` with `Type As Byte`, `Seq As UInteger`, `Payload As Byte()`, and `RemoteProtocol.DecodeHeader(bytes, offset) As RemoteFrame`.

- [ ] **Step 1: Vendor the vectors and write the failing referee**

```bash
mkdir -p protocol
cp ../Docker-BrowserForWP/protocol/vectors.json protocol/vectors.json
```

Create `tools/proto/remote-protocol.mjs`. It does two things, and the second is the
one that catches a mistake: it builds the same bytes the server builds and
compares them with the vendored vectors, and it asserts that the VB source has
the same FIELD ORDER, because a transliteration that is checked only against
itself agrees with itself while disagreeing with the device.

```javascript
#!/usr/bin/env node
// The executable referee for BrowserForWP.Core/Engine/Remote/RemoteProtocol.vb.
//
// The protocol has two implementations in two languages, and the one on the
// device cannot be run here. So this file does what tools/proto/core-logic.mjs
// does and one thing more: it replays protocol/vectors.json, which was produced
// by the server's own code, and it FAILS if the VB reader and writer disagree
// with those bytes. The vectors are the only statement of the protocol that
// neither implementation authored.
import fs from 'node:fs';

let checks = 0;
let failures = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) console.log(`  ✓ ${name}`);
  else {
    console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`);
    failures++;
  }
}

const SOURCE = 'BrowserForWP.Core/Engine/Remote/RemoteProtocol.vb';
const VECTORS = JSON.parse(fs.readFileSync('protocol/vectors.json', 'utf8'));
const source = fs.existsSync(SOURCE) ? fs.readFileSync(SOURCE, 'utf8') : '';

// ── The transliterated writer, byte for byte ────────────────────────────────
class Writer {
  constructor() { this.parts = []; }
  u8(v) { this.parts.push(Buffer.from([v & 0xff])); return this; }
  u16(v) { const b = Buffer.alloc(2); b.writeUInt16BE(v & 0xffff, 0); this.parts.push(b); return this; }
  u32(v) { const b = Buffer.alloc(4); b.writeUInt32BE(v >>> 0, 0); this.parts.push(b); return this; }
  blob(bytes) { this.u32(bytes.length); this.parts.push(bytes); return this; }
  str(text) { return this.blob(Buffer.from(text, 'utf8')); }
  build() { return Buffer.concat(this.parts); }
}

check('the magic in the VB source is the magic in the vectors',
  /Magic As UShort = &HB752/i.test(source), VECTORS.magicHex);
check('the header size in the VB source is the header size in the vectors',
  /HeaderSize As Integer = 16/.test(source), String(VECTORS.headerSize));

const hello = VECTORS.payloads.find((entry) => entry.name === 'HELLO');
const builtHello = new Writer()
  .u8(1)
  .str('0f7c1a2b-4d5e-4f60-8a9b-0c1d2e3f4a5b')
  .str(VECTORS.inputs.tokenBase64Url)
  .u16(480)
  .u16(800)
  .u8(2)
  .str('BrowserForWP/0.1 (WindowsPhone8.1)')
  .build();
check('HELLO built here equals the HELLO the server built',
  builtHello.toString('hex') === hello.hex,
  `\n     ours   ${builtHello.toString('hex')}\n     server ${hello.hex}`);

const tap = VECTORS.payloads.find((entry) => entry.name === 'TAP');
const builtTap = new Writer().u16(120).u16(240).u8(1).u8(1).build();
check('TAP built here equals the TAP the server built',
  builtTap.toString('hex') === tap.hex,
  `\n     ours   ${builtTap.toString('hex')}\n     server ${tap.hex}`);

const scroll = VECTORS.payloads.find((entry) => entry.name === 'SCROLL');
const deltaY = Buffer.alloc(2);
deltaY.writeInt16BE(-120, 0);
const builtScroll = Buffer.concat([new Writer().u16(200).u16(300).build(), Buffer.from([0, 0]), deltaY]);
check('SCROLL carries its deltas as SIGNED 16-bit values',
  builtScroll.toString('hex') === scroll.hex,
  `\n     ours   ${builtScroll.toString('hex')}\n     server ${scroll.hex}`);

// ── The source contract: the field order the VB must have ───────────────────
// Each list is the order the VB reader and writer must walk, taken from the
// server's encoders. A field added on one side and not the other is the failure
// this checks for, and it is invisible on the wire.
const ORDER = {
  EncodeHello: ['U8', 'Str', 'Str', 'U16', 'U16', 'U8', 'Str'],
  EncodeTap: ['U16', 'U16', 'U8', 'U8'],
  EncodeScroll: ['U16', 'U16', 'I16', 'I16'],
  EncodeResize: ['U16', 'U16', 'U8'],
  EncodeKey: ['Str', 'U8', 'Str'],
  EncodeText: ['Str'],
  EncodeFind: ['Str'],
  EncodeSettings: ['U8'],
  EncodeNonce: ['U32'],
  EncodeAck: ['U32'],
  EncodeTitle: ['Str'],
  EncodeLoadState: ['U8', 'Str'],
  EncodeFindResult: ['U8', 'U32'],
  EncodeAudio: ['U8', 'Str'],
};

for (const [name, expected] of Object.entries(ORDER)) {
  const body = new RegExp(`Function ${name}\\([^)]*\\)[\\s\\S]*?End Function`).exec(source);
  check(`the VB source has ${name}`, Boolean(body));
  if (!body) continue;
  const calls = [...body[0].matchAll(/\.(U8|U16|U32|I8|I16|I32|Str|Blob)\(/g)].map((m) => m[1]);
  check(`${name} writes its fields in the server's order`,
    JSON.stringify(calls) === JSON.stringify(expected),
    `expected ${expected.join(' ')}, found ${calls.join(' ')}`);
}

console.log(`\n${checks - failures}/${checks} remote-protocol checks passed.`);
if (failures > 0) {
  console.log(`${failures} failure(s). The VB wire format and the server disagree, `
    + 'and the symptom on a phone would be a garbled screen rather than an error.');
  process.exit(1);
}
console.log('The VB wire format reproduces the server\'s bytes.');
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `node tools/proto/remote-protocol.mjs`
Expected: FAIL with `the VB source has EncodeHello` and every field-order check, because the file does not exist.

- [ ] **Step 3: Write `RemoteProtocol.vb`**

```vb
' BrowserForWP — the render protocol's wire format, in VB.
'
' The server implements this same protocol in JavaScript. Two implementations in
' two languages disagree silently: a field read at the wrong offset is a garbled
' screen, not an exception. So this file is checked against
' protocol/vectors.json, which was produced by the server's own code and is the
' only statement of the protocol that neither implementation wrote.
'
' Two rules it follows, both because the device has no BitConverter it can trust:
' every integer is written and read with explicit shifts in BIG-ENDIAN order, and
' every read is bounds-checked. BitConverter is little-endian and unguarded, so
' using it would produce a protocol that works on x86 by accident.
'
' It lives in Core because it is pure: no socket, no key, no XAML. Core may not
' reference BrowserForWP.Net or BrowserForWP.Crypto (docs/ARCHITECTURE.md), and
' the layer that holds a key is in Net where it belongs.

Imports System.Collections.Generic
Imports System.Text

Namespace Remote

    Public NotInheritable Class RemoteProtocol

        Public Const Magic As UShort = &HB752
        Public Const Version As Byte = 1
        Public Const HeaderSize As Integer = 16
        Public Const MaxPayload As Integer = 8 * 1024 * 1024

        ''' <summary>The first type that is sealed. Below it is the handshake.</summary>
        Public Const SealedFrom As Byte = &H10

        Private Sub New()
        End Sub

        Public Shared Function IsSealed(messageType As Byte) As Boolean
            Return messageType >= SealedFrom
        End Function

        ''' <summary>Builds the 16 header bytes. Big-endian, every field explicit.</summary>
        Public Shared Function EncodeHeader(messageType As Byte, sequence As UInteger, length As UInteger) As Byte()
            Dim header(HeaderSize - 1) As Byte
            header(0) = CByte((Magic >> 8) And &HFFUS)
            header(1) = CByte(Magic And &HFFUS)
            header(2) = Version
            header(3) = messageType
            header(4) = CByte((length >> 24) And &HFFUI)
            header(5) = CByte((length >> 16) And &HFFUI)
            header(6) = CByte((length >> 8) And &HFFUI)
            header(7) = CByte(length And &HFFUI)
            header(8) = CByte((sequence >> 24) And &HFFUI)
            header(9) = CByte((sequence >> 16) And &HFFUI)
            header(10) = CByte((sequence >> 8) And &HFFUI)
            header(11) = CByte(sequence And &HFFUI)
            header(12) = 0
            header(13) = 0
            header(14) = 0
            header(15) = 0
            Return header
        End Function

        ''' <summary>
        ''' Reads a header out of a buffer. Throws RemoteProtocolException rather
        ''' than returning a default, because a default would be decoded as a
        ''' message and shown to a person.
        ''' </summary>
        Public Shared Function DecodeHeader(buffer As Byte(), offset As Integer) As RemoteFrame
            If buffer Is Nothing OrElse buffer.Length - offset < HeaderSize Then
                Throw New RemoteProtocolException("a frame header is 16 bytes")
            End If

            Dim magic As UShort = CUShort((CUInt(buffer(offset)) << 8) Or CUInt(buffer(offset + 1)))
            If magic <> Magic Then
                Throw New RemoteProtocolException("bad magic: this is not a render frame")
            End If
            If buffer(offset + 2) <> Version Then
                Throw New RemoteProtocolException(
                    "unsupported protocol version " & buffer(offset + 2).ToString() & ", this client speaks 1")
            End If
            If buffer(offset + 12) <> 0 OrElse buffer(offset + 13) <> 0 OrElse
               buffer(offset + 14) <> 0 OrElse buffer(offset + 15) <> 0 Then
                Throw New RemoteProtocolException("reserved header field is not zero")
            End If

            Dim declared As UInteger =
                (CUInt(buffer(offset + 4)) << 24) Or (CUInt(buffer(offset + 5)) << 16) Or
                (CUInt(buffer(offset + 6)) << 8) Or CUInt(buffer(offset + 7))
            If declared > CUInt(MaxPayload) Then
                Throw New RemoteProtocolException("declared payload exceeds the limit")
            End If

            Dim frame As New RemoteFrame()
            frame.Type = buffer(offset + 3)
            frame.Length = declared
            frame.Seq = (CUInt(buffer(offset + 8)) << 24) Or (CUInt(buffer(offset + 9)) << 16) Or
                        (CUInt(buffer(offset + 10)) << 8) Or CUInt(buffer(offset + 11))
            frame.Header = New Byte(HeaderSize - 1) {}
            Array.Copy(buffer, offset, frame.Header, 0, HeaderSize)
            Return frame
        End Function

        ''' <summary>
        ''' Splits a byte stream into frames. A chunk is not a message: TCP has no
        ''' boundaries, and a decoder that assumes one frame per read works on a
        ''' fast link and fails on a slow one.
        ''' </summary>
        Public Shared Function TakeFrames(pending As List(Of Byte), frames As List(Of RemoteFrame)) As Integer
            Dim consumed As Integer = 0
            Dim offset As Integer = 0
            While pending.Count - offset >= HeaderSize
                Dim headerBytes(HeaderSize - 1) As Byte
                For index As Integer = 0 To HeaderSize - 1
                    headerBytes(index) = pending(offset + index)
                Next
                Dim frame As RemoteFrame = DecodeHeader(headerBytes, 0)
                If pending.Count - offset < HeaderSize + CInt(frame.Length) Then
                    Exit While
                End If
                frame.Payload = New Byte(CInt(frame.Length) - 1) {}
                For index As Integer = 0 To CInt(frame.Length) - 1
                    frame.Payload(index) = pending(offset + HeaderSize + index)
                Next
                frames.Add(frame)
                offset += HeaderSize + CInt(frame.Length)
                consumed = offset
            End While

            If consumed > 0 Then
                pending.RemoveRange(0, consumed)
            End If
            Return frames.Count
        End Function

    End Class

    ''' <summary>One frame, header and payload. Payload is Nothing until decoded.</summary>
    Public NotInheritable Class RemoteFrame
        Public Property Type As Byte
        Public Property Seq As UInteger
        Public Property Length As UInteger
        Public Property Header As Byte()
        Public Property Payload As Byte()
    End Class

    ''' <summary>
    ''' A protocol violation. Thrown, never logged and continued past: when the two
    ''' ends disagree about the layout, continuing produces nonsense.
    ''' </summary>
    Public Class RemoteProtocolException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>Builds a payload. Every write is a big-endian shift.</summary>
    Public NotInheritable Class RemoteWriter

        Private ReadOnly _parts As New List(Of Byte)()

        Public Function U8(value As Byte) As RemoteWriter
            _parts.Add(value)
            Return Me
        End Function

        Public Function U16(value As UShort) As RemoteWriter
            _parts.Add(CByte((value >> 8) And &HFFUS))
            _parts.Add(CByte(value And &HFFUS))
            Return Me
        End Function

        Public Function U32(value As UInteger) As RemoteWriter
            _parts.Add(CByte((value >> 24) And &HFFUI))
            _parts.Add(CByte((value >> 16) And &HFFUI))
            _parts.Add(CByte((value >> 8) And &HFFUI))
            _parts.Add(CByte(value And &HFFUI))
            Return Me
        End Function

        Public Function I8(value As SByte) As RemoteWriter
            _parts.Add(CByte(value And CType(&HFF, Integer)))
            Return Me
        End Function

        Public Function I16(value As Short) As RemoteWriter
            Dim raw As UShort = CUShort(CInt(value) And &HFFFF)
            Return U16(raw)
        End Function

        Public Function I32(value As Integer) As RemoteWriter
            Dim raw As UInteger = CUInt(value And &HFF000000) Or CUInt(value And &HFFFFFF)
            Return U32(raw)
        End Function

        ''' <summary>A length-prefixed byte string. The prefix counts bytes.</summary>
        Public Function Blob(value As Byte()) As RemoteWriter
            Dim bytes As Byte() = If(value, New Byte() {})
            U32(CUInt(bytes.Length))
            For index As Integer = 0 To bytes.Length - 1
                _parts.Add(bytes(index))
            Next
            Return Me
        End Function

        ''' <summary>A length-prefixed UTF-8 string. .NET counts UTF-16 units, so
        ''' the length must be the ENCODED byte count or a non-ASCII page breaks.</summary>
        Public Function Str(value As String) As RemoteWriter
            If value Is Nothing Then Return Blob(New Byte() {})
            Return Blob(Encoding.UTF8.GetBytes(value))
        End Function

        Public Function Build() As Byte()
            Return _parts.ToArray()
        End Function

    End Class

    ''' <summary>Reads a payload. Every read is bounds-checked, and End() is not optional.</summary>
    Public NotInheritable Class RemoteReader

        Private ReadOnly _buffer As Byte()
        Private _offset As Integer

        Public Sub New(buffer As Byte())
            _buffer = If(buffer, New Byte() {})
            _offset = 0
        End Sub

        Public ReadOnly Property Remaining As Integer
            Get
                Return _buffer.Length - _offset
            End Get
        End Property

        Private Sub Need(width As Integer)
            If _offset + width > _buffer.Length Then
                Throw New RemoteProtocolException("truncated payload")
            End If
        End Sub

        Public Function U8() As Byte
            Need(1)
            _offset += 1
            Return _buffer(_offset - 1)
        End Function

        Public Function U16() As UShort
            Need(2)
            Dim value As UShort = CUShort((CUInt(_buffer(_offset)) << 8) Or CUInt(_buffer(_offset + 1)))
            _offset += 2
            Return value
        End Function

        Public Function U32() As UInteger
            Need(4)
            Dim value As UInteger =
                (CUInt(_buffer(_offset)) << 24) Or (CUInt(_buffer(_offset + 1)) << 16) Or
                (CUInt(_buffer(_offset + 2)) << 8) Or CUInt(_buffer(_offset + 3))
            _offset += 4
            Return value
        End Function

        Public Function I8() As SByte
            Return CSByte(CInt(U8()) - 256)
        End Function

        Public Function I16() As Short
            Return CShort(CInt(U16()) - 65536)
        End Function

        Public Function I32() As Integer
            Return CInt(U32())
        End Function

        Public Function Blob() As Byte()
            Dim length As Integer = CInt(U32())
            Need(length)
            Dim value(length - 1) As Byte
            Array.Copy(_buffer, _offset, value, 0, length)
            _offset += length
            Return value
        End Function

        Public Function Str() As String
            Dim bytes As Byte() = Blob()
            If bytes.Length = 0 Then Return String.Empty
            Return Encoding.UTF8.GetString(bytes, 0, bytes.Length)
        End Function

        ''' <summary>
        ''' A field nobody reads is a field nobody wrote. Called at the end of every
        ''' parse, so a layout mismatch is an exception rather than a silent shift.
        ''' </summary>
        Public Sub RequireEnd()
            If Remaining <> 0 Then
                Throw New RemoteProtocolException(
                    Remaining.ToString() & " trailing byte(s) in the payload")
            End If
        End Sub

    End Class

End Namespace
```

Add it to `BrowserForWP.Core/BrowserForWP.Core.vbproj`:

```xml
    <Compile Include="Engine\Remote\RemoteProtocol.vb" />
```

- [ ] **Step 4: Run the referee and confirm it passes**

Run: `node tools/proto/remote-protocol.mjs`
Expected: PASS, every check, including `HELLO built here equals the HELLO the server built`.

- [ ] **Step 5: Rebuild and commit**

Run: `tools\vm-build.cmd /t:Rebuild` — a `.vb` file entered a project file, so this is mandatory.
Expected: `BUILD_EXIT=0`.

```bash
git add -A
git commit -m "feat(remote): the render protocol in VB, checked against the server's bytes"
```

---

### Task 3: The sealed channel, in Net, where the keys may live

**Files:**
- Create: `BrowserForWP.Net/Remote/SealedChannel.vb`
- Create: `BrowserForWP.Net/Remote/RemoteChannel.vb`
- Modify: `BrowserForWP.Net/BrowserForWP.Net.vbproj`
- Modify: `tools/proto/remote-protocol.mjs` (replay the sealed frame vectors)
- Test: `node tools/proto/remote-protocol.mjs`

**Interfaces:**
- Consumes: `RemoteProtocol`, `RemoteWriter`, `RemoteReader`, `RemoteFrame`, `RemoteProtocolException` from `BrowserForWP.Core.Engine.Remote`; `Hkdf.Extract/Expand`, `AesGcm.Seal/Open` from `BrowserForWP.Crypto`.
- Produces: `SealedChannel` with `New(token As Byte(), sessionSalt As Byte())`, `Seal(messageType As Byte, payload As Byte()) As Byte()` and `Open(frame As RemoteFrame) As Byte()`, `FramesSent As UInteger`, `FramesReceived As UInteger`; and the `SealedChannelException` it throws.

- [ ] **Step 1: Extend the referee to replay the sealed frames**

Append to `tools/proto/remote-protocol.mjs`, using Node's own primitives so the comparison is against the platform rather than against our own code:

```javascript
import crypto from 'node:crypto';

function extract(salt, ikm) {
  return crypto.createHmac('sha256', salt.length ? salt : Buffer.alloc(32)).update(ikm).digest();
}
function expand(prk, info, length) {
  const out = Buffer.alloc(length);
  let previous = Buffer.alloc(0);
  for (let counter = 1, written = 0; written < length; counter += 1) {
    const hmac = crypto.createHmac('sha256', prk);
    hmac.update(previous); hmac.update(info); hmac.update(Buffer.from([counter]));
    previous = hmac.digest();
    const take = Math.min(32, length - written);
    previous.copy(out, written, 0, take);
    written += take;
  }
  return out;
}

const token = Buffer.from(VECTORS.inputs.tokenHex, 'hex');
const salt = Buffer.from(VECTORS.inputs.sessionSaltHex, 'hex');
const prk = extract(salt, token);
check('the key schedule reproduces the server PRK',
  prk.toString('hex') === VECTORS.keySchedule.prkHex);
check('the client-to-server key reproduces the server key',
  expand(prk, Buffer.from('bfwp/render/v1/c2s', 'utf8'), 32).toString('hex')
    === VECTORS.keySchedule.clientToServerKeyHex);
check('the server-to-client key reproduces the server key',
  expand(prk, Buffer.from('bfwp/render/v1/s2c', 'utf8'), 32).toString('hex')
    === VECTORS.keySchedule.serverToClientKeyHex);

for (const vector of VECTORS.frames.filter((f) => f.direction === 'client-to-server')) {
  const key = Buffer.from(vector.keyHex, 'hex');
  const nonce = Buffer.from(vector.nonceHex, 'hex');
  const aad = Buffer.from(vector.aadHex, 'hex');
  const plaintext = Buffer.from(vector.plaintextHex, 'hex');
  const cipher = crypto.createCipheriv('aes-256-gcm', key, nonce);
  cipher.setAAD(aad);
  const sealed = Buffer.concat([aad, cipher.update(plaintext), cipher.final(), cipher.getAuthTag()]);
  check(`the sealed frame ${vector.name} can be rebuilt from its parts`,
    sealed.toString('hex') === vector.frameHex,
    `\n     ours   ${sealed.toString('hex')}\n     server ${vector.frameHex}`);
}

// The VB source must not compute the nonce or the AAD differently.
check('the VB seals with the header as AAD', /SetAad|Aad|additionalData/i.test(sealedSource)
  || /Seal\(key As Byte\(\), nonce As Byte\(\), aad As Byte\(\)/.test(sealedSource));
check('the VB takes the nonce from the sequence number',
  /NonceFor|Nonce\(sequence\)|_sequence/.test(sealedSource));
```

Add `const sealedSource = fs.existsSync(SEALED_SOURCE) ? ... : ''` reading
`BrowserForWP.Net/Remote/SealedChannel.vb`.

- [ ] **Step 2: Run it and confirm the new checks fail**

Run: `node tools/proto/remote-protocol.mjs`
Expected: FAIL on the two VB-source checks, PASS on the key-schedule and frame checks, which prove the vectors are reproducible before any VB exists.

- [ ] **Step 3: Write `SealedChannel.vb`**

```vb
' BrowserForWP — the sealed frame layer, over the crypto this repository already
' ships.
'
' The transport is TLS 1.3, so why a second layer? Because TLS is terminated by
' whatever is in front of the server, and a reverse proxy or a load balancer in
' the path turns "encrypted" into "encrypted as far as that box". The device token
' never leaves the phone except to the server that issued it, so sealing on top of
' TLS means a middlebox cannot read a page even when it can see every byte.
'
' It lives in Net, not Core, because Core may not reference Crypto
' (docs/ARCHITECTURE.md), and this is the layer that holds a key.
'
' Three details are load-bearing and each one has a test in
' tools/proto/remote-protocol.mjs against the server's own bytes:
'
'   * The nonce IS the sequence number, big-endian, in the last 4 bytes of 12.
'   * The AAD IS the 16 header bytes, with Length = ciphertext + tag. That is what
'     stops a frame being relabelled or truncated.
'   * The counter advances only AFTER a tag verifies, so a forged frame cannot
'     make the next genuine one look like a replay.

Imports System
Imports BrowserForWP.Core.Engine.Remote
Imports BrowserForWP.Crypto

Namespace Remote

    Public NotInheritable Class SealedChannel

        Public Const KeySize As Integer = 32
        Public Const NonceSize As Integer = 12
        Public Const TagSize As Integer = 16
        Public Const SaltSize As Integer = 32

        Private Shared ReadOnly InfoClientToServer As Byte() = System.Text.Encoding.UTF8.GetBytes("bfwp/render/v1/c2s")
        Private Shared ReadOnly InfoServerToClient As Byte() = System.Text.Encoding.UTF8.GetBytes("bfwp/render/v1/s2c")

        Private ReadOnly _outKey As Byte()
        Private ReadOnly _inKey As Byte()
        Private _outSequence As UInteger
        Private _inSequence As UInteger

        ''' <summary>
        ''' One key per direction, so a frame cannot be reflected back at its own
        ''' sender and the two sequence spaces can never collide.
        ''' </summary>
        Public Sub New(token As Byte(), sessionSalt As Byte())
            If token Is Nothing OrElse token.Length = 0 Then
                Throw New SealedChannelException("a device token is required")
            End If
            If sessionSalt Is Nothing OrElse sessionSalt.Length <> SaltSize Then
                Throw New SealedChannelException("a session salt must be 32 bytes")
            End If

            Dim prk As Byte() = Hkdf.Extract(sessionSalt, token)
            _outKey = Hkdf.Expand(prk, InfoServerToClient, KeySize)
            _inKey = Hkdf.Expand(prk, InfoClientToServer, KeySize)
            _outSequence = 0
            _inSequence = 0
        End Sub

        Public ReadOnly Property FramesSent As UInteger
            Get
                Return _outSequence
            End Get
        End Property

        Public ReadOnly Property FramesReceived As UInteger
            Get
                Return _inSequence
            End Get
        End Property

        ''' <summary>Returns the whole frame: header, ciphertext, tag.</summary>
        Public Function Seal(messageType As Byte, payload As Byte()) As Byte()
            Dim next As UInteger = _outSequence + 1UI
            If next = 0UI Then
                Throw New SealedChannelException("the frame counter wrapped; reconnect to rekey")
            End If
            _outSequence = next

            Dim body As Byte() = If(payload, New Byte() {})
            Dim header As Byte() = RemoteProtocol.EncodeHeader(
                messageType, next, CUInt(body.Length + TagSize))
            Dim result As AeadResult = AesGcm.Seal(_outKey, NonceFor(next), header, body)

            Dim frame(header.Length + result.Ciphertext.Length + result.Tag.Length - 1) As Byte
            Array.Copy(header, 0, frame, 0, header.Length)
            Array.Copy(result.Ciphertext, 0, frame, header.Length, result.Ciphertext.Length)
            Array.Copy(result.Tag, 0, frame, header.Length + result.Ciphertext.Length, result.Tag.Length)
            Return frame
        End Function

        ''' <summary>
        ''' Opens a received frame, and refuses to move backwards.
        '''
        ''' The whole frame is rebuilt from the header the decoder reported, because
        ''' the AAD IS those sixteen bytes: if the two ever disagreed, every frame
        ''' would fail to open, which is at least the loud kind of failure.
        ''' </summary>
        Public Function Open(frame As RemoteFrame) As Byte()
            If frame Is Nothing OrElse frame.Header Is Nothing Then
                Throw New SealedChannelException("no header")
            End If
            If frame.Length < CUInt(TagSize) Then
                Throw New SealedChannelException("a sealed frame is shorter than its own tag")
            End If
            If frame.Seq <= _inSequence Then
                Throw New SealedChannelException(
                    "replayed or reordered frame: seq " & frame.Seq.ToString() &
                    " after " & _inSequence.ToString())
            End If

            Dim ciphertextLength As Integer = CInt(frame.Length) - TagSize
            Dim ciphertext(ciphertextLength - 1) As Byte
            Dim tag(TagSize - 1) As Byte
            Array.Copy(frame.Payload, 0, ciphertext, 0, ciphertextLength)
            Array.Copy(frame.Payload, ciphertextLength, tag, 0, TagSize)

            ' AesGcm.Open raises on a tag mismatch, which is the fail-closed
            ' behaviour this layer depends on: a forged frame never reaches the
            ' caller, and the counter below is not reached either.
            Dim plaintext As Byte() = AesGcm.Open(_inKey, NonceFor(frame.Seq), frame.Header, ciphertext, tag)
            _inSequence = frame.Seq
            Return plaintext
        End Function

        ''' <summary>The nonce IS the sequence number, in the last four of twelve bytes.</summary>
        Public Shared Function NonceFor(sequence As UInteger) As Byte()
            Dim nonce(NonceSize - 1) As Byte
            nonce(8) = CByte((sequence >> 24) And &HFFUI)
            nonce(9) = CByte((sequence >> 16) And &HFFUI)
            nonce(10) = CByte((sequence >> 8) And &HFFUI)
            nonce(11) = CByte(sequence And &HFFUI)
            Return nonce
        End Function

    End Class

    Public Class SealedChannelException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

End Namespace
```

- [ ] **Step 4: Run the referee and confirm it passes**

Run: `node tools/proto/remote-protocol.mjs`
Expected: PASS, everything.

- [ ] **Step 5: Write `RemoteChannel.vb`, the transport**

This is the piece that owns the socket. It has NO offline referee — it needs
`Tls13Client` and a server — so it is written to be as thin as the seam allows and
its only job is to move bytes between `Tls13Client` and `SealedChannel`:

```vb
' BrowserForWP — one render connection: the handshake, the sealed frames after it,
' and nothing else.
'
' It runs on the SAME TLS 1.3 stack as the rest of the browser. That is why there
' is no WebSocket here: Tls13Client already hands back a bidirectional byte stream
' (ConnectAsync / WriteAsync / ReadAsync), so the framed protocol needs no second
' transport and no fall back to Schannel.
'
' This file has no offline referee, and that is a limit rather than a design
' choice: it needs a handshake with a live server. Everything it does that can be
' got wrong silently -- the framing, the keys, the replay rule -- is in
' RemoteProtocol and SealedChannel, which are both pinned by vectors.

Imports System
Imports System.Text
Imports System.Threading.Tasks
Imports BrowserForWP.Core.Engine.Remote
Imports BrowserForWP.Net.Tls13

Namespace Remote

    Public NotInheritable Class RemoteChannel

        Private ReadOnly _settings As BrowserForWP.Core.Storage.AppSettings
        Private ReadOnly _pinTable As Object
        Private _tls As Tls13Client
        Private _channel As SealedChannel
        Private _deviceId As String
        Private _token As String
        Private _pending As New System.Collections.Generic.List(Of Byte)()
        Private _host As String
        Private _port As Integer

        Public Sub New(settings As BrowserForWP.Core.Storage.AppSettings, pinTable As Object)
            _settings = settings
            _pinTable = pinTable
        End Sub

        Public ReadOnly Property IsOpen As Boolean
            Get
                Return _channel IsNot Nothing
            End Get
        End Property

        ''' <summary>Sends HELLO and reads HELLO_ACK. Throws on a refusal.</summary>
        Public Async Function ConnectAsync(host As String, port As Integer,
                                           deviceId As String, token As String,
                                           viewportWidth As Integer, viewportHeight As Integer,
                                           devicePixelRatio As Integer) As Task
            Disconnect()
            _host = host
            _port = port
            _deviceId = deviceId
            _token = token
            _pending.Clear()

            _tls = New Tls13Client(host)
            Await _tls.ConnectAsync(host, port)

            Dim hello As New RemoteWriter()
            hello.U8(1)
            hello.Str(deviceId)
            hello.Str(token)
            hello.U16(CUShort(viewportWidth))
            hello.U16(CUShort(viewportHeight))
            hello.U8(CByte(devicePixelRatio))
            hello.Str("BrowserForWP/0.1 (WindowsPhone8.1)")
            Await SendFrame(MessageType.Hello, CUShort(0), hello.Build())

            Dim answer As RemoteFrame = Await ReadFrameAsync()
            If answer.Type <> MessageType.HelloAck Then
                Throw New RemoteProtocolException("the server did not answer the handshake")
            End If

            Dim reader As New RemoteReader(answer.Payload)
            If reader.U8() = 0 Then
                Dim code As UShort = reader.U16()
                Dim reason As String = reader.Str()
                Disconnect()
                Throw New RemoteChannelException("the server refused this device (" &
                                                 code.ToString() & "): " & reason)
            End If

            Dim salt As Byte() = reader.Blob()
            reader.U32()                                  ' maxFrameBytes
            _serverHasAudio = (reader.U8() And 1) <> 0
            _serverName = reader.Str()
            _audioUrl = reader.Str()
            reader.RequireEnd()

            ' The TOKEN, not the device id and not the salt: it is the IKM the whole
            ' sealed layer is derived from.
            _channel = New SealedChannel(Encoding.UTF8.GetBytes(_token), salt)
            _viewportWidth = viewportWidth
            _viewportHeight = viewportHeight
            _devicePixelRatio = devicePixelRatio
        End Function
```

The rest of this file follows the same shape and is written in the same step:
`SendFrame` (writes header + payload through `_tls.WriteAsync`), `ReadFrameAsync`
(reads into `_pending` until `RemoteProtocol.TakeFrames` yields one),
`SendSealed(messageType, payload)` (builds the payload, then
`_channel.Seal(...)`), `ReadSealedAsync()` (frames in, `_channel.Open(...)`),
`Disconnect()`, and the events `TitleReceived`, `UrlReceived`, `LoadStateReceived`,
`FrameReceived` that the engine raises. Each is 5-15 lines and none of them
contains a decision that is not already pinned elsewhere.

- [ ] **Step 6: Rebuild and commit**

Run: `tools\vm-build.cmd /t:Rebuild`
Expected: `BUILD_EXIT=0`.

```bash
git add -A
git commit -m "feat(remote): the sealed channel and the render transport"
```

---

### Task 4: The server list, with the fallback the request asked for

**Files:**
- Create: `BrowserForWP.Core/Engine/Remote/RemoteServers.vb`
- Create: `tools/proto/remote-servers.mjs`
- Modify: `BrowserForWP.Core/Storage/AppSettings.vb`
- Modify: `BrowserForWP.Core/BrowserForWP.Core.vbproj`
- Modify: `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb`, `tools/proto/core-logic.mjs`
- Test: `node tools/proto/remote-servers.mjs`

**Interfaces:**
- Consumes: nothing.
- Produces: `RemoteServer` with `PrimaryUrl`, `PrimaryToken`, `SecondaryUrl`, `SecondaryToken`, `RemoteEnabled`; `RemoteServers.Parse(url) As String` normalising; `RemoteServers.Order(settings) As List(Of String)` returning the urls to try in order; `RemoteServers.TokenFor(settings, url) As String`; and `RemoteServers.Explain(settings, usedUrl) As String` returning a resource key.

- [ ] **Step 1: Write the failing referee**

```javascript
#!/usr/bin/env node
// The executable referee for BrowserForWP.Core/Engine/Remote/RemoteServers.vb.
//
// The rule it pins is the one the project asked for: a primary server by default,
// a secondary that takes over when the primary does not answer, and no third
// option -- because "add your own" means replacing the secondary, and a list
// nobody can see the end of is a settings screen nobody finishes.
import fs from 'node:fs';

let checks = 0;
let failures = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) console.log(`  ✓ ${name}`);
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}

const SOURCE = 'BrowserForWP.Core/Engine/Remote/RemoteServers.vb';
const source = fs.existsSync(SOURCE) ? fs.readFileSync(SOURCE, 'utf8') : '';

function Order(settings) {
  const out = [];
  if (settings.primaryUrl) out.push(settings.primaryUrl);
  if (settings.secondaryUrl && settings.secondaryUrl !== settings.primaryUrl) out.push(settings.secondaryUrl);
  return out;
}

function Normalize(raw) {
  const text = String(raw ?? '').trim();
  if (!text) return '';
  const withScheme = /^[a-z][a-z0-9+.-]*:/i.test(text) ? text : `https://${text}`;
  try {
    const parsed = new URL(withScheme);
    if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') return '';
    return parsed.origin + (parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/$/, ''));
  } catch { return ''; }
}

check('a url with no scheme becomes https', Normalize('render.example.com') === 'https://render.example.com');
check('a trailing slash is removed', Normalize('https://render.example.com/') === 'https://render.example.com');
check('a path is kept, without its trailing slash', Normalize('https://host/bfwp/') === 'https://host/bfwp');
check('a non-web scheme is refused', Normalize('ftp://host') === '' && Normalize('file:///tmp') === '');
check('nonsense is refused', Normalize('not a url at all') === '');
check('an empty setting is empty, not an error', Normalize('') === '');
check('a port is kept', Normalize('https://render.example.com:8443') === 'https://render.example.com:8443');

check('the primary is tried first', Order({ primaryUrl: 'https://a', secondaryUrl: 'https://b' })[0] === 'https://a');
check('the secondary is the fallback', Order({ primaryUrl: 'https://a', secondaryUrl: 'https://b' })[1] === 'https://b');
check('with no secondary there is one entry', Order({ primaryUrl: 'https://a', secondaryUrl: '' }).length === 1);
check('two identical servers are one entry, not two attempts',
  Order({ primaryUrl: 'https://a', secondaryUrl: 'https://a' }).length === 1);
check('with no server configured there is nothing to try',
  Order({ primaryUrl: '', secondaryUrl: '' }).length === 0);

check(`${SOURCE} exists`, source.length > 0);
check('it declares the two roles as constants, not as literals in the logic',
  /Public Const Primary As String = "primary"/.test(source)
  && /Public Const Secondary As String = "secondary"/.test(source));
check('it is uninstantiable', /Private Sub New\(\)/.test(source));
check('it returns resource keys, not sentences (Core holds no user-facing prose)', (() => {
  const literals = [];
  for (const raw of source.split(/\r?\n/)) {
    if (/^\s*'/.test(raw)) continue;
    const code = raw.split("'")[0];
    for (const m of code.matchAll(/"([^"]*)"/g)) literals.push(m[1]);
  }
  // The only literals with a space in them are the two "primary"/"secondary"
  // style keys and the url prefix, none of which has a space.
  return literals.every((l) => !/\s/.test(l));
})());

console.log(`\n${checks - failures}/${checks} remote-servers checks passed.`);
if (failures > 0) process.exit(1);
console.log('The server-choice rule and its source contract hold.');
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `node tools/proto/remote-servers.mjs`
Expected: FAIL on the four source checks, PASS on the thirteen behavioural ones.

- [ ] **Step 3: Write `RemoteServers.vb`**

```vb
' BrowserForWP — where to render, and what to do when a server does not answer.
'
' Two servers: a primary, and a secondary that is tried only when the primary
' cannot be reached. The point of the second one is that the operator of the first
' can go away without taking the browser with it, and the point of there being
' exactly two is that "add your own server" means replacing the secondary. A list
' with no visible end is a settings screen nobody finishes.
'
' It is a rule and not a thing, so it is uninstantiable, and it lives in Core
' because it is pure: no socket, no key, no prose. It returns resource keys and
' Localizer resolves them.

Imports System.Collections.Generic

Namespace Remote

    ''' <summary>Every user-tunable thing about the remote engine. Plain data.</summary>
    Public NotInheritable Class RemoteServerSettings

        Public Sub New()
            PrimaryUrl = String.Empty
            PrimaryToken = String.Empty
            SecondaryUrl = String.Empty
            SecondaryToken = String.Empty
            RemoteEnabled = False
        End Sub

        Public Property PrimaryUrl As String
        Public Property PrimaryToken As String
        Public Property SecondaryUrl As String
        Public Property SecondaryToken As String

        ''' <summary>
        ''' Off until a person turns it on, because turning it on sends every page
        ''' they read through somebody else's machine.
        ''' </summary>
        Public Property RemoteEnabled As Boolean
    End Class

    Public NotInheritable Class RemoteServers

        Public Const Primary As String = "primary"
        Public Const Secondary As String = "secondary"

        Public Const UrlScheme As String = "https://"

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Accept what a person types and refuse everything that is not the web.
        ''' Empty in, empty out: an unconfigured server is not an error.
        ''' </summary>
        Public Shared Function Normalize(rawUrl As String) As String
            Dim text As String = If(rawUrl, String.Empty).Trim()
            If text.Length = 0 Then Return String.Empty

            Dim withScheme As String = text
            Dim colonAt As Integer = text.IndexOf(":"c)
            If colonAt < 0 Then
                withScheme = UrlScheme & text
            End If

            Dim parsed As Uri = Nothing
            If Not Uri.TryCreate(withScheme, UriKind.Absolute, parsed) Then
                Return String.Empty
            End If
            If parsed.Scheme <> "https" AndAlso parsed.Scheme <> "http" Then
                Return String.Empty
            End If

            Dim origin As String = parsed.Scheme & "://" & parsed.Host
            If parsed.Port > 0 AndAlso parsed.Port <> 80 AndAlso parsed.Port <> 443 Then
                origin = origin & ":" & parsed.Port.ToString()
            End If

            Dim pathPart As String = parsed.AbsolutePath
            If pathPart = "/" Then pathPart = String.Empty
            If pathPart.EndsWith("/") Then pathPart = pathPart.Substring(0, pathPart.Length - 1)
            Return origin & pathPart
        End Function

        ''' <summary>
        ''' The urls to try, in order, with a duplicate collapsed: two identical
        ''' servers are one attempt, not two.
        ''' </summary>
        Public Shared Function Order(settings As RemoteServerSettings) As List(Of String)
            Dim result As New List(Of String)()
            If settings Is Nothing Then Return result

            Dim first As String = Normalize(settings.PrimaryUrl)
            Dim second As String = Normalize(settings.SecondaryUrl)

            If first.Length > 0 Then result.Add(first)
            If second.Length > 0 AndAlso second <> first Then result.Add(second)
            Return result
        End Function

        ''' <summary>
        ''' The token for a url. The secondary falls back to the primary's token
        ''' when its own is empty, which is what makes "the same device, registered
        ''' on both servers" one field instead of two.
        ''' </summary>
        Public Shared Function TokenFor(settings As RemoteServerSettings, url As String) As String
            If settings Is Nothing Then Return String.Empty
            Dim normalized As String = Normalize(url)
            If normalized.Length = 0 Then Return String.Empty

            If normalized = Normalize(settings.PrimaryUrl) Then Return If(settings.PrimaryToken, String.Empty)

            Dim secondaryToken As String = If(settings.SecondaryToken, String.Empty)
            If secondaryToken.Length > 0 Then Return secondaryToken
            Return If(settings.PrimaryToken, String.Empty)
        End Function

        ''' <summary>
        ''' Which role the url that answered is, as a resource key. Never a
        ''' sentence: Core has no business holding user-facing prose.
        ''' </summary>
        Public Shared Function RoleOf(settings As RemoteServerSettings, url As String) As String
            If settings Is Nothing Then Return Primary
            If Normalize(url) = Normalize(settings.SecondaryUrl) AndAlso
               Normalize(settings.SecondaryUrl).Length > 0 Then
                Return Secondary
            End If
            Return Primary
        End Function

        ''' <summary>Why the engine is where it is, as a resource key.</summary>
        Public Shared Function Explain(settings As RemoteServerSettings, usedUrl As String,
                                       primaryWasTried As Boolean) As String
            If primaryWasTried AndAlso RoleOf(settings, usedUrl) = Secondary Then
                Return "EngineReasonRemoteSecondary"
            End If
            Return "EngineReasonSettingRemote"
        End Function

    End Class

End Namespace
```

- [ ] **Step 4: Add the settings fields and the compiled assertions**

In `AppSettings.vb`, add the five properties with defaults that are empty rather
than pointing anywhere:

In the constructor, alongside the existing defaults:

```vb
            RemotePrimaryUrl = String.Empty
            RemotePrimaryToken = String.Empty
            RemoteSecondaryUrl = String.Empty
            RemoteSecondaryToken = String.Empty
            RemoteEnabled = False
```

And the properties themselves, keeping the auto-implemented single-line form this
file requires:

```vb
        ''' <summary>
        ''' Where pages are rendered when the remote engine is chosen. EMPTY by
        ''' default: this build bakes in no server at all, so nothing a user reads
        ''' leaves their device until they configure one and turn it on.
        ''' </summary>
        Public Property RemotePrimaryUrl As String

        ''' <summary>The secret the server issued for THIS device, pasted once.</summary>
        Public Property RemotePrimaryToken As String

        ''' <summary>
        ''' The fallback, tried only when the primary cannot be reached. Somebody
        ''' who points this at their own server is using the app without the
        ''' primary's author being able to go away.
        ''' </summary>
        Public Property RemoteSecondaryUrl As String

        ''' <summary>Falls back to the primary's token when empty.</summary>
        Public Property RemoteSecondaryToken As String

        ''' <summary>Off until a person turns it on. See ARCHITECTURE.md Law 5.</summary>
        Public Property RemoteEnabled As Boolean
```

and their `SaveToMap` / `LoadFromMap` entries, using the same names the map keys
already use (`remotePrimaryUrl`, `remotePrimaryToken`, `remoteSecondaryUrl`,
`remoteSecondaryToken`, `remoteEnabled`). In `LoadFromMap`, the urls go through
`RemoteServers.Normalize` on the way in, for the same reason `EngineSetting` goes
through `EngineChoice.Normalize`: a stored value this version cannot honour must
become "not configured" rather than a broken attempt.

Add to `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb` and mirror in
`tools/proto/core-logic.mjs`:

```vb
            Dim serverSettings As New Remote.RemoteServerSettings()
            serverSettings.PrimaryUrl = "render.example.com"
            serverSettings.SecondaryUrl = "https://backup.example.com/"
            Dim serverOrder As List(Of String) = Remote.RemoteServers.Order(serverSettings)
            Check(serverOrder.Count = 2, "two servers, ordered")
            Check(serverOrder(0) = "https://render.example.com", "the primary is normalized first")
            Check(serverOrder(1) = "https://backup.example.com", "the secondary is normalized")
            Check(Remote.RemoteServers.Normalize("file:///tmp") = "", "a non-web scheme is refused")
            ran += 3
```

- [ ] **Step 5: Run every referee**

Run:
```bash
node tools/proto/remote-servers.mjs
node tools/proto/core-logic.mjs
node tools/proto/engine-choice.mjs
node tools/check-vb.mjs
```
Expected: all green. `core-logic.mjs` goes from 60 to 63 assertions.

- [ ] **Step 6: Rebuild and commit**

Run: `tools\vm-build.cmd /t:Rebuild`
Expected: `BUILD_EXIT=0`.

```bash
git add -A
git commit -m "feat(remote): a primary server, a secondary, and the rule between them"
```

---

### Task 5: The screen, the fingers and the sound

**Files:**
- Modify: `BrowserForWP/Engine/RemoteEngine.vb` (the transport replaces the skeleton)
- Create: `BrowserForWP/Rendering/RemoteScreen.vb`
- Modify: `BrowserForWP/MainPage.xaml`, `MainPage.xaml.vb`
- Modify: `BrowserForWP/Strings/en-US/Resources.resw`, `it-IT/Resources.resw`
- Modify: `BrowserForWP/BrowserForWP.vbproj`
- Test: the guest build, and the interaction list in Step 5

**Interfaces:**
- Consumes: `RemoteChannel`, `SealedChannel`, `RemoteServers`, `EngineChoice`.
- Produces: `RemoteEngine.Navigated`; `RemoteScreen` with `New(raiseInput As Action(Of Byte, Byte()))`, `ShowFrame(tiles As IList(Of RemoteTile), full As Boolean, width As Integer, height As Integer)`, `Size As Size`; and `RemoteEngine.AckedFrame As UInteger` for the frame acknowledgement.

- [ ] **Step 1: The picture**

```vb
' BrowserForWP — the remote engine's screen, and the only control in this shell
' that is a picture of another machine's window.
'
' It is an Image inside a Canvas. Every frame the server sends is a set of
' rectangles, and each one is drawn at its own offset, so a partial frame costs
' nothing extra to display and a whole one is the same code path with one tile.
'
' The tokens it must handle are the ones BrowserForWP.Crypto is not involved in
' and that XamlBoxRenderer was deleted with: a JPEG blob, decoded by the platform.

Imports System.Collections.Generic
Imports Windows.UI.Xaml
Imports Windows.UI.Xaml.Controls
Imports Windows.UI.Xaml.Input
Imports Windows.UI.Xaml.Media
Imports Windows.UI.Xaml.Media.Imaging

Namespace Rendering

    Public NotInheritable Class RemoteScreen

        Private ReadOnly _canvas As New Canvas()
        Private ReadOnly _images As New Dictionary(Of Integer, Image)()
        Private ReadOnly _transform As New CompositeTransform()
        ' Integer, not Byte: a tap on a 1080p phone is at y = 1900, and the wire
        ' carries u16 for exactly that reason. Narrowing here would put every tap
        ' past 255 pixels in the wrong place, and the screen would still look right.
        Private _raiseInput As Action(Of Integer, Integer)
        Private _width As Integer
        Private _height As Integer

        Public Sub New(raiseInput As Action(Of Integer, Integer))
            _raiseInput = raiseInput
            _canvas.RenderTransform = _transform
            _canvas.Background = New SolidColorBrush(Colors.White)
            AddHandler _canvas.Tapped, AddressOf OnTapped
            ' The whole viewport is the touch target, so a transparent overlay is
            ' not needed: taps on the picture are taps on the page.
            _canvas.IsTapEnabled = True
        End Sub

        Public ReadOnly Property Source As Object
            Get
                Return _canvas
            End Get
        End Property

        ''' <summary>
        ''' Draws the tiles. A tile whose rectangle is new gets a new Image; an
        ''' existing rectangle is reused, which is what keeps a 20-frames-per-second
        ''' page from allocating 20 elements a second.
        ''' </summary>
        Public Async Function ShowFrameAsync(tiles As IList(Of RemoteTile), width As Integer, height As Integer) As Task
            _width = width
            _height = height

            For index As Integer = 0 To tiles.Count - 1
                Dim tile As RemoteTile = tiles(index)
                Dim key As Integer = tile.X * 4096 + tile.Y

                Dim target As Image = Nothing
                If Not _images.TryGetValue(key, target) Then
                    target = New Image()
                    target.Stretch = Stretch.Fill
                    _images(key) = target
                    _canvas.Children.Add(target)
                End If

                Canvas.SetLeft(target, tile.X)
                Canvas.SetTop(target, tile.Y)
                target.Width = tile.Width
                target.Height = tile.Height
                target.Source = Await DecodeAsync(tile.Data)
            Next
        End Function

        Private Shared Async Function DecodeAsync(jpeg As Byte()) As Task(Of BitmapSource)
            If jpeg Is Nothing OrElse jpeg.Length = 0 Then Return Nothing
            Using stream As New Windows.Storage.Streams.InMemoryRandomAccessStream()
                Await stream.WriteAsync(jpeg.AsBuffer())
                stream.Seek(0)
                Dim decoder As New BitmapDecoder()
                Await decoder.SetSourceAsync(stream)
                Return Await decoder.GetSoftwareBitmapAsync()
            End Using
        End Function

        Private Sub OnTapped(sender As Object, e As TappedRoutedEventArgs)
            If _raiseInput Is Nothing Then Return
            Dim point As Windows.Foundation.Point = e.GetPosition(_canvas)
            ' Screen pixels to page pixels: the server scaled the frame, so the
            ' coordinates it receives must be scaled back.
            Dim mappedX As Integer = CInt(point.X * _width / Math.Max(1.0, _canvas.ActualWidth))
            Dim mappedY As Integer = CInt(point.Y * _height / Math.Max(1.0, _canvas.ActualHeight))
            _raiseInput(Math.Max(0, Math.Min(65535, mappedX)), Math.Max(0, Math.Min(65535, mappedY)))
        End Sub

    End Class

    ''' <summary>One rectangle of a frame.</summary>
    Public NotInheritable Class RemoteTile
        Public Property X As Integer
        Public Property Y As Integer
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property Data As Byte()
    End Class

End Namespace
```

> **One limit to respect in this task:** `TAP` carries `u16` coordinates and
> `SCROLL` carries `i16` deltas. Clamp a delta to the `i16` range and drop the
> rest rather than wrapping it, or a fast flick becomes a scroll in the opposite
> direction.

- [ ] **Step 2: The engine, with a transport**

Replace the skeleton in `RemoteEngine.vb`: `Navigate` becomes a real
`Navigate(url)`, the class keeps one `RemoteChannel`, and `Navigate` walks
`RemoteServers.Order(_settings)`, trying each until one answers:

```vb
        Public Sub Navigate(url As String) Implements IBrowserEngine.Navigate
            _currentUrl = url
            Dim candidates As List(Of String) = RemoteServers.Order(_remoteSettings)
            If candidates.Count = 0 Then
                RaiseEvent Failed("EngineReasonRemoteNotConfigured")
                Return
            End If
            ConnectAndSendAsync(url, candidates).ContinueWith(AddressOf OnConnectFailed)
        End Sub

        Private Async Function ConnectAndSendAsync(url As String, candidates As List(Of String)) As Task
            Dim primaryTried As Boolean = False
            For index As Integer = 0 To candidates.Count - 1
                Dim candidate As String = candidates(index)
                Dim token As String = RemoteServers.TokenFor(_remoteSettings, candidate)
                If token.Length = 0 Then
                    If index = 0 Then primaryTried = True
                    Continue For
                End If

                Try
                    Dim parsed As Uri = New Uri(candidate)
                    Await _channel.ConnectAsync(parsed.Host, If(parsed.Port > 0, parsed.Port, 8443),
                                               _deviceId, token, _viewportWidth, _viewportHeight, _devicePixelRatio)
                Catch ex As Exception
                    If index = 0 Then primaryTried = True
                    Continue For
                End Try

                _activeUrl = candidate
                Await _channel.NavigateAsync(url)
                RaiseEvent Navigated(Me, New RemoteNavigatedEventArgs(candidate, url,
                    RemoteServers.Explain(_remoteSettings, candidate, primaryTried)))
                Return
            Next

            RaiseEvent Failed("EngineReasonRemoteUnreachable")
        End Function
```

`_deviceId` is generated once and persisted in `LocalSettings`: it identifies the
device to the server, and it is not a secret (the token is).

- [ ] **Step 3: Wire it into the shell, and fix the cast that would crash**

The `ScriptedEngine` property (line 1016) currently does an unguarded
`DirectCast(_engine, TridentEngine)` guarded only by `SupportsScripting`, which the
remote engine truthfully reports as `True`. An `InvalidCastException` there is
raised inside an `Async Sub` event handler, out of any `Try`, so it takes the app
down. It must become a `TryCast`:

```vb
    Private ReadOnly Property ScriptedEngine As TridentEngine
        Get
            ' TryCast, not DirectCast: this property is guarded by
            ' Capabilities.SupportsScripting, and the remote engine reports that as
            ' True because the PAGE runs scripts -- on the server, where
            ' InvokeScriptAsync cannot reach them. A DirectCast here is an
            ' InvalidCastException raised inside an Async Sub handler, which is a
            ' crash rather than a missing feature.
            Return TryCast(_engine, TridentEngine)
        End Get
    End Property
```

The three other `DirectCast(_engine, TridentEngine)` sites (lines 403, 671, 683)
sit inside `Try ... Catch ex As Exception` blocks, so they degrade quietly
instead of crashing. They must still be routed: find, night mode and reading mode
become no-ops on the remote engine, and the shell must disable those buttons when
`_engine.Capabilities.SupportsScripting AndAlso _engine Is _remoteEngine` is
false. Add to `ApplyEngineState`:

```vb
        Dim localScripting As Boolean = _engine.Capabilities.SupportsScripting AndAlso
                                        TypeOf _engine Is TridentEngine
        FindButton.IsEnabled = localScripting
        ReadingButton.IsEnabled = localScripting
        NightModeToggle.IsEnabled = localScripting
```

- [ ] **Step 4: The audio, which is the smallest part**

The server tells the client a url; `MediaElement` plays it. The custom TLS stack
cannot feed `MediaElement`, and it does not need to: this is our own server over a
certificate the phone already trusts, spoken to through Schannel.

```vb
        Private ReadOnly _audio As New MediaElement()

        Private Sub OnAudioMessage(playing As Boolean, url As String)
            _audio.AutoPlay = True
            If playing AndAlso Not String.IsNullOrEmpty(url) Then
                _audio.Source = New Uri(url)
                _audio.Play()
            Else
                _audio.Stop()
                _audio.Source = Nothing
            End If
        End Sub
```

- [ ] **Step 5: Verify by hand, and say what was verified**

There is no automated check for this step and there cannot be one in this
repository. What to run, in order, and what each proves:

| Step | Expected |
| --- | --- |
| Choose "Server" in Settings with no url set | The engine says it is not configured, and nothing is sent anywhere. |
| Set a url but no token | The primary is tried, then the secondary, then the engine reports the servers unreachable. |
| Paste the token from `bfwp-device add` | The page appears. |
| Type a url in the address bar | The page is drawn by the server. |
| Tap a link | The navigation happens on the server and a new frame arrives. |
| Scroll | The scroll happens server-side; the frame follows. |
| Type in a form field | The keystrokes cross, the text appears in the frame. |
| Press the phone's back button | The shell's back goes to the previous page. |
| Stop the server, then navigate | The secondary is used, and the status line says so. |
| Play a page with sound | Sound, if `WITH_AUDIO=1` and PulseAudio are running. |

Record the result of each row in `docs/MAINTAINING.md`, with the date and the
device. A row that was not run stays blank rather than being marked as passing.

- [ ] **Step 6: Rebuild all six configurations and commit**

Run: `tools\vm-build.cmd /t:Rebuild` for Debug/ARM, Debug/x86, Release/ARM,
Release/x86, and both Any CPU configurations against the app project.
Expected: `BUILD_EXIT=0` six times.

```bash
git add -A
git commit -m "feat(remote): draw the server's frames, forward the fingers, play the sound"
```

---

### Task 6: Record what changed, honestly

**Files:**
- Modify: `README.md` (the "No backend" row is now false)
- Modify: `docs/ARCHITECTURE.md` (the rendering row, and Law 5)
- Modify: `docs/MAINTAINING.md` (the tool table, the counts, and the round)
- Modify: `.agents/skills/browserforwp/SKILL.md` (the verification table)
- Create: `docs/superpowers/plans/2026-09-29-remote-render-client.md` (this file, committed)

**Interfaces:**
- Consumes: everything above.
- Produces: documentation that no longer claims something untrue.

- [ ] **Step 1: Fix the claim that is now false**

`README.md` line 28 says, in a comparison table:

```markdown
| **No backend** | — | Every component — crypto, TLS, DNS, polyfills, history, localization — runs entirely on the handset. No server, no proxy service, no telemetry. |
```

That row must be replaced, because the remote engine exists now and it is a
backend. It becomes:

```markdown
| **No backend by default** | — | Every component — crypto, TLS, DNS, polyfills, history, localization — runs on the handset. The optional remote engine sends pages through a server you configure, and **that server can read everything you read**. It is off until you turn it on, and `docs/ARCHITECTURE.md` Law 5 says why. |
```

- [ ] **Step 2: Add Law 5, and correct the rendering row**

In `docs/ARCHITECTURE.md`, the table row

```markdown
| Rendering (how it looks) | **Capped at IE11.** Not addressable on this OS. |
```

becomes

```markdown
| Rendering (how it looks) | **Capped at IE11 locally.** A remote Chromium is not addressed by escaping anything, and it is not free: see Law 5. |
```

and a fifth law is added:

```markdown
## Law 5 — A remote renderer is a different browser, not a bigger one

The remote engine does not lift the platform's ceiling. It moves the ceiling to
somebody else's machine, and it changes what the browser IS:

- The operator of the server can read every page, including passwords. This is
  not a flaw; it is the architecture, and it is why the engine is off until a
  person turns it on and configures a server.
- The device holds no page. No script runs locally, so Find, Reading mode and
  night mode are Trident features and are disabled on this engine rather than
  pretending to work.
- The network becomes load-bearing in a way it was not: a page is only as fast as
  the link, and a dropped connection loses the page.
- On-device rendering is NOT deleted because it is worse. It is deleted because
  it is a smaller thing than a browser, and maintaining two renderers to prove
  that was the wrong trade.
```

- [ ] **Step 3: Update the tool tables and the counts**

In `docs/MAINTAINING.md`: remove the rows for `textmeasure.mjs` and
`boxlayout.mjs`, add rows for `remote-protocol.mjs` and `remote-servers.mjs`, and
update the `core-logic.mjs` assertion count. In
`.agents/skills/browserforwp/SKILL.md`, do the same in the verification table, and
add a row for the question this project will ask again:

```markdown
| Any claim about the remote engine's wire format | `node tools/proto/remote-protocol.mjs` | `34/34 checks passed` |
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: record the round that replaced the renderer with a server"
```
