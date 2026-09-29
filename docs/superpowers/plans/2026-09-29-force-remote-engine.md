# Forcing the server engine, and documenting how to configure it

**Goal:** when the engine chosen is **Server**, no page is ever drawn by the
on-device engine again — the page is drawn by the server or not at all, with the
reason on screen — and both READMEs explain the whole configuration (server,
certificate, port, device token, phone fields) to a reader who has never run it.

**Requested verbatim:** *"forza il render delle pagine su server e non su device, e
spiega all'utente nel readme come configurare tutto"* — plus one decision taken
with the owner when the plan was written: the device engine stays as a **choice**
(System WebView / Automatic), it is never a **fallback** for an explicit Server
choice.

## Global constraints

- **`en-US` is the default and fallback language.** Every new user-visible string
  goes in both `BrowserForWP/Strings/en-US/Resources.resw` and
  `it-IT/Resources.resw`. No hardcoded English in `.vb`.
- **Core holds resource keys, never prose.** `EngineChoice` returns
  `EngineReason…` keys; `MainPage` resolves them through `Localizer`.
- **`tools/proto/engine-choice.mjs` is a transliteration**, so it executes *its
  own* copy of the rule: every change to `Decide` needs a matching source
  contract in that file, and the mutation that breaks the rule must be refused.
- `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb` and
  `tools/proto/core-logic.mjs` are mirrors of each other and must stay in step.
- Verification floor: `node tools/check-vb.mjs` → `16 check groups run, 0
  finding(s)`; every `tools/proto/*.mjs` green; guest `BUILD_EXIT=0` with no
  `error BC` line.
- VB is case-insensitive; doc comments are parsed as XML (escape `<`/`>`).

## The rule, before and after

| Setting | `RemoteServers.Ready` | Measurement | Before | After |
| --- | --- | --- | --- | --- |
| Trident | any | any | Trident | Trident |
| **Remote** | **yes** | any | server | server |
| **Remote** | **no** | any | **Trident**, reason `…NotConfigured` | **server** (draws nothing), reason `…NotConfigured` |
| Auto | yes | no measurement | Trident | Trident |
| Auto | yes | below threshold | Trident | Trident |
| Auto | yes | at/past threshold | server | server |
| Auto | no | at/past threshold | Trident, reason `…NotConfigured` | Trident, reason `…NotConfigured` |

And the shell's announced fallback (`OnRemoteNavigated`, when the server reports
`EngineReasonRemoteNotConfigured` / `…Unreachable`): **before** it handed the page
to the on-device engine whatever the setting said; **after** it does that only
when `EngineChoice.MayFallBackToDevice(setting)` is True — i.e. for Automatic and
for a corrupt/legacy setting, never for an explicit Server.

Why the fallback was the worse answer, which is the argument this round has to
make in the code: a person who picks **Server** has said *where* pages come from.
Substituting the device engine answers a different question, silently, under a
setting that says otherwise — the exact shape of lie this repository keeps
finding. Automatic is the setting that *asks* for whichever engine works, and it
keeps both fallbacks.

## File structure

