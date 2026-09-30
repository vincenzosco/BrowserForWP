# Maintaining BrowserForWP

Read [`ARCHITECTURE.md`](ARCHITECTURE.md) first — the five platform laws explain
why several otherwise-reasonable changes are impossible.

## Requirements

| Purpose | Requirement |
| --- | --- |
| Building the app and the transport | **Visual Studio 2013 Update 4+** with the *Windows Phone 8.1 SDK*, on Windows |
| Verifying crypto off-device | **Node.js 18+** (any OS) |
| Regenerating image assets | **Python 3.8+** (any OS) — no third-party packages |
| Running the crypto tests | Visual Studio Test Explorer |

The application cannot be built on macOS or Linux. The *crypto* and the *test
vectors* can be fully verified there, which is why the verification tooling is
deliberately dependency-free and cross-platform.

## Build and run

```
1. Open BrowserForWP.sln
2. Debug | ARM   → a developer-unlocked handset
   Debug | x86   → the WP8.1 emulator
3. Deploy (F5)
```

`Build succeeded` is the expected result. A failure in
`BrowserForWP.Crypto` almost always means a managed-crypto API that WinRT 8.1
does not expose — check the API against the WinRT 8.1 surface, not against
desktop .NET.

## The verification commands

Run these before committing. They are cheap and each covers a different layer.

```bash
# Crypto + TLS 1.3 key schedule. Runs anywhere. Must print "53 assertions, 0 failure(s)".
# The 53rd asserts brace balance on the VB it emits: an empty RFC 5869 case once
# produced an unterminated initializer and broke the test project with BC30201.
node tools/gen-vectors.mjs

# Verify only, writing nothing. Useful in CI.
node tools/gen-vectors.mjs --check

# X25519 limb arithmetic. Must print "18 checks, 0 failure(s)".
# This is a line-for-line prototype of X25519.vb and is the ONLY way to verify
# that file off-Windows. If it fails, fix the prototype, not the VB.
node tools/proto/w25519.mjs

# The TLS 1.3 client. Must print "31 checks, 0 failure(s)".
# This one needs NETWORK access: it performs a real handshake with a real
# server. It is the transliteration source for BrowserForWP.Net/Tls13/, and the
# only check that can catch a protocol-level mistake, because a self-consistent
# TLS client round-trips its own bugs happily.
node tools/proto/tls13.mjs example.com
node tools/proto/tls13.mjs www.google.com
node tools/proto/tls13.mjs cloudflare.com

# Core logic. Must print "core-logic checks, 0 failure(s)". This is a
# transliteration of tests/BrowserForWP.Core.Tests/CoreLogicTests.vb, and it is
# the only way those assertions execute at all off-device. Keep the two in step.
node tools/proto/core-logic.mjs

# The remote render servers: a primary, a secondary tried only when the primary
# cannot be reached, and no third option, plus Ready -- the rule that says whether
# the hosted engine can be used at all. Must print "24/24 checks passed".
# tools/proto/textmeasure.mjs and boxlayout.mjs used to sit here; they were the
# referees for the on-device renderer and went with it (see "Round 9"), so this
# slot is where a browser decides WHERE to render instead of HOW.
node tools/proto/remote-servers.mjs

# The render protocol's wire format, both directions, checked against the bytes
# the SERVER's own code emitted (protocol/vectors.json in Docker-BrowserForWP).
# Must print "100/100 checks passed". It is the only statement of the protocol
# that neither implementation wrote, which is why a client-side typo in a field
# offset is caught here rather than as a garbled screen on a phone. When the
# server regenerates its vectors, the copy in protocol/ is replaced in the SAME
# commit, or this referee passes while the device is wrong.
node tools/proto/remote-protocol.mjs

# The input path: the hidden field that owns the soft keyboard and what is
# allowed to raise it, the gate that serialises writes to the stream, the
# rotation that must move the server's viewport AND this device's mapping, and
# the key names the keys bar offers. Must print "11/11 remote-input checks
# passed". `--probe` plants each defect it
# exists to catch and requires the matching check to refuse it: a check nobody
# has seen fail is decoration, and this repository has shipped decoration twice.
node tools/proto/remote-input.mjs
node tools/proto/remote-input.mjs --probe

# The engine-choice rule: which setting may fall back to the device engine, and
# the two rows that matter most — an absent measurement is never grounds for
# switching engines, and an explicit Server choice is never answered with a page
# from the device engine. Must print "42/42 checks passed".
node tools/proto/engine-choice.mjs

# The shell and delivery guards that arrived with the merged browser shell.
node tools/proto/shell-guards.mjs    # picker/tab re-entrancy, completed URL, sln registration
node tools/proto/trackerblock.mjs    # host blocklist matching
node tools/proto/pinstore.mjs        # pin normalisation and comparison
node tools/proto/useragents.mjs      # UA table and search-URL escaping
node tools/proto/lightweight.mjs     # lite defaults, caps, resource keys
node tools/proto/modern-sites.mjs    # shim markers, redirect rules, delivery wiring

# The native document engine. These four are prototypes AND referees: the VB in
# BrowserForWP.Core/Engine/Native/ is a hand transliteration of them, so when one
# fails, first prove the check is right before "fixing" the VB. Between them they
# have already rejected a CORRECT implementation three times -- a check that read
# comments and so forbade documenting the rule it enforced, an assertion that had
# dropped a child and so expected 2 where there are 3, and a mirror that tested
# source text where the rule was about behaviour.
node tools/proto/fetch-rules.mjs     # fetch rules: charset, media type, no Accept-Encoding
node tools/proto/htmlparse.mjs       # tokenizer + tree builder, implicit head/body
node tools/proto/csscascade.mjs      # CSS parse, specificity, matching, cascade, lengths
node tools/proto/boxtree.mjs         # box tree, anonymous blocks, diagnostics wiring

# The IE-adaptation decision record. Not a logic mirror: it asserts that
# IeModeProbe.vb exists, stays ES5 and reads documentMode, and that this file still
# records the four levers that make re-configuring Trident impossible.
node tools/proto/ie-adapt.mjs

# The sandbox-escape decision record, same kind of object, different instrument.
# There is no probe for this one -- nothing inside the container can measure a
# privilege it does not have -- so its evidence is the deployment-time fact: it
# parses Package.appxmanifest and asserts the declared capabilities are still
# resource access only. It also asserts that ARCHITECTURE.md Law 4 and the
# "Sandbox escape is closed" section here still state the four levers.
node tools/proto/sandbox-escape.mjs

# The probe verdict rule. It exists because an empty MissingFeatures list from a
# probe that never ran was rendered in the UI as "no missing web features detected".
node tools/proto/probe-verdict.mjs

# Confirm the polyfill shim is valid ES5 (comment-aware, so it does not
# false-positive on backticks inside comments).
node tools/check-polyfill.mjs

# Static VB.NET structural check. Must print "0 finding(s)", exit code 0.
# This is NOT a compiler. It catches block-balance errors, missing Implements
# members, project/disk drift, namespace mismatch (including NESTED Namespace
# blocks, which compose), resw key drift, unwired XAML handlers,
# {ThemeResource} keys the platform does not define, project flavour GUIDs that
# disagree with the target platform and the project type GUID of every
# BrowserForWP.sln entry (group 14: the .vbproj carries the flavour, the .sln
# carries the factory, and the flavour property's name may not appear ahead of
# its element anywhere in a .vbproj), (group 13) a plain ' comment stranded inside
# a ''' doc block plus every doc-comment tag that is unknown, mis-nested or left
# unclosed, (group 15) any use of Reflection.Emit, process creation, LoadLibrary
# or RWX-memory allocation plus any manifest capability that asks for privilege
# the platform cannot grant, (group 16) any API whose required capability the
# manifest does not declare, and (group 17) any declaration that introduces a VB
# keyword as a name — and it found a real End Property/End Class error. A green
# run still does not mean the project compiles.
node tools/check-vb.mjs

# The keyword probe: measures, against the actual compiler, which VB keywords
# vbc 12 refuses as an identifier. Group 17's list comes from here and NOT from
# the language reference, which lists `Out` as reserved while
# `Dim out(31) As Byte` compiles -- and it is on disk in X25519.vb. Needs the
# guest; the check itself does not.
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" cmd /c \
    "C:\Mac\Home\Documents\BrowserForWP\tools\keyword-probe.cmd"

# Regenerate the theme-resource key list that check-vb.mjs group 9 reads: the
# keys Windows Phone 8.1 itself defines, read out of the guest's design
# dictionaries. Do NOT point this at Windows Kits\8.1 — the desktop set is a
# different set, and a desktop-only key is precisely the bug the check exists to
# catch. Needs the guest; the check itself does not.
bash tools/wp81-theme-keys.sh

# Regenerate every WP8.1 image asset from the renderer.
python3 tools/make_logo.py

# The only oracle for the IDE's project system, and the only command here that
# can see the two things MSBuild is blind to: the project type GUIDs in
# BrowserForWP.sln and the flavour property's name appearing ahead of its
# element in a .vbproj (the factory locates that property by scanning the file
# as TEXT). Must print "seven projects loaded and built" and "DEVENV_EXIT=0",
# with only WMC9999 and no "not installed" line. A solution with the flavour GUID
# there instead loads NOTHING -- "Build: 0 succeeded or up-to-date".
#
# It is a .cmd IN THE GUEST and not a command line, and that is a Round 14
# correction: the one-liner this used to be does not survive the trip. `cmd /c`
# reaches the guest with the argument already split, so the quotes around
# `C:\Program Files (x86)\...` and around `"Debug|ARM"` are gone before devenv
# sees them, and the host shell cannot re-add them through eval-style escaping.
# The file has no quoting to lose.
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-devenv.cmd"
```

`devenv` rewrites the projects it opens — a BOM, CRLF line endings and a
`<Folder Include="My Project\" />` item appear in whichever `.vbproj` files it
touched. Revert those with `git checkout --` before committing, or a run that
meant only to change the solution will also rewrite four project files. (The
Round 14 run left the tree clean, which is luck of ordering and not a guarantee.)

Those vectors also produce `tests/BrowserForWP.Crypto.Tests/Vectors.generated.vb`.
That file is **generated** — never edit it by hand; regenerate it with
`node tools/gen-vectors.mjs`. It is compiled by the guest build, but nothing
executes the project that contains it.

See "Where the tests actually are" below before assuming those vectors are being
checked by a VB test run.

## Do not use these APIs in BrowserForWP.Crypto

A WP8.1 WinRT app compiles against the ".NET for Windows Store apps" profile.
None of the following exist, and none of them are compile errors you can fix
locally — the namespaces are simply absent:

- `System.Security.Cryptography.SHA256`, `SHA256Managed`, `HashAlgorithm`
- `System.Security.Cryptography.HMACSHA256`, `KeyedHashAlgorithm`
- `System.Security.Cryptography.RNGCryptoServiceProvider`, `RandomNumberGenerator`
- `System.Security.Cryptography.AesGcm`

