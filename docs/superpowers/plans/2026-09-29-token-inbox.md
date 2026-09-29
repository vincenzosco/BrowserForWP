# A page the phone serves, so a token can be pasted instead of typed

Status: done. Client (`BrowserForWP`), one feature; plus one small server change
(where a revocation request goes). Committed as one client commit; the server half
went in as its own commit in `Docker-BrowserForWP`.

## What was asked, in the user's words

"Fai in modo che l'app hosta una piccola pagina html per incollare con più
semplicità il token." And, from the questions: the app shows a **Wi-Fi address**,
you go there, put the token in a box and say whether it is **server 1 or server 2**
(optional); and when the token arrives the app must **fill the address too and
switch to *Server (Chromium remoto)***, so that after the paste there is nothing
left to do. Separately, on the server: **to remove a token, open an issue on
GitHub**.

## The problem this solves

A device token is 43 characters of base64url. It is minted by the server, it is
shown once, and the place it has to end up is a text field on a phone whose
keyboard is a soft keyboard from 2014. Pasting it on the handset means the token
has to be on the handset first, and the handset is where it was minted *from* a
browser that cannot paste into another app. The one device in the room that can
paste comfortably is the computer, so the token goes to the computer and the phone
comes to fetch it.

## Decisions, and what each one costs

1. **The listener lives only while the Settings screen is open.** It starts when
   the button is pressed, and it stops when the screen closes, when Stop is
   pressed, or after five wrong codes. The exposure window is the minute a person
   is looking at the address, and it is not a port that is open all afternoon.
2. **A four-digit code, shown on the same screen, is required by the form.** It is
   the same idea as the server's own access code, and it is what makes a stranger
   on the same Wi-Fi unable to point this phone at a server of their choosing.
   Four digits are typable on a keyboard; the code is what the *computer* types,
   once.
3. **Port 8777, and the first free port after it** (8777..8781). A fixed port is a
   URL a person can type; a *guessed* free port keeps a second attempt from failing
   with `EADDRINUSE` and nothing to say about it. The screen shows the port that
   actually bound.
4. **The address is chosen by a rule, not by `GetHostNames()[0]`**: the phone's own
   interfaces include loopback and link-local ones, and the page's URL has to be
   the address a computer on the same network can reach.
5. **The token is never echoed back.** The reply says which slot was written and
   ends with the last four characters. The page is plain HTTP inside the LAN, so
   sending the secret back over it is exactly what not to do.
6. **Plain HTTP on the local network is stated, not implied.** The token travels in
   the clear inside the network, to a phone that has no certificate for a name
   nothing resolves. That is the honest description of this feature, and the docs
   say it in those words.

## What each side gets

* `BrowserForWP.Core/Engine/Remote/TokenInbox.vb` — the rules, pure: form decoding,
  which address to advertise, what a token looks like, which slot the text names,
  and `Review(...)` returning a verdict carrying a **resource key** and a slot. No
  socket, no HTML, no prose: same shape as `RemoteServers`.
* `BrowserForWP/Engine/TokenPage.vb` — the shell: a `StreamSocketListener`, a
  minimal HTTP reader, and the HTML. Every sentence comes from `Localizer`, every
  decision from `TokenInbox`.
* `BrowserForWP/MainPage.xaml` / `.xaml.vb` — the Settings screen: a button that
  starts and stops it, the URL, the code, a status line, and the code that applies
  what `Review` approved (token and address into the chosen slot, engine to
  `Remote`, the settings screen refreshed, the page navigated).
* The server: `BFWP_ISSUES_URL` (default this repository's issues page) and the
  sentences that say a token is removed by opening an issue there.

## Verification

* `tools/proto/token-inbox.mjs` — a new referee: the decoding, the address rule,
  the token shape, the slot names and `Review` in all its refusals, plus source
  contracts (no HTML in Core, `no-store` and the CSP header present in the shell,
  the five-failure stop, the token never echoed).
* `tools/check-vb.mjs` — a new entry in `CAPABILITY_REQUIREMENTS` (a
  `StreamSocketListener` needs `internetClientServer`, which the manifest already
  declares), and a new group that checks every `Localizer.Get("literal")` key
  really exists in the `.resw` pair. That gap is this feature's risk: twelve new
  strings, and a mistyped key shows its own name on the screen.
* The four guest build configurations (`tools/vm-build.cmd`), because none of this
  can run on macOS and the compiler is the only thing here that reads VB.
* Server: `npm test`, and the deployment is checked by hand from the phone and from
  a computer on the same Wi-Fi — which is the one measurement this repository
  cannot make for itself, and it goes in MAINTAINING as a blank row until somebody
  makes it.

## Outcome (recorded, not rewritten)

What shipped, and what the verification actually said:

* `token-inbox.mjs` **86/86** (71 rule checks + 15 shell/source contracts), every
  other referee unchanged and green.
* `check-vb.mjs` **18 groups / 18 categories / 0 finding(s)**. The two groups added
  here found two real defects in files that predate the round: `X25519.vb` has
  `Dim carry` inside the method `Carry`, and `HttpClient13.vb` has `Dim port` beside
  a `Port` property. Both were renamed.
* The guest build is what found the three real errors of this round, and
  `check-vb.mjs` found none of them: a one-sided `ReadOnly Property` (VB 14, so
  `BC30126` and then a `BC30634` on every following line), a `Dim body` that
  shadowed the class's own `Body` method for the whole method, and
  `Dispatcher.BeginInvoke` where the WinRT `CoreDispatcher` has `RunAsync`. The
  first and the third are checks now.
* All four client configurations `BUILD_EXIT=0` — Debug/ARM, Release/ARM,
  Debug/x86, Release/x86.
* Server: `npm test` **204 pass / 0 fail**, and the live page at
  `https://34.132.106.149/` carries the removal link, checked with `curl`.

Left blank, because they need a handset and a second computer on the same Wi-Fi:
whether the listener binds, what the address and the code look like on the screen,
the form in a real browser, and the engine actually switching to *Server* after a
submit. That is one row of "The remote engine, verified by hand" in
`docs/MAINTAINING.md`, and it stays blank.

The `internetClientServer` capability was already declared — `StreamSocketListener`
is one of the APIs that needs it — and `check-vb.mjs` now asserts the pairing
rather than leaving it to be remembered.