| File | Action | Why |
| --- | --- | --- |
| `BrowserForWP.Core/Engine/EngineChoice.vb` | modify | `Decide` honours an explicit Remote unconditionally; new `MayFallBackToDevice`; header rewritten |
| `BrowserForWP.Core/Storage/AppSettings.vb` | modify | two doc comments that state the old rule |
| `BrowserForWP/Engine/RemoteEngine.vb` | modify | the `Failed` doc comment that states the old rule |
| `BrowserForWP/MainPage.xaml.vb` | modify | the fallback branch is gated; the forced branch reports why |
| `BrowserForWP/Strings/{en-US,it-IT}/Resources.resw` | modify | new `EngineForcedRemoteNoPage`; `RemoteNotice` no longer promises a device fallback |
| `tools/proto/engine-choice.mjs` | modify | the transliterated rule, the new rows, and the source contracts |
| `tools/proto/core-logic.mjs` | modify | mirror of `CoreLogicTests.vb` |
| `tools/proto/remote-servers.mjs` | modify | one comment that says `Ready` gates every hand-off |
| `tests/BrowserForWP.Core.Tests/CoreLogicTests.vb` | modify | mirror of `core-logic.mjs` |
| `README.md`, `README.it.md` | modify | new "Configuring the hosted renderer" section; the fallback promise corrected |
| `docs/ARCHITECTURE.md` | modify | Law 5's second bullet is the living statement of this rule |
| `docs/MAINTAINING.md` | modify | Round 19; the hand-verification expectations; the engine-choice row |
| `.agents/skills/browserforwp/SKILL.md` | modify | the `engine-choice.mjs` row and the mutation it names |
| `../Docker-BrowserForWP/README.md`, `docs/DEPLOY.md` | modify | the `add` command is wrong (root registry); the client's new behaviour |

---

## Task 1 — Core: an explicit server choice is not rewritten into a page on the device

**Deliverable:** `Decide(Remote, *)` returns `Remote` for every input, and
`MayFallBackToDevice(Remote)` is `False`.

Replace the header paragraph that begins `' The hosted engine is the default this
build ships with` with:

```vb
' AN EXPLICIT CHOICE IS NOT A PREFERENCE THAT LOSES TO AN ERROR. Two of the three
' answers can put a page on the server, and they are not the same statement. A
' person who picks Remote has said WHERE pages come from, so an unusable server
' leaves them with the server engine and a reason on screen -- never with a page
' drawn by the very engine they did not choose, which was a silent change of
' renderer under a setting that said otherwise. Auto is the setting for somebody
' who would rather let a measurement decide, and it is the only one that may hand
' a page to the device because the server could not take it.
'
' What is still asked before a page goes to the server is whether the server is
' usable at all -- an address, a device token and its switch on. RemoteServers.Ready
' answers it, Auto consults it, and a default is still not a promise that the
' server exists.
```

Replace the body of `Decide` and its doc comment:

```vb
        ''' <summary>
        ''' The engine to use. An explicit Trident choice wins over everything, and
        ''' an explicit Remote choice is honoured as written: this function does NOT
        ''' fall back to the device for it. Handing a page to the on-device engine
        ''' when somebody has chosen the server would be a silent change of renderer
        ''' under a setting that says otherwise, and what it hides is exactly what
        ''' the status line exists to report. The engine itself still refuses to draw
        ''' anything it cannot: RemoteEngine checks its own readiness on every
        ''' navigation and raises the reason, so a misconfigured install gets the
        ''' reason and no page rather than a page from the wrong engine.
        '''
        ''' Auto is the setting that may use either, and `hostedReady` -- whether the
        ''' hosted renderer has an address, a device token and its switch on, the
        ''' answer RemoteServers.Ready gives -- is consulted on that path, before a
        ''' page is handed to a server that is not there.
        ''' </summary>
        Public Shared Function Decide(setting As String, hostedReady As Boolean,
                                      probeMeasured As Boolean, missingFeatureCount As Integer) As String
            Dim wanted As String = Normalize(setting)
            If wanted = Trident Then Return Trident
            If wanted = Remote Then Return Remote

            If Not probeMeasured Then Return Trident
            If missingFeatureCount < AutomaticFallbackThreshold Then Return Trident
            If hostedReady Then Return Remote
            Return Trident
        End Function
```

Add after `Explain`:

```vb
        ''' <summary>
        ''' Whether a page the hosted engine could not draw may be handed to the
        ''' on-device engine. False for an explicit Remote: the shell asks this
        ''' before its announced fallback, so a page is never drawn by an engine its
        ''' reader did not choose. True otherwise, because Auto asks for whichever
        ''' engine works and Trident never needs a fallback at all.
        ''' </summary>
        Public Shared Function MayFallBackToDevice(setting As String) As Boolean
            Return Normalize(setting) <> Remote
        End Function
```