Use `WinRtCrypto` instead, which wraps `Windows.Security.Cryptography.Core`.
See [`ARCHITECTURE.md`](ARCHITECTURE.md#the-crypto-api-surface-on-wp81-winrt--read-this-before-editing-crypto)
for the replacement table.

`RegexOptions.Compiled` is also unsupported in this profile — do not add it back.

## Adding a test vector

1. Add the constant to the appropriate RFC section in `tools/gen-vectors.mjs`.
2. **Assert it against the recomputed value**, not the other way round — the
   script must fail if the implementation and the published constant disagree.
3. Run `node tools/gen-vectors.mjs`. It must reach
   `0 failure(s)` before it will emit anything; it refuses to write vectors that
   did not verify.
4. Add the corresponding `Assert` to the VB test project, referencing the
   generated name.

The script is fail-closed by design: if an assertion fails, **no** vector file is
written, so a broken implementation cannot quietly become the expected value.

## Adding a language

Worked example: adding German.

1. Create `BrowserForWP/Strings/de-DE/Resources.resw`, copying every key from
   `en-US/Resources.resw`.
2. Add `"de-DE"` to `_supported` in
   `BrowserForWP.Localization/LanguageCatalog.vb`.
3. Add its display name to `LanguageCatalog.DisplayName` (`"Deutsch"`).
4. Add `de-DE` to `<Resources>` in `Package.appxmanifest`.
5. Verify key parity (this is the step people skip):

   ```bash
   python3 - <<'PY'
   import xml.etree.ElementTree as ET, glob
   sets = {p: {d.get('name') for d in ET.parse(p).getroot().findall('data')}
           for p in sorted(glob.glob('BrowserForWP/Strings/*/Resources.resw'))}
   base = list(sets.values())[0]
   for p, keys in sets.items():
       print(p, len(keys), 'missing:', sorted(base - keys) or 'none', 'extra:', sorted(keys - base) or 'none')
   PY
   ```

6. Commit. `en-US` is the fallback — never remove it.

## Adding a feature

Follow `.agents/skills/browserforwp/SKILL.md`. In short: plan to
`docs/superpowers/plans/`, write the failing test, implement the minimum, verify,
commit, push.

**Decide the layer before writing code.** The single most common mistake is
putting network or key-schedule logic in `Core`, or XAML in `Net`. If you cannot
name the layer, the change is probably two changes.

## Modifying the transport

`BrowserForWP.Net` is the most delicate layer. Rules:

- Never weaken a check to make a site work. A handshake that fails
  authentication must throw, not fall back.
- Never add a TLS 1.2 fallback to `Tls13Client`. TLS 1.2 already exists through
  Schannel for legacy destinations; conflating the two hides which path a
  connection actually took.
- Any change to `KeySchedule` or `TlsRecordLayer` requires
  `node tools/gen-vectors.mjs` to pass **and** a handset run of
  **Settings → Run TLS probe**.
- The record layer's sequence number is part of the AEAD nonce. Reusing one is a
  catastrophic failure, not a glitch. `TlsRecordLayerTests` guards it.

## Modifying the engine layer

`IBrowserEngine` is a public contract. Adding a member means implementing it in
every engine. Before adding one, ask whether `Capabilities` or `InvokeScriptAsync`
already covers the need.

When adding a capability flag, set it to what the engine **actually** does.
`TridentEngine` claiming `SupportsTls13 = True` would be a lie that the UI then
repeats to the user — see the guard in the plan's Task 11, Step 3.

### The native document engine

`BrowserForWP.Core/Engine/Native/` is a second engine, built from scratch, whose
**front half** exists: fetch, tokenize, build the tree, parse CSS, match
selectors, resolve the cascade, build a box tree. Layout and rendering are Phase 2
and are not written. It executes no JavaScript, and never will.

Two rules apply, and both are enforced by prototypes rather than by inspection:

- **Change the prototype first.** `tools/proto/htmlparse.mjs`,
  `csscascade.mjs` and `boxtree.mjs` are executable specifications; the VB is a
  hand transliteration of them, because nothing in this environment executes VB.
  Change the `.mjs`, watch it pass, then port.
  `Engine/Native/NodeTypes.vb` is the vocabulary both sides share — rename a type
  there and the mirrors must follow in the same commit, and vice versa.
- **`Engine/Native/` must not reference `BrowserForWP.Net`.** Fetching is a seam
  (`IDocumentFetcher`) that the app layer implements
  (`BrowserForWP/Diagnostics/NetDocumentFetcher.vb`). That is what keeps Core free
  of the transport, and what makes the engine exercisable without a network.

Adding a stage means adding its mirror in the same commit. A stage with no mirror
has no way to fail before a handset run, and this pipeline's defects have so far
been found by execution, never by reading.

## Build host requirements

A real build needs **all three** of the following. Without them, the only
verification available is `tools/check-vb.mjs`, which is a static checker and
not a compiler.

1. **A Windows host with the toolchain installed.** An **x64** host is what
   Microsoft documents and the safe default. It is not the only host that works:
   this project builds on an **ARM64** Windows 11 Parallels guest, driving
   `msbuild.exe` directly from the command line.

   An earlier revision of this file asserted that an Arm64 VM "cannot do this
   build, at any setting", citing Microsoft's statement that pre-17.4 Visual
   Studio is unsupported on Arm. That statement is about the **IDE**; it does not
   follow that the **command-line build** fails, and nobody had tested it. It
   works. The cost of assuming otherwise was three rounds of compile errors that
   a two-minute build would have surfaced immediately.
2. **Visual Studio 2013 Update 4 or later.** Update 2 is the documented minimum
   for Windows Phone 8.1.
3. **The Windows Phone 8.1 SDK and the Windows 8.1 SDK.** The latter is required
   by `TargetPlatformVersion 8.1`.

## The build that actually works

```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd"
```

`tools/vm-build.cmd` cds to the repository, prints the MSBuild version, checks the
three toolchain paths, then runs

```
msbuild BrowserForWP.sln /nologo /v:minimal /p:Configuration=Debug /p:Platform=ARM
```

prints a diagnostic summary, and finishes with `=== BUILD_EXIT=<n> ===`. Extra
arguments are forwarded, so `tools\vm-build.cmd /t:Rebuild` performs a clean
build.

**The script decides pass/fail, and it is stricter than MSBuild.** It exits
non-zero if an `error BC`, `error MSB` or `error APPX` line appears in the log,
even when MSBuild itself returns 0, and it reports the one allow-listed
diagnostic (`WMC9999`) by name. A clean exit code is therefore a statement about
the log, not merely about MSBuild's opinion of it.

Both halves of that are verified, not asserted:

- a normal run prints `known-noise: WMC9999 (allowed, ...)`, `Real compiler
errors: none` and `=== BUILD_EXIT=0 ===`;
- a deliberate `Return "this is not an Integer"` in a function returning `Integer`
  prints `UNEXPECTED COMPILER ERRORS` and `=== BUILD_EXIT=1 ===`. A checker that
  has never been observed to fail is not a checker.

**Quoting is load-bearing.** `prlctl exec` takes the command and its arguments as
SEPARATE argv entries. `prlctl exec <vm> "cmd /c ver"` fails *silently*, because
argv[0] becomes a program literally named `cmd /c ver`. `--current-user` and `-u`
also fail on this guest (no stored credentials); do not guess at them. That is why
the build lives in a batch file rather than a one-liner.

Verified toolchain in that guest (`BIOS type: efi-arm64`, Windows 11, Parallels
Tools 27.0.0-58628):

| Component | Path | Version |
| --- | --- | --- |
| MSBuild | `C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe` | 12.0.40629.0 |
| Visual Studio | `...\Microsoft Visual Studio 12.0\Common7\IDE\devenv.exe` | 2013 |
| Windows Phone SDK | `C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1` | 8.1 |
| Windows SDK | `C:\Program Files (x86)\Windows Kits\8.1` | 8.1 |
| Repository | `C:\Mac\Home\Documents\BrowserForWP` (Parallels shared folder) | |

`devenv.exe` is present but is not the build driver: every build and every
measurement in this file comes from `msbuild.exe` on the command line.

### Status of the build

**The solution builds, cleanly, for `Debug|ARM` and `Release|ARM`.** Each build
produces the four library DLLs, `BrowserForWP.exe`, `App.xbf`, `MainPage.xbf` and
a package set (`*.appx`, `*.appxbundle`, `*.appxupload`) under
`BrowserForWP\AppPackages\`. Repeated `Rebuild` runs are consistent: 12
consecutive builds, `BUILD_EXIT=0` every time, zero `BC` errors.

The sections below are the round-by-round record. They are kept in full because
the error taxonomy is the reusable part — every one of these families looked like
something other than what it was. Read the two `Status of the build` entries in
order.

### Round 1 and Round 2

Approximately 78 errors, but they were the product of two defects:

| Symptom | Real cause | Fix applied |
| --- | --- | --- |
| `The property "Content" can only be set once. MainPage.xaml (1,1)` | `MainPage.xaml` had **two direct children of `<Page>`** (the layout grid and the settings overlay). `Page.Content` can hold one object. | Wrapped both in a single root `<Grid>`; the overlay is declared second so it still draws on top. |
| `'Sub Main' was not found`, `'InitializeComponent' is not declared`, and ~65 × `'<Name>' is not declared` | **Consequences of the first row.** The XAML compiler rejected `MainPage.xaml`, so `MainPage.g.vb` was never generated — no partial class, therefore no `x:Name` fields and no generated entry point. | Same fix. |
| `Value '128274' cannot be converted to 'Char'` (×2) | `ChrW(&H1F512)` / `ChrW(&H1F513)`. Those are supplementary-plane code points; a `Char` is 16 bits. | `Char.ConvertFromUtf32`. |
| `'Localization' is not declared`; `Type 'IBrowserEngine' / 'BrowserSession' / 'TridentEngine' is not defined`; `The referenced component 'BrowserForWP.Core' / '.Crypto' / '.Localization' could not be found` | **CORRECTED in Round 6 — the diagnosis written in this row was wrong.** The libraries did produce referenceable assemblies: they compile, and their output is present for all six configurations. The "declared no `TargetPlatformIdentifier`" explanation cannot hold either, because each file already set that value in the conditional `PropertyGroup` at its foot, so the line this row credits changed no build. The `could not be found` family is emitted by the IDE's project system, which compares project **flavour GUIDs**: the libraries carried the Windows Store flavour while the app carried the Windows Phone 8.1 one. | `<TargetPlatformIdentifier>WindowsPhoneApp</TargetPlatformIdentifier>` was added to all four library projects. It is a no-op, kept as documentation. The fix that actually removes the warnings is the flavour swap in Round 6. |
| `Impossibile trovare il percorso specificato.` (no file attributed) | A project-level build step failed. Building over a Parallels **shared folder** is the prime suspect: MSBuild and the XAML/PRI compiler are unreliable on that path. | **Copy the repository to local disk in the guest and build there.** |

**The lesson worth keeping:** the two defects in rows 1 and 3 were the whole
story, and row 1's error message understates it by an order of magnitude. When a
build produces dozens of unidentified-identifier errors, look for the one
structural failure that stopped code generation before reading any of them.

### Shared folder or local disk?

Both work. Building in place over `C:\Mac\Home\Documents\BrowserForWP` is what
produced every artefact described in this file. Copying to local disk is worth
trying when a failure carries *no file path attached at all*, but it is a
diagnostic of last resort rather than a prerequisite — and on this repository it
was never the cause. The advice below it was, in hindsight, a plausible guess
that the next round's evidence did not support.

### Round 2 — what was fixed, and what is still open

The second build removed two whole error families, confirming the first two fixes:
`The property "Content" can only be set once` and `Value '128274' cannot be
converted to 'Char'` no longer appear.

**Cause of the remaining `Sub Main` / `InitializeComponent` / `x:Name` cascade:**
`BrowserForWP.vbproj` declared `<Content Include="Polyfill\compat.js" />`, but no
such file existed in the project — the polyfill lives in `BrowserForWP.Polyfill`.
A declared content item with no file fails the build early, so the XAML compile
never runs and `App.g.vb` is never generated. This is why `Sub Main` and
`InitializeComponent` were missing *again* after the XAML was already correct.

Fixed by including the canonical file with a `Link`, so it ships at
`Polyfill\compat.js` without being duplicated in the repository. **The checker
did not catch this**, because its "declared file exists" test only covered
`.vb`/`.xaml`/`.resw`; it now covers every declared item, with a negative control.

### Round 3 — the libraries compile, and fifty-four new errors

With the early abort gone, the build reached actual compilation and reported the
real state of `BrowserForWP` and `BrowserForWP.Net`. `BrowserForWP.Core`,
`.Crypto` and `.Localization` built there and then; `BrowserForWP.Net` produced
**54 errors across 6 files**.

They came from five distinct causes. None of them is visible to
`tools/check-vb.mjs`, and four of the five are *VB-specific* traps that a
transliteration from JavaScript walks straight into.

| Symptom | Real cause | Fix applied |
| --- | --- | --- |
| `BrowserForWP.Net` failed wholesale: `IDisposable`, `List`, `Encoding`, `ArgumentException`, `Math`, `Array`, `InvalidOperationException` all "not defined"; `BC36948` on every `Async`; `BC30665` on every `Throw` | `BrowserForWP.Net.vbproj` had **no `<Import Include>` ItemGroup** and none of the `OptionStrict`/`OptionInfer`/`OptionCompare` PropertyGroups, and closed with `Microsoft.VisualBasic.targets` instead of the XAML targets. The other three libraries have all of them. Every source file still *parsed*, so it read as broken code rather than a broken project. | Rewrote the project file on the `.Crypto` template: 16 `<Import Include>` entries plus the option groups and the XAML targets import. |
| 24 errors in `ClientHelloBuilder.vb`: `BC30203` "identifier expected", then `U16`, `Bytes`, `Vec8`, `ToArray` "not declared" | **Leading-dot method chains** — `New TlsWriter().` at end of line, next call on the following line. That is legal from **VB 14 (VS2015)**; this is **VB 12 (VS2013)**, which has no implicit line continuation after a period. One syntax mistake, twenty-four errors, all naming the wrong thing. | Rewrote each chain as a `With` block with one call per line. |
| `'SupportedVersions' is not a member of 'Integer'`, `'KeyShare' is not a member of 'Integer'`, `'Alpn' is not a member of 'Integer'` | A local variable named `extensionType` **shadowed the `ExtensionType` enum** — VB is case-insensitive. The compiler blames the enum member, not the local. | Renamed the local to `extType`. Same class of bug as `supported`/`Supported`, `tag`/`Tag`, `Default`/`DefaultTag`, `value`/`Value()`, `shared`/`Shared`. |
| `'SubReaderVec24' is not a member of 'BrowserForWP.Net.Tls13.TlsReader'`, followed by a wall of `BC30574` late-binding errors | `TlsReader` only had `SubReaderVec16`. `Certificate`'s `certificate_list` is a 3-byte length (`RFC 8446 §4.4.2`). | Added `SubReaderVec24()`. |
| `BC30390`: `WinRtCrypto.ToBuffer ... is not accessible in this context because it is 'Friend'` | Assembly-scoped `Friend` used from a *different* assembly. | Made `ToBuffer` (and `ToArray`) `Public`. |
| `BC30002: Type 'CertificateVerifyInfo' is not defined`, in `CertificateValidator` | The class was **nested inside `ServerMessageParser`**; the referencing file named it unqualified. | Moved it to namespace level. |
| `BC30456: 'Verify' is not a member of 'CryptographicEngine'` | The WinRT type exposes `VerifySignature`, not `Verify`. | Corrected. |
| `BC30456: 'ASCII' is not a member of 'System.Text.Encoding'` | `Encoding.ASCII` is genuinely **absent** from the .NET for Windows Store apps profile (it needs `ASCIIEncoding`, which the profile removes). | Replaced with a local `AsciiBytes` helper that also rejects non-ASCII labels instead of silently transcoding them. |
| `BC32006`: cannot convert `Char` to `Integer` | VB has no `Char`-to-`Integer` conversion under `Option Strict`. | `AscW`, as the error itself suggests. |
| `BC30512: Long to Integer`, `BC30311: String to Windows.Networking.HostName`, `BC30290` local shadows its function, `BC30201` comment inside a continued array literal | `UInteger - Integer` widens to `Long`; `StreamSocket.ConnectAsync` needs a `HostName`; `Dim value(...)` inside `Function Value()`; a trailing `'` comment on a continued line inside `New Byte() {...}`. | `CInt(received)`, `New HostName(host)`, renamed the local, moved the comment out of the braces. |

**The lesson worth keeping:** when a whole project fails at once, check whether it
has the *project-level* imports and option groups that its siblings have. Every
file parsing correctly is exactly what makes this look like a code problem.

### Round 4 — the solution builds

`BUILD_EXIT=0`, no `BC` errors, and the package set is produced. Two further
defects were found and verified by experiment:

1. **A XAML theme key that does not exist on WP8.1. THIS ENTRY WAS WRONG, AND THE
   SWAP IT DESCRIBES IS THE BUG.** Read the correction before using any part of it.
   What the round did: `MainPage.xaml` used
   `Background="{ThemeResource TextBoxBackgroundThemeBrush}"`, and that key was
   replaced with `TextControlBackground` on the evidence that "the diagnostic
   disappears, and returns when the old key is restored".

   Both halves of that evidence are false.

   - `TextBoxBackgroundThemeBrush` **is** a Windows Phone 8.1 key. It is defined in
     the phone's own design dictionary
     (`C:\Program Files (x86)\Windows Phone Kits\8.1\Include\abi\Xaml\Design\themeresources.xaml`,
     `x:Key="TextBoxBackgroundThemeBrush"`, line 264) and *used by the phone's own
     `TextBox` style* (`generic.xaml`, line 2413). It is not a WP8.0 Silverlight
     name. `ApplicationPageBackgroundThemeBrush` is valid too, which that entry
     gets right.
   - `TextControlBackground` is defined **nowhere** on this platform: none of the
     523 keys in the phone's dictionaries is it. The nearest name,
     `TextControlBackgroundThemeOpacity`, is a `Double` where a `Brush` is needed.
     It is a UWP / Windows 10 name, and the Windows 8.1 *desktop* dictionaries do
     not define it either.
   - The diagnostic the swap rested on does not track the key at all. It appeared
     in **12 of 12** runs of the matrix documented further down this file, every one
     of them taken *with* the swapped key in place, and it appears again now that
     the key is back (`docs/superpowers/plans/2026-09-28-xaml-theme-resources.md`,
     "Outcome").

   So the swap replaced a working key with one that cannot resolve, and the
   address-bar brush of `MainPage.xaml` has been unresolvable ever since. A Visual
   Studio session reports it while reading the XAML — `The resource
   "TextControlBackground" could not be resolved.` — because `{ThemeResource}` is
   resolved when the page *loads*, not when it compiles, so no build here can see
   it. `tools/check-vb.mjs` group 9 can: it checks every `{ThemeResource}` key in
   the app's XAML against `tools/wp81-theme-keys.txt`, the 523 keys extracted from
   the phone's own dictionaries by `bash tools/wp81-theme-keys.sh`.

   **The transferable lesson:** a diagnostic whose presence varies between sessions
   is not evidence about source code. The original probe was right that WMC9999 is
   deterministic and harmless *within* a session, and wrong to let a single
   before/after observation of a varying log line rewrite a platform name.

2. **Ambiguous image assets.** The packaging step warned six times with
   `APPX1621`: a mixture of `Assets\Logo.png` and `Assets\Logo.scale-240.png`
   matching the same logical name. The manifest names the *logical* path, so the
   base variants must be qualified too. Renamed every base asset to
   `.scale-100.png` (what the WP8.1 template itself generates). Zero `APPX1621`
   after the change. `python3 tools/make_logo.py` and the `.vbproj` `<Content>`
   items were updated together — keep them in step.

**Remaining warnings, all understood and accepted:** two × `BC40000` on
`New ResourceLoader(ResourceMap)` in `Localizer.vb`. The suggested replacement,
`ResourceLoader.GetForCurrentView(name)`, returns a **cached** loader, so it would
silently stop honoring a runtime language change — which `Localizer` depends on.
The deprecated constructor is the one with the semantics this code needs, the
warning is a forward-compatibility note about a "TBD" future release that will
never ship for WP8.1, and the alternative cannot be tested on a handset from here.
A deliberate, documented trade-off.

**`WMC9999` is a diagnostic from the VS2013 XAML compiler, and it is harmless.**
It is not a defect in this codebase and it must not be chased with source edits.

```
Microsoft.Windows.UI.Xaml.Common.targets(327,9): Xaml Internal Error error
WMC9999: La chiave specificata non era presente nel dizionario.
```

*(the given key was not present in the dictionary)*

Measure it with `tools/wmc9999-probe.sh`, which runs three build modes `RUNS`
times each and hashes the compiled XAML from every run:

```
RUNS=4 bash tools/wmc9999-probe.sh
```

**Measured result (12 runs):** `WMC9999=1` in **12 of 12** runs — 4/4
`sln /t:Rebuild`, 4/4 app-project `/t:Rebuild`, and 4/4 app-project incremental
builds. `distinct XBF hash pairs across 12 runs: 1`. The probe exits non-zero if
that count is ever anything but 1.

So within a single host session the diagnostic is **deterministic**, not
intermittent, and the compiled XAML is invariant. Both artefacts agree every
time:

```
App.xbf      = b7af0673a52d230302275b6c60fa2a64
MainPage.xbf = 817580f71c93802ca8818c328074ea85
```

**It is also independent of the XAML theme keys, which had once been believed to
cause it.** Round 4 swapped a `{ThemeResource}` key specifically to silence this
diagnostic; every one of the 12 runs above was taken with that swapped key present,
and the diagnostic is still there now that the key has been removed again. It is not
caused by any key in `MainPage.xaml`, and it is never a reason to edit a source file
— correcting a real bad key is a separate matter, covered by check group 9.

Earlier in the same day, isolated ad-hoc builds reported `WMC9999=0` three times
with sources that are not distinguishable from today's, including one solution
build with `/p:BuildProjectReferences=false`. Those zeros are **not reproduced**
by the matrix above. The honest reading is that the behaviour is stable *within*
a session and differed *between* sessions — per-session toolchain state, not a
property of the inputs. Treat the "intermittent" characterisation as superseded
by this measurement.

An attempt to isolate it to the app's `obj` directory was inconclusive: those
files are owned by the guest's MSBuild user, so they cannot be deleted from the
macOS host, and `/t:Rebuild` already runs a Clean. What is established without
that experiment is enough for the allow-list, because the invariance of the
`.xbf` is checked rather than argued.

**Five hypotheses were tested and eliminated:**

| Hypotheses tested | Result |
| --- | --- |
| The `.resw` `PRIResource` items — set `Condition="false"` | `WMC9999` still present. Not PRI resources. |
| Project-level PRI generation — `/p:GenerateProjectPriFile=false` | Still present. |
| `/p:BuildingInsideVisualStudio=true` | Still present. |
| The `Release` configuration (vs `Debug`) | Still present. |
| The app's unused `xmlns:local` / `mc:Ignorable="d"` declarations, on the theory that the XAML compiler's type dictionary collides because all four referenced assemblies also declare types under `BrowserForWP` | Still present, 3/3. |

**If you are tempted to chase it anyway:** do not change source to do so. The
diagnostic is emitted by a task that has already produced correct output, and the
remaining leads are inside Microsoft's toolchain. The one experiment that would
actually move this forward is a build on an x64 host at the same VS2013 update
level, to test whether running the toolchain under Arm64 emulation is the trigger
— that is an untested hypothesis, and it is recorded as one.

`tools/vm-build.cmd` allow-lists this diagnostic **by name** and fails the build
on every other `error BC` / `error MSB` / `error APPX` line.

### Round 5 — the merged fork is reviewed, and made to build

A second repository was merged into `main` as PR #2 (branch `Gjhkyio/main`, 20
commits, 42 files). It closes the three gaps Round 4 left open and adds the
browser shell: polyfill injection, a TLS probe runner, a pin store, tabs, find,
reading and night modes, tracker blocking, lite redirects, persisted settings,
history and favourites, and two VB test projects.

It was merged **without ever running the guest build.** The first guest build
after the merge failed with four distinct defects. All four are fixed, and each
one is a family worth recognising again:

1. **A profile gap in `List(Of T)`.** `HistoryStore.List()` called
   `_entries.AsReadOnly()`. `ReadOnlyCollection(Of T)` is not part of the
   ".NET for Windows Store apps" profile, so this is `BC30456` — the same shape
   as the `SHA256` / `Encoding.ASCII` gaps above, and `tools/check-vb.mjs`
   cannot see it. Replaced with the profile-safe copy,
   `New List(Of HistoryEntry)(_entries)`.
