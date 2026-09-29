---
name: browserforwp
description: Use when working on the BrowserForWP project (Windows Phone 8.1 browser with a modern on-device TLS 1.3 transport). Covers the mandatory plan → test → commit → push loop, how to locate and build the project, and exactly how to add or modify a feature. Trigger this whenever a change touches BrowserForWP.sln, its projects, the crypto/TLS layers, the UI, or the localization resources.
---

# BrowserForWP — Project Skill

Governs every change to this repository. Read
[`docs/MAINTAINING.md`](../../../docs/MAINTAINING.md) and
[`docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md) before touching the
transport or engine layers.

## The non-negotiable loop

Every request — feature, bug, refactor, docs — runs this loop. No exceptions,
including "trivial" one-line changes.

```
1. PLAN      write the plan to docs/superpowers/plans/YYYY-MM-DD-<slug>.md
2. TEST      write the failing test FIRST (see "Testing rules")
3. IMPLEMENT the minimum that makes it pass
4. VERIFY    run the verification commands for the layer you touched
5. COMMIT    one logical change per commit, conventional-commit subject
6. PUSH      git push — always, at the end of every completed task
```

Step 6 is not optional. If a push fails, say so loudly and stop; do not
silently leave work unpushed and continue.

## Step 1 — Always plan first

Save to `docs/superpowers/plans/YYYY-MM-DD-<feature-slug>.md`. A plan has:

- A one-sentence **Goal**.
- **Global Constraints** copied verbatim from the request (version floors,
  platform limits, "no backend", "on-device only").
- A **File Structure** section: every file created or modified, and why.
- One section per **task**, each task ending in an independently testable
  deliverable with its own commit.
- **Exact** file paths, **complete** code in every code step, **exact**
  commands with **expected output**.

Never write `TBD`, "add error handling", "similar to Task N", or a step that
describes work without showing the code. If a step changes code, show the code.

**Hard platform constraint — check every plan against it.** Windows Phone 8.1
cannot host Chromium or Firefox, cannot replace Trident, and cannot exceed
TLS 1.2 through the OS. Anything that assumes otherwise is a plan error. See
[`docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md#the-three-platform-laws).

**Second hard constraint — the API surface.** A WP8.1 WinRT app compiles against
the ".NET for Windows Store apps" profile, *not* desktop .NET. `SHA256`,
`HMACSHA256` and `RNGCryptoServiceProvider` do **not exist** there; use
`WinRtCrypto` (`Windows.Security.Cryptography.Core`). `RegexOptions.Compiled` and
`Encoding.ASCII` are unsupported too, and `CryptographicEngine` exposes
`VerifySignature`, never `Verify`. Before introducing any `System.*` type, confirm
it exists in that profile — do not assume a desktop API is available.
`tools/check-vb.mjs` flags this family; the list is not exhaustive, so the flag is
a floor, not a ceiling.

**Second-and-a-half — the language is VB 12, not VB 14.** The toolchain is Visual
Studio 2013. Implicit line continuation after a `.` arrived in **VB 14 (VS2015)**,
so the JavaScript-style fluent chain that reads so naturally is a syntax error
here:

```vb
Dim w = New TlsWriter().
    U8(1).
    ToArray()
```

That is `BC30203` on the trailing dot, and then `U16`, `Bytes`, `Vec8` and
`ToArray` all report "not declared" — 24 errors in one file, none of which names
the real cause. Use a `With` block with one call per line; it reads the same and
compiles. `tools/check-vb.mjs` catches this now, because it cost a whole round.

**Second-and-three-quarters — VB is case-insensitive, so a local can shadow a
type or a member.** `Dim extensionType = ...` next to the `ExtensionType` enum
makes every `ExtensionType.X` in the file report "'X' is not a member of
'Integer'", pointing at the enum and never at the local. The same trap produced
`shared`/`Shared`, `supported`/`Supported`, `tag`/`Tag` (a `Page` property),
`Default`/`DefaultTag` (a keyword), and `value` inside `Function Value()`
(`BC30290`). Name locals after what they *hold*, not after the type.

**Second-and-seven-eighths — a VB keyword is not a name, and the compiler will not
say so.** `Dim next As UInteger = _outSequence + 1UI` produced `BC30201
"expression expected"` on that one line and then **eleven** `BC30451 "'header' is
not declared"` lines after it, every one of them naming something that plainly IS
declared. Eleven errors, none of which names the cause, from one word. `next`,
`error`, `date`, `step`, `set`, `in`, `of`, `to` are all words a person reaches for
without thinking. `tools/check-vb.mjs` group 17 refuses the shape now.

**Its word list is MEASURED, not quoted.** The first version came from the language
reference, and the reference disagrees with the compiler: `Out` is in its reserved
list and `Dim out(31) As Byte` compiles — it is on disk in `X25519.vb` and that
project builds in all six configurations. `async`, `await` and `custom` are
tolerated too. So the list comes from `tools/keyword-probe.cmd`, which compiles one
`Dim <word> As Integer` per candidate and reads the answer (117 candidates, 113
refused, 4 accepted). **Never add a word to group 17 without running the probe.**
And note what the probe's own first run taught: a single file with 117 candidates
reported "legal" for the last sixteen, because vbc 12 is pre-Roslyn and gives up
after about a hundred errors *with no message*. It is batched now, with a sentinel
at the end of each batch. When a measurement can fail by going quiet, the sample
size is not the problem — a witness is.

**Second-and-seven-eighths — a `{ThemeResource}` key is resolved at page LOAD, so
no build here can check it.** It has to exist in the **phone's** dictionaries, not
the desktop's: Windows 8.1 ships its own `themeresources.xaml` and `generic.xaml`
under `Windows Kits\8.1`, the two sets differ, and a UWP-era name compiles,
packages, and then cannot resolve on the handset. `tools/check-vb.mjs` group 9
checks every key in the app's XAML against `tools/wp81-theme-keys.txt` (523 keys,
regenerated with `bash tools/wp81-theme-keys.sh`); declare app-local keys in
`App.xaml` and they are accepted. `MainPage.xaml` asked for `TextControlBackground`
and shipped because a previous round swapped a *working* key for it and misread an
intermittent `WMC9999` as proof — see `docs/MAINTAINING.md` Round 4.

