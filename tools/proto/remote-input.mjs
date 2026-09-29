#!/usr/bin/env node
// The executable referee for the remote input path — BrowserForWP.
//
// WHY THIS IS A SOURCE REFEREE AND NOT A TEST OF BEHAVIOUR.
// The path it covers runs on a handset: a tap, the soft keyboard, a keystroke and
// a rotation. None of those can happen here, and a test that pretended otherwise
// would be a test of a mock. What CAN be checked is the set of statements that
// are true of the source and are exactly the ones that go wrong silently:
//
//   * the hidden field is built once, because a field rebuilt per navigation or
//     per rotation loses the word the person was halfway through;
//   * every write to the stream is gated, because two concurrent seals can reuse
//     a sequence number and two concurrent writes interleave their records;
//   * the viewport the server draws at and the viewport a finger is mapped
//     through are set together, because a rotation that moves only one of them
//     puts every tap at the wrong place on a picture that looks right;
//   * every key name the shell offers is a name the server can press;
//   * every button label has a resource key in BOTH languages, because
//     check-vb.mjs group 6 only compares the two files with each other and an
//     empty label is an empty button, not a missing one;
//   * a TAP is not a request to type, and the soft keyboard is raised only by
//     the page's own answer (the FOCUS message), because the failure this
//     replaced was a keyboard over every link on every tap.
//
// RUNNING IT
//   node tools/proto/remote-input.mjs          # report, exit 1 on any failure
//   node tools/proto/remote-input.mjs --probe  # negative control: plant each
//                                              # defect and require the matching
//                                              # check to REFUSE it
//
// `--probe` is the half that keeps this file honest. A check that has never been
// seen to fail is decoration, and this repository has shipped decoration twice.

import fs from 'node:fs';

const PROBE = process.argv.includes('--probe');

const SCREEN = 'BrowserForWP/Rendering/RemoteScreen.vb';
const CHANNEL = 'BrowserForWP/Engine/RemoteChannel.vb';
const ENGINE = 'BrowserForWP/Engine/RemoteEngine.vb';
const PAGE = 'BrowserForWP/MainPage.xaml.vb';
const PAGE_XAML = 'BrowserForWP/MainPage.xaml';
const EN = 'BrowserForWP/Strings/en-US/Resources.resw';
const IT = 'BrowserForWP/Strings/it-IT/Resources.resw';

// Playwright's key names, from its documentation — NOT measured on this host,
// because Playwright is not installed here (see docs/MAINTAINING.md: the Docker
// image has never been built). That is a real limit and it is the reason the
// server is the arbiter in practice: a name it does not recognise is a key that
// presses nothing, and this shell offers exactly the ones it needs.
const PLAYWRIGHT_KEYS = new Set([
  'Tab', 'Enter', 'Escape', 'Backspace',
  'ArrowLeft', 'ArrowUp', 'ArrowDown', 'ArrowRight',
]);

// Keys this shell deliberately does NOT offer, and why. They live here so that a
// future edit that adds one has to read the reason and delete it.
const DELIBERATELY_ABSENT = new Map([
  ['Shift+Tab', 'the protocol carries a modifier byte and the server ignores it, so this would press Tab'],
  ['Control+Enter', 'same: browser.js presses { key } and drops { modifiers }'],
]);

function readSources() {
  const sources = {};
  for (const file of [SCREEN, CHANNEL, ENGINE, PAGE, PAGE_XAML, EN, IT]) {
    sources[file] = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
  }
  return sources;
}

const count = (text, pattern) => (text.match(pattern) || []).length;

// ── The checks ──────────────────────────────────────────────────────────────
// Each is a function of the sources, returning true (holds) or false (defect).