Run: `node tools/proto/engine-choice.mjs; echo "EXIT=$?"`

Expected: red, and specifically on the source contract that asserts the explicit
path consults readiness — which is Task 2.

## Task 2 — The referees, in the same commit

**Deliverable:** `engine-choice.mjs` prints every check green and refuses the two
mutations below; `core-logic.mjs` and `CoreLogicTests.vb` state the same rows.

`tools/proto/engine-choice.mjs`:

```js
function Decide(setting, hostedReady, probeMeasured, missingFeatureCount) {
  const choice = Normalize(setting);
  if (choice === Trident) return Trident;
  if (choice === Remote) return Remote;
  if (!probeMeasured) return Trident;
  if (missingFeatureCount < Threshold) return Trident;
  return hostedReady ? Remote : Trident;
}

function MayFallBackToDevice(setting) {
  return Normalize(setting) !== Remote;
}
```

Replace the three checks of the old "default engine is not chosen" block with the
new rows, and replace the two readiness source contracts with:

```js
check('Decide honours an explicit remote choice and does not consult readiness for it',
  /If wanted = Remote Then Return Remote/.test(source)
  && !/If wanted = Remote Then\s*\n\s*If hostedReady/.test(source),
  'an explicit server choice is not rewritten into a page on the device');
check('Decide consults readiness on the automatic path, which is the only one that may ask',
  (source.match(/If hostedReady Then Return Remote/g) ?? []).length === 1);
check('MayFallBackToDevice is False for Remote and True for everything else',
  /Public Shared Function MayFallBackToDevice\(setting As String\) As Boolean\s*\n\s*Return Normalize\(setting\) <> Remote/.test(source));
check('the shell gates its fallback on that rule',
  /If IsHostedEngineUnusable\(e\.StatusKey\)[\s\S]{0,400}MayFallBackToDevice\(_appSettings\.EngineSetting\)/.test(mainPage),
  'a page must not be handed to the device engine when the reader chose the server');
check('and the other branch names what happened instead',
  /EngineForcedRemoteNoPage/.test(mainPage));
```

`tools/proto/core-logic.mjs` / `CoreLogicTests.vb`, the engine-choice rows:

```js
check('engine choice: the server engine stays chosen when it is not configured',
  chooseEngine('remote', false, false, 0) === 'remote');
```

```vb
Check(EngineChoice.Decide(EngineChoice.Remote, False, True, 99) = EngineChoice.Remote,
      "engine choice: an explicit server choice is not rewritten into the device engine")
Check(Not EngineChoice.MayFallBackToDevice(EngineChoice.Remote),
      "engine choice: a chosen server is not replaced by the device engine")
Check(EngineChoice.MayFallBackToDevice(EngineChoice.Auto),
      "engine choice: automatic may still fall back")
```

`remote-servers.mjs`: the comment line "Ready is the question EngineChoice.Decide
asks before it hands a page over" becomes "…before it hands a page over on the
automatic path".

Run: `node tools/proto/engine-choice.mjs && node tools/proto/core-logic.mjs`

Expected: `N/N engine-choice checks passed` and `72 assertions, 0 failure(s)`.

**The two mutations, run by hand, both of which must go red:**

1. put `If wanted = Remote Then If hostedReady Then Return Remote … End If` back
   in `EngineChoice.vb` → the first check above fails;
2. delete `MayFallBackToDevice(_appSettings.EngineSetting)` from `MainPage.xaml.vb`
   → the shell check fails.

## Task 3 — The shell, and the sentence it says instead

**Deliverable:** with Server selected and no usable server, the app draws no page
and says why in both languages.

`MainPage.xaml.vb`, in `OnRemoteNavigated`:

