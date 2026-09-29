#!/usr/bin/env node
// The executable referee for BrowserForWP.Core/Engine/EngineChoice.vb.
//
// A transliteration, like tools/proto/core-logic.mjs: the VB is what the device
// runs, this is what runs here, and the two are kept in step by hand. It ALSO
// asserts the source contract around the rule, because two of this repository's
// conventions are easy to break silently:
//
//   * Core may not produce a user-facing sentence -- it returns a resource key
//     and Localizer resolves it. A literal with a space in this file would be
//     the beginning of an English string in a library.
//   * The constants are read by name from MainPage, so a rename here that this
//     file did not notice is a build error there. Asserted rather than trusted.
//
// Since Round 19 it also carries the rule that an explicit choice is not a
// preference that loses to an error: an explicit Remote comes back as Remote
// whatever the server's readiness says, and a page may be handed to the device
// engine only where MayFallBackToDevice allows it.
//
// The decision table's most important row is the one about the ABSENT
// measurement: an automatic fallback must not switch engines because a probe
// never ran. `ProbeReport.CouldRun = False` means "nothing was measured", and
// this project has already shipped one lie of that exact shape.
import fs from 'node:fs';

let failures = 0;
let checks = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}
function readIfPresent(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}

const SOURCE = 'BrowserForWP.Core/Engine/EngineChoice.vb';
const SOURCE_ENGINE = 'BrowserForWP/Engine/RemoteEngine.vb';
const MAINPAGE = 'BrowserForWP/MainPage.xaml.vb';

// ── The transliterated rule ────────────────────────────────────────────────
const Trident = 'trident';
const Remote = 'remote';
const Auto = 'auto';
const Threshold = 8;

function Normalize(setting) {
  if (setting === Trident || setting === Remote) return setting;
  return Auto;
}

function Decide(setting, hostedReady, probeMeasured, missingFeatureCount) {
  const choice = Normalize(setting);
  if (choice === Trident) return Trident;
  if (choice === Remote) return Remote;
  if (!probeMeasured) return Trident;
  if (missingFeatureCount < Threshold) return Trident;
  return hostedReady ? Remote : Trident;
}

// Whether a page the server could not draw may be handed to the device engine.
// False for an explicit Remote: the shell asks this before its announced
// fallback, so no page is ever drawn by an engine its reader did not choose.
function MayFallBackToDevice(setting) {
  return Normalize(setting) !== Remote;
}

function Explain(setting, hostedReady, probeMeasured, missingFeatureCount) {
  const choice = Normalize(setting);
  if (choice === Trident) return 'EngineReasonSetting';
  if (choice === Remote) {
    return hostedReady ? 'EngineReasonSettingRemote' : 'EngineReasonRemoteNotConfigured';
  }
  if (!probeMeasured) return 'EngineReasonAutoNoMeasurement';
  if (missingFeatureCount < Threshold) return 'EngineReasonAutoFits';
  return hostedReady
    ? 'EngineReasonAutoTooManyMissingFeatures'
    : 'EngineReasonRemoteNotConfigured';
}

// ── The decision table ─────────────────────────────────────────────────────
check('an explicit remote choice wins over a healthy probe',
  Decide(Remote, true, true, 0) === Remote);
check('an explicit remote choice is honoured with no measurement at all',
  Decide(Remote, true, false, 0) === Remote);
check('an explicit trident choice wins over a broken probe',
  Decide(Trident, true, true, 99) === Trident);
check('an explicit trident choice is honoured with no measurement at all',
  Decide(Trident, true, false, 0) === Trident);
check('auto with NO measurement stays on Trident',
  Decide(Auto, true, false, 0) === Trident);
check('auto with no measurement and a huge count still stays on Trident',
  Decide(Auto, true, false, 99) === Trident);
check('auto on a page the probe could run stays on Trident',
  Decide(Auto, true, true, 0) === Trident);
check('auto one short of the threshold stays on Trident',
  Decide(Auto, true, true, 7) === Trident);
check('auto at the threshold switches to the remote engine',
  Decide(Auto, true, true, 8) === Remote);
check('auto past the threshold stays switched',
  Decide(Auto, true, true, 99) === Remote);

