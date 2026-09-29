# BrowserForWP — Architecture

## The five platform laws

Every design decision in this repository follows from five facts about
Windows Phone 8.1. Each was verified, not assumed. If you are about to write
code that contradicts one of them, stop — the platform will not honour it.

Law 4 was added on 2026-09-28, after Law 1 was reached a third time by a
different route ("escape the sandbox when a request arrives"). Law 5 was added on
2026-09-29, when the on-device renderer was deleted and an optional remote engine
replaced it. Plans written before those dates say "the three platform laws" and
are dated records, not errors.

### Law 1 — The rendering engine is Trident (IE11) and cannot be replaced

`Windows.UI.Xaml.Controls.WebView` on WP8.1 is bound to the OS's Trident
engine. There is no API to substitute it, and no Chromium or Gecko binary
exists for WinRT-ARM 8.1:

| Engine | Status on Windows Phone 8.1 |
| --- | --- |
| Chromium / Blink | No platform port exists. AppContainers cannot host a sandboxed multi-process renderer. |
| Gecko (Firefox) | Mozilla cancelled Firefox for Windows Phone in 2015. No binary ever shipped. |
| EdgeHTML / Chromium-Edge | Windows 10 Mobile only; cannot run on WP8.1. |
| Trident (IE11) | **The only engine available.** |

**Consequence:** web *rendering* is capped at IE11. This is why
`IBrowserEngine` exists — it makes the cap a configuration detail instead of an
assumption baked into every call site.

**That claim is true of behaviour, and the cap has a way around it that is not an
escape.** There are two implementations of the seam: the system `WebView`, and
`BrowserForWP/Engine/RemoteEngine.vb`, which draws the page with Chromium on a
server somebody has to run — Law 5 is about what that costs. Between Round 8 and
Round 10 there was a third, this repository's own on-device renderer, and it was
deleted on purpose rather than because it did not work. What the shell *does*
still branches only on `EngineCapabilities` — including whether the engine has a
script host at all — but `MainPage` wires each engine's own events, so it knows
the types at exactly one site. Putting the lifecycle on the interface would remove
that, and it is deferred rather than done (`docs/MAINTAINING.md`, deferred item 11)
so that the sentence above is not left standing on a claim the code no longer
earns.

### Law 2 — The OS offers TLS 1.2 at most

Schannel on WP8.1 negotiates TLS 1.0/1.1/1.2. There is no API to raise the
ceiling, and no system setting that enables 1.3.

**Consequence:** the *system* `WebView` can never use TLS 1.3. The application's
*own* transport can, because it does not go through Schannel at all — it
implements TLS 1.3 (RFC 8446) in managed code over a raw
`Windows.Networking.Sockets.StreamSocket`. That is `BrowserForWP.Net`.

### Law 3 — AppContainers block loopback

Windows AppContainers block traffic to `127.0.0.1` by default. This is
documented behaviour intended to stop an app from talking to a server on the
device.

**Consequence:** the classic architecture — run a local TLS-terminating proxy on
loopback and point the `WebView` at it — **does not work here**. That design was
evaluated and rejected. Anyone proposing "let's just proxy it locally" is
proposing something the OS will refuse.

### Law 4 — An app cannot leave its AppContainer

The sandbox is not a wall an application climbs at runtime. It is the identity of
the process, fixed by whoever created it, and no version of Windows offers an
operation called "leave the sandbox". So the shape *"the app starts sandboxed
and steps outside when a request arrives"* is not a technique this platform
blocks — it is a technique that does not exist anywhere.