```vb
            If IsHostedEngineUnusable(e.StatusKey) AndAlso
               EngineChoice.MayFallBackToDevice(_appSettings.EngineSetting) Then
                Dim reason As String = Localizer.Get(e.StatusKey)
                Dim wantedUrl As String = e.Url
                If String.IsNullOrEmpty(wantedUrl) Then
                    wantedUrl = _appSettings.Homepage
                End If
                UseEngine(EngineChoice.Trident)
                StatusText.Text = Localizer.Get("EngineFallbackOnDevice") & "  " & reason
                _engine.Navigate(wantedUrl)
                RefreshTabsList()
                Return
            End If

            If IsHostedEngineUnusable(e.StatusKey) Then
                ' The server engine was asked for BY NAME, so a page that could not
                ' be drawn there is not drawn anywhere: the device engine is not a
                ' substitute for the engine somebody chose, and drawing it here
                ' would answer a different question under a setting that says
                ' otherwise. What is left to say is which failure it was and where
                ' the fix is, which is what the reason key already says.
                StatusText.Text = String.Empty
                ErrorText.Text = Localizer.Get("EngineForcedRemoteNoPage") & "  " &
                                 Localizer.Get(e.StatusKey)
                If e.StatusKey = "EngineReasonRemoteUnreachable" AndAlso
                   Not String.IsNullOrEmpty(e.Detail) Then
                    ErrorText.Text = ErrorText.Text & " (" & e.Detail & ")"
                End If
                ErrorText.Visibility = Visibility.Visible
                RefreshTabsList()
                Return
            End If
```

Both `.resw` files, immediately after `EngineFallbackOnDevice`:

```xml
  <data name="EngineForcedRemoteNoPage" xml:space="preserve">
    <value>No page: the server engine is chosen, and nothing is drawn on the device.</value>
  </data>
```

```xml
  <data name="EngineForcedRemoteNoPage" xml:space="preserve">
    <value>Nessuna pagina: è scelto il motore server e sul dispositivo non viene disegnato nulla.</value>
  </data>
```

`RemoteNotice` in both languages currently promises the removed behaviour
("switch it off … to keep every page on this phone"); rewrite it to state the rule.

Run: `node tools/check-vb.mjs 2>&1 | tail -3`

Expected: `16 check groups run, 0 finding(s)`.

## Task 4 — The READMEs, which are the request's second half

**Deliverable:** a reader who has never seen the server can get from "nothing" to
"a page drawn by Chromium" using only the README.

- New section **Configuring the hosted renderer** in `README.md` /
  **Configurare il renderer ospitato** in `README.it.md`, between the
  platform-truth section and Features: the server (address, port 8443, the
  certificate the phone will actually validate, `BFWP_MAX_SESSIONS`), the device
  token (`docker compose exec render bin/bfwp-device.sh add "my phone"`, shown
  once), the four phone fields, what to expect, and how to render on the phone
  instead.
- The **Hosted renderer by default** row of both tables: the fallback sentence is
  now false and says what the app does instead.
- `../Docker-BrowserForWP/README.md`: step 3 uses the wrapper (a bare
  `node bin/bfwp-device.js` writes a root-owned registry the server cannot read),
  step 4 points at the four fields, and the paragraph promising that an
  unregistered server "costs nothing but a missing picture" is corrected.
- `docs/ARCHITECTURE.md` Law 5, second bullet.
- `docs/MAINTAINING.md`: `### Round 19`, a pointer in Round 13's table, the four
  expectations of "The remote engine, verified by hand" whose expected result
  changed, and the `engine-choice.mjs` row of "Where the tests actually are".
- `SKILL.md`: the `engine-choice.mjs` row (new count, new contract) and the "two
  mutations" sentence.

## Verification

- `node tools/check-vb.mjs` → `16 check groups run, 0 finding(s)`
- every `tools/proto/*.mjs` → green
- guest: `tools\vm-build.cmd` in four configurations → `BUILD_EXIT=0`
- the two mutations above → red, then reverted

**Not verified, and not claimable here:** the new behaviour on a handset. The
rows of "The remote engine, verified by hand" whose *expected* column changed stay
**blank** with their results.
