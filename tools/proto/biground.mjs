#!/usr/bin/env node
// Failing-first mirror for the 2026-09-30 big round.
// Must print: biground checks, 0 failure(s). Exits 1 otherwise.
import fs from 'node:fs';
import path from 'node:path';
const ROOT = process.cwd();
const read = (p) => { const f = path.join(ROOT, p); return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : ''; };
let n = 0;
const check = (name, ok, d = '') => { console.log((ok ? '  ok ' + name : '  MISS ' + name) + (ok || !d ? '' : ': ' + d)); if (!ok) n++; };

// ── new Core stores ──
const saved = read('BrowserForWP.Core/Storage/SavedPages.vb');
check('SavedPages.vb with cap 20', saved.includes('MaxEntries As Integer = 20') && saved.includes('RemoveAt'));
const sites = read('BrowserForWP.Core/Storage/SiteSettings.vb');
check('SiteSettings.vb per-host size+images', sites.includes('TextSizePct') && sites.includes('ImagesOff') && sites.includes('Function GetSetting'));
const dial = read('BrowserForWP.Core/Storage/SpeedDial.vb');
check('SpeedDial.vb 8 slots', dial.includes('MaxSlots As Integer = 8'));
const bak = read('BrowserForWP.Core/Storage/BackupManager.vb');
check('BackupManager.vb header+sections', bak.includes('BROWSERFORWP-BACKUP-1') && bak.includes('TryParseBackup'));

// JS backup roundtrip mirror (must equal the VB section scheme)
function buildBackup(sections) {
  const lines = ['BROWSERFORWP-BACKUP-1'];
  for (const k of ['settings', 'history', 'favorites', 'pins', 'saved', 'sites', 'speeddial']) {
    lines.push('[' + k + ']' + Buffer.from(sections[k] ?? '', 'utf8').toString('base64'));
  }
  return lines.join('\n');
}
function tryParseBackup(text) {
  const out = {};
  const lines = String(text ?? '').split('\n');
  if (lines[0] !== 'BROWSERFORWP-BACKUP-1') return null;
  for (const l of lines.slice(1)) {
    const m = /^\[([a-z]+)\](.*)$/.exec(l);
    if (!m) return null;
    out[m[1]] = Buffer.from(m[2], 'base64').toString('utf8');
  }
  return out;
}
const rt = tryParseBackup(buildBackup({ settings: 'a=1\nb=2', history: 'x|y', favorites: '', pins: '', saved: '', sites: '', speeddial: '' }));
check('backup roundtrip', rt !== null && rt.settings === 'a=1\nb=2' && rt.history === 'x|y');
check('backup rejects garbage', tryParseBackup('hello') === null);

// ── caps ──
check('history capped at 50', /MaxEntries As Integer = 50/.test(read('BrowserForWP.Core/Storage/HistoryStore.vb')));
check('favorites capped at 50', /MaxEntries As Integer = 50/.test(read('BrowserForWP.Core/Storage/FavoritesStore.vb')));
check('pins capped at 25', read('BrowserForWP.Net/Tls13/PinStore.vb').includes('MaxPins As Integer = 25'));
check('session tabs capped at 6', /MaxSessionTabs As Integer = 6/.test(read('BrowserForWP.Core/Storage/AppSettings.vb')));

// ── single search default ──
const appSet = read('BrowserForWP.Core/Storage/AppSettings.vb');
check('SearchTemplate property removed', !appSet.includes('Property SearchTemplate'));
check('MigrateSearchTemplate removed', !appSet.includes('MigrateSearchTemplate'));
check('search uses lite default', appSet.includes('DefaultSearchTemplate'));

// ── diagnostics simplification ──
const mainPage = read('BrowserForWP/MainPage.xaml.vb');
check('ParsePage handler removed', !mainPage.includes('ParsePageButton_Click'));
check('IeMode handler kept', mainPage.includes('IeModeButton_Click'));