| Lever such a plan needs | Why it is absent |
| --- | --- |
| Stop being an AppContainer process | **No self-de-sandboxing API.** Container membership lives in the process token and is set by the parent at creation; nothing in WinRT changes it, and WP8.1's profile exposes no process creation at all. There is no JIT to unlock either: an AppContainer denies writable+executable pages, and the `NETFX_CORE` profile has no `Reflection.Emit`. |
| Hand the work to a free helper process | **A child of an AppContainer process is created in the same container.** A helper that is not in the container has to be launched by a full-trust parent, which the app is not. |
| Declare the privilege in the manifest | **Capabilities grant resources, never memory policy.** Privilege is declared at *package* time in `Package.appxmanifest`, reviewed at publish. There is no 8.1 capability meaning "may create executable pages"; this package declares `internetClientServer` and nothing else. |
| Ask a full-trust service to do it | **Broker contracts exist to perform specified operations.** An `AppServiceConnection` does a defined job for an app; it does not hand over a DOM, a renderer or memory. Microsoft defines the operations, so an engine cannot be requested. |

**This is also why an SDK update could not have delivered it.** The SDK decides
what you compile against; the kernel and the AppContainer process policy decide
what the process may do. A patched SDK can give you an API that links and dies at
runtime, and the device's firmware is not ours to change.

Where a third-party engine *is* obtainable, it is obtainable as a
**deployment-time decision, not a request-time escape**:

| Route to a third-party engine | Windows Phone 8.1 | Windows 10 desktop | Windows 10 Mobile |
| --- | --- | --- | --- |
| Leave the sandbox when a request arrives | Does not exist | Does not exist | Does not exist |
| Ask a full-trust broker for it | No such broker | Platform-defined operations only | No such broker |
| Be full-trust from the start | No: no EXE deployment on a phone | **Yes** — desktop bridge / `runFullTrust`; shipping CEF or WebView2 is routine | No |
| Ship the platform's engine | Trident, in `WebView` | EdgeHTML, then WebView2 (Chromium) | EdgeHTML |

**Consequence:** there is no way to make THIS DEVICE draw a page with an engine
other than Trident. A tokenizer, cascade, layout and painter in managed code was
built here (Round 7) precisely to test that, and it was deleted in Round 10: it
was a smaller thing than a browser, and a page that needs a modern engine is
better served by a machine that has one — which is Law 5, and which is not an
escape either, because the page is drawn somewhere else rather than the process
leaving. On Windows 10 *desktop* a modern engine is a different project on a
different OS, reached not by escaping anything but by targeting the platform
where third-party engines were never sandboxed. Plainly: *"we could have
Chromium"* is a statement about the operating system, not about a capability to
request.

### Law 5 — A remote renderer is a different browser, not a bigger one

The remote engine does not lift the platform's ceiling. It moves the ceiling to
somebody else's machine, and it changes what the browser IS:

- The operator of the server can read every page, including passwords. This is
  not a flaw; it is the architecture. It is also why the disclosure is not a
  sentence in a README but three things a person can act on: the switch in
  Settings, the address field beside it, and a status line that names the engine
  that drew the page. **This build ships with the hosted engine as its default**
  -- `AppSettings.DefaultHostedUrl` is the project's own server -- so the question
  is live from the first page rather than from whenever somebody finds a settings
  screen.
- A default is not a promise that the server exists, so the rule is that wanting
  the hosted engine is not having it. `EngineChoice.Decide` asks
  `RemoteServers.Ready` first: an address, a device token and the switch on. A
  fresh install has the address and no token, because a token is issued per device
  by the server and pasted in by hand, so its first page is drawn **on the phone**
  with the reason on screen. If the server stops answering, the same rule hands the
  page to the on-device engine and says so in the status line -- a browser that
  renders nothing is not a browser, and a silent change of engine would be worse
  than either.
- The device holds no page. No script runs locally, so Find, Reading mode and
  night mode are Trident features and are disabled on this engine rather than
  pretending to work.
- **The input path is split down the middle, and which half is whose matters.**
  The keyboard is the phone's: a 1x1 transparent `TextBox` owns the soft keyboard
  and empties itself into `TEXT` and `KEY` messages. The field is the server's:
  the phone cannot see a caret, cannot prefill, and cannot know whether a tap
  landed on an input at all, so the keyboard comes up on every tap and the keys a
  soft keyboard has no way to send (Tab, Escape, the arrows) are a bar in the
  shell. Nothing here mirrors the page's field, and nothing pretends to.