// THE RULE THE ROUND THAT WROTE THIS CHANGED, and the one the file is now about:
// two of the three answers can put a page on the server, and they are not the
// same statement. An explicit Remote is where the reader said pages come from,
// so an unusable server leaves them with the server engine and a reason on
// screen -- never with a page drawn by the engine they did NOT choose. The four
// rows below are the whole point.
check('an explicit remote choice is kept when the server is not configured',
  Decide(Remote, false, true, 0) === Remote);
check('and it is kept with a healthy probe too',
  Decide(Remote, false, true, 99) === Remote);
check('and switching the server OFF does not move the page onto the device engine',
  Decide(Remote, false, false, 0) === Remote);
check('the reason still names the missing configuration, not the page',
  Explain(Remote, false, true, 0) === 'EngineReasonRemoteNotConfigured');
check('a configured server is still what an explicit remote choice gets',
  Decide(Remote, true, false, 0) === Remote);

// Which is only true if the shell asks this before it substitutes an engine. A
// page may be handed to the device engine when the setting allows it -- Auto is
// the setting that ASKS for whichever engine works -- and never when the reader
// named the server.
check('a page may fall back to the device engine on every setting but an explicit server',
  MayFallBackToDevice(Trident) && MayFallBackToDevice(Auto)
  && MayFallBackToDevice('native') && MayFallBackToDevice('') && MayFallBackToDevice('ie')
  && !MayFallBackToDevice(Remote));

// Auto is otherwise untouched: it consults readiness before it moves a page to
// the server, and a default engine is still not a promise that a server exists.
check('an automatic switch to an unusable server does not happen',
  Decide(Auto, false, true, 99) === Trident
  && Explain(Auto, false, true, 99) === 'EngineReasonRemoteNotConfigured');
check('an automatic switch to a usable server still happens',
  Decide(Auto, true, true, 99) === Remote);

// The keyword this app used to store for the renderer it no longer has. An
// upgrade that reads it must become indistinguishable from Auto -- and the
// property that matters is the first line below, not the second: an unmeasured
// page stays on Trident, so an upgrade cannot move anybody onto a server they
// never chose. It CAN still reach the remote engine once a measurement says
// Trident cannot cope, which is Auto's whole purpose.
check('the keyword an older version stored normalises to Auto, not to an engine',
  Normalize('native') === Auto);
check('a stale keyword cannot move a page onto the remote engine without evidence',
  Decide('native', true, false, 0) === Trident && Decide('native', true, false, 99) === Trident,
  'with no measurement, nothing may switch');
check('an empty setting is auto, not an error',
  Decide('', true, true, 8) === Remote && Decide('', true, true, 0) === Trident);
check('an unrecognised setting is auto, not an error',
  Decide('ie', true, true, 8) === Remote);
check('the threshold is 8',
  Decide(Auto, true, true, 7) === Trident && Decide(Auto, true, true, 8) === Remote);

// ── The reasons are keys, one per reachable decision ───────────────────────
const reasons = [
  Explain(Remote, true, true, 0),
  Explain(Trident, true, true, 0),
  Explain(Auto, true, true, 0),
  Explain(Auto, true, true, 9),
  Explain(Auto, true, false, 0),
];
check('every reason is a resource key, not a sentence',
  reasons.every((r) => /^EngineReason[A-Za-z]+$/.test(r)), reasons.join(', '));
check('the five reasons are five distinct keys',
  new Set(reasons).size === 5, reasons.join(', '));

// The sixth key is the reason a person sees when the engine they chose is not set
// up, and it is also one of the two keys the SHELL reads -- together with
// MayFallBackToDevice -- to decide whether to hand the page to the device engine.
// Two files depend on those exact spellings.
check('the unusable-hosted reason is its own key',
  Explain(Remote, false, false, 0) === 'EngineReasonRemoteNotConfigured'
  && !reasons.includes('EngineReasonRemoteNotConfigured'));
check('the shell and the rule agree on which keys mean "hand it to the device"',
  /Private Shared Function IsHostedEngineUnusable[\s\S]{0,600}EngineReasonRemoteNotConfigured[\s\S]{0,600}EngineReasonRemoteUnreachable/.test(
    readIfPresent('BrowserForWP/MainPage.xaml.vb')));
