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
  if (choice === Remote) return hostedReady ? Remote : Trident;
  if (!probeMeasured) return Trident;
  if (missingFeatureCount < Threshold) return Trident;
  return hostedReady ? Remote : Trident;
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

// The rule this build added when the hosted engine became the default one. A
// default is not a promise that a server exists: an install that has not been
// registered has an address and no token, and "draw the page on a machine we
// cannot use" is not one of the available answers. The three rows below are the
// whole point -- wanting the hosted engine is not having it, and the fallback is
// to the engine that always exists.
check('the default engine is not chosen while the server is unusable',
  Decide(Remote, false, true, 0) === Trident);
check('and it is not chosen with a healthy probe either',
  Decide(Remote, false, true, 99) === Trident);
check('it is chosen as soon as the server is usable',
  Decide(Remote, true, false, 0) === Remote);
check('the reason names the missing configuration, not the page',
  Explain(Remote, false, true, 0) === 'EngineReasonRemoteNotConfigured');
check('an automatic switch to an unusable server does not happen',
  Decide(Auto, false, true, 99) === Trident
  && Explain(Auto, false, true, 99) === 'EngineReasonRemoteNotConfigured');
check('an automatic switch to a usable server still happens',
  Decide(Auto, true, true, 99) === Remote);
check('switching a server OFF takes the hosted engine away even when it is addressed',
  Decide(Remote, false, false, 0) === Trident);

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

// The sixth key, reachable only now that the hosted engine is the default: it is
// the reason a person sees when the engine they were promised is not set up, and
// it is also the key the SHELL reads to decide whether to hand the page to the
// device. Two files depend on that exact spelling.
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
check(`${SOURCE} exists`, source.length > 0);
check('it declares the three choices as constants',
  /Public Const Trident As String = "trident"/.test(source)
  && /Public Const Remote As String = "remote"/.test(source)
  && /Public Const Auto As String = "auto"/.test(source));
check('it declares the threshold as a named constant, not a magic number',
  /AutomaticFallbackThreshold As Integer = 8/.test(source));
check('it is uninstantiable', /Private Sub New\(\)/.test(source));
// A transliteration drifts silently, and this file is one: the checks above
// execute the functions defined HERE, so a VB edit that deleted the readiness
// branch would leave every one of them green. That is not a hypothetical -- the
// first draft of this round's referee passed with the branch removed. These four
// source checks are what make the rule above a statement about the VB rather than
// about this file, and the mutation that removed the branch is refused by them.
check('Decide consults readiness on the explicit path',
  /If wanted = Remote Then\s*\n\s*If hostedReady Then Return Remote\s*\n\s*Return Trident\s*\n\s*End If/.test(source),
  'wanting the hosted engine is not having it');
check('Decide consults readiness on the automatic path too',
  (source.match(/If hostedReady Then Return Remote/g) ?? []).length === 2,
  'both paths that can return the hosted engine must ask first');
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