- The network becomes load-bearing in a way it was not: a page is only as fast as
  the link, and a dropped connection loses the page.
- On-device rendering is NOT deleted because it is worse. It is deleted because
  it is a smaller thing than a browser, and maintaining two renderers to prove
  that was the wrong trade.

**What this does not change.** Law 1 still holds for the pages this device draws
itself. The two engines share the seam, the tab model, the history store and the
address bar — `IBrowserEngine` was built for exactly this, and adding this engine
changed none of them. What it did *not* keep identical is the shell's own wiring:
`MainPage` subscribes to each engine's events itself (`NavigationStarting` on the
`WebView`, `Navigated` and `Audio` on this one), and it disables the three
Trident-only buttons when the engine is remote. That is deferred item 11, unchanged
by this round. The engine is a settings choice, and
`docs/MAINTAINING.md` records the hand-verification table for it — which is empty,
because no handset and no server were available to the person who wrote it.

## What that means for "modern"

The honest summary: **modern transport, modern compatibility layer, unchanged
renderer.**

| Layer | Status |
| --- | --- |
| Transport (the wire) | **Modern.** TLS 1.3, X25519, ChaCha20-Poly1305, DNS-over-HTTPS, all on-device. |
| Compatibility (what pages can run) | **Improved.** An injected ES5 shim raises the floor for modern pages. |
| Rendering (how it looks) | **Capped at IE11 on the device.** The hosted engine draws with a real Chromium, and that is not addressed by escaping anything: it moves the page to a machine somebody runs, and it is not free. See Law 5. |

The compatibility probe (`BrowserForWP.Core/Diagnostics`) exists so that the
remaining gap is *reported*, not mysterious.

## Layers

```
BrowserForWP              (app)      XAML shell, assets, UI strings
    |
    +-- BrowserForWP.Core (core)     IBrowserEngine, tabs, history, address bar
    +-- BrowserForWP.Net  (net)      TLS 1.3, DoH, HTTP client
    +-- BrowserForWP.Crypto(crypto)  HKDF, X25519, ChaCha20-Poly1305, AES-GCM
    +-- BrowserForWP.Localization    language resolution + string lookup
    +-- BrowserForWP.Polyfill        injected ES5 compatibility layer
```

**Dependency direction is strictly downward and one-way:**

- `Crypto` depends on nothing. It is pure algorithms.
- `Net` depends on `Crypto`. It never touches XAML.
- `Core` depends on **neither** `Net` nor `Crypto`. It knows about documents,
  history and engines, not about key schedules.
- `Localization` depends on nothing but the WinRT globalization APIs.
- The app depends on all of them.

A change that adds a dependency pointing upward is a design bug. The value of
the split is that `Crypto` can be fully verified off-device (see
`tools/gen-vectors.mjs`) and `Core` can be unit-tested without a network.

## The engine seam

```vb
Public Interface IBrowserEngine
    ReadOnly Property Capabilities As EngineCapabilities
    ReadOnly Property Source As Object
    Sub Navigate(url As String)
    Sub GoBack() : Sub GoForward() : Sub Reload() : Sub [Stop]()
    Function InvokeScriptAsync(script As String) As Task(Of String)
End Interface
```

`EngineCapabilities` is not decoration. `TridentEngine` reports:

```vb
.SupportsTls13 = False            ' Law 2
.SupportsModernJavaScript = False ' IE11 has no ES6+
.SupportsFetch = False
```

so the UI can warn accurately rather than silently degrade. A future
`WebView2Engine` (Chromium, Windows 10+) or `GeckoViewEngine` (Android) reports
the truth for its platform and **nothing above this interface changes**.

## TLS 1.3 design

Implemented from the RFCs, in five pieces:

1. **`Hkdf`** — RFC 5869 Extract/Expand, plus the RFC 8446 §7.1 `HkdfLabel`
   framing (`BuildLabelInfo`) and `DeriveSecret`.
