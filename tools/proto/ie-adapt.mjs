#!/usr/bin/env node
// Decision record for "adapt Internet Explorer instead of writing an engine".
// It encodes what must be TRUE for that branch to be viable and FAILS while it
// is unmeasured. The on-device result is recorded separately and is NOT asserted
// here: no handset is available in this environment.
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

// A negative assertion on an empty string passes vacuously, so existence is
// asserted separately from every property.
const probe = readIfPresent('BrowserForWP.Core/Diagnostics/IeModeProbe.vb');
check('IeModeProbe.vb exists', probe.length > 0);
check('the instrument reads documentMode', probe.includes('documentMode'));
check('the instrument reads X-UA-Compatible', probe.includes('X-UA-Compatible'));
check('the instrument is ES5 only (no let/const/arrow)',
  probe.length > 0 && !/\blet\s+\w|\bconst\s+\w|=>/.test(probe));

const recorded = readIfPresent('docs/MAINTAINING.md');
check('the branch is recorded as closed', recorded.includes('IE-adaptation is closed'));
check('document-mode forcing is recorded as unavailable',
  recorded.includes('no API to set the WebView document mode'));
check('MSHTML access from WinRT is recorded as unavailable',
  recorded.includes('no MSHTML surface is reachable from a WinRT app'));
check('feature-flag control is recorded as unavailable',
  recorded.includes('no API to toggle IE11 feature flags'));
check('a newer Trident is recorded as never shipping',
  recorded.includes('no Trident newer than IE11 ever shipped for this OS'));

console.log(`\n${checks - failures}/${checks} ie-adapt checks passed.`);
if (failures > 0) {
  console.log(`${failures} unmeasured/unrecorded item(s). The IE-adaptation branch is NOT closed.`);
  process.exit(1);
}
console.log('The IE-adaptation branch is closed with evidence.');
