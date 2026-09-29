# Plan — a FOCUS message, so the keyboard rises only on a field that takes text

Date: 2026-09-29
Status: implemented

## The report, which is deferred item 17

Nothing in the protocol says *where the focus is*. The client therefore cannot
know whether a tap landed on a text box, and the only way it has to make the soft
keyboard appear is to focus its own hidden field — so **the keyboard comes up on
every tap**, including on a link, on a button and on empty space. On a phone that
is not a cosmetic problem: it covers half the page a person is trying to read.

The fix cannot be local. `RemoteScreen.OnTapped` focuses the 1x1 field because
that is the only trigger it has; deciding whether to focus is a question about the
*page*, and the page is on the server.

## The message

`FOCUS = 0x27`, server to client, sealed, one byte:

| Offset | Type | Field | Meaning |
| --- | --- | --- | --- |
| 0 | u8 | `editable` | 1 when the focused element accepts text, 0 when nothing focused can |

One byte and not a structure, because that single bit is the whole question the
client asks. The *kind* of field (password, email, number) would let the phone pick
a soft-keyboard layout through `InputScope`, and that is deliberately **not** in
this revision: a keyboard layout cannot be verified without a handset, and the
decoders reject trailing bytes, so adding it later is a protocol change in both
repositories plus the vectors — which is the honest cost of shipping it, and the
reason it is an item rather than a field nobody reads.

## Where the server decides it

- `src/browser.js` gains `focus()`: one `page.evaluate` that answers whether
  `document.activeElement` is a text field (`input` of a text-bearing type,
  `textarea`, or `isContentEditable`). This is the authoritative answer and it
  costs one round trip inside the server.
- `src/session.js` gains `_reportFocus()`: ask the browser, compare with the last
  value sent, and write a `FOCUS` message **only when it changed**. Deduplication
  matters — Tab through a form is one message per field, not one per key.
- It is called after `TAP`, after `KEY` (Tab moves focus; so does Escape), and when
  a load completes, where the page's own focus has been thrown away. The last value
  is reset to "unknown" on navigation so the first report after a page change is
  always sent, even when it repeats the previous value.
- A browser with no `focus()` — the tests' fake browser, or a future backend that
  cannot answer — must leave the session working and silent, not throw.

## Where the client obeys it

- `RemoteScreen` stops focusing the field on tap. `SetPageFocus(editable)` records
  what the page said: `True` focuses the hidden field (and re-enables it first, if
  a previous message disabled it), `False` disables it. Disabling is how a hidden
  `TextBox` is made to let go: WinRT has no `Unfocus()`, and disabling the focused
  control is the one state change that both drops focus and closes the soft
  keyboard.
- `FocusKeyboard()` — the rotation path — becomes a no-op unless the page's focused
  element is editable, and the rotation call site asks `WantsKeyboard` rather than
  `HasKeyboardFocus`: a re-arranged tree can drop focus, and a keyboard that closes
  itself when the phone turns is a keyboard the person did not dismiss.
- The keys bar is unaffected. Tab, Escape and the arrows are `KEY` messages, not
  the soft keyboard, so they keep working on a page with nothing editable in it —
  which is the case that would otherwise lose them.

## What this does not do, and says so

**A page that focuses a field by itself does not raise the keyboard until the
person touches something.** Playwright can report the page's own `focusin` through
an exposed binding, and that is the obvious next step; it is not in this revision
because it is the one part of this change that cannot be tested on this host at
all, and a page-driven path that silently degrades is worth less than a gap with a
number on it.

**CORRECTION, made while executing this plan: it is deferred item 20, not 19.**
Item 19 is the keys bar, which had not been seen on a handset either, and this
plan was written from memory of the list. `docs/MAINTAINING.md` item 20 also carries
the second case the plan missed: tapping the SAME field again after dismissing the
keyboard by hand raises nothing, because the server reports on a change of answer
and the answer has not changed. Both are the same missing binding.

## Verification

- Server: `npm test` (no `node_modules` needed — `playwright` is imported
  dynamically, and the suite runs without it). New cases: the message round-trips,
  a value other than 0/1 is refused, trailing bytes are refused, a `TAP` produces a
  `FOCUS` carrying the browser's answer, a repeated answer produces nothing, a load
  completing re-reports, and a browser without `focus()` is not an error.
- Server vectors: `npm run gen-vectors` rewrites `protocol/vectors.json`, and
  `test/vectors.test.js` regenerates the same object and fails if the file drifted.
- Client: `tools/proto/remote-protocol.mjs` replays the new vector byte-for-byte
  and asserts the VB field order; `tools/proto/remote-input.mjs` gains checks that
  the tap path no longer focuses, that the keyboard is gated on the message, and
  that the engine routes it to the screen — each with a planted defect in
  `--probe`, which is the half that keeps the file honest.
- Client: `tools/check-vb.mjs`, then six configurations on the phone's toolchain
  (`tools\vm-build.cmd`), then `devenv.com` to prove the solution still loads.

## Not verified, and recorded as such

The handset rows: that the keyboard appears on a tap that lands on a field, that
it stays down on a tap that does not, and that a rotation keeps it. They go into
the hand-verification table in `docs/MAINTAINING.md` and stay blank.

The Chromium row: `browser.js`'s `focus()` query needs Playwright and a browser,
neither of which is installed on this host. Its logic is exercised only through
the session's fake browser, which is exactly the part that is testable here.
