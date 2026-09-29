#!/usr/bin/env node
// Failing-first mirror of BrowserForWP.Core/Browser/TrackerBlocklist.vb.
// Must print: trackerblock checks, 0 failure(s). Exits 1 otherwise.
import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();
let failures = 0;
function check(name, ok, detail = '') {
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}

const vbPath = path.join(ROOT, 'BrowserForWP.Core', 'Browser', 'TrackerBlocklist.vb');
const vbExists = fs.existsSync(vbPath);
check('TrackerBlocklist.vb exists', vbExists);
const vb = vbExists ? fs.readFileSync(vbPath, 'utf8') : '';
if (vbExists) {
  check('exposes ShouldBlock', vb.includes('Function ShouldBlock'));
  check('documents subresource limit', /subresource/i.test(vb));
}

// Executable mirror of the suffix-match rule.
const BLOCKED = ['doubleclick.net', 'google-analytics.com', 'connect.facebook.net',
  'outbrain.com', 'taboola.com', 'adsrvr.org'];
function cleanHost(h) {
  if (!h) return '';
  let c = h.trim().toLowerCase();
  const ci = c.indexOf(':');
  if (ci >= 0) c = c.slice(0, ci);
  return c;
}
function shouldBlock(host) {
  const c = cleanHost(host);
  if (!c) return false;
  return BLOCKED.some((s) => c === s || c.endsWith('.' + s));
}
check('exact tracker blocked', shouldBlock('doubleclick.net') === true);
check('subdomain tracker blocked', shouldBlock('a.doubleclick.net') === true);
check('lookalike allowed', shouldBlock('notdoubleclick.net') === false);
check('normal host allowed', shouldBlock('example.com') === false);
check('empty allowed', shouldBlock('') === false);
check('port stripped', shouldBlock('doubleclick.net:443') === true);

if (failures > 0) { console.log(`\n${failures} trackerblock failure(s).`); process.exit(1); }
console.log('\ntrackerblock checks, 0 failure(s)');