// ── new shell wiring ──
const xaml = read('BrowserForWP/MainPage.xaml');
for (const nm of ['SavePageButton', 'RetryButton', 'OfflineOverlay', 'OfflineBody', 'OfflineBackButton',
  'SiteHostText', 'TextSmallerButton', 'TextLargerButton', 'ImagesToggle',
  'SpeedDialList', 'SpeedDialAddButton', 'SpeedDialRemoveButton',
  'SavedPagesList', 'DeleteSavedButton', 'BackupButton', 'RestoreButton', 'BackupStatus']) {
  check('xaml has ' + nm, xaml.includes('x:Name="' + nm + '"'));
}
check('search picker removed from xaml', !xaml.includes('SearchEnginePicker'));
check('parse block removed from xaml', !xaml.includes('ParsePageButton'));
check('iemode block kept in xaml', xaml.includes('IeModeButton'));
for (const h of ['SavePageButton_Click', 'RetryButton_Click', 'OfflineBackButton_Click',
  'ImagesToggle_Checked', 'SpeedDialAddButton_Click', 'BackupButton_Click', 'RestoreButton_Click',
  'DeleteSavedButton_Click', 'SavedPagesList_SelectionChanged', 'SpeedDialList_SelectionChanged']) {
  check('handler ' + h, mainPage.includes('Sub ' + h + '('));
}
check('lazy settings population', mainPage.includes('_settingsPopulated'));
check('single-injection guard', mainPage.includes('_injectedToken'));
check('engine runners', read('BrowserForWP.Core/Engine/TridentEngine.vb').includes('ExtractArticleTextAsync'));

// ── languages ──
const catalog = read('BrowserForWP.Localization/LanguageCatalog.vb');
for (const t of ['de-DE', 'fr-FR', 'es-ES', 'ja-JP']) check('catalog has ' + t, catalog.includes(t));
const vbproj = read('BrowserForWP/BrowserForWP.vbproj');
for (const t of ['de-DE', 'fr-FR', 'es-ES', 'ja-JP']) check('vbproj has ' + t, vbproj.includes(t));

// ── resw: 104 keys everywhere ──
const NEWKEYS = ['SavePage', 'SavedPages', 'SavedDelete', 'TextSize', 'Images', 'SpeedDial',
  'SpeedDialAdd', 'SpeedDialRemove', 'Backup', 'Restore', 'BackupDone', 'Retry'];
const DEADKEYS = ['SearchEngineLabel', 'ParseThisPage', 'ParseFailed', 'ParseBoxCount',
  'EngineNative', 'EngineReasonSettingNative'];
const REMOTEKEYS = ['EngineRemote', 'RemoteUseServer', 'RemoteKeys', 'KeyTab', 'KeyEnter',
  'RemoteNotice', 'RemoteServerLabel', 'RemoteTokenLabel'];
const LANGS = ['en-US', 'it-IT', 'de-DE', 'fr-FR', 'es-ES', 'ja-JP'];
const keysets = {};
for (const lang of LANGS) {
  const r = read('BrowserForWP/Strings/' + lang + '/Resources.resw');
  keysets[lang] = new Set([...r.matchAll(/<data name="([^"]+)"/g)].map((m) => m[1]));
  check(lang + ' resw exists', r.length > 0);
}
if (LANGS.every((l) => keysets[l].size > 0)) {
  const ref = [...keysets['en-US']].sort();
  check('en-US has 104 keys (got ' + ref.length + ')', ref.length === 104);
  for (const lang of LANGS.slice(1)) {
    const missing = ref.filter((k) => !keysets[lang].has(k));
    const extra = [...keysets[lang]].filter((k) => !keysets['en-US'].has(k));
    check(lang + ' parity', missing.length === 0 && extra.length === 0,
      'missing:' + missing.join(',') + ' extra:' + extra.join(','));
  }
  for (const k of NEWKEYS) check('new key ' + k, keysets['en-US'].has(k));
  for (const k of DEADKEYS) check('dead key gone: ' + k, !keysets['en-US'].has(k));
  for (const k of REMOTEKEYS) check('remote key kept: ' + k, keysets['en-US'].has(k));
}

if (n) { console.log('\n' + n + ' biground failure(s).'); process.exit(1); }
console.log('\nbiground checks, 0 failure(s)');