2. **`X25519`** — RFC 7748 Montgomery ladder over `2^255 - 19`, hand-rolled
   because WinRT 8.1 exposes no X25519 primitive and group `x25519` is mandatory
   for TLS 1.3.

   Field arithmetic uses **radix 2^16 (16 limbs) in `Int64`**. A power-of-two
   radix is required, not merely convenient: it is the only representation where
   limb weights add exactly, `w(i+j) = w(i) + w(j)`. The widely used radix-2^25.5
   does not have that property (`w(1)+w(1) = 52` while `w(2) = 51`), and
   reconstructing its alternating half-limb bookkeeping from memory produced
   wrong-but-plausible output when first attempted here. With 2^16 limbs there
   are exactly two fold factors, both derivable from `p = 2^255 - 19`: products
   above limb 15 fold down with **38**, and limb 15's top bit folds with **19**.

   `BigInteger` is deliberately avoided. Its presence in the ".NET for Windows
   Store apps" profile could not be confirmed, and betting the crypto layer on an
   unverifiable platform dependency is not acceptable when the alternative is
   arithmetic that can be proven.

   **This implementation is not constant-time.** The carry chain branches and the
   ladder's conditional swap is a real branch, so an attacker able to measure the
   handset's timing may learn something. Documented rather than left implicit.

3. **No managed AEAD at all.** BrowserForWP offers exactly one TLS 1.3 cipher
   suite: `TLS_AES_128_GCM_SHA256`. RFC 8446 §9.1 makes that suite
   mandatory-to-implement, so a single-suite offer costs **no interoperability**.
   That decision removed the need for a hand-written ChaCha20-Poly1305 entirely —
   it was a performance optimisation for hardware without AES acceleration, and
   this handset's AES path is hardware-backed. It was deleted rather than left as
   dead code carrying real risk.
4. **`AesGcm`** — a thin adapter over the platform provider, because managed
   AES-GCM is unacceptably slow on 2014 ARM silicon. It owns the `AeadResult`
   type and is the only AEAD the record layer ever calls.
5. **`KeySchedule`** — the RFC 8446 §7.1 chain:
   `early → derived → handshake → derived → master`, plus
   `c hs traffic / s hs traffic / c ap traffic / s ap traffic` and the finished
   keys.

### Why the transcript ordering matters

Two bugs are unusually easy to introduce here and both are silent:

- The **Finished** MAC is computed over the transcript hash of everything
  *before* the Finished, not after.
- The **application traffic secrets** are derived over the transcript hash that
  *includes* the Finished.

`tools/gen-vectors.mjs` asserts both orderings against RFC 8448 §3, using the
raw handshake messages rather than published hashes, so a regression in either
direction fails the build.

## The crypto API surface on WP8.1 WinRT — read this before editing Crypto

A WP8.1 WinRT app (`NETFX_CORE`) compiles against the ".NET for Windows Store
apps" profile, which **strips the classic managed crypto types**. None of these
resolve:

| Type you might reach for | Status | Use instead |
| --- | --- | --- |
| `System.Security.Cryptography.SHA256` / `SHA256Managed` | absent | `HashAlgorithmProvider` + `HashAlgorithmNames.Sha256` |
| `System.Security.Cryptography.HMACSHA256` | absent | `MacAlgorithmProvider` + `CryptographicEngine.Sign` |
| `System.Security.Cryptography.RNGCryptoServiceProvider` | absent | `CryptographicBuffer.GenerateRandom` |
| `System.Security.Cryptography.AesGcm` | absent | `CryptographicEngine.EncryptAndAuthenticate` |