2. **A solution platform mapping with no matching conditional group.** The tests
   were added to `BrowserForWP.sln` with `Debug|ARM.Build.0 = Debug|ARM`, but
   their `.vbproj` files only defined `Debug|AnyCPU`. A solution build for ARM
   then fails inside `Microsoft.Common.CurrentVersion.targets` with "The
   OutputPath property is not set for project …" — an error that names the
   *pair*, never the missing `PropertyGroup`. Both test projects now carry the
   same six configurations (`AnyCPU`/`ARM`/`x86` × Debug/Release) as every other
   library here.
3. **An emitter that only worked for non-empty data.** `tools/gen-vectors.mjs`
   wrote `New Byte() { _` with **no closing brace** for a zero-length vector.
   RFC 5869 case 3 has an empty salt *and* an empty info, so the generated
   `Vectors.generated.vb` contained two unterminated initializers that swallowed
   the declarations after them: `BC30201` in the test project. The generator now
   has an explicit empty case **and** asserts brace balance on the emitted VB,
   refusing to write the file when it is unbalanced. Verified by negative
   control: the pre-fix file has 57 `{` and 55 `}`.
4. **Deprecated WinRT APIs.** `WebView.NavigationFailed` and
   `DataPackage.SetUri` both raise `BC40000` here. Failure is now handled through
   `NavigationCompleted`'s `IsSuccess` / `WebErrorStatus`, which carry the reason
   the deprecated event does not, and the share path uses `SetWebLink`. Both of
   those warnings are gone; the only warnings left are the two deliberate,
   already-documented `ResourceLoader` ones above.

   **Count warnings from a rebuild, never from an incremental build.** After the
   first fix round this build reported "Warnings: none" — and that was wrong, or
   rather it was measured against the wrong thing: an incremental build reuses the
   cached `BrowserForWP.Localization` DLL and never recompiles `Localizer.vb`, so
   it hides that project's two warnings. `tools\vm-build.cmd /t:Rebuild` shows
   them. A cleaner-looking log is not a cleaner tree.

Also found and fixed while reviewing, none of which a compiler could see:

- Both test projects **compiled but nothing executed them.** See "Where the
  tests actually are".
- The Settings overlay was titled **"Diagnostics"**: the two heading keys were
  swapped with the engine label. `DiagnosticsTitle` now reads "Diagnostics", the
  engine block is labelled "Rendering engine", and the previously hardcoded
  English `"compatibility layer active"` / `"native"` are catalogue keys.
- The engine status was prefixed with `DiagnosticsProbe` — the TLS probe's own
  label, "Run TLS probe" — producing "Run TLS probe: native".
  `DiagnosticsProbe` was a duplicate of `TlsProbeRun` and is deleted.
- The security-details dialog embedded English `"(TLS 1.2 max, WebView). UA="`
  in code. It is now the `SecurityWebViewCeiling` key in both languages.
- `ErrorNoConnection`, `Loading`, `LoadComplete`, `PinMismatch` and
  `SecurityTls13` had no consumer. All five are wired (failure reason, status
  line, pin verdict, probe headline). **61 resource keys, 0 unused, en/it parity
  intact** — checked with a real XML parser, not a tag count.
- `TlsProbeRunner` carried a second copy of the pin comparison; it now calls
  `CertificateValidator.VerifyPin`, so there is one implementation.
  `TlsProbeResult` gained `PinMismatch` so the UI shows the localized sentence
  instead of the English `pin-MISMATCH` token buried in the detail line.

**Unchanged on purpose:** `AddressNormalizer` refuses `localhost:8080`, because a
colon before any slash is read as the scheme `localhost`. The comment above that
branch promises localhost support; in practice only a `localhost` with *no* port
navigates. Left as-is and recorded: loopback is unreachable from an AppContainer
anyway, and that class is security-relevant parsing a merge review should not
rewrite without a device test to justify it.

### Still open

Items 1–3 of the previous revision are **closed** by Round 5: injection, the
probe and the pin store all exist and are wired. What remains is this.

1. **The VB test projects compile, but nothing executes them.** Both are in
   `BrowserForWP.sln` and the guest build produces their DLLs — real progress on
   the Round 4 gap — but no runner invokes `RunAll()`. A WP8.1 ARM class library
   cannot run on the desktop, and there is no handset and no emulator, so
   **"compiled" is not "tested"**. The assertions now run off-device through
   `tools/proto/core-logic.mjs` (72 assertions), a transliteration of
   `CoreLogicTests.vb` that must be kept in step with it. That mirror exists
   because the VB suite's first defect was invisible without execution: an
   assertion naming the heavy `duckduckgo.com` search URL that the lite-first
   default had replaced.
2. **Pinning is enforced only on the app's own transport.** `PinStore` and
   `CertificateValidator.VerifyPin` are real and wired, so a stored pin is checked
   against the leaf SPKI on every probe and a mismatch is surfaced. They **cannot**
   apply to browsing: the `WebView` rides Schannel, whose validation this app
   cannot hook. A pin therefore protects the app's TLS 1.3 path, never the pages
   you *view* in the `WebView`.

   **Corrected while reviewing this phase, because the sentence above had become
   half-false.** Once `NetDocumentFetcher` existed, a *parsed* page started
   travelling the app's own TLS 1.3 path — and `FetchAsync` did not check pins, so
   a pinned host was fetched with its pin silently ignored while this file claimed
   the path was protected. It now passes `SessionInfo.LeafCertificateDer` through
   `CertificateValidator.VerifyPin` before decoding the body, and an unreadable
   certificate fails rather than passing. So: a page **viewed** in the `WebView` is
   still unpinned (impossible — Schannel); a page **parsed** by the diagnostics
   fetcher is pinned. `README.md`'s "certificate pinning for the app's transport layer" is
   accurate, and the qualifier is now load-bearing, not decorative.
3. **Never run on a handset.** XAML layout, `WebView` behaviour,
   `DOMContentLoaded` injection, reading-mode fallback, lite redirects, night mode
   and 2014-hardware performance are all unverified. Compiling is not running.

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

### Round 8 — the native engine becomes an engine you can pick

Round 7 drew a page behind a Diagnostics button. This round made that reachable
the way every other page is: Settings gains a rendering-engine choice
(*Automatic*, *System WebView*, *BrowserForWP native*), and the
320-pixel preview, its button and its resource key are gone. The native engine
draws into `ContentHost`, so the address bar, the tab list, the history store and
the hardware Back button drive it unchanged.

- **`EngineChoice`** is the selection rule as pure Core logic: an explicit choice
always wins over the probe, and an *absent* measurement never moves anything.
That last row is the one that matters, and `tools/proto/engine-choice.mjs`
refuses it exhaustively — the repository has already shipped one lie of that
shape, when a probe that never ran was reported as "no missing web features
detected".
- **`NativeEngine`** implements the existing seam. `Source` returns one `Border`
created once, because the shell takes that object at wire time and keeps it.
It claims `SupportsTls13 = True`, which no other engine in this product may
honestly claim, because the fetch goes through `BrowserForWP.Net` rather than
Schannel.
- **`EngineCapabilities.SupportsScripting` is new, and `NeedsPolyfillLayer`
  stopped being `Not SupportsModernJavaScript`.** That expression was wrong for
an engine with no script host: it would have answered `True` and had the shell
inject `compat.js` into a document with no `window`. Three shapes, all asserted.
- **The reader fallback and the engine fallback are alternatives now.** When a
measurement says Trident cannot cope, rendering with our own engine beats
injecting a reader, and doing both would fight over one document. The reader is
still reachable from the Reading button.

**Two corrections this round made to earlier documents.** The Phase-2 roadmap in
`2026-09-29-native-engine-pipeline.md` said the automatic fallback should fire
when `CompatibilityProbe.CouldRun` is `False`. That is the exact mistake
`EngineChoice` exists to refuse, and the implemented rule is stricter: only a
measurement that *ran* may move the engine. And `docs/ARCHITECTURE.md` claimed
the engine seam makes the engine "a configuration detail instead of an assumption
baked into every call site": that is still true of behaviour and newly false of
construction, because the shell knows two engine types at one site. Both are now
recorded where the claims are made.

**The guest found what no checker could.** `ApplyLocalizedStrings` still set the
`Content` of the button whose XAML had just been deleted — `BC30451`, reported
against the page rather than against the handler. Four subsystems had to agree
for this round to work (Core, the engine, the XAML and two resource files) and
only the compiler checks that all four do.

**Verified:** six configurations `BUILD_EXIT=0` with only the two deliberate
`ResourceLoader` warnings; `engine-choice.mjs` 21/21; `boxtree.mjs` 48/48 (six of
those are the transliterated `PageCss`, which moved out of `MainPage` this round);
`core-logic.mjs` 60 assertions; `check-vb.mjs` 0 finding(s).
**Not verified:** the on-device output. Nothing in this round has run on a
handset either, and the engine picker itself has never been seen. The first handset
session should record, in this section, what the automatic fallback actually does
on a real broken page.

### Sandbox escape is closed

"Let the app start sandboxed and step outside when a request arrives" was
examined and closed, the way IE-adaptation was, and it is the reason
`docs/ARCHITECTURE.md` has a Law 4. The four levers such a plan needs, and why
each is absent:

- **no self-de-sandboxing API.** Container membership lives in the process token
  and is set by the parent at creation; nothing in WinRT changes it, and WP8.1's
  profile exposes no process creation. There is also no JIT to unlock: an
  AppContainer denies writable+executable pages, and the `NETFX_CORE` profile has
  no `Reflection.Emit`. A patched SDK does not help — the SDK decides what
  compiles, the kernel decides what the process may do.
- **a child process inherits the container.** Where process creation exists at
  all (Windows 10, not WP8.1) a child born from an AppContainer app is created in
  that same container. A "free" helper has to be launched by a full-trust parent,
  which the app is not.
- **capabilities grant resources, never memory policy.** Privilege is declared at
  package time; this one declares `internetClientServer` and nothing else.
  `runFullTrust` and `codeGeneration` are Windows 10 capabilities, and
  `runFullTrust` is restricted to Microsoft-signed packages.
- **broker contracts perform specified operations.** An `AppServiceConnection`
  does a defined job for an app; it does not hand over a DOM, a renderer or
  memory. WP8.1 app services were app-to-app only, with both manifests declaring
  the relationship.

`tools/proto/sandbox-escape.mjs` asserts the manifest fact — that this package
still asks for resource access only — and that this section still states the four
levers. It is a decision record like `ie-adapt.mjs`, not a logic mirror, and it has
no runtime instrument on purpose: nothing inside the container can measure a
privilege it does not have.

**And the Windows 10 answer, because it gets asked every time.** On Windows 10
*desktop* a packaged app using the desktop bridge is a real Win32 process: it can
JIT, spawn processes and ship its own engine, which is how packaged CEF and
WebView2 applications exist. That is a deployment-time decision rather than a
request-time escape, and it was never available on Windows 10 *Mobile*. So the
question "couldn't we have a modern engine?" has a yes in it — on a different
operating system, as a different project.

### Deferred work

Recorded rather than fixed. None of these is a broken promise; each is a place
where the code is more confident than the corpus of checks behind it. Items 1 to
10 are inherited from the native-engine phase, whose renderer was deleted in
Round 10 while the parser it left behind (and the diagnostics that show it) stayed;
items 11 onwards are current.

1. **The pipeline has never seen a real page.** Tokens, tree, cascade and boxes
   have only ever run against the prototypes' own fixtures. The first real
   document is the real test, and no check in this repository predicts it.
   Likewise, `NetDocumentFetcher`'s redirect loop and its latin1 branch have
   never run against a live server or a genuine latin1 page.
2. **`line-height` is inherited as a resolved pixel value**, not as the
   multiplier CSS inherits, so an element whose font-size differs from its
   parent's keeps the parent's line box height. Invisible until there is layout.
3. **An `http://` URL is accepted and then speaks TLS to port 80.** It fails, but
   with a handshake error rather than "unsupported scheme".
4. **A fresh `DohResolver` per fetch**, so its TTL cache never spans more than one
   request. Correct and wasteful.
5. **Error details are English tokens beside localized copy**, so a failure reads
   "Recupero non riuscito. HTTP 404". The fix is an error code plus resource keys,
   which is a design change rather than a patch.
6. **`<pre>` loses its formatting**, because whitespace collapses everywhere. The
   declared subset says so, but the user-agent sheet gives `pre` a monospace font,
   which promises the opposite.
7. **`IeModeProbe` mutates the document it inspects** by appending a `meta` tag.
   That is deliberate, it is the strongest form of the test, and it is reachable
   only from a Diagnostics button — know that before calling it anywhere else.
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
11. **The engine lifecycle is not on `IBrowserEngine`.** `NavigationStarting` and
    `NavigationCompleted` are still re-raised from the `WebView` rather than
the interface, so the shell wires whichever engine it built and therefore knows
    two engine types at exactly one site. Behaviour still branches only on
    `EngineCapabilities`; construction does not. Worth doing, and it is a bigger
    diff across every call site than the user-visible feature it would unblock.
12. **Pins are not enforced on the render channel.** `Tls13Client` takes a host and
    no pin table, so `RemoteChannel` connects to a pinned server without consulting
    `PinStore`. A pin is therefore checked by the diagnostics fetch and by the TLS
    probe, and **not** by the engine that carries every page. `README.md` says so in
    both languages rather than letting the feature read as universal.
13. **No per-tab page state in the remote engine.** One connection, one page:
    switching tabs navigates the same session and the page's own state (scroll
    position, a form half filled) is gone. `BrowserSession` keeps URLs, not
    documents.
14. **CLOSED in Round 11 — a rotation resizes the remote viewport.**
    `Window.Current.SizeChanged` re-measures, re-maps the screen and sends
    `RESIZE`, and `tools/proto/remote-input.mjs` asserts all three parts. The item
    is kept rather than deleted so that a reader who remembers the gap finds it
    closed instead of gone.
15. **CLOSED FOR THE SERVER, STILL OPEN FOR THE PHONE.** This item used to be one
    flat sentence: the remote engine has never spoken to a server. Half of it is
    now false, measured on 2026-09-29. `Docker-BrowserForWP` was deployed to a VM
    and `bin/bfwp-smoke.js` -- a real client, added for this -- completed sessions
    against it: TLS 1.3, the handshake, a sealed `NAVIGATE`, a 480x800 JPEG drawn
    by Chromium, the `ACK` releasing the next frame, and the `FOCUS` behaviour of a
    tap, `10/10` three times in a row, from another machine over an SSH tunnel and
    from inside the container. What that does NOT touch is any line of the VB:
    `RemoteScreen`, `RemoteEngine`, the soft-keyboard proxy, the 1/dpr scale and
    the audio element have still never run on a handset. At the time, the phone also
    needed a certificate it would accept and port 8443 opened; Round 16 supplied
    both -- the port, and a publicly trusted certificate for the address -- and did
    **not** close this item, because a satisfied precondition is not a passed test.
    The phone now stops at two gates of its own, named in item 21. So
    § "The remote engine, verified by hand" is still a table of blank rows, and the
    distinction is stated rather than implied: the protocol and the server have
    been verified end to end; the device has not.
16. **CLOSED in Round 13 — the address IS shipped, by decision, and the cost is
    written down.** This item used to say the opposite: a default would send every
    page, and every password, through a machine the user did not choose, so the
    engine stayed off until an address and a token were configured. The owner
    decided the browser must render modern pages without being set up first, so
    `AppSettings.DefaultHostedUrl` is the project's own server, the engine is the
    default one, and the switch ships on. What keeps that from being a silent
    transfer of every page is the part the item could not have anticipated:
    `RemoteServers.Ready` means a fresh install is NOT ready (no device token),
    so its first page is drawn on the phone; the status line names the engine that
    drew each page; Settings carries the address, the switch and the notice; and
    README and ARCHITECTURE Law 5 say who can read what. What is still open is the
    pair this item always implied: **nobody has run a build against that server**
    (no token for it exists here, and no Docker on this host), so its reachability
    and its identity are unverified -- and the channel is still unpinned
    (item 12).
17. **CLOSED in Round 14 — the page tells the phone where its focus is.** This
    item used to be the report: nothing in the 23 message types said "focus
    moved", so the client could not know whether a tap landed on a text box, and
    **the soft keyboard came up on every tap** — including on a link, a button
    and empty space. `FOCUS = 0x27` is now a sealed one-byte message that the
    server sends when the answer CHANGES, and `RemoteScreen.SetPageFocus` is the
    only thing that raises the keyboard: a tap is not a request to type. Two
    repositories moved in one commit, with the vectors. What the message is not
    is the *kind* of field — see item 20.
18. **The `KEY` message carries a modifier byte the server ignores.**
    `browser.js` reads `{ key, text }` and drops `{ modifiers }`, so Shift+Tab and
    Control+Enter are not expressible. The shell offers neither, and a button that
    sent a modifier while pressing an unmodified key would be a lie in the UI.
19. **The keys bar itself has never been seen.** It is nine buttons and a
    scrollable strip in `MainPage.xaml`, wired by
    `tools/proto/remote-input.mjs` to names the server can press and to labels in
    both languages — and no handset has drawn it. It is one more row of § "The
    remote engine, verified by hand" that is blank.
20. **The keyboard rises on a CHANGE of answer, which leaves two cases out.**
    The server reports on a change, so tapping the SAME text field again after
    dismissing the keyboard by hand produces no message and the keyboard stays
    down. And a page that focuses a field by itself — autofocus, a search box, a
    dialog that opens with the cursor in it — raises nothing until the person
    touches something. Both are the same missing binding: the page's own
    `focusin`, reported through an exposed function, which is the one part of
    `FOCUS` that cannot be exercised on this host at all (it needs Playwright and
    a browser, neither installed; see the notes in `Docker-BrowserForWP`).
    Raising the keyboard optimistically on every tap is tempting and is exactly
    the defect item 17 closed. The type of field (password, email, number), which
    would let the phone pick an `InputScope` keyboard layout, is deliberately not
    in the message either: it cannot be confirmed without a handset, and the
    decoders reject trailing bytes, so adding it later costs a protocol change in
    both repositories plus the vectors.
