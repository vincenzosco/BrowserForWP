# BrowserForWP

**A modern-transport browser for Windows Phone 8.1 — the crypto, TLS and DNS are on the handset, and the pages this phone cannot draw are drawn by a hosted server. That server is the default engine, it is one switch away from off, and this page tells you what it costs before you use it.**

[English](README.md) · [Italiano](README.it.md)

---

## Read this first: what is actually possible on Windows Phone 8.1

This project makes an unusually honest promise, so here is the ground truth you
deserve before reading any feature list.

Windows Phone 8.1 is a **closed platform released in 2014 and end-of-lifed in
July 2017**. Its browser stack is **Trident (Internet Explorer 11)**, and the
operating system gives third-party apps **no way to substitute a rendering
engine**. This is a structural limitation of the OS, not a limitation of this
project's ambition.

| Goal | Reality on Windows Phone 8.1 | What BrowserForWP does |
| --- | --- | --- |
| Ship the **Chromium** engine | No build of Chromium/Blink exists for WinRT-ARM 8.1. App containers cannot host a sandboxed multi-process renderer. | Provides a pluggable `IBrowserEngine`. Ships `TridentEngine` on WP8.1; `WebView2Engine` (Chromium) and `GeckoViewEngine` (Firefox) drop in on any platform that has them. Since Round 10 there is a third option that needs no port: the **hosted engine** runs Chromium on a server and sends the picture over the app's own TLS 1.3 channel. It is the engine this build ships set to, and Settings replaces it with any server you run. See *Hosted renderer by default* below, and Law 5 in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). |
| Ship the **Firefox / Gecko** engine | Mozilla cancelled Firefox for Windows Phone in 2015. No binary ever shipped. | Same pluggable abstraction as above. |
| **TLS 1.3** | Schannel on WP8.1 tops out at **TLS 1.2**, and the OS offers no API to raise it. | **Implemented from the RFCs, in managed code, on-device**: a complete TLS 1.3 client (`BrowserForWP.Net`) running over a raw `StreamSocket`, so the app's own network layer speaks TLS 1.3 today. |
| **Modern HTTPS** | The system `WebView` negotiates whatever Schannel supports. | `Tls13Client` + DNS-over-HTTPS resolver + certificate pinning for the app's transport layer. |
| **Modern web pages** | IE11 cannot parse or run modern JavaScript. | An on-device ES5 compatibility bundle (`BrowserForWP.Polyfill`) injected at `DOMContentLoaded` and again on completion, plus a compatibility diagnostic that tells you *why* a given site failed. The bundle raises the floor but cannot parse ES6 syntax or supply `Proxy`/`Intl`/grid — see the disclosure below. |
| **A from-scratch engine** | No new engine can be built *instead of* Trident on this OS, and Trident cannot be re-configured (see [`docs/MAINTAINING.md`](docs/MAINTAINING.md) § *IE-adaptation is closed*). | A **document pipeline** lives in `BrowserForWP.Core/Engine/Native`: it fetches a page over the app's own TLS 1.3 transport — the only path in this product that can load anything above TLS 1.2 — and parses a **declared subset** of HTML and CSS into a box tree, visible under **Diagnostics → Parse current page**. It does **not** execute JavaScript and never will. An on-device *renderer* for that tree was built in Round 7 and **deleted in Round 10**: it was a smaller thing than a browser, and maintaining two renderers to prove that was the wrong trade (Law 5). |
| **Hosted renderer by default** | — | Every component — crypto, TLS, DNS, polyfills, history, localization — runs on the handset. The *rendering* is the exception, and it is the default one: pages go to a server that draws them in Chromium, and **whoever runs that server can read everything you read, passwords included**. The address ships set to this project's own hosted server. You can point it at a server you run, or choose another engine and keep every page on the phone — `docs/ARCHITECTURE.md` Law 5 is the full cost, and Settings states it next to the switch. A device that is not registered yet, or a server that does not answer, draws **no page at all** while the server engine is chosen and says why: choosing the server is a statement about where pages come from, and the app does not answer it with a page from the engine you did not choose. **Configuring the hosted renderer** below is the whole sequence, and **Automatic** is the setting that may render on the phone. |