const CHECKS = [
  {
    name: 'the hidden field is constructed once',
    why: 'a field rebuilt per navigation or rotation loses a half-typed word, and the ' +
         'person sees their text disappear with no error anywhere',
    run: (s) => count(s[SCREEN], /New TextBox\(\)/g) === 1,
  },
  {
    name: 'the field is prepared once, from the constructor',
    why: 'a second call is a second field, and only one of them can have focus',
    run: (s) => count(s[SCREEN], /PrepareKeyboardProxy\b/g) === 2,
  },
  {
    name: 'the write path is gated on both sends',
    why: 'two concurrent seals can reuse a sequence number (an AEAD nonce reuse) and ' +
         'two concurrent writes interleave records on one socket',
    run: (s) => count(s[CHANNEL], /_writeGate\.WaitAsync\(\)/g) === 2 &&
                count(s[CHANNEL], /_writeGate\.Release\(\)/g) === 2 &&
                count(s[CHANNEL], /Finally\s*\r?\n\s*_writeGate\.Release\(\)/g) === 2,
  },
  {
    name: 'a rotation tells the server, and re-maps this device',
    why: 'moving only the local mapping offsets every tap; moving only the server ' +
         'letterboxes a picture against a stale mapping',
    // Three parts, because the design has three: the subscription, the handler
    // moving BOTH numbers (through ApplyViewport, the single place they move) and
    // the RESIZE that tells the server. A handler that only re-mapped, or only
    // resized the server, is the defect -- so each part is checked where it lives.
    run: (s) => /AddHandler Windows\.UI\.Xaml\.Window\.Current\.SizeChanged, AddressOf OnWindowSizeChanged/.test(s[ENGINE]) &&
                /Private Sub OnWindowSizeChanged[\s\S]*?ApplyViewport\(\)[\s\S]*?RemoteMessageType\.Resize/.test(s[ENGINE]) &&
                /Private Sub ApplyViewport[\s\S]*?_screen\.SetViewport\(/.test(s[ENGINE]),
  },
  {
    name: 'the keys bar offers only names the server can press',
    why: 'a key name the server does not know is a button that does nothing at all',
    run: (s) => {
      const named = [...s[PAGE].matchAll(/SendRemoteKey\("([^"]*)"\)/g)].map((m) => m[1]);
      if (named.length === 0) return false;
      if (named.some((k) => !PLAYWRIGHT_KEYS.has(k))) return false;
      // Every key the bar has a button for is wired, so half a bar cannot ship.
      return [...PLAYWRIGHT_KEYS].every((k) => named.includes(k));
    },
  },
  {
    name: 'every key label has a resource key in both languages',
    why: 'a missing key is an empty label, and group 6 of check-vb.mjs only compares ' +
         'the two files WITH EACH OTHER, so a key absent from both passes it',
    run: (s) => {
      const wanted = [...s[PAGE].matchAll(/Localizer\.Get\("(Key[^"]*)"\)/g)].map((m) => m[1]);
      if (wanted.length === 0) return false;
      return wanted.every((k) => s[EN].includes(`<data name="${k}"`) &&
                                 s[IT].includes(`<data name="${k}"`));
    },
  },
  {
    name: 'every button the bar names is a button the code names too',
    why: 'a button with no code-behind entry compiles and does nothing',
    run: (s) => {
      const buttons = [...s[PAGE_XAML].matchAll(/x:Name="(Key[A-Za-z]*Button)"/g)].map((m) => m[1]);
      if (buttons.length === 0) return false;
      return buttons.every((b) => s[PAGE].includes(b));
    },
  },
  {
    name: 'a tap is not a request to type',
    why: 'focusing the hidden field on every tap is exactly the defect FOCUS exists ' +
         'to remove: the keyboard covers half the page on links, buttons and empty ' +
         'space, and the picture is what the person was trying to read',
    run: (s) => {
      const tapped = /Private Sub OnTapped\([\s\S]*?\r?\n        End Sub/.exec(s[SCREEN]);
      return Boolean(tapped) && !/\.Focus\(/.test(tapped[0]);
    },
  },
  {
    name: 'the field is enabled before it is focused, and disabled to let go',
    why: 'a previous FOCUS message may have disabled the field, so focusing it without ' +
         're-enabling it is a silent no-op; and WinRT has no Unfocus(), so not ' +
         'disabling it leaves the keyboard up on a page that says nothing is editable',
    run: (s) => {
      const body = /Public Sub SetPageFocus\([\s\S]*?\r?\n        End Sub/.exec(s[SCREEN]);
      if (!body) return false;
      const enabledAt = body[0].indexOf('_ime.IsEnabled = True');
      const focusAt = body[0].indexOf('_ime.Focus(FocusState.Programmatic)');
      const disabledAt = body[0].indexOf('_ime.IsEnabled = False');
      return enabledAt > 0 && focusAt > enabledAt && disabledAt > focusAt;
    },
  },
  {
    name: 'the keyboard belongs to the page, not to the rotation',
    why: 'a re-arranged tree drops focus, so asking the FIELD after a rotation closes a ' +
         'keyboard nobody dismissed; and a rotation on a page with nothing editable ' +
         'must not raise one',
    run: (s) => /Public Sub FocusKeyboard\(\)[\s\S]*?If Not _pageFocusEditable Then Return/.test(s[SCREEN]) &&
                /Private Sub OnWindowSizeChanged[\s\S]*?_screen\.WantsKeyboard/.test(s[ENGINE]) &&
                !/_screen\.HasKeyboardFocus/.test(s[ENGINE]),
  },
  {
    name: 'the server\'s FOCUS message reaches the screen',
    why: 'a message decoded and dropped is a keyboard that never rises, which looks ' +
         'exactly like a server that never sent anything',
    run: (s) => /Case RemoteMessageType\.Focus[\s\S]*?RemoteMessages\.DecodeFocus\(payload\)[\s\S]*?_screen\.SetPageFocus\(/.test(s[ENGINE]),
  },
];

// ── The negative control ────────────────────────────────────────────────────
// One mutation per check, each planting the defect that check exists for. A
// mutation that does NOT make its check fail is reported as loudly as a failing
// check: it means the check cannot see the thing it is named after.

const MUTATIONS = [
  { check: 0, what: 'a second TextBox', apply: (s) => { s[SCREEN] = s[SCREEN].replace('Private ReadOnly _ime As New TextBox()', 'Private ReadOnly _ime As New TextBox()\n        Private ReadOnly _spare As New TextBox()'); } },
  { check: 1, what: 'a second call to PrepareKeyboardProxy', apply: (s) => { s[SCREEN] = s[SCREEN].replace('Private Sub PrepareKeyboardProxy()', 'Private Sub PrepareKeyboardProxy()\n            PrepareKeyboardProxy()'); } },
  { check: 2, what: 'a Release outside a Finally', apply: (s) => { s[CHANNEL] = s[CHANNEL].replace(/\r?\n(\s*)Finally\r?\n(\s*)_writeGate\.Release\(\)/, '\n$1End Try\n$1_writeGate.Release()\n$1Try'); } },
  { check: 3, what: 'a rotation that never reaches the server', apply: (s) => { s[ENGINE] = s[ENGINE].replace(/\r?\n\s*Send\(RemoteMessageType\.Resize,[\s\S]*?\)\r?\n/, '\n'); } },
  { check: 4, what: 'a key name the protocol cannot express', apply: (s) => { s[PAGE] = s[PAGE].replace('SendRemoteKey("Tab")', 'SendRemoteKey("Shift+Tab")'); } },
  { check: 5, what: 'a label whose key exists in neither file', apply: (s) => { s[PAGE] = s[PAGE].replace('Localizer.Get("KeyTab")', 'Localizer.Get("KeyTabMissing")'); } },
  { check: 6, what: 'a button the code-behind never mentions', apply: (s) => { s[PAGE] = s[PAGE].replace(/KeyRightButton/g, 'RightArrowButton'); } },
  { check: 7, what: 'a tap that raises the keyboard again', apply: (s) => { s[SCREEN] = s[SCREEN].replace(/(Private Sub OnTapped\([\s\S]*?If _raiseInput Is Nothing Then Return\r?\n)/, '$1\n            _ime.Focus(FocusState.Programmatic)\n'); } },
  { check: 8, what: 'a negative answer that enables the field anyway', apply: (s) => { s[SCREEN] = s[SCREEN].replace('_ime.IsEnabled = False', '_ime.IsEnabled = True'); } },
  { check: 9, what: 'a rotation that raises the keyboard regardless', apply: (s) => { s[SCREEN] = s[SCREEN].replace(/If Not _pageFocusEditable Then Return\r?\n/, ''); } },
  { check: 9, what: 'a rotation that asks the field instead of the page', apply: (s) => { s[ENGINE] = s[ENGINE].replace('_screen.WantsKeyboard', '_screen.HasKeyboardFocus'); } },
  { check: 10, what: 'a FOCUS message decoded and dropped', apply: (s) => { s[ENGINE] = s[ENGINE].replace(/\r?\n                    Case RemoteMessageType\.Focus[\s\S]*?_screen\.SetPageFocus\(focused\.Editable\)/, ''); } },
];

function main() {
  const sources = readSources();
  let failures = 0;

  console.log('── remote input path');
  for (const check of CHECKS) {
    const ok = check.run(sources);
    console.log(`  ${ok ? '\u2713' : '\u2717'} ${check.name}`);
    if (!ok) {
      failures++;
      console.log(`      ${check.why}`);
    }
  }

  console.log(`\n${CHECKS.length - failures}/${CHECKS.length} remote-input checks passed.`);

  if (PROBE) {
    console.log('\n── negative control (each check must refuse its own defect)');
    let blind = 0;
    for (const mutation of MUTATIONS) {
      const planted = readSources();
      mutation.apply(planted);
      const check = CHECKS[mutation.check];
      const refused = !check.run(planted);
      console.log(`  ${refused ? '\u2713' : '\u2717'} refuses ${mutation.what}`);
      if (!refused) blind++;
    }
    if (blind > 0) {
      console.log(`\n${blind} planted defect(s) were NOT refused: those checks cannot see what they are named after.`);
      process.exit(1);
    }
    console.log('\nEvery planted defect was refused.');
  }

  if (failures > 0) {
    console.log('\nThe input path does not hold its own contracts yet.');
    process.exit(1);
  }
  console.log('The hidden field, the write gate, the rotation and the keys hold their contracts.');
}

main();