21. **HALF CLOSED in Round 17 — the client matched a NAME and the deployed
    certificate is for an ADDRESS.** The first gate was ours and is shut:
    `X509Reader` now collects the SAN's **iPAddress** entries as well as its
    `dNSName` ones, and `CertificateValidator.MatchSubjectAltName` asks which kind
    of host we were given and takes the matching branch. The reason it was ever
    written the other way is worth keeping: a certificate for an address carries
    it in an `iPAddress` entry and, under Let's Encrypt's `shortlived` profile,
    **no** common name at all, so name matching failed with "nothing to match
    against" on a chain that validated perfectly. It is not an exotic mistake --
    OpenSSL's own hostname check behaves identically (`-verify_hostname` ->
    `verify error:num=62:hostname mismatch` on a certificate that `openssl x509
    -checkip` accepts, measured against this server) -- because name matching and
    address matching are two different checks in every TLS client, and only one of
    them usually gets written.

    **The second gate is not ours and is still shut.** The chain ends at `ISRG
    Root X2` cross-signed by `ISRG Root X1`, and whether a 2014-era handset was
    ever issued either root cannot be measured from this host. A pin cannot
    substitute for it -- item 12 -- because `IsValid` is the chain AND the name, by
    design, and a design that lets a pin skip the chain is a decision for the
    owner rather than a gap to be patched quietly. So the phone may still refuse
    every byte at the trust step, and until a handset runs it, the honest status of
    this item is "one gate closed, one unmeasured".
22. **The SNI extension carries an IP literal, which RFC 6066 §3 says it must
    not.** `ClientHelloBuilder` sends `server_name` with whatever host it was
    given, including an address, and the extension is defined for host names
    ("Literal IPv4 and IPv6 addresses are not permitted in HostName"). Measured
    2026-09-29: the deployment's server accepts it and completes the handshake, and
    `tools/proto/tls13.mjs` shows the literal going out, so nothing is broken
    today. Written down rather than fixed because the change is to the handshake
    itself, the only oracle for it is one server that tolerates the current form,
    and a strict server is hypothetical until one is met. The change, when it is
    worth making, is one line: omit the extension when the host parses as an
    address, which `CertificateValidator.TryParseIpLiteral` already answers.

### Error taxonomy

The library project files are hand-authored. If one of them stops being
recognised, recreate it in the IDE (File -> New -> Project -> Visual Basic ->
Windows Phone Apps -> Class Library) and re-add the existing `.vb` files — that
is what "the SDK has not seen this file" means in practice.

Families actually observed, in order of how misleading they are:

- **Project-level imports missing.** A whole project's types reporting "not
  defined". See Round 3.
- **VB 12 vs VB 14 syntax.** Leading-dot chains. `tools/check-vb.mjs` now flags
  them; it could not before, and this was 24 errors in one file.
- **Case-insensitive shadowing.** A local named after a type, property or
  enclosing member. The error names the *type*, never the local.
- **Reserved words as member names.** `Public Property Error As String` is
  `BC30183`, because `Error` is a reserved keyword in VB (the legacy `Error`
  statement) and there is no fallback spelling. What makes this family misleading
  is the *second*, spurious diagnostic it drags in: `BC42312`, "XML documentation
  comments must precede a member or type declaration", pointing at a doc comment
  that is perfectly correct — so the message you chase is the wrong one. Rename
  (`ErrorMessage`) rather than escape (`[Error]`): escaping compiles, but it puts
  brackets at every call site, a pattern nothing else in this repository uses.
  Found by compiling the native-engine plan's Task 3, which had never been
  compiled before it was executed.
- **`Friend` across assemblies** (`BC30390`), and **nested classes** named
  unqualified from another file (`BC30002`).
- **Profile gaps.** `System.Security.Cryptography` does not exist in the
  ".NET for Windows Store apps" profile — use `WinRtCrypto`.
  `Encoding.ASCII` and `RegexOptions.Compiled` are absent too.
  `tools/check-vb.mjs` now flags all three.
- **More profile gaps, found in Round 5.** `List(Of T).AsReadOnly()` is not in the
  profile either — `ReadOnlyCollection(Of T)` is missing, so the call is
  `BC30456` rather than a silent degradation. Check any BCL helper against the
  profile surface before using it; `tools/check-vb.mjs` flags this family.
- **`ControlChars` is not in the Store profile** (`BC30451`) — even though
  `Microsoft.VisualBasic.Strings` is, since `AscW` and `ChrW` both compile. So the
  shape of `Microsoft.VisualBasic` here is partial, and the friendly constants
  (tab, CR, LF, form feed) are exactly the part that went missing. Test whitespace
  with `Char.IsWhiteSpace`, which is what the class-attribute split in
  `SelectorMatcher` does. Found by compiling the native-engine plan's Task 5;
  `tools/check-vb.mjs` now flags it as well.
- **APIs that compile and then fail at run time.** `System.Text.Encoding.GetEncoding`
  *is* in this profile — the guest build accepts it, so it is **not**
  `BC30456` — yet Microsoft's own documentation for the method says unsupported
  code pages throw (`ArgumentException` for some, `NotSupportedException` for
  others) and that callers must catch rather than trust. Whether
  `GetEncoding("ISO-8859-1")` resolves on a WP8.1 handset cannot be settled from
  this machine, so no code here may depend on either answer: `NetDocumentFetcher`
  asks and falls back to UTF-8. Note the shape of this family — the compiler is
  *silent*, so a build-only check can never see it, and "it built" is not
  evidence about it. A plan that anticipated `BC30456` here was anticipating the
  wrong failure.
- **A `Configuration|Platform` pair with no `PropertyGroup`.** Adding a project to
  the solution with `Debug|ARM.Build.0 = Debug|ARM` while its `.vbproj` defines
  only `Debug|AnyCPU` fails the entire build with "The OutputPath property is not
  set for project … Configuration='Debug' Platform='ARM'". The message names the
  pair, never the missing group. Every library in this repo defines all six.
- **Generated code that was only ever tested on non-empty data.**
  `gen-vectors.mjs` emitted `New Byte() { _` with no closing brace for a
  zero-length vector, so the generated file failed to compile (`BC30201`) for
  RFC 5869 case 3 — a case with an empty salt *and* an empty info. Checked inputs
  are not a checked emitter: the generator now asserts brace balance on its own
  output before writing it.
- **Deprecated WinRT APIs.** `WebView.NavigationFailed` and
  `DataPackage.SetUri` are `BC40000` on this OS. Prefer `NavigationCompleted`'s
  `IsSuccess` / `WebErrorStatus` (they carry the reason, the deprecated event does
  not) and `DataPackage.SetWebLink`. Treat a new `BC40000` as a design question
  rather than as noise to allow-list.
- **XML comment hazards.** A `--` run inside a `<!-- -->` comment makes MSBuild
  refuse to load a project (`MSB4025`), which surfaces from a solution build as
  the unrelated-looking `MSB4078` "project file is not supported by MSBuild". An
  unescaped `<0..2^24-1>` copied from an RFC grammar invalidates a `'''` doc
  comment (`BC42304`). Both are now checked.
- **Namespace duplication.** The full name is `<RootNamespace>.` plus the file's
  own `Namespace` block. `BrowserForWP.Crypto.vbproj` sets `RootNamespace` to
  `BrowserForWP` precisely because its sources declare `Namespace Crypto`;
  `BrowserForWP.Net.vbproj` must keep `BrowserForWP.Net` because its sources
  import `BrowserForWP.Net.Tls13` and `BrowserForWP.Net.Http`.

### Round 6 — the flavour GUID behind four IDE warnings

**Reported:** four warnings in the Visual Studio error list, all attributed to the
app project, none of them with a diagnostic code.

```
The referenced component 'BrowserForWP.Core' could not be found.
The referenced component 'BrowserForWP.Crypto' could not be found.
The referenced component 'BrowserForWP.Net' could not be found.
The referenced component 'BrowserForWP.Localization' could not be found.
```

**What was ruled out.** The four `<ProjectReference>` items are well formed:
existing paths, `<Project>` GUIDs matching each library's own `<ProjectGuid>`,
`<Name>` equal to each `<AssemblyName>`. Every referenced project is in the
solution with `ActiveCfg` and `Build.0` for all six configurations, and each
library's `bin` output exists for all six. A `Rebuild` is `BUILD_EXIT=0` in all
six configurations — the four `ARM`/`x86` ones as solution builds, the two
`Any CPU` ones as project builds, since that solution platform's name contains a
space and cannot survive `prlctl exec`. Nothing is missing, so the message is not
about absence.

**What the message is.** Not a compiler diagnostic. Every other failure in this
file carries a code (`BC30456`, `MSB4078`, `APPX1621`); this one carries none,
which places it in the IDE's project system rather than in `vbc`. The string
"referenced component" does not occur anywhere under
`C:\Program Files (x86)\MSBuild`, under the Windows Phone 8.1 SDK, or under
`C:\Program Files (x86)\Windows Kits\8.1` on the guest. **No build on this project
could ever have printed it**, which is why `tools/vm-build.cmd` stayed green
through every round the warning was present.

**The cause.** The first GUID in `ProjectTypeGuids` is the project *flavour*, and
a Windows Phone 8.1 app may only resolve references to a project of the same
flavour. The four libraries — and both test libraries — declared
`{BC8A1FFA-BEE3-4634-8014-F334798102B3}` while also declaring
`TargetPlatformIdentifier` `WindowsPhoneApp`. The two statements contradict each
other, and the project system reads the GUID.

That GUID is not a guess and neither is the replacement. Both values come from
the VS2013 templates in the guest:

| Template, under `Common7\IDE\ProjectTemplates\VisualBasic\` | Flavour GUID |
| --- | --- |
| `Windows Phone 8.1\1033\WindowsPhoneClassLibrary\ClassLibrary.vbproj` | `{76F1466A-8B6D-4E39-A767-685A06062A39}` |
| `Windows Phone 8.1\1033\WindowsPhoneBlankApplication\Application.vbproj` | `{76F1466A-8B6D-4E39-A767-685A06062A39}` |
| `Windows Store\1033\ClassLibrary_WindowsStoreApps\ClassLibrary.vbproj` | `{BC8A1FFA-BEE3-4634-8014-F334798102B3}` |

The app template and the Windows Phone 8.1 class library template agree, and
these project files carried the value from the third row. `MSBuild` never reads
`ProjectTypeGuids`, which is exactly why this survived five rounds of green guest
builds.

`BrowserForWP.sln` states a project type GUID per project too, and it disagreed
the same way: `{BC8A1FFA-...}` for the six libraries, `{F184B08F-...}` — the plain
VB language GUID — for the app. Which of the two statements the IDE acts on was
settled by looking at what is registered:

```
reg query "HKLM\SOFTWARE[\WOW6432Node]\Microsoft\VisualStudio\12.0" /s /f "<guid>"
```

Only `{F184B08F-C81C-45F6-A57F-5ABD9991F28F}` is registered there, as the VB
project factory (under `Projects` and `LocalData`). Neither flavour GUID appears
anywhere in the VS2013 hive, so a `.sln` entry cannot select a Store or Phone
factory on its own.

**CORRECTED in Round 12 — what followed here was wrong.** It continued "and the
project file is what the loader falls back to", and concluded that the `.sln` was
a consistency fix rather than a cure. There is no fallback. A solution whose
`Project` lines name an unregistered factory loads **no project at all**, and a
registry query settles which factories exist, not what the loader does with a name
that is not among them. Round 12 measured it and moved the `.sln` to
`{F184B08F-...}`; the flavour stays in the `.vbproj`, which is the other rule.

**Fixed** by swapping the flavour GUID in `BrowserForWP.Core`, `.Crypto`,
`.Localization`, `.Net` and both test libraries, and in the seven `Project`
entries of `BrowserForWP.sln`, so both files say `{76F1466A-...}` everywhere. The
four comments that credited the `TargetPlatformIdentifier` line with making the
project resolvable were wrong and are corrected; the line itself is kept, because
it agrees with the conditional `PropertyGroup` at the foot of each file, and it
now says why it is there.

**Enforced** by group 14 of `tools/check-vb.mjs`: a project that declares
`TargetPlatformIdentifier` `WindowsPhoneApp` must carry the Windows Phone 8.1
flavour, the Windows Store flavour must not appear in any project here, and a
`.sln` entry must carry the same flavour as its project. Against the reproduced
pre-fix state the group is RED with 13 findings — six `.vbproj` and seven
`BrowserForWP.sln` lines — and GREEN against these files. Its message quotes both
template paths so the next reader can re-derive the rule instead of trusting it.

**Noticed while measuring it:** the two solution platforms named `Any CPU` cannot
be selected from the host with `/p:Platform="Any CPU"` — `prlctl exec` reaches
`cmd.exe` as one string and MSBuild splits the argument at the space. Build the app
project with `/p:Platform=AnyCPU` instead; the effect is the same, and that is how
the two `Any CPU` configurations were checked. All six end in `BUILD_EXIT=0`,
carrying only the two deliberate `ResourceLoader` warnings.

**What this does not prove.** The diagnostic itself cannot be reproduced off the
IDE, because the project system is the component that emits it. The evidence for
the fix is the template comparison above, which is objective and re-runnable, and
not a before/after screenshot of an error list.

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

**The guest build taught group 12 a new hazard.** `FontStyles` (`System.Windows`)
is WPF and does not exist in the WinRT profile at all: four `BC30451` errors, in
code that this repository's own plan had written. XAML markup resolves
`FontStyle="Italic"` through the enum; code has to name
`Windows.UI.Text.FontStyle.Italic`. The hazard is now in `check-vb.mjs` group 12,
with its negative control run — 4 findings with `FontStyles` restored, 0 without.
That is the second time a checker group has been earned by a failed build rather
than by a theory, and the reason group 12's entries are all paid for.

**The round also earned a rule, at a price.** Task 1's commit (`c079267`) declared
`BrowserForWP/Rendering/XamlTextMeasurer.vb` in the app project while that file
still contained `FontStyles` at its lines 50 and 52 — so **it does not build**, and
it was not the only such commit: the plan scheduled a Node prototype per task and
the guest build only once, at the very end, so the red trees sat there until Task 4.
A Node prototype cannot know a platform hazard, and a single build at the end
cannot say which commit introduced one. From this round on:

> **A task that adds a `.vb` file to a `.vbproj` ends with a rebuild, not merely a
> prototype run.**

It is in the loop in `.agents/skills/browserforwp/SKILL.md` too, because the plan
that broke it was written by the process the loop describes.

**Verified:** `node tools/proto/boxlayout.mjs` 19/19, `textmeasure.mjs` 10/10,
`core-logic.mjs` 60 assertions, `check-vb.mjs` 0 finding(s), six configurations
`BUILD_EXIT=0` with only the two deliberate `ResourceLoader` warnings.

> **Superseded.** Both of those referees were deleted with the renderer they
> measured, in Task 1 of the remote-render client plan — a green check standing
> over a deleted implementation is worse than no check, because it reads as
> coverage. The paragraph above is left as the record of what was verified when it
> was written.
**Not verified:** the on-device output. Nothing in this round has been drawn on a
handset; the geometry is asserted off-device and the rendering is not asserted at
all. Record the first real render's surprises here when someone runs it.

### Round 9 — the sealed channel, and three checks that had to be earned

The remote-render client (plan `docs/superpowers/plans/2026-09-29-remote-render-client.md`,
Task 3) adds two files: `BrowserForWP.Net/Remote/SealedChannel.vb`, which seals a
frame with AES-256-GCM under a key derived from the device token and the
connection's salt, and `BrowserForWP/Engine/RemoteChannel.vb`, which joins the
protocol to the TLS client.

**A layer boundary moved the design, and the boundary was right.** The obvious
home for the sealed channel was `Core`, next to `RemoteProtocol`. `Core` may not
reference `Crypto` ([`ARCHITECTURE.md`](ARCHITECTURE.md)), and the sealed channel
IS the layer that holds a key — so it went to `Net`, which references only
`Crypto`. And `Net` may not reference `Core`, so it cannot build the 16-byte header
that is also the AEAD's additional authenticated data. Rather than write the header
twice — a second implementation of the one thing `protocol/vectors.json` exists to
pin — `SealedChannel` takes it as a `Func(Of Byte, UInteger, UInteger, Byte())`
delegate and refuses a null one. `RemoteChannel` lives in the app because the app is
the only layer that may see both. `tools/proto/remote-protocol.mjs` grew from 53 to
91 checks and asserts both halves; two of those checks went red the moment `next`
was renamed (below), which is the referee reading the source rather than trusting it.

**A VB keyword as a local variable, and eleven errors that all named the wrong
thing.** `Dim next As UInteger = _outSequence + 1UI` — `Next` closes a `For`. vbc
answered with `BC30201` on that line and then `BC30451 "'header' is not declared"`
for each of the eleven following lines, every one of them naming something that
plainly IS declared. Nothing in this repository could have caught it: `check-vb.mjs`
had no group for name legality, and `tools/proto/remote-protocol.mjs` reads source
text for structure, not for legal identifiers. That is the worst ratio this project
has had between "one mistake" and "warnings that mislead".

**Group 17 exists now, and its list is MEASURED.** The first version of the list
was written from the language reference, and the reference is not the compiler:
`Out` is in its reserved list and `Dim out(31) As Byte` compiles — it is on disk in
`X25519.vb` and that project builds in all six configurations. So
tools/keyword-probe compiles one `Dim <word> As Integer` per candidate and reads the
answer: **117 candidates, 113 refused, 4 accepted** (`out`, `async`, `await`,
`custom`). All four would have been false positives.

**And the first probe run was wrong in the other direction.** One file, 117
candidates: vbc reported one error per candidate up to line 146 and then stopped,
with no message, because vbc 12 is pre-Roslyn and gives up after about a hundred
errors. The last sixteen words came back "legal" because they had never been
compiled. The probe is now three batches of under fifty declarations, each ending
with a sentinel whose refusal proves the batch reached its end, and the wrapper
prints that verdict rather than a count. A measurement whose failure mode is
silence needs a witness, not a bigger sample.

**Group 13 earned two more rules, both from the same defect, twice in one round.**
A plain `'` comment stranded inside a `'''` doc block ends the block, so the
closing tag that follows belongs to a second comment that never opened —
`BC42301` plus `BC42304`, and the documentation is discarded. It appeared in
`SealedChannel.vb` and then in `RemoteProtocol.vb`, written one round earlier. Then
the fix for the second one introduced the same trap at one remove: prose that
mentions a closing summary tag closes the element early, and the guest build said `BC42304` again. Group 13 previously looked only for `<` followed by a *digit* — one
way to reach the warning, not the rule. It now balances the tags. It also reported
`<paramref>` as an unknown tag, which the compiler accepts: the allow-list is
checked against the compiler too.

All three of those are warnings. A warning does not fail a build, and that is
precisely the harm: three cheap findings that train a reader to skim the warning
list, which is where the next real one will be. The one rule this round added to
the table above is therefore about *warnings* as much as about keywords.

**A namespace checker that could not see nesting.** `fileNamespaces` matched every
`Namespace` line separately and prefixed the project's root namespace, so
`Namespace Engine` / `Namespace Remote` produced `…Core.Engine` and `…Core.Remote`
and never `…Core.Engine.Remote`. The compiler composes them. A correct
`Imports BrowserForWP.Core.Engine.Remote` was therefore reported as matching no
namespace in the solution — a false alarm on code that compiles, which is the one
thing an import checker must never do. `RemoteProtocol.vb` had declared that
namespace for a whole round and it stayed invisible until something imported it.

**Task 4 — a primary, a secondary, and one defect that would have been silent.**
`RemoteServers.vb` holds the rule the request asked for: two servers, the second
tried only when the first cannot be reached, and no third — because "add your own
server" means replacing the secondary. The first draft of `Normalize` asked
whether a colon appeared before a scheme, and `render.example.com:8443` has one:
the host read as scheme `render.example.com`, the port as its path, the scheme was
neither `https` nor `http`, and the function returned empty. A server a person had
just typed would **vanish from the settings screen with no message at all**, which
is worse than a rejected field, because there is nothing to correct. The test is
`scheme://`, and `Uri` is no longer trusted for the rest either: the authority must
look like a host, because what `Uri` accepts can differ between profiles and this
function's contract is "nonsense becomes not configured". Both cases are pinned in
`core-logic.mjs`.