check('a fallback that reloaded the session tab instead of the asked-for page is refused',
  /result\.Url = If\(pageUrl, String\.Empty\)/.test(readIfPresent(SOURCE_ENGINE)),
  `${SOURCE_ENGINE} must carry the requested page on a failure, or the fallback loads the previous one`);

// ── The source contract ────────────────────────────────────────────────────
const source = readIfPresent(SOURCE);
const mainPage = readIfPresent(MAINPAGE);
check(`${SOURCE} exists`, source.length > 0);
check('it declares the three choices as constants',
  /Public Const Trident As String = "trident"/.test(source)
  && /Public Const Remote As String = "remote"/.test(source)
  && /Public Const Auto As String = "auto"/.test(source));
check('it declares the threshold as a named constant, not a magic number',
  /AutomaticFallbackThreshold As Integer = 8/.test(source));
check('it is uninstantiable', /Private Sub New\(\)/.test(source));
// A transliteration drifts silently, and this file is one: the checks above
// execute the functions defined HERE, so a VB edit that reinstated the old
// fallback would leave every one of them green. That is not a hypothetical -- the
// first draft of an earlier round's referee passed with the readiness branch
// deleted from the VB. The source checks below are what make the rule above a
// statement about the VB rather than about this file, and both mutations are
// refused by them: re-adding a readiness branch to the explicit path, and
// deleting the shell's gate.
check('Decide honours an explicit remote choice and has no readiness branch for it',
  /If wanted = Remote Then Return Remote/.test(source)
  && !/If wanted = Remote Then\s*\n\s*If hostedReady Then Return Remote/.test(source),
  'an explicit server choice is not rewritten into a page on the device');
check('Decide consults readiness on the automatic path, the only one that may ask',
  (source.match(/If hostedReady Then Return Remote/g) ?? []).length === 1,
  'a page may be moved to the server only when the server exists');
check('MayFallBackToDevice is False for Remote and True for everything else',
  /Public Shared Function MayFallBackToDevice\(setting As String\) As Boolean\s*\n\s*Return Normalize\(setting\) <> Remote/.test(source),
  'the shell asks this before it hands a page to the device engine');
check('the shell gates its announced fallback on that rule',
  /If IsHostedEngineUnusable\(e\.StatusKey\)[\s\S]{0,400}MayFallBackToDevice\(_appSettings\.EngineSetting\)/.test(mainPage),
  'a page must not be handed to the device engine when the reader chose the server');
check('and the branch that is left says nothing will be drawn on the device',
  /EngineForcedRemoteNoPage/.test(mainPage),
  'the reason alone does not say that the device engine will not substitute itself');
check('Explain reports the missing configuration rather than a page problem',
  (source.match(/Return "EngineReasonRemoteNotConfigured"/g) ?? []).length === 2);
check('Explain never reports a page problem for an unusable server',
  !/If wanted = Remote Then\s*\n\s*Return "EngineReasonSettingRemote"/.test(source));
check('Decide takes the hosted-renderer answer before the measurement',
  /Function Decide\(setting As String, hostedReady As Boolean,\s*\n?\s*probeMeasured As Boolean/.test(source),
  'the hosted engine is the default, so "is it usable" is the first question');

// A space inside a string literal here is how an English sentence gets into a
// library. Comments are skipped (they are allowed to name things in English),
// and this is a source check rather than a behavioural one -- deliberately, and
// narrowly scoped to one file.
const literals = [];
for (const raw of source.split(/\r?\n/)) {
  if (/^\s*'/.test(raw)) continue;
  const code = raw.split("'")[0];
  for (const m of code.matchAll(/"([^"]*)"/g)) literals.push(m[1]);
}
check('no string literal in it contains a space (no English sentences in Core)',
  literals.every((l) => !/\s/.test(l)), JSON.stringify(literals));

// Every key Explain can return is defined by its own name in this file, so the
// UI's resw keys and this list cannot drift apart unnoticed.
check('every reason key appears in the file it belongs to',
  reasons.every((r) => source.includes(r)), reasons.join(', '));

console.log(`\n${checks - failures}/${checks} engine-choice checks passed.`);
if (failures > 0) {
  console.log(`${failures} engine-choice failure(s). The rule and the VB that `
    + 'implements it disagree, or the source contract is broken.');
  process.exit(1);
}
console.log('The engine-choice rule and its source contract hold.');
