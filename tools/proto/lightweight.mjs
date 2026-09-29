#!/usr/bin/env node
// Content guards for the lightweight round
// (docs/superpowers/plans/2026-09-28-lightweight-browser.md).
// Must print: lightweight checks, 0 failure(s). Exits 1 otherwise.
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

const settings = read('BrowserForWP.Core/Storage/AppSettings.vb');
check('lite homepage default', settings.includes('lite.duckduckgo.com'));
check('night/block/restore/session props',
  settings.includes('NightMode') && settings.includes('BlockTrackers') &&
  settings.includes('RestoreSession') && settings.includes('LastSessionTabs'));
check('single search default, no migration',
  !settings.includes('Property SearchTemplate') && !settings.includes('MigrateSearchTemplate'));
check('session tab cap', /MaxSessionTabs As Integer = 6/.test(settings));

const mainPage = read('BrowserForWP/MainPage.xaml.vb');
check('no google search template', !mainPage.includes('google.com/search'));
check('no bing search template', !mainPage.includes('bing.com/search'));
check('duckduckgo lite is the search default', settings.includes('lite.duckduckgo.com'));
check('tracker block enforced', mainPage.includes('ShouldBlock'));
check('night mode applied', mainPage.includes('SetNightModeAsync'));
check('private mode skips history', /PrivateMode[\s\S]{0,300}history/i.test(mainPage) || mainPage.includes('PrivateMode'));

const history = read('BrowserForWP.Core/Storage/HistoryStore.vb');
check('history capped at 50', /MaxEntries As Integer = 50/.test(history));
const favorites = read('BrowserForWP.Core/Storage/FavoritesStore.vb');
check('favorites capped at 50', /MaxEntries As Integer = 50/.test(favorites));
const pins = read('BrowserForWP.Net/Tls13/PinStore.vb');
check('pins capped at 25', /MaxPins As Integer = 25/.test(pins));

const engine = read('BrowserForWP.Core/Engine/TridentEngine.vb');
check('find runner', engine.includes('FindInPageAsync'));
check('reading runner', engine.includes('EnterReadingModeAsync'));
check('night runner', engine.includes('SetNightModeAsync'));

check('reading script', read('BrowserForWP.Core/Browser/ReadingMode.vb').includes('__bfwpReading'));
check('night css', read('BrowserForWP.Core/Browser/NightMode.vb').includes('bfwp-night'));

const xaml = read('BrowserForWP/MainPage.xaml');
check('diagnostics overlay split', xaml.includes('DiagnosticsOverlay'));
check('find bar', xaml.includes('FindBox'));
check('share button', xaml.includes('ShareButton'));
check('reading button', xaml.includes('ReadingButton'));

for (const lang of ['en-US', 'it-IT']) {
  const resw = read(`BrowserForWP/Strings/${lang}/Resources.resw`);
  for (const key of ['ReadingMode', 'NightMode', 'Find', 'Share', 'PrivateMode',
    'BlockTrackers', 'RestoreSession', 'Diagnostics', 'FindNoMatch', 'BlockedTracker']) {
    check(`${lang} has ${key}`, resw.includes(`name="${key}"`));
  }
}

const sln = read('BrowserForWP.sln');
check('tests registered in sln',
  sln.includes('BrowserForWP.Core.Tests') && sln.includes('BrowserForWP.Crypto.Tests'));
check('no Release build for Core.Tests',
  !/\{C0DE0004-0004-4A2B-9C3D-1B2C3D4E5F04\}\.Release[^\n]*Build\.0/.test(sln));
check('no Release build for Crypto.Tests',
  !/\{C0DE0005-0005-4A2B-9C3D-1B2C3D4E5F05\}\.Release[^\n]*Build\.0/.test(sln));

if (failures > 0) { console.log(`\n${failures} lightweight failure(s).`); process.exit(1); }
console.log('\nlightweight checks, 0 failure(s)');