**The referee earned its keep twice on the day it was written.** `19/19` on first
run took two corrections: the private constructor that makes the class
uninstantiable was missing, and `LooksLikeAHost` initially refused a colon, so
`https://host:8443` — a port, which is the normal way to reach a local server —
was rejected by the very check meant to allow it. A behaviour-only test would have
missed the first and a source-only test the second.

**Two referees had to be retired with the code they measured.**
`tools/proto/textmeasure.mjs` and `tools/proto/boxlayout.mjs` were the referees for
the on-device renderer, and Task 1 deleted that renderer. They were left in the
docs for a round, which is the worst of both worlds: a green check standing over a
deleted implementation reads as coverage. Deleted, with the commands and table rows
that named them. The same sweep found `tools/proto/modern-sites.mjs` asserting
`mainPage.includes('>= 8')` — the auto-reader threshold, which had been moved into
`EngineChoice.AutomaticFallbackThreshold` precisely so the number would live in one
place. It now asserts that shape, so the *fix* stops reading as a regression.

**And the group count was never a count of groups.** `checksRun` was incremented
inside loops over files, so `check-vb.mjs` reported 71, 72 and 73 groups across
three rounds in which exactly one group was added — and this file quoted all three.
The run list is an array now and the number is its length: **16 groups over 17
numbered categories**. A count that moves for reasons the reader cannot see is
worth less than no count, and it had been copied into two documents.

**Verified:** `node tools/proto/remote-protocol.mjs` 91/91,
`remote-servers.mjs` 19/19, `check-vb.mjs` 16
groups / 0 finding(s), `core-logic.mjs` 66 assertions, `boxtree.mjs` 48/48,
`engine-choice.mjs` 23/23, `gen-vectors.mjs` 53 assertions, `check-polyfill.mjs`
ES5-valid, and `vm-build.cmd /t:Rebuild` on the guest with
`=== Real compiler errors === none` and **only the two deliberate `BC40000`
`ResourceLoader` warnings** — the doc-comment warnings are gone. The keyword probe
self-checks all three batches.
**Not verified:** the channel has never spoken to a live server. `SealedChannel`'s
bytes are pinned by vectors and `RemoteChannel` is a socket, a loop and error
handling, but the handshake has not run against the Node server, and nothing has
been drawn on a handset.

### Round 10 — the server draws, the phone holds the picture

The remote engine stops being a skeleton. `RemoteEngine` walks
`RemoteServers.Order`, connects over the app's own TLS 1.3 stack, decodes `FRAME`
into tiles it draws on a `Canvas`, and turns a tap, a drag and a keystroke into
`TAP`, `SCROLL` and `TEXT`. `RemoteChannel` gains `NavigateAsync`; the shell gains
the four server fields (the plan specified a settings surface and then asked a
person to paste a token into one) and a `MediaElement` for the server's `AUDIO`
message.

**The read loop's handler signature changed, and both changes are load-bearing.**
It now receives the frame's SEQUENCE NUMBER and is AWAITED. Without the sequence,
`ACK` cannot name a frame — and the server holds its screencast until an ACK for
the frame in flight arrives (`src/session.js`, rule 4), so a client that could not
acknowledge would receive exactly one frame per connection **and look perfectly
healthy**. Without the await, drawing a frame (which decodes a JPEG) would be
overtaken by the next message: two frames decoded at once, drawn in the wrong
order, with the ACK for the older one arriving last. Neither defect is visible in
a screenshot; both are visible in the protocol.

**A close this client asked for is no longer reported as a failure.**
`ReplaceChannel` retires the previous connection asynchronously, so its read loop
reached its end *after* the new page had been reported, and the shell put an error
over a working page on every second navigation. `Disconnect` now records that the
close was requested, and the loop skips the callback for it.

**Three defects, and none of them was caught by a checker.** Two came from the
guest compiler: `RemoteServers` is declared in `BrowserForWP.Core.Remote` while the
wire format is in `BrowserForWP.Core.Engine.Remote`, and both files share the
folder `BrowserForWP.Core/Engine/Remote/`, so the import looked right and group 5
accepted it — a folder is not a namespace (BC30451 and BC30002, twice). And
`DisplayInformation.ResolutionScale` is *obsolete on Windows Phone*, which BC40019
says in Italian while naming `RawPixelsPerViewPixel`; the deprecated call was the
one that sizes the picture on the glass. The third came from reading the file this
round edits: `OnNavigatedTo` applied the localized strings before choosing an
engine, and `ApplyLocalizedStrings` reads `_engine.Capabilities` — **so the app
crashed on start**, on a first launch, inside a handler with no `Try` around it. It
had been that way since the engine became a choice. Every row of every
hand-verification table in this file begins at the screen it never drew.

**Verified:** six configurations `BUILD_EXIT=0` (four solution builds plus both
`Any CPU` app-project builds) with only the two deliberate `BC40000`
`ResourceLoader` warnings; `remote-protocol.mjs` 91/91; `remote-servers.mjs` 19/19;
`core-logic.mjs` 66 assertions; `engine-choice.mjs` 23/23; `boxtree.mjs` 48/48;
`check-vb.mjs` 16 groups over 17 categories, 0 finding(s).
**Not verified:** everything that needs a handset or a server — see § "The remote
engine, verified by hand", where every row is blank rather than marked as passing,
and deferred items 15 and 16.

### Round 11 — the keyboard, and the write that had to be serialised

The input path, end to end: a tap focuses the hidden field (and therefore raises
the soft keyboard), what the keyboard types crosses as `TEXT`, Enter and
Backspace cross as `KEY`, the page redraws and the picture shows the text. Plus the
keys a phone keyboard cannot send at all — Tab, Escape, Backspace and the four
arrows — as a scrollable bar above the page, and a rotation that moves the
server's viewport and this device's finger mapping together.

**The half nobody can see: the writes were not serialised.** `RemoteEngine.Send`
is fire-and-forget and `Tls13Client.WriteAsync` does not serialise its callers, so
two messages could be in `SealedChannel.Seal` at once — reading the same sequence
number and encrypting two records with the same nonce, which the server answers by
closing the channel. Nothing had noticed because nothing typed: until this round,
every message came from one place at a time, and typing is many small messages
from two, the UI thread and the frame-acknowledging read loop.
`RemoteChannel` now takes a `SemaphoreSlim` around **seal and write** in both send
paths, and `tools/proto/remote-input.mjs` asserts it is there and inside a
`Finally`.

**Why tap-then-type is not a race, measured rather than assumed.** `src/server.js`
chains the messages of a connection (`queue = queue.then(() => session.onFrame(frame))`)
and `Session.onFrame` awaits `_dispatch`, so the `TAP` that focuses a field has
finished before the `TEXT` that follows it is handled. On the client side the same
order is what the gate above protects.

**What this round does NOT do, and says so.** The protocol has no message meaning
"focus moved", so the phone cannot know whether a tap landed on a text box: **the
keyboard comes up on every tap**, including on a link. Mirroring the page's focused
field needs a new message type in both repositories (deferred item 17). The `KEY`
message's modifier byte is carried and ignored by the server, so Shift+Tab is not
offered rather than offered and wrong (item 18).

**Two defects, one from the compiler and one from the referee's own negative
control.**

- `Window.SizeChanged` takes its arguments from `Windows.UI.Core` and its event
  from `Windows.UI.Xaml`; the first build of the handler said
  `Windows.UI.Xaml.WindowSizeChangedEventArgs` and got BC30002 — a type name no
  checker here reads.
- The rotation check in `remote-input.mjs` was wrong twice before it was right: it
demanded `_screen.SetViewport` inside the handler, when the design funnels both
numbers through `ApplyViewport`, and its planted defect replaced the FIRST
occurrence of a button name, so the name survived later in the file and the
mutation planted nothing at all. The `--probe` run is what exposed it: **a
mutation that does not fail its check is a check that cannot see what it is named
after.**

**Verified:** `remote-input.mjs` 7/7 with all 7 planted defects refused;
`remote-protocol.mjs` 91/91; `remote-servers.mjs` 19/19; `engine-choice.mjs` 23/23;
`core-logic.mjs` 66 assertions; `boxtree.mjs` 48/48; `check-vb.mjs` 16 groups over
17 categories, 0 finding(s); six configurations `BUILD_EXIT=0` with only the two
deliberate `BC40000` warnings.
**Not verified:** the whole of it that needs a soft keyboard. That a tap raises
the system keyboard, that a keystroke arrives after it, that the keys bar's buttons
press what they say, and that a rotation keeps the keyboard up are all unrun — see
the blank rows in § "The remote engine, verified by hand".

### Round 12 — the solution an IDE can load, and the comment that broke a project

**Reported:** opening `BrowserForWP.sln` in a tool that is not Visual Studio — a
solution selector on macOS — lists `(0 projects)`, with every project beside it
marked `(unavailable)`. The projects themselves were fine: six configurations
`BUILD_EXIT=0`. Both differences from a loadable solution are in the *text* of the
solution file.

**The `.sln` names a project factory, and a name that is not registered is not a
hint, it is an empty solution.** Round 6 put `{76F1466A-...}`, the Windows Phone
8.1 *flavour*, in all seven `Project` lines, reading the registry and concluding
the loader ignored the field. The registry shows which factories exist. What the
loader *does* with an unregistered name was never measured until now, and the
oracle is the IDE's own loader — `devenv.com` uses the project system, not MSBuild:

```
# .sln says {76F1466A-8B6D-4E39-A767-685A06062A39} in the seven Project lines
Build: 0 succeeded or up-to-date, 0 failed, 0 skipped     <- no project loaded

# .sln says {F184B08F-C81C-45F6-A57F-5ABD9991F28F}
Build: 7 succeeded, 0 failed, 0 up-to-date, 0 skipped
```

One field apart, everything else identical. `MSBuild` reads neither the GUID nor
the separator, which is why no round of guest builds could see the difference, and
the lessons sit on opposite sides of the same file: the **`.vbproj` carries the
flavour** (Round 6, still right), the **`.sln` carries the factory** (this round,
and the reason is that VS2013 registers only `{F184B08F-...}` while resolving a
solution entry through whatever factory it names).

**Second defect, found while measuring the first.** With the solution loading,
the IDE's project system took six of the seven projects and refused
`BrowserForWP.Crypto` with

```
BrowserForWP.Crypto.vbproj : error  : The application for the project is not installed.
```

which carries no diagnostic code, so every build on this project stayed green while
the project could not be opened — the same blind spot as Round 6, one layer down.
It is not the name, the path or the surrounding solution. Bisected on the guest,
each row one `devenv.com /build` run against a one-project solution:

| Variant of `BrowserForWP.Crypto.vbproj` | Loads? |
| --- | --- |
| untouched | no |
| byte-identical copy under another file name | no |
| every XML comment removed | **yes** |
| comment block N removed, for each N in turn | only N = 2 loads |
| comments intact, the two mentions inside block 2 reworded | **yes** |
| block 2 removed, one mention added to a leading comment | no |

The last two rows are the finding. That file is the only project here whose
comments named the flavour property, and the name is what the IDE keys on: **the
Windows Phone project factory locates that property by scanning the project file
as TEXT, not by parsing it as XML, so the first occurrence of the name is the one
it reads.** In `BrowserForWP.Crypto.vbproj` the comment's mention came first, the
real element second; the scan read the first, the flavour came back empty, and the
project was refused. This is why the word cannot appear in prose here, and why it
is written as "the flavour property" everywhere else.

**Fixed:** all seven `Project` lines in `BrowserForWP.sln` moved to
`{F184B08F-C81C-45F6-A57F-5ABD9991F28F}` and to `/` separators — a backslash is an
ordinary character in a file name on any host that is not Windows, so the project
could not be found even with the GUID right. The offending comment in
`BrowserForWP.Crypto.vbproj` now says "the flavour property" and carries the reason,
so nobody restores the name as a kindness.

**Enforced** by group 14 of `tools/check-vb.mjs`, three rules in one group: the
flavour GUID wherever `TargetPlatformIdentifier` is `WindowsPhoneApp`; a registered
factory GUID and `/` separators in the `.sln`; and the flavour property's name
nowhere ahead of its element, comments included. Negative controls run for the two
new rules — reverting the `.sln` to the flavour GUID produces 8 findings, one added
mention in a comment produces the third.

**Verified:** `check-vb.mjs` 16 groups over 17 categories, 0 finding(s), with both
negative controls red; all 20 referees in `tools/proto/` green (`remote-protocol`
91/91, `remote-input` 7/7, `remote-servers` 19/19, `engine-choice` 23/23,
`core-logic` 66 assertions, `boxtree` 48/48, `csscascade` 47/47, the rest unchanged);
six configurations `BUILD_EXIT=0` carrying only the two deliberate `BC40000`
warnings; and `devenv.com BrowserForWP.sln /build "Debug|ARM"` — the IDE loading
and building every project — `Build: 7 succeeded, 0 failed`.
**Not verified:** the selector the report came from. No Visual Studio, and no
solution reader other than `devenv.com`, runs on this machine; what is measured is
the project system those tools hand the file to, not the tool itself.

**Noticed while measuring it:** `devenv` rewrites the projects it opens — BOM, CRLF
line endings and a `<Folder Include="My Project\" />` item appear in whichever
`.vbproj` files it touched, and on an earlier run they nearly went into a commit.
Revert them (`git checkout -- <file>`), or a round that only meant to change the
solution will also rewrite four project files.

### Round 13 — the hosted engine becomes the default engine

**Asked for:** the browser has to render modern pages without being set up first,
so the hosted engine is the default one, pointed at the server this project runs.

**What changed, and what deliberately did not.** The default engine is
`EngineChoice.Remote`, the switch ships on, and `AppSettings.DefaultHostedUrl` is
`https://34.132.106.149`. What did NOT change is the part that makes a default
honest: **wanting the hosted engine is not having it.** `EngineChoice.Decide` now
takes a `hostedReady` input and consults it before either path that can return the
hosted engine, `RemoteServers.Ready` defines that input as *switch on, an address,
and a token for that address*, and a fresh install is therefore ready at nothing
-- it has the address and no token, because a token is issued per device by
`bfwp-device add` and pasted in by hand. Its first page is drawn on the phone,
with the reason in the status line.

The decision table, which is now the file's whole contract:

| Setting | Usable hosted server | Measurement | Engine |
| --- | --- | --- | --- |
| Trident | any | any | Trident |
| Remote (the default) | yes | any | the hosted server |
| Remote | no | any | Trident, reason `EngineReasonRemoteNotConfigured` |
| Auto | yes | no measurement | Trident |
| Auto | yes | below the threshold | Trident |
| Auto | yes | at or past the threshold | the hosted server |
| Auto | no | at or past the threshold | Trident, reason `EngineReasonRemoteNotConfigured` |

> The second and third `Remote` rows are superseded by Round 19: an explicit
> Server choice is no longer rewritten into a page on the device, and
> `MayFallBackToDevice` is the rule that decides when the announced fallback may
> happen at all.

**The fallback is announced, not silent.** When the hosted engine reports
`EngineReasonRemoteNotConfigured` or `EngineReasonRemoteUnreachable`, those two
keys are not page errors: they are the default engine failing to be an engine, and
`OnRemoteNavigated` hands the page to the on-device engine. The status line says
which engine drew it and why the other one did not, because a browser that quietly
switches to a different renderer is the shape of lie this repository keeps finding.
`IsHostedEngineUnusable` names the two keys in one place, and `engine-choice.mjs`
asserts that the shell and the rule still agree on them.

`RemoteNavigationResult` grew the consequence: **a failure carries the page that
was asked for.** It used to carry an empty url, and the fallback would then have
read `_session.ActiveTab.Url` -- the PREVIOUS page -- and loaded it, plausibly and
wrongly. The engine knows what it was asked to render; it now says so.

