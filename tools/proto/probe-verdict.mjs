#!/usr/bin/env node
// A report that could not run must never read as "fully compatible".
// Mirrors ProbeReport.IsFullyCompatible in
// BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb.
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

// The rule under test: compatible iff the probe actually ran AND found nothing.
function isFullyCompatible(sawAnswer, missingFeatures) {
  return sawAnswer && missingFeatures.length === 0;
}
check('no answer is not compatibility', isFullyCompatible(false, []) === false);
check('answer with gaps is not compatibility', isFullyCompatible(true, ['fetch']) === false);
check('answer with no gaps is compatibility', isFullyCompatible(true, []) === true);

const probe = readIfPresent('BrowserForWP.Core/Diagnostics/CompatibilityProbe.vb');
check('CompatibilityProbe.vb exists', probe.length > 0);
check('ProbeReport carries CouldRun', probe.includes('CouldRun'));
check('IsFullyCompatible requires CouldRun', /IsFullyCompatible[\s\S]{0,120}CouldRun/.test(probe));

const main = readIfPresent('BrowserForWP/MainPage.xaml.vb');
check('the UI distinguishes an unanswered probe', main.length > 0 && main.includes('ProbeNotRun'));

const en = readIfPresent('BrowserForWP/Strings/en-US/Resources.resw');
const it = readIfPresent('BrowserForWP/Strings/it-IT/Resources.resw');
check('ProbeNotRun exists in en-US', en.includes('name="ProbeNotRun"'));
check('ProbeNotRun exists in it-IT', it.includes('name="ProbeNotRun"'));

console.log(`\n${checks - failures}/${checks} probe-verdict checks passed.`);
if (failures > 0) {
  console.log(`${failures} probe-verdict failure(s).`);
  process.exit(1);
}
