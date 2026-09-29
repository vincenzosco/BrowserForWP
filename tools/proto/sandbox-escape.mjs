#!/usr/bin/env node
// Decision record for "start sandboxed, step outside when a request arrives".
// Like ie-adapt.mjs this is not a logic mirror and does not pretend to be: it
// asserts the DEPLOYMENT-TIME fact the whole branch rests on (what this package
// actually declares) and that the reasoning is still recorded in the two living
// documents. There is no runtime instrument for this branch and there cannot be
// one -- nothing inside the container can measure a privilege it does not have --
// so the manifest is the closest thing to evidence this repository can hold.
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

// A negative assertion on an empty string passes vacuously, so the manifest is
// asserted to exist and to declare something before anything is asserted absent.
const manifest = readIfPresent('BrowserForWP/Package.appxmanifest');
check('Package.appxmanifest exists', manifest.length > 0);

const declared = [...manifest.matchAll(/<Capability\s+Name="([^"]+)"\s*\/>/g)]
  .map((m) => m[1]).sort();
check('the manifest declares at least one capability', declared.length > 0);
check('it declares resource access only, not process privilege',
  declared.join(',') === 'internetClientServer',
  `declared: ${declared.join(',') || 'none'}`);
check('it declares no runFullTrust (a Windows 10 capability we cannot use)',
  !declared.includes('runFullTrust'));
check('it declares no codeGeneration (a Windows 10 capability we cannot use)',
  !declared.includes('codeGeneration'));

const architecture = readIfPresent('docs/ARCHITECTURE.md');
check('Law 4 exists', architecture.includes('### Law 4'));
check('Law 4 is the sandbox law', architecture.includes('cannot leave its AppContainer'));
check('self-de-sandboxing is recorded as unavailable',
  architecture.includes('No self-de-sandboxing API'));
check('child inheritance is recorded',
  architecture.includes('A child of an AppContainer process is created in the same container'));
check('capabilities are recorded as resource-only',
  architecture.includes('Capabilities grant resources, never memory policy'));
check('broker contracts are recorded as operation-scoped',
  architecture.includes('Broker contracts exist to perform specified operations'));
// Matched without the surrounding markup: this phrase is emphasised in the law,
// so the literal with a leading "a" does not occur in the file.
check('the Windows 10 answer is recorded as a deployment decision',
  architecture.includes('deployment-time decision, not a request-time escape'));

const maintained = readIfPresent('docs/MAINTAINING.md');
check('the branch is recorded as closed in MAINTAINING.md',
  maintained.includes('### Sandbox escape is closed'));
check('the JIT denial is recorded',
  maintained.includes('writable+executable'));
check('the Windows 10 Mobile contrast is recorded',
  maintained.includes('never available on Windows 10 *Mobile*'));

console.log(`\n${checks - failures}/${checks} sandbox-escape checks passed.`);
if (failures > 0) {
  console.log(`${failures} unmeasured/unrecorded item(s). `
    + 'The sandbox-escape branch is NOT closed, or the package now asks for '
    + 'privilege the platform cannot grant.');
  process.exit(1);
}
console.log('The sandbox-escape branch is closed with evidence.');