**One definition instead of two.** The settings-to-plain-data snapshot existed in
RemoteEngine and was about to be written a second time in the shell;
`AppSettings.RemoteSettings()` is now the one copy, and `RemoteServers.Ready`
consumes it. Two copies of "which server did the person just configure" is how the
engine and the settings screen come to disagree.

**An upgrade does not move anybody onto the server.** The defaults are for a fresh
install. A stored `remoteEnabled=0` with an empty address stays that way, so an
install that predates this round keeps rendering exactly where it did, and the
only way onto the hosted engine is a person choosing it or configuring it.

**The referee could not see its own rule, and that is the round's real defect.**
`tools/proto/engine-choice.mjs` is a transliteration, so it executes *its own* copy
of `Decide`: the first version of it passed with the readiness branch DELETED from
`EngineChoice.vb`, and the mutation is what exposed it. Four source-contract checks
now pin the branch in the VB -- readiness consulted on both paths that can return
the hosted engine, the reason key returned twice, and no "chosen in Settings"
reason for a server that is not usable -- and both mutations are refused. A mirror
that drifts reports green for behaviour the VB no longer has; that sentence is in
this file already, and this round is the first time it was earned twice.

**Verified:** `engine-choice.mjs` **38/38**, not the 34/34 this sentence recorded
when the round was written — the number in the document was never the number the
referee in that commit prints, and Round 14 measured it. Two mutations refused,
`remote-servers.mjs` 24/24, `core-logic.mjs` 72 assertions, `check-vb.mjs` 16 groups
over 17 categories and 0 finding(s), the other seventeen referees unchanged, six
configurations `BUILD_EXIT=0` on the phone's toolchain, `devenv.com` loading and
building all seven projects (`Build: 6 succeeded, 0 failed, 1 up-to-date`), and the
handset rows this round adds to the table above -- which are still blank, because
there is still no handset.
**Not verified:** every claim that needs the phone. That the first page of a fresh
install really is drawn by Trident, that pasting a token switches it, that a dead
server falls back with the right words on screen: all of it is in the table above,
unrun. The default is also not exercised against the real server
(`34.132.106.149`): no device token exists for this host, and Docker is not
installed here, so nothing in this round has ever spoken to it.

### Round 14 — the keyboard rises only on a field that takes text

**Asked for:** the soft keyboard must come up on a text field and stay down
everywhere else. Deferred item 17, and the reason it was deferred is that it cannot
be fixed on the client: `RemoteScreen.OnTapped` focused its hidden field because
that was the only trigger it had, and *whether a tap landed on something that takes
text* is a fact about the page — which is on the server.

**The message, and why it is one byte.** `FOCUS = 0x27`, server to client, sealed,
`u8 editable`. Not `{ editable, fieldType }`: the *kind* of field (password, email,
number) would let the phone choose an `InputScope` soft-keyboard layout, and that is
the one part of this change that **cannot be checked without a handset**. The
decoders reject trailing bytes on both sides, so adding the field later costs a
protocol change in two repositories plus the vectors — which is the honest price,
and the reason it is a numbered gap (item 20) rather than a field nobody reads.

**Where the server decides it.** `src/browser.js` gains `focus()`: one
`page.evaluate` answering whether `document.activeElement` is a text-bearing
`input`, a `textarea`, or `isContentEditable`. `src/session.js` gains
`_reportFocus()`: ask, compare with the last byte sent, and write only when it
changed — a `FOCUS` per keystroke would spend the channel on a byte that did not
move. It is asked after `TAP`, after `KEY` (Tab moves focus, so does Escape) and
when a load completes, where the document's focus is gone whatever the previous
page said. Navigation resets the last answer to "not asked yet", so the first
report after a page change is always sent — even when it repeats the previous byte,
because it is a different document's fact. A browser with no `focus()` leaves the
session working and silent.