`SHA256` derives from `HashAlgorithm`, and `HMACSHA256` and
`RNGCryptoServiceProvider` derive from the same stripped namespace, so the whole
family fails together. This was confirmed against Microsoft's documentation and
against the reported symptom ("Cannot find type
System.Security.Cryptography.SHA256 on Windows Phone 8.1").

`System.Text.RegularExpressions.RegexOptions.Compiled` is likewise
**unsupported** — runtime regex code generation does not exist in this profile.
It is omitted deliberately in `AddressNormalizer`.

Every platform-specific call is funnelled through
`BrowserForWP.Crypto/WinRtCrypto.vb`, so the rest of the library stays portable
and reviewable.

## Verification strategy

The Windows Phone 8.1 SDK is Windows-only, so the crypto cannot be compiled on
macOS or Linux. Rather than accept "untested", the algorithms are verified in a
language-independent way:

`tools/gen-vectors.mjs` recomputes every value from first principles using
Node's crypto, **asserts it against the constant published in the RFC**, and only
then emits the VB test data. Current status: **52 assertions, 0 failures**,
covering RFC 5869 A.1–A.3, RFC 7748 §5.2/§6.1, RFC 8439 §2.8.2, RFC 8448 §3 and
NIST CAVS AES-GCM.

This means a wrong reduction or a wrong counter increment is caught before the
code ever reaches a handset, and the VB tests are grounded in values that were
independently checked rather than hand-copied.

### The X25519 prototype — why there are two verifications

`tools/gen-vectors.mjs` verifies X25519 *as an algorithm*, using Node's own
crypto as the oracle. It cannot verify the limb arithmetic, because VB does not
run off-Windows.

`tools/proto/w25519.mjs` closes that gap. It is a **line-for-line prototype of
`X25519.vb`** — same limb layout, same fold factors, same carry chain, same
encode/decode — and it is executed. It reproduces every RFC 7748 and RFC 8448
vector, cross-checks add/sub/mul/sq/invert against `BigInt` over 66 randomised
cases, and round-trips encode/decode. It also **range-asserts every intermediate
against `Int64`**, so a value that would overflow on the handset fails here
instead of wrapping silently.

```bash
node tools/proto/w25519.mjs    # must print: 18 checks, 0 failure(s)
```

### The TLS 1.3 prototype — the strongest check in this repo

`tools/gen-vectors.mjs` proves the *primitives* are right. It cannot prove the
*protocol* is right, and a self-consistency test cannot either: a TLS client that
is wrong in the same way when sealing and opening will round-trip its own
records perfectly and still be unable to talk to any real server.

So `tools/proto/tls13.mjs` is a **complete TLS 1.3 client and HTTP/1.1 client**,
written from the RFCs, that **completes real handshakes with real servers**:

```bash
node tools/proto/tls13.mjs example.com    # must print: 31 checks, 0 failure(s)
node tools/proto/tls13.mjs www.google.com
node tools/proto/tls13.mjs cloudflare.com
```

It verifies, against live servers, that: the ClientHello is well-formed enough to
be answered; the key schedule derives the keys the server actually used (proven
by successfully decrypting the server's records); the server's `Finished`
verifies; the server accepts **our** `Finished`; and application data flows both
ways and returns a well-formed HTTP status line.

It is the transliteration source for everything under `BrowserForWP.Net/Tls13/`.
Three genuine bugs were found this way and are documented where they live:

| Bug | Symptom | Where |
| --- | --- | --- |
| `TLSInnerPlaintext` written as `type \|\| content` | Dead handshake right after ServerHello, no alert. **Only a live server catches this** — AEAD decryption still succeeds, because the tag covers the ciphertext, not the plaintext's field order. | `TlsRecordLayer.vb` |
| `server_name` missing its `NameType` byte; `key_share` missing the inner 2-byte key length | Server answers with a bare `decode_error` | `ClientHelloBuilder.vb` |
| ALPN read from `ServerHello` | Silently reports "no ALPN" for every server | `ServerMessageParser.vb` |

A fourth, different in kind: the prototype originally advertised `h2` in ALPN
while speaking only HTTP/1.1. Servers obeyed, selected HTTP/2, and answered our
HTTP/1.1 request with `http2_handshake_failed`. RFC 7301 requires a client to be
able to speak every protocol it offers, so the fix was to stop claiming it.

It caught three real bugs during development: a multiples-of-p offset that
reduced to zero, a wrong `a^9` in the inversion chain, and `BB + a24*E` where
RFC 7748 specifies `AA + a24*E`. Each would have produced plausible-looking
output. **If the prototype fails, fix the prototype — never hand-edit
`X25519.vb` to compensate.**
