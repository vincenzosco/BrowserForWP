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

// ── The transliterated rule ────────────────────────────────────────────────
const Trident = 'trident';
const Remote = 'remote';
const Auto = 'auto';
const Threshold = 8;

function Normalize(setting) {
  if (setting === Trident || setting === Remote) return setting;
  return Auto;
}

function Decide(setting, probeMeasured, missingFeatureCount) {
  const choice = Normalize(setting);
  if (choice === Remote) return Remote;
  if (choice === Trident) return Trident;
  if (!probeMeasured) return Trident;
  return missingFeatureCount >= Threshold ? Remote : Trident;
}

function Explain(setting, probeMeasured, missingFeatureCount) {
  const choice = Normalize(setting);
  if (choice === Remote) return 'EngineReasonSettingRemote';
  if (choice === Trident) return 'EngineReasonSetting';
  if (!probeMeasured) return 'EngineReasonAutoNoMeasurement';
  return missingFeatureCount >= Threshold
    ? 'EngineReasonAutoTooManyMissingFeatures'
    : 'EngineReasonAutoFits';
}

// ── The decision table ─────────────────────────────────────────────────────
check('an explicit remote choice wins over a healthy probe',
  Decide(Remote, true, 0) === Remote);
check('an explicit remote choice is honoured with no measurement at all',
  Decide(Remote, false, 0) === Remote);
check('an explicit trident choice wins over a broken probe',
  Decide(Trident, true, 99) === Trident);
check('an explicit trident choice is honoured with no measurement at all',
  Decide(Trident, false, 0) === Trident);
check('auto with NO measurement stays on Trident',
  Decide(Auto, false, 0) === Trident);
check('auto with no measurement and a huge count still stays on Trident',
  Decide(Auto, false, 99) === Trident);
check('auto on a page the probe could run stays on Trident',
  Decide(Auto, true, 0) === Trident);
check('auto one short of the threshold stays on Trident',
  Decide(Auto, true, 7) === Trident);
check('auto at the threshold switches to the remote engine',
  Decide(Auto, true, 8) === Remote);
check('auto past the threshold stays switched',
  Decide(Auto, true, 99) === Remote);

// The keyword this app used to store for the renderer it no longer has. An
// upgrade that reads it must become indistinguishable from Auto -- and the
// property that matters is the first line below, not the second: an unmeasured
// page stays on Trident, so an upgrade cannot move anybody onto a server they
// never chose. It CAN still reach the remote engine once a measurement says
// Trident cannot cope, which is Auto's whole purpose.
check('the keyword an older version stored normalises to Auto, not to an engine',
  Normalize('native') === Auto);
check('a stale keyword cannot move a page onto the remote engine without evidence',
  Decide('native', false, 0) === Trident && Decide('native', false, 99) === Trident,
  'with no measurement, nothing may switch');
check('an empty setting is auto, not an error',
  Decide('', true, 8) === Remote && Decide('', true, 0) === Trident);
check('an unrecognised setting is auto, not an error',
  Decide('ie', true, 8) === Remote);
check('the threshold is 8',
  Decide(Auto, true, 7) === Trident && Decide(Auto, true, 8) === Remote);

// ── The reasons are keys, one per reachable decision ───────────────────────
const reasons = [
  Explain(Remote, true, 0),
  Explain(Trident, true, 0),
  Explain(Auto, true, 0),
  Explain(Auto, true, 9),
  Explain(Auto, false, 0),
];
check('every reason is a resource key, not a sentence',
  reasons.every((r) => /^EngineReason[A-Za-z]+$/.test(r)), reasons.join(', '));
check('the five reasons are five distinct keys',
  new Set(reasons).size === 5, reasons.join(', '));

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