**Where the client obeys it.** `RemoteScreen.SetPageFocus(editable)` is the only
code that raises the keyboard. `False` **disables** the hidden `TextBox`: WinRT has
no `Unfocus()`, and disabling the focused control is the one state change that both
drops the focus and closes the keyboard. `True` re-enables it *before* focusing,
because the message that asks for focus is often the one after the message that took
it away. `FocusKeyboard()` — the rotation path — is a no-op unless the page said so,
and the rotation call site now asks `WantsKeyboard` (the page's answer) instead of
`HasKeyboardFocus` (the field's state): a re-arranged tree reports `Unfocused`
whether or not the person was halfway through a sentence, so the old question would
have closed a keyboard nobody dismissed. `OnTapped` no longer touches focus at all,
and the keys bar is unaffected — Tab, Escape and the arrows are `KEY` messages, not
the soft keyboard.

**The defect this round found is a syntax error, and the suite found it.** The first
version of the server change wrote `await this._reportFocus()` inside
`_onBrowserEvent`, which was not `async` — `SyntaxError: Unexpected reserved word`,
and because a module that cannot be parsed takes its whole test file with it,
`session.test.js` reported **1 test, 1 failure** rather than one wrong behaviour. It
had been written by reading the message and not by running it. `_onBrowserEvent` is
now `async`, with the reason in its comment: the event source does not await it, and
the handling up to the first `await` is synchronous, so the messages that need no
answer keep the order they were emitted in.

**And the referee's own blind spot, again.** The helper that reads the FOCUS
messages out of the transcript opened every frame in it, including the plaintext
`HELLO_ACK` — and the sealer's replay rule refused it (`seq 0 after 0`), which then
refused the *second* reading of the same frame too. Two readings of a transcript want
two `Opener`s, not a second message. Both were caught by running it.

**Three recorded numbers did not match their artefacts, and running the commands is
what said so.** `tools/proto/engine-choice.mjs` prints `38/38` and had been written
down as 34/34 in Round 13's record and in the verification table here;
`tools/proto/fetch-rules.mjs` prints `31/31` and the skill table said 25/25 since the
pin-enforcement check was added in `03734d1`. Both are corrected in place with the
correction marked, because a document that tells a reader to expect a number the
tool does not print is a document that teaches them to ignore it. This round's own
numbers were then taken from the terminal rather than from this file.

**And the IDE oracle was not runnable as printed.** The command for `devenv.com` in
this file was a host one-liner with `""`-escaped inner quotes, and `cmd /c` cannot
receive it: the argument reaches the guest already split, the quotes around
`C:\Program Files (x86)\...` and `"Debug|ARM"` are gone before devenv sees them, and
the guest answers `"""C:\Program` is not recognized as an internal or external
command`. It is now `tools/vm-devenv.cmd`, which has no quoting to lose, asserts its
verdict rather than relaying devenv's exit code, and exits 1 on a project the IDE
refuses. Writing it also measured two things the one-liner never would have: the
summary says **`Rebuild All: 7 succeeded, 0 failed, 0 skipped`** and not `Build: 7
succeeded` (the wording follows the verb), and a plain `/build` of a clean tree
prints **no `Build started` line for a project it considers current**, so an oracle
that counted those lines reported `6 loaded, expected 7` against a tree that was
more correct than the run before it. `/Rebuild` is what makes the count exact.

**The client's half is a separate commit in a separate repository, and the vectors
are the join.** `protocol/vectors.json` in the client is the server's own file, copied
in the same round it was regenerated: 23 payloads and 21 sealed frames became 24 types
with `FOCUS_EDITABLE` and `FOCUS_NONE` pinned. `remote-protocol.mjs` refuses a client
whose order or count has drifted, because that failure is a garbled screen with no
error anywhere.

**Verified:** `npm test` in `Docker-BrowserForWP` 148/148 (five new session cases:
a tap reports the browser's answer, an unchanged answer is not sent again, Tab and a
completed load re-report, a navigation makes the next report unconditional, a browser
without `focus()` is silent and not fatal); `remote-protocol.mjs` 100/100 (was 91);
`remote-input.mjs` 11/11 with all 12 planted defects refused (was 7/7); `check-vb.mjs`
16 groups over 17 categories, 0 finding(s); six configurations `BUILD_EXIT=0`; `devenv.com`
loading all seven projects.
**Not verified:** the four rows this round adds to § "The remote engine, verified by
hand" (a tap on a field, a tap that must not raise one, a rotation with nothing
editable, and the same field after a manual dismissal) — they are blank, because
there is still no handset. `browser.js`'s `focus()` needs Playwright and a browser,
neither installed here, so its logic is exercised only through the session's fake
browser; that part of the change has never been near a live page. Item 20 is the
other half: a page that focuses a field by itself, and a re-tap on a field whose
answer has not changed, both still raise nothing.

### Round 15 — the server, on a real machine, answering a real client

**Asked for:** put the render server on the `docker1` VM with `gcloud` and verify
that it works. "It works" had never been tested: every previous round of
`Docker-BrowserForWP` ended with the image never built and the Chromium paths
covered by nothing at all.

**What was done:** the repository cloned onto the VM, a self-signed certificate for
the IP generated, the image built and the container started with
`BFWP_MAX_SESSIONS=2`, a device registered, and a client written for the purpose --
`bin/bfwp-smoke.js` in the server repository -- which dials the server, completes
TLS 1.3, seals a `NAVIGATE`, receives a JPEG from Chromium, acknowledges it, taps a
text field and checks the answer. It says **`10/10`**, three times in a row from
this Mac over an SSH tunnel and again from inside the container.

**Every failure it found was invisible to everything already in the repository,
and there were four.** In order, because each one hid the next:

- `docker compose build` refused the whole compose file: a named volume and a tmpfs
  both mounted at `/run/bfwp`. No build had ever run the file.
- The container restart-looped on `EACCES ... privkey.pem`. The server runs as
  `pwuser` (uid 1000) and a bind mount keeps the HOST's owner, so a key written
  `0600` by certbot or openssl is unreadable. The deploy notes' one-off `chown`
  works until the next renewal, which rewrites the key as root, sixty days later,
  on a timer. There is now an entrypoint that stages a readable copy and drops to
  `pwuser`, and it took three more measured failures of its own (SETUID/SETGID and
  CHOWN removed by `cap_drop: ALL`; and a `chmod` after a `chown` needing
  CAP_FOWNER, fixed by swapping two lines).
- The server then accepted a device, sealed frames, and could not launch a browser:
  `package.json` declared `playwright: ">=1.40 <2"`, npm resolved it to 1.63.0, and
  the base image ships 1.49.1's browsers. The version is pinned exactly and the
  BUILD now reads it back and refuses to produce an image where the pair disagrees.
- A device registered against the running server was refused with UNKNOWN_DEVICE
  until a restart. `bfwp-device.js` writes the registry from a second process; the
  server had read it once. It reloads on a changed file now, which is not only
  convenience -- a `disable` the server cannot see is a lost phone that still
  connects.

**The client half of this round is empty, and that is the honest shape of it.** No
line of VB changed, because nothing on the device could be exercised: the smoke
client speaks the protocol from Node, which pins the wire format and the server's
behaviour and says nothing about `RemoteScreen`, the soft-keyboard proxy or the
picture.

**The FOCUS message, verified on a real Chromium for the first time.** Round 14
shipped it with the server half covered by a fake browser and the client half
unrun. Against the live deployment: the load-time report is `editable=false`, a tap
that lands on the text field reports `editable=true`, typing redraws the page, `Tab`
moves the answer to the button, and a second tap on that button produces **no
message at all** -- which is the deduplication the protocol promises, observed
rather than asserted.

**Measured, for the sizing question:** one live session with a page in it held its
container at **231 MiB peak**. On this VM (953 MB) that is two or three devices, not
the sixteen the compose default allows.

**What is still not verified, and is now a short list with names on it.** The handset
has never spoken to this deployment, and cannot yet: port 8443 is closed on the GCE
firewall (no rule allows it), and the certificate is self-signed for the bare IP,
which the phone refuses by design -- it validates the chain and the name before it
sends a byte. Both are the owner's decisions to make, not gaps in the code. The audio
capture still needs a sound device.

**Verified:** `npm test` 155/155 in the server repository, `remote-protocol.mjs`
100/100 and `remote-input.mjs` 11/11 with all twelve mutations refused in this one,
the six configurations and `devenv` unchanged, and the deployment itself: healthy,
seven devices registered, `10/10` from two directions.

### Round 16 — the deployment becomes reachable, and publicly trusted

Round 15 ended with a short list and names on it: port 8443 was closed, and the
certificate was self-signed for the bare IP, which the phone refuses by design. The
owner's answers were "open it to the world", "no domain, use a Let's Encrypt
certificate anyway", and "no connection limit", and this round is the three of them.

**A certificate for an address is possible now.** Let's Encrypt opened IP
certificates to the general public in January 2026, under the `shortlived` profile:
160 hours, no common name, the address in the SAN as an `iPAddress` entry. Certbot
has supported it since 5.3 (`--ip-address`) and 5.4 (webroot), so it comes from a
venv on the host -- Ubuntu 24.04's archive predates both. What that buys is that
`BFWP_PUBLIC_URL` can stay an address, and no name has to exist or resolve.

**What a six-day certificate costs, and how it is paid.** A renewal timer is part
of the installation, not an improvement to it, and the renewal has to reach the
container: `Docker-BrowserForWP` gained `bin/bfwp-renew-hook.sh`, which copies the
new pair into the one directory the container mounts, restarts the service and waits
for the container's own health check before reporting success, plus
`deploy/certbot-renew.{service,timer}` to run it twice a day.

**Measured, from another machine, over the Internet, with no tunnel:** TLS 1.3 with
`ecdsa_secp256r1_sha256`, `Verification: OK` against the public roots,
`openssl x509 -checkip 34.132.106.149` -> `does match certificate`, and
`bin/bfwp-smoke.js --verify` -> **`10/10`**, including the tap that reports an
editable focus and the unchanged answer that stays quiet. That is the first time
the deployment has been accepted by a client that checks the certificate at all --
Round 15's runs had verification off, because the certificate could not pass it.
The renewal loop was then executed for real rather than reasoned about:
`certbot renew --force-renewal` on the host -> the hook copied the pair, restarted
the service, waited for healthy, and the server began serving a new serial
(`053F4D02...`, expiring `Oct 6 06:19:05 2026 GMT`).

**Session limit:** the deployment asked for none, so `BFWP_MAX_SESSIONS` is 512, the
server's own maximum. The honest number is still 231 MiB per live session on a 953 MB
VM -- two or three devices -- and the server now logs `512 session(s) allowed` next
to a host that cannot hold a fortieth of it. That is written down here rather than
treated as a limit that was lifted.

**Four defects, and only one of them was in the code.**

- **`docker compose restart` does not re-read `.env`.** It restarts the container
  that exists, with the environment it was created with, so the server went on
  logging `2 session(s) allowed` after the file said 512. Only `up -d` recreates it.
- **The staging trap is silent.** `certbot certonly --cert-name X` does nothing at
  all when X exists and is not yet due: exit code 0, no new file, and the deployment
  kept serving `(STAGING)` issuers after a command that looked like it had worked.
  The hook now refuses a certificate whose SAN is not the address it serves, which
  is the check that would have caught it.
- **A hook's stderr is reported as "error output".** certbot printed *"Hook
  'deploy-hook' ran with error output"* for a renewal that succeeded, because the
  hook narrated its progress on stderr. Progress goes to stdout now; only failure
  goes to stderr.
- **The verification I wrote first was the wrong verification.** `openssl s_client
  -verify_hostname 34.132.106.149` reports `hostname mismatch` on a certificate
  whose SAN holds that address, because OpenSSL consults `dNSName` entries only.
  The chain was fine; the check was not. Same gap as item 21, in a reference
  client, found by trying to prove something instead of asserting it.

**One thing was checked and turned out to need nothing.** The obvious worry was that
the shipped default (`https://34.132.106.149`) would dial 443 while the server
listens on 8443. It does not: `RemoteServers.Normalize` drops the port and
`RemoteEngine.PortFor` puts `DefaultPort` (8443) back for a url that never carried
one, which is stated in the `AppSettings` comment. So the shipped address and the
deployed port already agreed, and neither end changed.

**What this round does NOT verify:** anything about the handset, and it is now two
specific gates rather than a general unknown -- item 21. § "The remote engine,
verified by hand" stays blank, and the deployment being reachable does not fill a
single row of it.

**Verified:** on the host, `docker compose ps` healthy with `512 session(s)`
logged and five to six devices registered; from outside, the four commands above;
the renewal loop end to end; `certbot-renew.timer` enabled with its next run listed.
**Not verified:** the phone (item 21), and the audio path, unchanged from Round 15.

### Round 17 — the client answers for an address, and one gate closes

Round 16 ended with a deployment nobody could use: a publicly trusted certificate
for an address that this client's TLS stack could not match. The owner's answer to
the choice put to them was "decide", so the rule was: close what is ours, write
down what is not, and add nothing that weakens a check.

**What changed.** `X509Reader` reads the SAN's `iPAddress` entries (tag `0x87`) as
**raw bytes** -- 4 for IPv4, 16 for IPv6 -- and `CertificateValidator` gained
`MatchSubjectAltName`, which parses the host into bytes and compares bytes when the
host is an address, and falls through to the untouched RFC 6125 name rules when it
is a name. Comparing bytes rather than text means no rule about spelling an address
can be wrong: no leading zeros, no case, no `::` compression, no formatting at all
except in the diagnostic sentence. An address that is not in the SAN does **not**
fall back to the name rules -- a second chance would be a check that did not earn
its first.

**Verified, and this is the interesting part: against the deployment.**

```
node tools/proto/tls13.mjs 34.132.106.149 8443 --handshake-only
```

`50 checks, 0 failure(s)`, including `the certificate is for this host
34.132.106.149` -- a real handshake against the real server, with the mirror of the
VB rule deciding. `--handshake-only` is new and exists for this: the render server
is not an HTTP server, so without it the run ends red for a reason that has nothing
to do with certificates. Against `example.com` the same file is now `53 checks, 0
failure(s)`.

**Fifteen offline cases, one of them the deployment's own SAN** (`IP Address:34.132.106.149`,
captured from the live certificate), covering: a different address, a name against
an address SAN, an address against a name SAN, a mixed SAN, two spellings of one
IPv6 address, a bracketed host, a dotted-quad tail, and three spellings that must be
**refused** because they mean more than one thing (a leading zero, a zone id
`%eth0`). Plus six **source contracts** reading the VB itself, because a
transliteration runs its own copy of the rule -- the defect that once let
`engine-choice.mjs` pass with the `hostedReady` branch deleted from the VB. All
three mutations are refused: removing the `iPAddress` branch from the reader,
reverting the validator to name-only matching, and comparing an entry against
itself.

**Two defects found by running against reality rather than reading it.**

- **The referee crashed on the deployed certificate before any check ran:**
  `Cannot read properties of undefined (reading 'replace')`. Node reports
  `subject` as **undefined**, not empty, for a certificate whose subject is empty,
  and the `shortlived` profile issues exactly that -- no common name, the address in
  the SAN only. A prototype that prints the subject is fine for a website and fails
  on the first certificate that is only an address.
- **A guest build had rewritten `tests/BrowserForWP.Core.Tests.vbproj`** with a
  BOM, CRLF endings, an added `<Folder Include="My Project\" />` and no final
  newline. Found by `git status` before committing, not by any check; reverted, and
  the behaviour is already documented at "the loop" below (`devenv` rewrites the
  projects it opens). The lesson is narrow and worth writing down: run `git status`
  after a guest build, because the IDE's rewrites are not part of any diff that a
  build prints.

**What was deliberately NOT done.** A pin that would let a certificate skip the
chain check, which is the only way this could work if the handset does not trust
ISRG's roots. `IsValid` is the chain AND the name by design, item 12 says the pins
are not even consulted by the render channel, and relaxing a certificate check to
make something work is the owner's decision to take in the open -- not a patch. It
is the alternative left standing in item 21.

**Verified:** `tools/proto/tls13.mjs` (both runs, `50` and `53` checks, 0 failures),
the three refused mutations, `node tools/check-vb.mjs` -> 16 groups / 0 findings,
and six configurations in the guest, `BUILD_EXIT=0`: Debug/ARM, Debug/x86,
Release/ARM, Release/x86 as solution builds, plus `BrowserForWP.vbproj` as Debug and
Release `Any CPU` (the Release one also producing the package).
**NOT re-run this round:** `tools/vm-devenv.cmd`, because no `.sln` and no project
file changed -- and it rewrites project files, which is the finding above.
**Not verified:** anything on a handset, unchanged.

### Round 18 -- a resource map that does not exist, and the first look at a real device

The app ran, loaded pages, and had no working UI strings. `Localizer` asked WinRT
for the resource map `"Strings/Resources"`, which does not exist, so every lookup
threw `ResourceMap Not Found`, the exception was swallowed by design, and the labels
were the raw keys. On the handset the debug output showed about fifty of those
exceptions per launch -- one per string, because a failed load was not remembered.

**The measurement came first, because the name exists nowhere in the source.** The
map names live inside the built package. Extracting them (UTF-16 strings inside
`BrowserForWP/bin/<platform>/Debug/resources.pri`; macOS `strings` sees only ASCII,
and the Windows 8.1 SDK's `makepri.exe dump` refuses a phone PRI with `PRI file is
invalid`, 0xdef00101) gives the same four names on Debug/AnyCPU, x86/Debug and
ARM/Debug:

```
Resources    Files    Polyfill    Assets
```

No `Strings`, and no `Strings/Resources`. The strings live in the map named after
the `.resw` FILE, and the language folder is a qualifier rather than a path. That is
what the constant says now.

**What guards it.** `tools/check-vb.mjs` group 6 compared the two languages' key
sets and nothing else -- parity cannot see a map that does not exist.
`Localizer.ResourceMap` is now required to be one of the map names the `.resw` files
become. Mutation: putting `"Strings/Resources"` back makes the check report it.

**The second defect, which is why the output was a flood rather than a line.**
`Loader` constructed the `ResourceLoader` lazily and left the field empty when the
construction threw, so the next lookup tried again. `TryCreateLoader()` records
`_loaderUnavailable` now and `Loader` consults it: a broken map costs one exception
and then returns the key, which is what `[Get]` already documented as its behaviour.
The check for that is a **shape** contract and says so -- the VB does not run
off-device, so the behaviour is evidenced below rather than by the check.

**Two things the compiler and the plan itself got wrong.**

- The first version of `[Get]` read `Dim loader = Loader`. VB is case-insensitive, so
  that local shadows the property it is reading: BC30980, then BC30574 and BC30512
  under `Option Strict On`, in all four configurations. The local is `resolver` now.
  Nothing in this repository could have caught it, which is the standing argument for
  the guest build being the arbiter.
- **The plan's own mutation step was dangerous.** It restored the file with
  `git checkout --`, which restores the last COMMIT -- so it deleted the uncommitted
  change it existed to protect, on its first execution. Both mutation steps copy to
  `/tmp` first and `diff` the restore now. A plan is an artifact like any other, and
  this one was wrong in a way that only executing it revealed.

**Measured on a real handset, and this is the first time anything here has been.**
Deployed with F5 to a Windows Phone 8.1 device, the debug output contains **no
`ResourceMap Not Found` line at all**, and the labels are words -- "Cerca o digita un
indirizzo", "Vai" -- instead of keys. The map resolves, the strings arrive, and the
language is the phone's.

**What that run is NOT evidence about.** It is evidence about the app, not about the
remote engine: the page was drawn with the device's own engine, and no row of
§ "The remote engine, verified by hand" was filled, because the wording on the status
line was not read off that screen. Those rows stay blank. What has changed is that
the handset now runs the app, so pasting a device token is the next experiment rather
than a leap.

**Emulators are not available on this development host, and the workarounds are
worth knowing.** The WP8.1 emulator is a Hyper-V VM, which Parallels on Apple silicon
cannot nest: asking `AppDeployCmd` to start one from a non-interactive session fails
with `PrlJob_GetResult: Invalid argument`, and `prlctl exec` runs in **session 0**,
where a `CopyFromScreen` capture is a blank 1024x768 image (measured). Two things DO
work and carried this round: `prlctl capture <vm> --file <png>` photographs the
guest's display whatever session is in front, and macOS Vision OCR (a 30-line Swift
script, validated against an image whose text was known) reads it -- which is how the
debug output above was read without asking anyone to transcribe it.

**The XAML designer is not an oracle for this, and it is broken anyway.** It crashes
with `System.Runtime.Remoting.RemotingException` and "Designer process terminated
unexpectedly"; and even healthy it would not answer this question, because it does
not run code-behind, and all 106 UI strings in `MainPage` come from `Localizer.Get`
there. `x:Uid` appears zero times in the XAML.

**Verified:** `node tools/check-vb.mjs` -> `16 check groups run, 0 finding(s)`, with
the map-name check mutation-tested; four solution configurations in the guest,
`BUILD_EXIT=0` (Debug/ARM, Debug/x86, Release/ARM, Release/x86) with no `error BC`
lines; and on the handset, no `ResourceMap Not Found` in the debug output with the
labels reading as words.
**Not verified:** the remote engine on a device (the table of blank rows) and the
audio path. Both as before.

### Round 19 — the server engine draws the page, or nothing does

**Asked for:** force page rendering onto the server and never the device, and
explain the whole configuration in the README. One decision was taken with the
owner while the plan was written: the device engine stays reachable **as a
choice** (System WebView, Automatic) and stops being a **fallback** for an
explicit Server choice.

**What changed, and the argument for it.** `EngineChoice.Decide` returned
`Trident` for an explicit `Remote` whenever `RemoteServers.Ready` said no, and
`OnRemoteNavigated` handed the page to the on-device engine whenever the server
reported `…NotConfigured` or `…Unreachable`. Both were argued at the time as
honesty -- "wanting the hosted engine is not having it", "a browser that renders
nothing is not a browser" -- and both were a silent change of renderer under a
setting that says otherwise: a person who picks **Server** said WHERE pages come
from, and substituting the device engine answers a different question. The
fallback is now the property of the setting that ASKS for it.

| Setting | Usable server | Measurement | Engine |
| --- | --- | --- | --- |
| Trident | any | any | Trident |
| Remote | yes | any | the hosted server |
| **Remote** | **no** | any | **the hosted server, which draws nothing: reason `EngineReasonRemoteNotConfigured`** |
| Auto | yes | no measurement | Trident |
| Auto | yes | below the threshold | Trident |
| Auto | yes | at or past the threshold | the hosted server |
| Auto | no | at or past the threshold | Trident, reason `EngineReasonRemoteNotConfigured` |

`EngineChoice.MayFallBackToDevice(setting)` is False for `Remote` and True for
everything else, and it is the rule the shell asks: the announced fallback in
`OnRemoteNavigated` is now gated on `IsHostedEngineUnusable(e.StatusKey) AndAlso
EngineChoice.MayFallBackToDevice(_appSettings.EngineSetting)`. The branch that is
left reports `EngineForcedRemoteNoPage` followed by the reason, so a misconfigured
install gets the sentence that says nothing will be drawn and where the fix is,
rather than a page from the engine nobody chose. `EngineReasonRemoteUnreachable`
still carries the address that failed, in parentheses, because that one is a
token; `…NotConfigured`'s engine detail is an English developer string and is not
shown.

**The referee had to be turned around, and that is the part worth reading.** Its
old source contracts asserted the OPPOSITE (`Decide consults readiness on the
explicit path`, "on the automatic path too", two occurrences of `If hostedReady
Then Return Remote`). They now assert one occurrence on the automatic path, that
the explicit path has no readiness branch at all, that `MayFallBackToDevice` is
what the shell gates on, and that the leftover branch names the new key. Both
mutations were run by hand and refused: reinstating the readiness branch in
`EngineChoice.vb`, and deleting the gate from `MainPage`. The first version of the
new negative pattern also matched `Explain`'s own `If wanted = Remote Then` /
`If hostedReady Then …`, and it is anchored on `Return Remote` now -- a source
contract that fails for a reason unrelated to what it names is worse than none.

**The two strings that said the old thing.** `RemoteNotice`, the disclosure in
Settings, promised that switching the server off would "keep every page on this
phone", which is what it no longer does; it now says that with no address, token
or switch nothing is drawn at all, and that another engine is the way to read
pages on the phone. `EngineForcedRemoteNoPage` is new, in both languages.

**Documentation, which was half the request.** Both READMEs gained a
**Configuring the hosted renderer** section (the server, the certificate, port
8443, `BFWP_MAX_SESSIONS`, the device token, the four phone fields, what to
expect) and their **Hosted renderer by default** row no longer promises the
fallback. The server repository's README had a defect of its own: step 3 told the
operator to run `docker compose exec render node bin/bfwp-device.js add`, the
exact command `bin/bfwp-device.sh` exists to replace, which writes a root-owned
registry the server cannot then read -- it names the wrapper now, and the closing
paragraph about a server that "costs nothing but a missing picture" is corrected,
because the missing picture is the whole cost.

**Verified:** `engine-choice.mjs` **42/42** with both mutations refused,
`core-logic.mjs` 73 assertions, `remote-servers.mjs` 24/24, `check-vb.mjs` 16
groups over 17 categories and 0 finding(s), the other sixteen referees unchanged,
four solution configurations `BUILD_EXIT=0`.
**Not verified:** the no-page state on glass. It is one more row of the table
below, and that table is still blank -- `MayFallBackToDevice` is exercised
off-device, but the empty page, the error strip and the localized sentence have
never been seen on a handset.

### Round 20 -- a page the phone serves, so a token is pasted instead of typed

**The request:** the app hosts a small HTML page so a device token can be pasted
more easily -- the app shows a Wi-Fi address, you go there, put the token in a box
and say whether it is server 1 or the backup -- and after the paste there is
nothing left to do. Plus, on the server: a token is removed by opening an issue.

**What was built.** `BrowserForWP.Core/Engine/Remote/TokenInbox.vb` holds the
rules: decoding an urlencoded body, choosing which of the phone's own addresses a
computer can reach, what a token looks like, which slot a word names, and `Review`,
which decides whether a submission may be saved. `BrowserForWP/Engine/TokenPage.vb`
holds the listener, the small amount of HTTP a browser actually sends, and the HTML.
Every sentence comes from the catalogue, and every decision from Core -- the same
split as `RemoteServers`, for the same reason: the parts worth testing are the parts
that do not need a phone.

**Five decisions, each with a cost that is documented rather than implied.** The
listener runs ONLY while the Settings screen is open (button, screen closing, or
five wrong codes). A four-digit code, shown on that screen, is required by the form.
Port 8777 is asked for and the first free port after it is taken, so the address
shown is the one that bound. The token is never echoed back -- the reply masks it to
four characters. And the page is plain HTTP inside the local network, which the
README says out loud: the token travels in the clear on your own Wi-Fi, to a phone
that has no certificate for a name nothing resolves.

**Three things the GUEST BUILD found that no checker did**, in the order they
appeared: a `ReadOnly Property` with no `Get` (auto-implemented one-sided properties
are VB 14; this project is VB 12, and the error is BC30126 followed by a BC30634 in
every following line); a `Dim body As String` that shadowed this class's own `Body`
method for the whole method -- VB is case-insensitive and a local wins for the entire
method, including the calls written ABOVE it; and `Dispatcher.BeginInvoke`, which is
WPF's Dispatcher, while the WinRT `CoreDispatcher` has `RunAsync`.

The second and third are now checks rather than lessons: `check-vb.mjs` gained **a
group for locals that shadow a member of their own class** and an entry in
`PROFILE_HAZARDS` for `BeginInvoke`. The new group immediately found the same pattern
in two files that predate this round -- `X25519.vb` had `Dim carry` inside the method
`Carry`, and `HttpClient13.vb` had `Dim port` beside a `Port` property. Both compiled,
because neither method referenced the name it was hiding; both are renamed.

**And a group for the catalogue**, which this round needed twelve new keys for and
had no way to check: every literal `Localizer.Get("...")` key must exist in the
`.resw` pair. First run: **108 keys asked for, all present, 10 call sites computed
and reported as uncheckable**. Its own first draft used the checker's `cleanLines`
helper, which strips string literals, and reported "0 asked for" as a green line --
written down here because a check that cannot see its own subject is the failure mode
this repository keeps meeting.

**On the server, the other half of the request.** `BFWP_ISSUES_URL` (default: this
repository's issues) is now printed wherever a token is mentioned -- under the form,
under the refusal a lost token walks into, and next to the device id that has to be
quoted in the request. The operator's own half (`bfwp-device release <id>`) sits on
the same page, for whoever has a shell.

**Verified:** `token-inbox.mjs` **86/86** (71 rules + 15 shell contracts), every
other referee green, `check-vb.mjs` **18 groups / 0 finding(s)**, four solution
configurations `BUILD_EXIT=0`, server `npm test` **204 pass / 0 fail**, and the live
page on docker1 carries the removal link (checked with `curl` from outside).
**Not verified:** everything that needs the handset -- the listener binding, the
address and the code as they appear on the screen, the form in a computer's browser,
and the engine switching to *Server* after a paste. That is row 11 of the table
below, and it is blank like the rest.

### Round 21 -- the token button closed the browser, and the path is now guarded

**The report:** tapping the button that starts the token page ends the app, with
`A first chance exception of type 'System.InvalidOperationException' occurred in
mscorlib.ni.dll`. No stack: the first-chance line is what the debugger prints for
exceptions the code CATCHES as well, and the phone's Output window carried only
that line, twice.

**What the code says, read line by line.** Every call under that button is already
inside a guard except two, and they are the reason this round exists:
`Dim listener As New StreamSocketListener()` sat OUTSIDE `StartAsync`'s per-port
`Try`, and `AddHandler listener.ConnectionReceived` sat outside it too -- the two
WinRT calls that make a listener usable, in the one method whose caller is an
`Async Sub` with a `Try` but no `Catch`. An exception from either therefore leaves
`StartAsync`, leaves the click handler, and is rethrown on the UI thread where
nothing catches it. That is the shape of a process ending, and it is the shape the
report describes. `NewCode()`'s `CryptographicBuffer` pair was checked against the
platform documentation rather than guessed at, and is correct: `CopyToByteArray`
is `ByRef value As Byte()`, `Nothing` is the documented input, and the same call is
already the whole of `WinRtCrypto.ToArray`.

**What changed.** Both calls moved inside the per-port guard, so a listener that
cannot exist and a listener that cannot be subscribed to are the same answer as a
port that is taken: try the next one, and say `TokenInboxNoPort` when there is none.
And `TokenInboxToggleButton_Click` grew a `Catch`: it closes the listener and puts
`ex.GetType().Name & ": " & ex.Message` on the status line. That is not a silent
swallow -- a settings toggle that can end a browser is a worse defect than a
listener that will not start, and the framework's own sentence about why is worth
more than this file's guess at one.

**What is NOT known, and is written down rather than implied.** The root cause has
not been seen from here: there is no emulator on this host (see "Emulators do not
work here"), the handset is the owner's, and a first-chance line names an exception
module and not a frame. So this round ships a fix for the two real holes and a
reading off the phone in exchange: the next tap either starts the page, or prints
the exception's own type and message on the Settings screen, which is the only
stack this feature can produce on that device. Do not describe this round as "the
crash was fixed"; describe it as "the path can no longer end the process, and it
now reports what it could not do."

**Verified:** `token-inbox.mjs` **86/86**, every other referee green,
`check-vb.mjs` **18 groups / 0 finding(s)**, and the four client configurations
`BUILD_EXIT=0` (Debug/ARM, Release/ARM, Debug/x86, Release/x86). **Not verified:**
whether either guard is the defect the report came from, which needs the handset.

## The loop

Every change follows five steps, in order. The canonical version lives in
[`.agents/skills/browserforwp/SKILL.md`](../.agents/skills/browserforwp/SKILL.md);
this is the summary.

1. **Plan** the change before writing code (`superpowers:writing-plans`).
2. **Implement** the smallest change that satisfies the plan.
3. **Verify** every layer touched, with the commands above.
4. **Commit** with a Conventional Commit message.
5. **Push.** A commit that is not pushed is not done.

Then update the skill if any tool, command, file layout or constraint changed.

## Where the tests actually are

Both VB test projects from `2026-09-28-browserforwp.md` now **exist and compile**:
`tests/BrowserForWP.Core.Tests/` and `tests/BrowserForWP.Crypto.Tests/`, both
registered in `BrowserForWP.sln` (Debug configurations only) and built by
`tools/vm-build.cmd`. They are deliberately **not MSTest projects** — they hold a
plain `Public Shared Function RunAll() As Integer` that throws on the first failed
check, so they need no test framework the guest might not have.

What is and is not covered:

| Path | What it is | Consumed by |
| --- | --- | --- |
| `tools/gen-vectors.mjs`, `tools/proto/*.mjs` | The executable prototypes. `gen-vectors.mjs` recomputes HKDF, X25519 and AES-GCM and asserts RFC 5869 / 7748 / 8448 and NIST CAVS vectors; `tls13.mjs` completes real handshakes against live servers. | `node`, on any machine. **This is the real crypto verification.** |
| `tests/BrowserForWP.Crypto.Tests/` (`Vectors.generated.vb` + `VectorsSmokeTests.vb`) | Generated VB constants from those same vectors, plus length/shape checks. | **Compiled by the guest build; never executed.** |
| `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb` | Address normalisation, tab state, session/UA, settings, history, favourites, pin normalisation, hostname wildcards, language matching. | **Compiled by the guest build; never executed.** |
| `tools/proto/core-logic.mjs` | A transliteration of `CoreLogicTests.vb`. 72 assertions, exit 1 on failure. | `node`, on any machine. **This is what actually runs those assertions.** |
| `tools/proto/remote-input.mjs` | The remote input path as source contracts: one hidden `TextBox` built once, the `SemaphoreSlim` gate over every write to the stream, the rotation that moves both viewports and sends `RESIZE`, the eight key names the keys bar offers, that every label has a key in both `.resw` files, and — since Round 14 — that **a tap is not a request to type** and that the soft keyboard is raised only by the page's own answer, which the engine must route to the screen. Eleven checks, plus `--probe`, which plants each defect (twelve mutations) and requires its check to refuse it. | `node`, on any machine. |
| `tools/proto/remote-protocol.mjs` | The render protocol's wire format: header, every encoder and decoder, the frame splitter, and the AEAD seal both ways, checked byte-for-byte against `protocol/vectors.json` — which the SERVER's own code produced. The only statement of the protocol that neither implementation wrote. 100 checks, including both values of `FOCUS` and its refusal of a third. | `node`, on any machine. |
| `BrowserForWP/Strings/**/Resources.resw`, and the map name in `BrowserForWP.Localization/Localizer.vb` | Two languages, one key set (**128 keys**, and every literal key the code asks for must be one of them -- the `Localizer keys` group, added in Round 20), AND the name of the resource map the code asks WinRT for — the question parity was not asking, and one whose wrong answer runs silently. Round 18. | `node tools/check-vb.mjs`, group 6. The map-name inference is justified by a measurement of the built `resources.pri`, recorded in Round 18, because the PRI itself is a per-platform build output and is not committed. |
| `tools/proto/remote-servers.mjs` | `RemoteServers.vb`: url normalisation, the primary/secondary order, duplicate collapsing, and the source contract that Core holds resource keys and not prose. | `node`, on any machine. |
| `tools/proto/engine-choice.mjs` | The `EngineChoice` decision table -- including that an explicit Server choice is never the device engine, and that `MayFallBackToDevice` is False for it -- plus the source contract around it: the constants by name, the readiness branch on the automatic path only, the shell's gate, and the reasons as resource keys rather than sentences. | `node`, on any machine. |
| `tools/check-vb.mjs` | 18 categories / 18 check groups over every `.vb`, `.vbproj`, `.xaml` and `.resw`, including every `{ThemeResource}` key, every project's flavour GUID and the factory GUID and separators of every `BrowserForWP.sln` entry, doc-comment structure, every privileged API name and every manifest capability that would ask the platform for something it cannot grant, every API whose capability the manifest fails to declare, every declaration that names a VB keyword, and the two groups Round 20 added: every literal `Localizer.Get("...")` key exists in the `.resw` pair, and no local shadows a member of its own class (`Dim carry` inside `Carry` is the shape of that bug, and two files in the tree had one). | `node`, on any machine. |
| `tools/proto/token-inbox.mjs` | `TokenInbox.vb` -- the rules behind the page the phone serves -- and the source contracts of the shell that serves it: form decoding including malformed escapes, which of the phone's own addresses is advertised, the token's shape, the slot names, every refusal of `Review` (code first, then token, then slot, then address), plus the shell's `no-store` and CSP headers, the five-failure stop, and that the token is written once and masked. 86 checks. | `node`, on any machine. |
| `tools/keyword-probe/`, `tools/keyword-probe.cmd` | One `Dim <word> As Integer` per candidate, compiled by the real vbc, so group 17's list is measured rather than quoted. Batched, with a per-batch sentinel, because vbc 12 stops after about a hundred errors **with no message** and the first single-file version read that truncation as "legal". | `bash`, with the guest reachable. |
| `tools/wp81-theme-keys.sh` | Regenerates `tools/wp81-theme-keys.txt`, the 523 theme-resource keys Windows Phone 8.1 defines, read from the guest's design dictionaries. | `bash`, with the guest reachable. |
| `tools/vm-build.cmd` | The real compiler, and the arbiter of pass/fail. | The Windows guest. |
| `tools/vm-devenv.cmd` | The IDE's own project system, via `devenv.com /rebuild`, run in the guest. The only oracle for the two things `MSBuild` is blind to: a `.sln` naming an unregistered project factory, and a `.vbproj` whose flavour property is named ahead of its own element. Asserts `Rebuild All: 7 succeeded, 0 failed, 0 skipped` and no `not installed` line, and exits 1 otherwise. | The Windows guest. |
| `tools/wmc9999-probe.sh` | Build-diagnostic characterisation and XAML output invariance. | `bash`, on the host. |

**"Compiled" is still not "tested", and the distinction is not academic.**
`CoreLogicTests.vb` shipped an assertion naming the heavy `duckduckgo.com` search
URL that the lite-first default had replaced. It compiled cleanly, so nothing
complained; only running it would have. A WP8.1 ARM class library cannot run on
the desktop and there is no handset or emulator, which is exactly why
`tools/proto/core-logic.mjs` exists: it is the executable half of that suite.

**Keep the two in step.** If `CoreLogicTests.vb` gains a case, `core-logic.mjs`
must gain it too, and vice versa. A mirror that drifts is worse than no mirror,
because it reports green for behaviour the VB no longer has.

Crypto is the one layer covered better off-device than a VB project could cover
it, because the prototypes exercise the *same algorithm* against published RFC
vectors and live servers. Do not claim UI or XAML coverage: neither exists.

## The remote engine, verified by hand

The remote engine has no end-to-end test here, and cannot have one: it needs a live
server and a handset, and this development host is an Apple silicon Mac with no
phone, no emulator and no Docker. The table below **is** the verification for that
task, and it is **empty on purpose**. A row that was not run stays blank; filling
one in from reading the code would make this table worth less than not having it.

Ran it: 2026-09-29. Device: none available. Server: **up, publicly trusted and
answering** since that date -- `Docker-BrowserForWP` runs on `34.132.106.149` with
a Let's Encrypt certificate for the bare address and port 8443 open (Round 16), and
`bin/bfwp-smoke.js --verify` says `10/10` against it with certificate validation
**on**. Every row below is still blank, because the rows are about the PHONE, and
the phone has two gates of its own in front of them: item 21. The server half is
verified elsewhere: see item 15, and "A deployment that answers, and a device that
has not spoken to it" below. The blank table is the point of this section, and
exactly one row will ever fill it: a real device.

| Step | Expected | Result |
| --- | --- | --- |
| Fresh install, nothing configured by hand | The address is the hosted server and the token is empty, so **no page is drawn**, and the status line and the settings screen say the hosted server is not ready. Nothing is sent anywhere, and nothing is drawn on the device either (Round 19). | |
| Paste the token from `bfwp-device add` | The next page is drawn by the server, and the status line names the server that answered. | |
| Clear the address, keep the token | Nothing is drawn, with the reason on screen. | |
| Stop the server, then navigate | Nothing is drawn; the error strip says the servers did not answer and names the address. Choosing System WebView (Trident) in the picker draws the same page on the device. | |
| Pick Automatic, then stop the server and navigate | The page IS drawn on the device and the status line says the hosted server did not answer -- the fallback Automatic keeps, and the one an explicit Server choice no longer gets. | |
| Type a url in the address bar | The page is drawn by the server. | |
| Tap a link | The navigation happens on the server and a new frame arrives. | |
| Scroll | The scroll happens server-side; the frame follows. | |
| Tap a text field | The server reports focus on a field that takes text, and the keyboard rises — a moment after the finger lifts, because the answer comes from the page. | |
| Tap a link, a button, then empty space | The keyboard does **not** rise on any of the three, and it goes back down if it was up. | |
| Open the token page from Settings, then open the address it shows in a computer's browser and submit the token with the four-digit code | The Settings screen shows a Wi-Fi address and a four-digit code; the page loads in the computer's browser and shows the form; a submit fills that server's **address and token**, turns the switch on and switches the engine to **Server (Chromium remotely)** -- the next page is drawn by the server, with nothing left to do. Closing Settings stops the listener, and so does the fifth wrong code. | |
| Type in a form field | The keystrokes cross, the text appears in the frame. | |
| Dismiss the keyboard by hand, then tap the same field again | The keyboard does **not** come back: the server reports on a change, and this is item 20, not a surprise. | |
| Press the phone's back button | The shell's back goes to the previous page. | |
| Stop the server, then navigate | The secondary is used, and the status line says so. | |
| Play a page with sound | Sound, if `WITH_AUDIO=1` and PulseAudio are running. | |
| Turn the phone while a field has focus | The keyboard stays up, the picture fills the new shape, and a tap still lands where the finger is. | |
| Turn the phone on a page with nothing editable | No keyboard appears: the rotation asks the page, not the field. | |
| Press Tab in the keys bar | The next field on the page takes focus, the frame shows it, and the keyboard follows only if the field it landed on takes text. | |

**What this round does verify**, and with what:

| Claim | Evidence |
| --- | --- |
| The input path holds its contracts (one field, gated writes, rotation, key names, a tap that is not a request to type, a keyboard the page controls) | `node tools/proto/remote-input.mjs` → `11/11`, and `--probe` refuses all 12 planted defects |
| The wire format reproduces the server's own bytes, including both values of `FOCUS` | `node tools/proto/remote-protocol.mjs` → `100/100 checks passed` |
| The primary/secondary rule, url normalisation and the readiness rule hold | `node tools/proto/remote-servers.mjs` → `24/24`; `node tools/proto/core-logic.mjs` → `72 assertions, 0 failure(s)` |
| The engine decision table holds, including the hosted default and the setting that may fall back | `node tools/proto/engine-choice.mjs` → `42/42`, and the two mutations that reinstate the device fallback are refused |
| The SERVER works, end to end, against a real deployment | `bin/bfwp-smoke.js` in `Docker-BrowserForWP` → `10/10`: TLS 1.3, a sealed `NAVIGATE`, a real 480x800 JPEG from Chromium, the `ACK` releasing the next frame, and a tap that reports an editable focus. Run 2026-09-29 against `34.132.106.149`, from another machine and from inside the container |
| No mechanical defect of the seventeen checked kinds | `node tools/check-vb.mjs` → `16 check groups run, 0 finding(s)` |
| It compiles, for real, on the phone's toolchain | Six configurations, `BUILD_EXIT=0`: Debug/ARM, Debug/x86, Release/ARM, Release/x86 as solution builds, and Debug/Release as `Any CPU` app-project builds. Only the two deliberate `BC40000` warnings. |
| The IDE's own project system still accepts the solution | `tools\vm-devenv.cmd` in the guest → `seven projects loaded and built`, `Rebuild All: 7 succeeded, 0 failed, 0 skipped`, `DEVENV_EXIT=0` |
| The deployment is reachable from the Internet and accepted by a client that VALIDATES the certificate | From another machine, no tunnel: TLS 1.3 `ecdsa_secp256r1_sha256`, `Verification: OK`, `openssl x509 -checkip 34.132.106.149` → `does match certificate`, and `bin/bfwp-smoke.js --verify` → `10/10` (Round 16) |
| The certificate renews itself with nobody watching | `certbot renew --force-renewal` on the host → the deploy hook staged the pair, restarted the service and waited for healthy; the server then served serial `053F4D02...`; `certbot-renew.timer` is `enabled`, next run listed (Round 16) |

**Three defects this round found**, two by the compiler and one by reading the
file being edited. All three had passed every checker in the repository.

*Found by the guest build:*

- `RemoteServers` and `RemoteServerSettings` are declared in
  `BrowserForWP.Core.**Remote**`, while the wire format is in
  `BrowserForWP.Core.Engine.**Remote**`. Both files sit in the same folder,
  `BrowserForWP.Core/Engine/Remote/`, so `Imports BrowserForWP.Core.Engine.Remote`
  looked right and `tools/check-vb.mjs` group 5 accepted it — that namespace does
  exist, it is simply not the one the type is in. BC30451 plus BC30002, twice.
- `DisplayInformation.ResolutionScale` is **obsolete on Windows Phone** and
  "can return incorrect results"; the phone's own compiler says so, in Italian, in
  `BC40019`, and names the replacement (`RawPixelsPerViewPixel`). It is a
  deprecation warning about the exact value that sizes the picture on the glass,
  and it arrived as a warning in an otherwise green build.

*Found by reading `MainPage.xaml.vb`, and it is the worst of the three:*

- `OnNavigatedTo` applied the localized strings BEFORE it chose an engine, and
  `ApplyLocalizedStrings` reads `_engine.Capabilities`. On a first launch
  `_engine` is `Nothing`, so the shell dereferenced it and threw inside
  `OnNavigatedTo`, where nothing catches: **the app crashed on start.** It has
  been that way since the engine became a choice, and every table in this file
  that says "not run on a handset" is why nobody noticed. No compiler rejects it
  and no checker here could see it. The fix is the order of two adjacent lines,
  with the reason written where the lines are.

**Not verified, and not claimed:** everything that needs a handset or a server.
That includes the whole of `Rendering/RemoteScreen.vb` — the tile decode, the
1/dpr scale, tap and scroll mapping, and the hidden `TextBox` that owns the soft
keyboard — and the audio path, whose server half does not exist yet either (the
capture end needs a sound card; see the notes in `Docker-BrowserForWP`).

## Release checklist

- [ ] `node tools/gen-vectors.mjs` → `53 assertions, 0 failure(s)`
- [ ] `python3 tools/make_logo.py` → 12 PNGs, all `*.scale-100` / `*.scale-240`, no git diff
- [ ] Polyfill ES5 check passes
- [ ] `node tools/proto/core-logic.mjs` → `core-logic checks, 0 failure(s)`
- [ ] `tools\vm-build.cmd /t:Rebuild` in the guest → `BUILD_EXIT=0`, no `BC`
      errors, no warnings
- [ ] `tools\vm-devenv.cmd` in the guest → `DEVENV_EXIT=0`, seven projects
      loaded, no `not installed` line
- [ ] `node tools/proto/w25519.mjs` → `18 checks, 0 failure(s)`
- [ ] `node tools/proto/tls13.mjs example.com` → `53 checks, 0 failure(s)`
- [ ] `node tools/proto/tls13.mjs 34.132.106.149 8443 --handshake-only` →
      `50 checks, 0 failure(s)`, including `the certificate is for this host`
      (needs the deployment up; the 15 offline host-matching cases and the 6 source
      contracts run either way)
- [ ] `RUNS=4 bash tools/wmc9999-probe.sh` → `distinct XBF hash pairs across 12 runs: 1`
- [ ] Handset: TLS probe reports `TLS 1.3`
- [ ] Handset: switch the phone to Italian — **every** UI string changes; no
      English leaking through
- [ ] `README.md` and `README.it.md` still agree on the platform-limitation
      section
- [ ] `Package.appxmanifest` version bumped
- [ ] Everything committed and pushed

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `PermissionError` from `make_logo.py` | The project tree is owned by another user: `sudo chown -R "$(whoami)" BrowserForWP BrowserForWP.sln` |
| `git status` is clean but the tree is full, or a build output cannot be deleted | The guest writes through the shared folder **as root**, so `bin/`, `obj/`, `AppPackages/`, `BundleArtifacts/` and everything `devenv` leaves behind are root-owned and only `sudo` can remove them. None of it is source and no tracked file is root-owned. One line, from the repository root: `sudo rm -rf "Visual Studio 2013" "Visual Studio 2013Templates" BrowserForWP/AppPackages BrowserForWP/BundleArtifacts */bin */obj tests/*/bin tests/*/obj BrowserForWP/*.vbproj.user` — the two `Visual Studio 2013…` directories are what a `devenv` run leaves at the repository root (one empty `Backup Files/<solution>/` per solution opened, plus a copy of the IDE's templates), they are ignored by `.gitignore` and they reappear on the next measuring run. `sudo chown -R "$(whoami)" .` also works and saves the sudo on future deletions, but the next guest build re-creates root files. |
| `gen-vectors.mjs` exits 1 | An algorithm and an RFC constant disagree. The failing line prints both values — fix the algorithm, not the constant. |
| TLS handshake fails on every site | Check the `supported_versions` and `key_share` extensions in `ClientHelloBuilder`; a malformed extension makes servers close the connection immediately. |
| Site fails only on the handset | Run the compatibility probe. It is almost always a missing script feature, not a transport problem. |
| UI shows English on an Italian phone | `Localizer.Initialize()` was not called in `App.OnLaunched` before the frame was created. |
