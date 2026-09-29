#!/usr/bin/env node
// Failing-first checks for the run-modern-sites round
// (docs/superpowers/plans/2026-09-28-modern-sites.md).
// Must print: modern-sites checks, 0 failure(s). Exits 1 otherwise.
import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();
const read = (p) => {
  const full = path.join(ROOT, p);
  return fs.existsSync(full) ? fs.readFileSync(full, 'utf8') : '';
};
let failures = 0;
function check(name, ok, detail = '') {
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}

// 1. Shim markers in compat.js.
const compat = read('BrowserForWP.Polyfill/compat.js');
for (const marker of ['MapShim', 'SetShim', 'WeakMap', 'SymbolShim',
  'msMatchesSelector', 'closest', 'CustomEvent', 'arrayBuffer',
  'IntersectionObserver', 'isIntersecting']) {
  check(`compat.js has ${marker}`, compat.includes(marker));
}
const compatBytes = Buffer.byteLength(compat, 'utf8');
check(`compat.js <= 40KB (is ${compatBytes})`, compatBytes <= 40960);
check('no strict-this global bug', !compat.includes('var globalScope = this;'));
check('window passed as argument', compat.includes("typeof window !== 'undefined' ? window"));

// 2. Redirect-rule mirror (must match LiteRedirects.vb exactly).
function redirectUrl(pageUrl) {
  if (!pageUrl) return null;
  let u;
  try { u = new URL(pageUrl); } catch { return null; }
  const host = u.hostname.toLowerCase();
  const pq = u.pathname + u.search + u.hash;
  const labels = host.split('.');
  if (host.endsWith('.wikipedia.org') && !labels.includes('m')) {
    return 'https://' + host.replace('.', '.m.') + pq;
  }
  if (['facebook.com', 'www.facebook.com', 'm.facebook.com'].includes(host)) {
    return 'https://mbasic.facebook.com' + pq;
  }
  if (['reddit.com', 'www.reddit.com'].includes(host)) {
    return 'https://old.reddit.com' + pq;
  }
  return null;
}
check('wiki article redirects with path',
  redirectUrl('https://en.wikipedia.org/wiki/X') === 'https://en.m.wikipedia.org/wiki/X');
check('already-m wiki untouched',
  redirectUrl('https://en.m.wikipedia.org/wiki/X') === null);
check('facebook keeps path+query',
  redirectUrl('https://www.facebook.com/a?b=c') === 'https://mbasic.facebook.com/a?b=c');
check('reddit redirects', redirectUrl('https://reddit.com/r/x') === 'https://old.reddit.com/r/x');
check('normal host untouched', redirectUrl('https://example.com/') === null);
check('garbage untouched', redirectUrl('not a url') === null);

// 3. VB delivery guards.
const liteVb = read('BrowserForWP.Core/Browser/LiteRedirects.vb');
check('LiteRedirects.vb exists with RedirectUrl',
  liteVb.includes('Function RedirectUrl'));
const mainPage = read('BrowserForWP/MainPage.xaml.vb');
check('DOMContentLoaded hooked', mainPage.includes('DOMContentLoaded'));
// The auto-reader threshold used to be a bare `>= 8` in the shell. It is now
// EngineChoice.AutomaticFallbackThreshold, because two literals spelling the same
// rule is one place for them to disagree -- so this asserts the SHAPE that
// replaced it: the shell asks EngineChoice instead of carrying the number. A
// check pinned to the literal would have gone red for the fix rather than for a
// regression, which is what happened.
check('auto-reader threshold comes from EngineChoice, not a literal in the shell',
  mainPage.includes('EngineChoice.AutomaticFallbackThreshold')
  && !/MissingFeatures\.Count\s*>=\s*\d/.test(mainPage));
check('reader fallback string used', mainPage.includes('ReaderFallback'));
check('lite redirect enforced', mainPage.includes('LiteRedirects.RedirectUrl'));
const settings = read('BrowserForWP.Core/Storage/AppSettings.vb');
check('LiteRedirects setting', settings.includes('LiteRedirects'));

// 4. resw parity for the 2 new keys.
for (const lang of ['en-US', 'it-IT']) {
  const resw = read(`BrowserForWP/Strings/${lang}/Resources.resw`);
  check(`${lang} has ReaderFallback`, resw.includes('name="ReaderFallback"'));
  check(`${lang} has LiteRedirects`, resw.includes('name="LiteRedirects"'));
}

if (failures > 0) { console.log(`\n${failures} modern-sites failure(s).`); process.exit(1); }
console.log('\nmodern-sites checks, 0 failure(s)');