**Second-and-fifteen-sixteenths — the first GUID in `ProjectTypeGuids` is the
project *flavour*, and only the IDE's project system reads it.** A Windows Phone
8.1 app can resolve a reference only to a project of the same flavour, so every
project here must carry `{76F1466A-8B6D-4E39-A767-685A06062A39}` — the value both
the Windows Phone 8.1 app template and the Windows Phone 8.1 class library
template write. `{BC8A1FFA-BEE3-4634-8014-F334798102B3}` is the **Windows Store**
library flavour, and the libraries here carried it for five rounds while declaring
`TargetPlatformIdentifier` `WindowsPhoneApp`. The error list then says
`The referenced component 'BrowserForWP.Core' could not be found.` for a project
that is present, correct and already built, and **no build can contradict it**:
`MSBuild` never reads `ProjectTypeGuids`, and the string does not exist anywhere
under `MSBuild`, the Windows Phone 8.1 SDK or `Windows Kits\8.1` in the guest.
A diagnostic with no `BC`/`MSB`/`APPX` code is the tell that the build is not the
component speaking. Flavour is now checked by `tools/check-vb.mjs` group 14; the
authoritative values are the VS2013 templates under
`Common7\IDE\ProjectTemplates\VisualBasic\` in the guest — compare against those
rather than editing a GUID until a warning disappears (see Round 6).

**Second-and-thirty-one-thirty-seconds — `BrowserForWP.sln` names a project
*factory*, not a flavour, and both rules are measured.** Every `Project` line in
the solution must carry `{F184B08F-C81C-45F6-A57F-5ABD9991F28F}`, the plain VB
GUID — the only project factory registered in this VS2013 installation — and its
path must use `/`, not `\`. The flavour GUID of the paragraph above belongs in the
`.vbproj` and nowhere else. Round 6 concluded the opposite for the `.sln` and was
wrong: it read the registry (which shows which factories *exist*) and inferred the
loader ignores the field, when the loader instead finds no factory and loads
nothing at all. Measured, one field apart, `devenv.com BrowserForWP.sln /build
"Debug|ARM"`: `{76F1466A-...}` gives `Build: 0 succeeded or up-to-date, 0 failed,
0 skipped` — not one project loaded — and `{F184B08F-...}` gives
`Build: 7 succeeded, 0 failed`. `MSBuild` reads none of it, so all six
configurations stay green either way.

**Second-and-sixty-three-sixty-fourths — never write the name of the flavour
property inside a comment in a `.vbproj`.** The IDE's Windows Phone project
factory locates it by scanning the file as **text**, not by parsing it as XML, so
the first occurrence of the name wins. Put one in a comment ahead of the element —
as `BrowserForWP.Crypto` did — and the flavour reads as empty, and the IDE refuses
the whole project with `The application for the project is not installed.`: no
diagnostic code, no build able to see it. Say "the flavour property" in prose.
Measured by bisection in Round 12: a byte-identical copy of the project still
refuses, the same file with its comments removed loads, removing one comment block
restores it, and adding one mention to a leading comment of a file that loaded a
moment earlier breaks it again.

**Third — never edit `X25519.vb` or `BrowserForWP.Net/Tls13/` by hand.** Both
are transliterations of executable prototypes, and those prototypes are the only
real checks available off-Windows. Change the prototype first, watch it pass, then
port the change:

- `X25519.vb` -> `tools/proto/w25519.mjs` -> `18 checks, 0 failure(s)`
- `BrowserForWP.Net/Tls13/*` -> `tools/proto/tls13.mjs` -> `31 checks, 0 failure(s)`
- `BrowserForWP.Net/Tls13/PinStore.vb` -> `tools/proto/pinstore.mjs` -> `0 failure(s)`
- `ProbeReport` verdict rule -> `tools/proto/probe-verdict.mjs` -> `9/9 checks passed`

`PinStore.vb` is pure host/pin logic rather than protocol code, so its mirror is
a plain logic mirror like `core-logic.mjs`, not a wire-format prototype. It still
falls under the rule: change the mirror, watch it pass, then port.
`probe-verdict.mjs` mirrors the `IsFullyCompatible` rule and separately asserts
the file contract around it, because the defect it guards against is a *verdict*
being drawn from an absent measurement.

`ie-adapt.mjs` is not a logic mirror and does not pretend to be: it is a decision
record. It asserts that `IeModeProbe.vb` exists, stays ES5, and reads
`documentMode`/`X-UA-Compatible`, and that `docs/MAINTAINING.md` still states the
four levers that make re-configuring Trident impossible. Its ninth check — the
measured `documentMode` from a real handset — cannot run here and is deliberately
not asserted; record it in `MAINTAINING.md` when someone has a device.

`sandbox-escape.mjs` is the same kind of object for Law 4, and it needed a
different instrument because there is no probe for this one: nothing inside the
container can measure a privilege it does not have. Its evidence is the
**deployment-time** fact instead — it parses `Package.appxmanifest` and asserts
that the declared capabilities are exactly `internetClientServer`, i.e. resource
access and not process or memory privilege, and that neither `runFullTrust` nor
`codeGeneration` (both Windows 10, both unavailable here) has quietly appeared. The
rest of it asserts that `docs/ARCHITECTURE.md` Law 4 and `MAINTAINING.md` §
"Sandbox escape is closed" still state the four levers. Run it before writing any
sentence that contains "JIT", "escape the sandbox" or "full trust".

**Fourth — a self-consistent TLS client proves nothing.** Sealing and opening your
own records will round-trip any bug that is symmetric. `tools/proto/tls13.mjs`
completes handshakes with real servers precisely so that field-order and
length-prefix mistakes cannot hide; three such bugs were found this way. If you
change record framing or ClientHello layout, re-run it against a live host.

**Fifth — the profile and the project files have their own failure families.** The
Round 5 merge review (PR #2) added four, every one of which passed
`tools/check-vb.mjs` and still failed the real build:

- **`List(Of T).AsReadOnly()` does not exist in the profile.**
  `ReadOnlyCollection(Of T)` is missing, so the call is `BC30456`, not a silent
degradation. Copy the list instead. Same family as the `SHA256` gap, and the
checker does not know this member.
- **A `Configuration|Platform` pair with no `PropertyGroup`.** A project added to
  `BrowserForWP.sln` with `Debug|ARM.Build.0 = Debug|ARM` whose `.vbproj` defines
  only `Debug|AnyCPU` fails the *entire* build inside
  `Microsoft.Common.CurrentVersion.targets` with "The OutputPath property is not
  set for project … Configuration='Debug' Platform='ARM'". The message names the
  pair, never the missing group. Every library here defines all six.
- **Generated code needs its own invariant, not just checked inputs.**
  `gen-vectors.mjs` emitted `New Byte() { _` with no closing brace for a
  zero-length vector, so RFC 5869 case 3 (empty salt *and* empty info) produced a
  file that did not compile (`BC30201`). The generator now asserts brace balance
  on its own output and refuses to write an unbalanced file.
- **Deprecated WinRT APIs are `BC40000` here.** `WebView.NavigationFailed` and
  `DataPackage.SetUri`. Use `NavigationCompleted`'s `IsSuccess` /
  `WebErrorStatus` — which carry the reason the deprecated event does not — and
  `DataPackage.SetWebLink`. A build with zero warnings is the goal; a new
  `BC40000` is a design question, not noise to allow-list.

**Sixth — a merged pull request is unreviewed code until the guest build says
otherwise.** PR #2 arrived as twenty commits that had never been compiled on the
real toolchain; the first guest build failed on four defects at once. A PR's own
verification section is a claim, not evidence, and "the author says it is
tested" is exactly the assumption this repository exists to stop making. Run
`tools\vm-build.cmd` before treating a merge as done, and diff the fork against
`docs/MAINTAINING.md` § "Still open" — that is where a merge usually contradicts
the rest of the repository.

## Step 4 — Verification commands

Run the checks for the layer you actually changed. Do not claim a layer is
verified if you skipped its command.

| You changed | Run | Expected |
| --- | --- | --- |
| Anything in `BrowserForWP.Crypto/` | `node tools/gen-vectors.mjs` | `53 assertions, 0 failure(s)` |
| `X25519.vb` (or its prototype) | `node tools/proto/w25519.mjs` | `18 checks, 0 failure(s)` |
| Anything in `BrowserForWP.Net/Tls13/` | `node tools/proto/tls13.mjs example.com` | `31 checks, 0 failure(s)` |
| `PinStore.vb` / pin comparison | `node tools/proto/pinstore.mjs` | `0 failure(s)` |
| Test vectors themselves | `node tools/gen-vectors.mjs` | every line prefixed `✓`, exit code 0 |
| The vector emitter itself | `node tools/gen-vectors.mjs` | `emitted VB braces balanced`, else it refuses to write, exit code 1 |
| `BrowserForWP.Core/` logic | `node tools/proto/core-logic.mjs` | `core-logic checks, 0 failure(s)` (72 assertions) |
| `Engine/Native/IDocumentFetcher.vb` / fetch rules | `node tools/proto/fetch-rules.mjs` | `25/25 checks passed` |
| `Engine/Native/Html*.vb` | `node tools/proto/htmlparse.mjs` | `30/30 checks passed` |
| `Engine/Native/CssParser.vb` / `SelectorMatcher.vb` | `node tools/proto/csscascade.mjs` | `47/47 checks passed` |
| `Engine/Native/{Style,UserAgent,BoxTree}*.vb`, `DocumentDumper.vb`, or the diagnostics wiring | `node tools/proto/boxtree.mjs` | `48/48 checks passed` |
| UA table / settings | `node tools/proto/useragents.mjs` | `0 failure(s)` |
| Tracker blocklist | `node tools/proto/trackerblock.mjs` | `0 failure(s)` |
| Lite defaults / caps / resources | `node tools/proto/lightweight.mjs` | `0 failure(s)` |
| Shim delivery / redirect rules | `node tools/proto/modern-sites.mjs` | `0 failure(s)` |
| Picker/tab re-entrancy, sln registration | `node tools/proto/shell-guards.mjs` | `0 failure(s)` |
| `RemoteServers.vb`, the settings fields it reads, `Ready`, or anything that chooses WHERE to render | `node tools/proto/remote-servers.mjs` | `24/24 checks passed`. The old `textmeasure.mjs` / `boxlayout.mjs` rows were deleted with the on-device renderer |
| `EngineChoice.vb`, or anything that selects an engine, falls back automatically, or decides whether the hosted engine is usable | `node tools/proto/engine-choice.mjs` | `34/34 checks passed`. It is a transliteration, so it also carries source checks: a mutation that deletes the `hostedReady` branch from the VB is NOT visible to the transliterated rule and IS refused by those |
| The engine that actually drew a page, and what the status line says | `BrowserForWP/MainPage.xaml.vb` (`IsHostedEngineUnusable`) + the run | Mutating `Decide` to ignore readiness, or dropping the requested url from a failure, must turn `engine-choice.mjs` red. Both were done |
| Any claim about the remote engine's wire format, header, frame splitter or sealed frames | `node tools/proto/remote-protocol.mjs` | `91/91 checks passed`, byte-for-byte against the vectors the SERVER's own code emitted |
| The remote input path: the hidden keyboard field, the write gate, a rotation, the keys bar | `node tools/proto/remote-input.mjs --probe` | `7/7 remote-input checks passed` AND `Every planted defect was refused`. The `--probe` half is not optional: it plants each defect the checks exist for, and a mutation that does not fail its check means that check cannot see what it is named after |
| `CompatibilityProbe.vb` / any probe verdict | `node tools/proto/probe-verdict.mjs` | `9/9 checks passed` |
| Any claim about re-configuring Trident | `node tools/proto/ie-adapt.mjs` | `9/9 checks passed` |
| Any claim about leaving the AppContainer, or about getting JIT memory | `node tools/proto/sandbox-escape.mjs` | `15/15 checks passed` |
| `BrowserForWP.Polyfill/compat.js` | `node tools/check-polyfill.mjs` | `is valid ES5` |
| Any `.vb`, `.vbproj`, `.xaml` or `.resw` | `node tools/check-vb.mjs` | `0 finding(s)`, exit code 0 (16 groups over 17 categories) |
| A declaration that names a VB keyword (`Dim next`, `Function Error`) | `node tools/check-vb.mjs` | `0 finding(s)`; group 17. Its word list is measured by `tools/keyword-probe.cmd`, not quoted from the language reference |
| Any `'''` doc comment, and any `Imports` of a BrowserForWP namespace | `node tools/check-vb.mjs` | `0 finding(s)`; group 13 balances doc-comment tags and refuses a plain `'` line stranded inside a `'''` block, and group 2 composes NESTED `Namespace` blocks. Both cost real warnings in Round 9 |
| Adding or changing a word in group 17 | `tools\keyword-probe.cmd` in the guest | all three batches report `sentinel refused` and `controls clean`; the refused line numbers ARE the measurement. A batch whose sentinel is not refused is void, not clean |
| A manifest capability, or any use of JIT / process creation / full trust | `node tools/check-vb.mjs` | `0 finding(s)`; group 15 refuses `Reflection.Emit`, `CreateProcess`, `Process.Start`, `LoadLibrary`, `VirtualAlloc`/`VirtualProtect` and the capabilities `runFullTrust`, `codeGeneration`, `allowElevation`, `packageManagement` |
| Any API that a capability gates (location, camera, microphone, contacts, calendar, libraries, network) | `node tools/check-vb.mjs` | `0 finding(s)`; group 16 fails when the code uses such an API and no manifest declares the capability. It is one-directional on purpose — a browser may hold capabilities no line of its code references, because hosted pages are what ask |
| A `{ThemeResource}` key in XAML | `node tools/check-vb.mjs` | `0 finding(s)`; group 9 checks every key against `tools/wp81-theme-keys.txt` |
| Any `.vbproj` `ProjectTypeGuids`, and the `.sln` entries | `node tools/check-vb.mjs` | `0 finding(s)`; group 14 requires the Windows Phone 8.1 flavour GUID wherever `TargetPlatformIdentifier` is `WindowsPhoneApp`, requires the VB factory GUID and `/` separators in every `Project` line of `BrowserForWP.sln`, and refuses the flavour property's name anywhere ahead of its element — including in a comment, which is a load failure the IDE reports and no build can |
| Whether a solution actually loads | `devenv.com BrowserForWP.sln /build "Debug|ARM"` in the guest | `Build: 7 succeeded, 0 failed` and no `not installed` line. The only oracle for the IDE's project system: `MSBuild` reads neither the type GUIDs nor the text-scan trap, so it is green when the IDE cannot open a project at all |
| The theme-key oracle itself | `bash tools/wp81-theme-keys.sh` | `wrote .../tools/wp81-theme-keys.txt (523 keys)` |
| `BrowserForWP/Assets/**` | `python3 tools/make_logo.py` | one line per generated PNG, exit code 0 |
| UI / XAML / VB app code | Build in the guest: `tools\vm-build.cmd /t:Rebuild` | `BUILD_EXIT=0`, no `BC` errors; only the two deliberate `ResourceLoader` warnings |
| Unexplained build diagnostics | `RUNS=4 bash tools/wmc9999-probe.sh` | `distinct XBF hash pairs across 12 runs: 1` |
| TLS / DoH / sockets | Deploy to handset, run **Diagnostics → TLS probe** | reports negotiated `TLS1.3` |

The two VB test projects under `tests/` are compiled by the guest build but
**nothing executes them** — an ARM class library cannot run on the desktop and
there is no handset or emulator. `node tools/proto/core-logic.mjs` is the
executable half of `CoreLogicTests.vb`; keep the two in step. Never report the
`tests/` projects as "tests passing".

`node tools/gen-vectors.mjs` is the fastest real signal available off-Windows:
it recomputes the algorithms from the RFCs and aborts on any mismatch. If you
edit crypto and this script still passes, your change is at least
algorithmically sound.

## Step 5 — Commit conventions

Conventional Commits, imperative mood, subject ≤ 72 chars. Reference the plan.

```
feat(tls): add X25519 key agreement
fix(core): keep history index in range after back-navigation
docs(plan): add plan for polyfill injection pipeline
test(crypto): cover HKDF with RFC 5869 A.2
chore(assets): regenerate tiles at scale-240
```

Body: what changed and **why**. One logical change per commit — do not bundle
a refactor with a feature.

## Step 6 — Push

```bash
git push
```

Always run it after a completed task. If there is no upstream:
`git push -u origin main`. Never force-push a shared branch.

## How to find the project

```
BrowserForWP.sln              ← open this in Visual Studio 2013+
BrowserForWP/                 ← the WP8.1 app: XAML UI, assets, UI strings
  Diagnostics/TlsProbeRunner.vb  ← app-layer glue: Net's TLS 1.3 stack → Settings UI
BrowserForWP.Core/            ← engine abstraction, tabs, history, address bar,
                                 settings/history/favourites stores, reading and
                                 night modes, tracker blocklist, lite redirects
  Engine/Native/              ← the native document engine: IDocumentFetcher seam,
                                 HTML tokenizer + tree builder, CSS parser,
                                 selector matcher, cascade, box tree
BrowserForWP.Net/             ← TLS 1.3, DoH, HTTP client, certificate pin store
BrowserForWP.Crypto/          ← HKDF, X25519, AES-GCM (no ChaCha: one suite, see below)
BrowserForWP.Localization/    ← language resolution + string lookup
BrowserForWP.Polyfill/        ← compat.js, packaged AND injected at DOMContentLoaded
                                 and again on navigation completed
tests/*.Tests/                ← VB logic checks; compiled by the guest, NOT executed
docs/ARCHITECTURE.md          ← design + the platform laws
docs/MAINTAINING.md           ← build/run/extend recipes
tools/gen-vectors.mjs         ← crypto verification + the VB vector emitter
tools/proto/*.mjs             ← runnable prototypes and logic mirrors
                                 (w25519, tls13, core-logic, pinstore, useragents,
                                 trackerblock, lightweight, modern-sites,
                                 shell-guards, ie-adapt, sandbox-escape,
                                 probe-verdict, fetch-rules, htmlparse,
                                 csscascade, boxtree, engine-choice,
                                 remote-servers, remote-protocol)
tools/make_logo.py            ← regenerates every image asset
tools/proto/remote-servers.mjs← primary/secondary order, url normalisation (referee)
tools/proto/engine-choice.mjs ← which engine renders, and when it may fall back
BrowserForWP/Engine/RemoteEngine.vb ← the remote engine: walks the two servers,
                                 owns the connection, and raises Navigated/Audio
BrowserForWP/Engine/RemoteChannel.vb ← one connection: handshake, sealed frames,
                                 read loop, frame acknowledgement, and the
                                 SemaphoreSlim gate that serialises every write
                                 (seal AND write: two seals can share a sequence)
BrowserForWP/Rendering/RemoteScreen.vb ← the Canvas of JPEG tiles, plus tap,
                                 scroll and soft-keyboard forwarding. NO handset
                                 has ever run one line of it (see the empty
                                 verification table in docs/MAINTAINING.md)
tools/check-vb.mjs            ← 17 categories / 16 check groups of static
                                 VB/XAML/project/resw/theme-key/flavour/
                                 import/name-legality/doc-comment checks
tools/keyword-probe/          ← one `Dim <word> As Integer` per candidate,
tools/keyword-probe.cmd          compiled by the real vbc, so group 17's list is
                                 measured rather than quoted. Batched, with a
                                 sentinel per batch: vbc 12 stops after ~100
                                 errors with no message at all
tools/check-polyfill.mjs      ← ES5 validity of the shim
tools/wp81-theme-keys.sh      ← regenerates the phone's 523 theme-resource keys
                                 (guest-side) into tools/wp81-theme-keys.txt,
                                 which check-vb.mjs group 9 reads
tools/vm-build.cmd            ← the real build, run inside the Windows guest
tools/wmc9999-probe.sh        ← characterises the WMC9999 diagnostic + XAML drift
```

Rule of thumb: **crypto knows nothing about TLS; TLS knows nothing about the
UI; Core knows nothing about crypto.** A change that violates a layer boundary
is a design bug, not a shortcut.

## How to add a new feature

Worked example: *add a "desktop site" toggle.*

1. **Plan.** `docs/superpowers/plans/2026-09-28-desktop-site-toggle.md`.
2. **Decide the layer.** This is browser state + UI, so it lives in
   `BrowserForWP.Core` and `BrowserForWP/`, and touches neither crypto nor TLS.
3. **Decide how this change can be verified, and be specific.** The `tests/`
   projects are compiled by the guest build but nothing runs them, so "add a unit
   test" is still not an executable option — see `docs/MAINTAINING.md` § "Where
   the tests actually are". Pick one of these two, and write down which:

   - **The logic is pure and has no WinRT dependency** (like
     `AddressNormalizer`, or a user-agent table). Extract it into
     `BrowserForWP.Core` behind a function whose inputs and outputs are plain
     strings or integers, then assert its behaviour from a Node script in
     `tools/` that mirrors that function. This is what
     `tools/proto/w25519.mjs` does for X25519, and it is the only pattern in
     this repository that has caught real bugs before they shipped.
   - **The logic touches XAML, the WebView, or a WinRT API.** Nothing off-device
     can check it. Say so in the commit message, build in the guest, and write
     out the exact handset steps a reviewer should repeat. Do not describe this
     as "tested".

4. **Write the check before the implementation**, whichever you chose. For a pure
   function that means the Node script, and it must fail first:

   ```bash
   node tools/proto/useragents.mjs
   # Expected BEFORE the implementation: FAIL — no such function: EffectiveUserAgent
   ```

   For UI work it means writing the manual verification steps down *now*, while
   you still remember what "correct" looks like, so step 9 has something concrete
   to check against.

   **Superseded step.** This walkthrough used to say: write a `<TestMethod>` into
   `tests/BrowserForWP.Core.Tests/`, then run it and watch it fail. That project
   now exists and compiles, but no runner executes it off-device, so watching it
   fail is still impossible. Mirror the logic in `tools/proto/` instead — exactly
   what `core-logic.mjs` does for `CoreLogicTests.vb` — and keep the two in step.
5. **Implement the minimum.** This feature has since shipped, so the real code is
   the reference: the UA strings live in
   `BrowserForWP.Core/Browser/UserAgents.vb` as constants plus a pure
   `EffectiveUserAgent(desktopMode As Boolean)`, and `BrowserSession` delegates:

   ```vb
   Public Property DesktopMode As Boolean = False

   Public ReadOnly Property EffectiveUserAgent As String
       Get
           Return UserAgents.EffectiveUserAgent(DesktopMode)
       End Get
   End Property
   ```

   Keep the lookup in the pure class so the Node mirror can call it; a `If`
   inline in `BrowserSession` would put it out of `useragents.mjs`'s reach.

6. **Re-run the check from step 4.** Expected: it passes now — the Node script
   for a pure function, or the guest build plus the manual handset steps for UI
   work.
7. **Localize any new user-visible string.** Add the key to **both**
   `BrowserForWP/Strings/en-US/Resources.resw` and
   `BrowserForWP/Strings/it-IT/Resources.resw`. A key present in only one
   language is a bug — see "Adding a language" below.
8. **Wire the UI.** Add the control to `MainPage.xaml`, its handler to
   `MainPage.xaml.vb`, and bind the label to the resource key.
9. **Verify.** Build in the guest, then deploy:

   ```bash
   prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
       "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
   ```

   Expected: `=== BUILD_EXIT=0 ===`. Then deploy to a handset and toggle the
   setting; confirm the server sees the desktop user agent.
10. **Commit and push.**

   ```bash
   git add BrowserForWP.Core/Browser/BrowserSession.vb \
           BrowserForWP/MainPage.xaml BrowserForWP/MainPage.xaml.vb \
           BrowserForWP/Strings/en-US/Resources.resw \
           BrowserForWP/Strings/it-IT/Resources.resw \
           docs/superpowers/plans/2026-09-28-desktop-site-toggle.md
   git commit -m "feat(core): add desktop-site user agent toggle"
   git push origin HEAD
   ```

## How to modify an existing feature

Same loop, but the diagnosis comes first.

1. **Reproduce.** Write a test that fails for the *observed* behaviour, not the
   behaviour you assume. If you cannot reproduce it, you cannot fix it.
2. **Locate by layer, not by grepping the symptom.** A site rendering wrongly
   is a Core/polyfill problem. A site refusing to connect is a Net/TLS problem.
   A string showing in the wrong language is a Localization problem. Grepping
   the symptom usually finds the wrong file.
3. **Check the platform laws before "fixing".** If the report is "modern site
   does not work", confirm it is not simply IE11 failing on syntax the OS
   cannot parse. Run **Diagnostics → Compatibility probe** first. Do not reach
   for a code change that the platform will never honour.
4. **Change one thing.** Update the test to encode the new expected behaviour,
   watch it fail, make it pass.
5. **Regression-guard.** If the bug could return, leave the test that catches it.
6. **Verify, commit, push** — as above.

## Adding a language

1. Create `BrowserForWP/Strings/<bcp-47>/Resources.resw`, e.g. `fr-FR`.
2. Copy every key from `en-US`. Missing keys fall back to English at runtime,
   but the parity test will flag them.
3. Register the display name in `BrowserForWP.Localization/LanguageCatalog.vb`.
4. Add the language to the `<Resources>` list in `Package.appxmanifest`.
5. Test that `Localizer` picks it when
   `ApplicationLanguages.PrimaryLanguageOverride` is set to it.

`en-US` is the **default and fallback** language. Never delete it.

## Things that will get a change rejected

- Claiming a Chromium or Firefox engine works on WP8.1. It still does not: the
  remote engine draws with Chromium **on somebody else's machine**, which is a
  different browser with a different owner, not this one with a bigger engine
  (`docs/ARCHITECTURE.md` Law 5).
- Claiming the system `WebView` uses TLS 1.3.
- A local loopback proxy feeding the `WebView` (AppContainers block
  `127.0.0.1`).
- A new user-visible string added to only one language file.
- A change to `BrowserForWP.Crypto/` without a passing
  `node tools/gen-vectors.mjs`.
- Work left uncommitted or unpushed.
- Reporting `tools/check-vb.mjs` passing as "it compiles". It is a static checker,
  not a compiler, and its own output says so. Run `tools\vm-build.cmd`.
- Asserting that something "cannot be built" or "is not supported" without having
  tried it. This repository has already paid for that mistake once: the README
  claimed the ARM64 guest could not build, for three rounds, and it can.
- Describing a component as a shipped feature when nothing calls it. Before
  listing anything as a feature, `grep` for a caller. The live catalogue is
  `docs/MAINTAINING.md` § "Still open": `Tls13Client` / `HttpClient13` /
  `DohResolver` are reachable only through **Diagnostics → TLS probe**, never
  through a page load, because the `WebView` navigates via Schannel; and both
  `tests/` projects compile while no runner executes them. Compiled is not
  reachable; reachable is not verified on a handset.
- Adding a project to `BrowserForWP.sln` without the matching
  `Configuration|Platform` groups in its `.vbproj` (see the fifth constraint).
- Replacing a `{ThemeResource}` key without checking it against
  `tools/wp81-theme-keys.txt`, or "fixing" any build diagnostic by renaming a
  platform key. A working key was once swapped for a UWP name that way, and the
  page's brush stopped resolving.
- "Fixing" `The referenced component '…' could not be found.` by adding or moving
  `TargetPlatformIdentifier`, or by asserting a code defect. It is the project
  system reporting a project **flavour** mismatch (`ProjectTypeGuids`), the line
  it needs is not an MSBuild property, and a green `tools/vm-build.cmd` proves
  nothing about it either way.
- Reporting the `tests/` projects as passing, or claiming `BrowserForWP.Core` is
  covered because they compile. `node tools/proto/core-logic.mjs` is what runs.
- Implying the `WebView`'s own traffic is pinned, or that it ever uses TLS 1.3.
  Pins apply to the app's transport; the page load goes through Schannel at
  TLS 1.2 max.

## The loop — do all five steps, in order, every time

1. **Plan.** Write or update the plan for the change first, using
   `superpowers:writing-plans`. No code before the plan exists. If the change
   invalidates part of an existing plan, update that plan in the same commit.
2. **Implement.** The smallest change that satisfies the plan. Apply the
   prototype rule: anything under `BrowserForWP.Net/Tls13/` or `X25519.vb` is
   changed in `tools/proto/` first, verified there, then transliterated.
3. **Verify.** Run the commands in the table above for every layer touched.
   Report which commands were run and their real output. Never claim a layer is
   verified because a different layer passed **and never let a guest build wait
   for the end of the task list: a task that adds a `.vb` file to a `.vbproj`
   ends with a rebuild, because a Node prototype cannot see a profile hazard.
   Three commits of the layout round were non-building trees for exactly this
   reason — the prototypes were green and the compiler was not asked.**
4. **Commit.** Conventional Commits, imperative mood, subject <= 72 characters.
5. **Push.** `git push origin HEAD`. A commit that is not pushed does not count
   as done. Confirm with `git ls-remote origin refs/heads/main` matching
   `git rev-parse HEAD`.

**Then update this file.** If the change added, removed or altered any tool,
command, file layout or constraint, that fact belongs here before the turn ends.
A skill describing a previous version of the project is actively harmful, because
it is what the next contributor trusts.

## How to build — there IS a compiler here

The development host is an Apple silicon Mac, so the sources are written
off-platform. They are not uncompiled: an **ARM64 Windows 11 Parallels guest**
hosts the whole VS2013 toolchain and builds the solution for real.

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd"
```

Expected tail: `=== BUILD_EXIT=0 ===`, with no `BC` errors. Add `/t:Rebuild` for a
clean build; the batch file forwards extra arguments to MSBuild.

Three things about that command, all of which cost time to learn:

- **The command and its arguments must be separate argv entries.**
  `prlctl exec <vm> "cmd /c ver"` fails *silently* — argv[0] becomes a program
  named `cmd /c ver`. `--current-user` and `-u` fail on this guest too. This is
  why the build is a batch file.
- **Do not trust this guest's Italian build log for the first reading.**
  `error BC30203: È previsto un identificatore` is "identifier expected";
  `La chiave specificata non era presente nel dizionario` is "the given key was
  not present in the dictionary". Grep for `error BC` / `error MSB` / `WMC`
  rather than reading the prose.
- **`WMC9999` is allow-listed known noise, and it is deterministic here.** It
  appeared in **12 of 12** measured builds under `vm-build.cmd`, with
  byte-identical `App.xbf` and `MainPage.xbf` across all of them — asserted, not
  assumed, by `tools/wmc9999-probe.sh`, which exits non-zero if the compiled XAML
  ever varies. It is non-fatal and never changes the exit code. An earlier
  revision of this file called it "intermittent"; the measurement says otherwise,
  and the earlier zeros were `obj` state. Do not edit source to chase it.
- **Count warnings from `/t:Rebuild`, never from an incremental build.** The
  incremental path sees unchanged projects as up to date and skips their compile
  entirely, so it produces a *cleaner* log than a clean build does. This is not
  hypothetical: an incremental run reported "Warnings: none" while
  `BrowserForWP.Localization` still had two `BC40000`s that a rebuild shows. Two
  other `BC40000`s were removed in Round 5 — `WebView.NavigationFailed` and
  `DataPackage.SetUri` — and the only warnings that should remain are the two
  deliberate `ResourceLoader` ones documented in `docs/MAINTAINING.md`.

A previous revision of this section claimed the ARM64 guest "cannot host this
build" and that a real build needs an x64 host. **That was wrong.** It was
inferred from Microsoft's "Visual Studio does not support Arm processors"
documentation instead of from trying it; the documentation is about the IDE, not
the command-line build. It is recorded here because the failure mode was
assuming rather than testing, and that is the one this repo is supposed to be
immune to.

**How to use the two verification paths together.** `tools/check-vb.mjs` is fast
and runs anywhere; the guest build is authoritative and slow. Run the checker
first, then the build. But a green checker proves *nothing* about compilation —
every error family in rounds 3 and 4 passed it. Before the guest round trip was
discovered, every non-trivial decision had to be backed by an executable
prototype; that method found four real TLS bugs and is still worth keeping for
protocol work. It is no longer a substitute for compiling.

**And the two are blind to different things, which is why order matters.** A
green checker knows nothing about the compiler; a compiler run once at the end of
five tasks knows nothing about *when* a break was introduced. The layout round
proved both halves: `FontStyles` (WPF) passed every off-device check and failed
the build four times, and the commit that introduced it was three commits behind
the build that found it. Build whenever a `.vb` joins a `.vbproj`.