> **On the on-device loopback proxy idea:** Windows AppContainers block
> `127.0.0.1` traffic by default, so a local proxy cannot feed the system
> `WebView`. That architecture is deliberately **not** used here. See
> [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full constraint analysis.

**Bottom line:** you get a genuinely modern *transport* layer (TLS 1.3, DoH,
pinning) on an unchanged *rendering* layer — because the rendering layer cannot
be changed on this OS. The *content* layer (polyfills) is injected into every
page at `DOMContentLoaded` and again on completion; it raises the floor for
feature-detecting sites but cannot fix ES6 syntax or missing engine features.
The engine abstraction means the day you point this at a device with a real
modern engine, the transport and content layers come with you.

---

## Configuring the hosted renderer

The server is the engine this build ships set to, and it does not quietly fall
back: while **Settings → Rendering engine** says **Server (Chromium remotely)**, a
page is drawn by the server or not at all, and the status line says which. Two
halves have to meet — a server that answers, and a phone registered with it — and
this is the whole sequence.

### 1. Run the server

The server is a separate repository,
[`Docker-BrowserForWP`](https://github.com/vincenzosco/Docker-BrowserForWP): a
TLS 1.3 listener in front of a real Chromium. The phone never fetches the page —
it sends the url and receives JPEG frames — so nothing of the page, its scripts or
its cookies is on the device.
[`docs/DEPLOY.md`](https://github.com/vincenzosco/Docker-BrowserForWP/blob/main/docs/DEPLOY.md)
there is the full walkthrough for a host of your own; in short:

```bash
git clone https://github.com/vincenzosco/Docker-BrowserForWP
cd Docker-BrowserForWP

# A real certificate in ./tls: the phone validates the chain and the name (or the
# address) before it sends a byte, so a self-signed one is refused.
export BFWP_PUBLIC_URL=https://render.example.com:8443     # YOUR address
docker compose up -d --build
```

Three things that fail only on the phone, and so are worth checking first:

- **Port 8443 has to be reachable.** That is the render channel (`BFWP_PORT`,
  default `8443`); open it in the host's firewall.
- **The certificate has to validate on the handset**: the chain *and* the name or
  address the phone was given. With no domain, Let's Encrypt issues for a bare
  address under its six-day `shortlived` profile, and `docs/DEPLOY.md` installs
  the renewal timer such a certificate cannot outlive.
- **`BFWP_MAX_SESSIONS` is a memory ceiling.** One live page measured ~231 MiB;
  the compose default of 16 assumes 2.5 GB and up.

### 2. Register the phone, and keep the token

Every device needs a token of its own, and the server prints it **once**:

```bash
docker compose exec render bin/bfwp-device.sh add "my phone"
```

It prints a device id and a token; only the token's SHA-256 is stored, so it
cannot be read back later (run `add` again if it is lost). The subcommands
`list`, `disable <id>`, `enable <id>` and `remove <id>` manage the rest, and no
restart is needed: the server re-reads the registry when the file changes. The
wrapper and not `node bin/bfwp-device.js`: the image starts as root, and a
registry written as root is one the server itself cannot read.

### 3. Point the phone at it

On the handset, **Settings → Server**:

| Field | What goes in it |
| --- | --- |
| *Server address* | The address the certificate is for, scheme included: `https://render.example.com`, or `https://203.0.113.9` for an address certificate. No port is needed — the render channel's own port (8443) is added unless a url spells out another one. |
| *Device token* | The token from step 2. |
| *Draw pages on the server* | On. |
| *Backup server address / token* (optional) | A second server, tried only when the primary does not answer. It borrows the primary's token when its own is empty, so one registered device can cover both. |

Then **Settings → Rendering engine → Server (Chromium remotely)**, which is what
this build ships with. The line under the picker states the decision: *"Chosen in
Settings: the server you configured draws these pages…"* once the address, the
token and the switch agree, or *"The hosted server is not ready: it needs an
address and this device's token"* until they do.

### 4. What to expect, including when it does not work

- **Nothing is drawn on the device while the server engine is chosen.** An
  unconfigured or unreachable server leaves the page area empty, with the reason
  on screen. That is deliberate: falling back would quietly change who draws your
  page, under a setting that says otherwise.
- **To read pages on the phone instead**, pick **System WebView (Trident)** or
  **Automatic** in the same picker. *Automatic* draws on the phone and moves to
  the server only for pages its compatibility probe says Trident cannot cope
  with; it is also the only setting that may fall back to the phone when a server
  stops answering.
- **To test a server before a phone is involved**: in the server repository,
  `node bin/bfwp-smoke.js --host <host> --port 8443 --device <id> --token <token> --verify`
  prints `10/10` when the whole path works — TLS 1.3, a page drawn by Chromium, a
  tap that reaches it.
- **What it costs, again**: TLS terminates on that machine, so the pages and the
  passwords in them are plaintext in its memory. That is what remote rendering is.
  The switch, the address and the status line are the three things you can act on.

---

## Features

- **Full browser shell** — address bar, back / forward / reload / stop, tabs,
  progress indicator, per-site security readout.
- **Smart address bar** — normalises input, distinguishes URLs from search
  queries, restores missing schemes, and refuses unsafe schemes.
- **TLS 1.3 transport (RFC 8446)** — managed implementation over
  `Windows.Networking.Sockets.StreamSocket`.
- **DNS over HTTPS (RFC 8484)** — resolves hostnames off the wire, so a stale
  or hijacked local resolver cannot break or redirect you.
- **Certificate pinning** — user-managed per-site pins with explicit,
  reversible override (remove the pin to undo it). The pin is checked against
  the leaf's SPKI when the app's own TLS 1.3 transport connects. The `WebView`'s
  traffic rides Schannel, whose validation this app cannot hook. So a pin
  protects the app's transport layer — which includes pages fetched by the
  diagnostics parser — but it does not pin the pages you view in the `WebView`,
  and it does not pin the **remote engine's** render channel either: `Tls13Client`
  takes a host and no pin table, so a pinned host is still fetched by the
diagnostics path and unpinned by the render path. That gap is recorded in
  [`docs/MAINTAINING.md`](docs/MAINTAINING.md) rather than left to be discovered.
- **ES5 compatibility bundle** (`BrowserForWP.Polyfill/compat.js`) — written,
  ES5-checked, packaged, and injected at `DOMContentLoaded` and on completion
  via `TridentEngine.InjectPolyfillAsync`. Raises the floor; cannot parse ES6
  syntax or supply `Proxy`/`Intl`/grid. See the disclosure below.
- **Bilingual UI** — English and Italian, auto-selected from the phone's
  display language, with per-app override.
- **Diagnostics** — a built-in probe that reports exactly which modern feature
  caused a page to fail, so the limitation is visible rather than mysterious.

## Repository layout

```
BrowserForWP/
├── BrowserForWP.sln            Visual Studio 2013+ solution
├── BrowserForWP/              Windows Phone 8.1 app (VB.NET / WinRT / XAML)
│   ├── MainPage.xaml(.vb)     Browser shell UI
│   ├── Assets/                Logo, tiles, splash (generated)
│   └── Strings/               en-US / it-IT UI resources
├── BrowserForWP.Core/         Engine abstraction, tabs, history, address bar
├── BrowserForWP.Net/          TLS 1.3, DoH, HTTP/1.1 client
├── BrowserForWP.Crypto/       HKDF, X25519, AES-128-GCM
├── BrowserForWP.Localization/ Language resolution + string lookup
├── BrowserForWP.Polyfill/     On-device JS compatibility bundle
├── docs/
│   ├── ARCHITECTURE.md        Design + platform constraint analysis
│   ├── MAINTAINING.md         How to build, run, and extend the project
│   └── superpowers/plans/     Implementation plans (one per feature)
├── tests/                     Unit tests (run in Visual Studio)
├── tools/
│   ├── make_logo.py           Regenerates every image asset from SVG
│   └── gen-vectors.mjs        Emits RFC known-answer vectors for the tests
└── .agents/skills/browserforwp/SKILL.md
                               Project skill: plan → commit → push → extend
```

## Building

Requirements: **Visual Studio 2013 Update 4 or later** with the *Windows Phone
8.1 SDK*, on Windows. The solution targets `TargetPlatformVersion 8.1` and
`WindowsPhoneApp`.

```
1. Open BrowserForWP.sln
2. Select a phone target: Debug | ARM  (device) or Debug | x86 (emulator)
3. Deploy to a developer-unlocked handset or the WP8.1 emulator
```

> The rendering engine, crypto and TLS code cannot be exercised on macOS or
> Linux: the WP8.1 SDK is Windows-only. The pure-managed crypto in
> `BrowserForWP.Crypto` is mirrored by `tests/`, which runs in Visual Studio.

Regenerate image assets and test vectors at any time:

```bash
python3 tools/make_logo.py          # rewrites BrowserForWP/Assets/*.png
node tools/gen-vectors.mjs          # rewrites tools/out/*.json
```

## Localization

The app ships **en-US** (default) and **it-IT**. The display language is chosen
in this order:

1. A per-app override, if the user has set one.
2. `Windows.Globalization.ApplicationLanguages.Languages` — the phone's
   ordered display-language list.
3. Fallback: `en-US`.

Adding a language means adding one folder of resources and one entry to the
language table — no code changes. See
[`docs/MAINTAINING.md`](docs/MAINTAINING.md#adding-a-language).

## Contributing

Read [`docs/MAINTAINING.md`](docs/MAINTAINING.md) for the build/test workflow
and [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) before changing the engine or
transport layers. The project skill at
[`.agents/skills/browserforwp/SKILL.md`](.agents/skills/browserforwp/SKILL.md)
describes the required plan → test → commit → push loop.

## License

[MIT](LICENSE) © 2026 vincenzosco

---

## Disclosure: this project is 100% AI-made

Every part of BrowserForWP — the architecture, the TLS 1.3 stack, the crypto,
the UI, the tooling, the documentation and this notice — was written by an AI
coding agent, with a human directing the work and reviewing the result at each
step.

That is a real statement about how much you should trust it, so here is the
honest position rather than a boast:

- **It compiles.** The whole solution builds with MSBuild 12 / Visual Studio 2013
  for `Debug|ARM` and `Release|ARM` inside an ARM64 Windows 11 guest, and
  produces an installable package. Getting there took four rounds; the record of
  every error family — and of the assumptions that were wrong — is in
  [`docs/MAINTAINING.md`](docs/MAINTAINING.md).
- **Off-Windows, `tools/check-vb.mjs` is a filter, not a verdict.** It runs
  twelve categories of mechanical check on any machine and has found genuine
  defects, but it does not type-check. Everything it cannot see has failed in
  the guest while passing here: a `Friend` member used from another assembly, a
  nested class named unqualified from a third file, a local variable shadowing a
  type, and a XAML `{ThemeResource}` key that WP8.1 does not define.
- **The crypto and the TLS 1.3 protocol are verified, but not on a handset.**
  `tools/gen-vectors.mjs` (52 assertions against RFC 5869/7748/8439/8448 and
  NIST AES-GCM), `tools/proto/w25519.mjs` (18 checks) and
  `tools/proto/tls13.mjs` (31 checks, completing real handshakes with Google,
  Cloudflare and example.com) all pass.
- **A second engine exists, and only its front half.**
  `BrowserForWP.Core/Engine/Native` fetches a page over the app's own TLS 1.3
  transport and parses a declared subset of HTML and CSS into a box tree. It does
  **not** execute JavaScript, and never will. There is no layout and no rendering
  yet: the pipeline's output is text, printed under **Diagnostics → Parse current
  page**. Its behaviour is asserted by the prototypes that mirror it
  (`tools/proto/htmlparse.mjs`, `csscascade.mjs`, `boxtree.mjs`) plus the guest
  build; like everything else here, it has never been run on a handset.
- **It has never run on a phone.** XAML layout, WebView behaviour and
  performance on 2014 hardware are unverified. Compiling is not running.
- **The compatibility bundle has limits, and they are architectural.** It is
  injected at `DOMContentLoaded` and again on completion, and it raises the
  floor for feature-detecting sites — but no injected script can parse ES6
  syntax the engine chokes on, or supply `Proxy`, `Intl`, or CSS grid. The
  compatibility probe reports exactly which gap a page hit, and a reading-mode
  fallback plus lite-version redirects cover the rest.
- **An earlier claim in this README was wrong, and here is the correction.** A
  previous revision stated that the app could not be built, because the only
  available Windows VM is ARM64 and Microsoft does not support pre-17.4 Visual
  Studio on Arm processors. That documentation is accurate about the **IDE**; it
  says nothing about the **command-line build**, which works. Nobody had tried
  it. It was tried, and it builds.
- **Claims were tested, and the false ones were dropped.** Chromium and Firefox
  cannot run on this OS and TLS 1.3 cannot be obtained from it; both facts are
  stated plainly rather than papered over. The work that *was* possible — a
  from-scratch TLS 1.3 stack — was done and verified.

Treat this as a well-documented starting point that still needs a real build and
a real device pass, not as a finished, shipped product.
