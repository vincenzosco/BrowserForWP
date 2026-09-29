#!/usr/bin/env node
// Failing-first guards for the shell bugfix round (see
// docs/superpowers/plans/2026-09-28-shell-bugfix.md).
// Asserts the VB re-entrancy guards exist AND simulates the event-loop model
// to prove the unguarded pattern loops while the guarded one settles.
// Must print: shell-guard checks, 0 failure(s). Exits 1 otherwise.
import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();
let failures = 0;
function check(name, ok, detail = '') {
  if (ok) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}

const mainPage = fs.readFileSync(
  path.join(ROOT, 'BrowserForWP', 'MainPage.xaml.vb'), 'utf8');
const sln = fs.readFileSync(path.join(ROOT, 'BrowserForWP.sln'), 'utf8');

// 1. Guard fields exist.
check('guard field _populatingLanguage', mainPage.includes('_populatingLanguage'));
check('guard field _refreshingTabs', mainPage.includes('_refreshingTabs'));

// 2. Completed URL comes from the event args, not the control object.
const completedSub = mainPage.slice(mainPage.indexOf('Sub OnNavigationCompleted'));
check('OnNavigationCompleted uses e.Uri',
  completedSub.includes('e.Uri.ToString()'));
check('OnNavigationCompleted no longer uses Source.ToString()',
  !completedSub.slice(0, 800).includes('_engine.Source.ToString()'));

// 3. Handlers early-return while repopulating.
check('LanguagePicker handler respects guard',
  /LanguagePicker_SelectionChanged[\s\S]{0,400}_populatingLanguage/.test(mainPage));
check('TabsList handler respects guard',
  /TabsList_SelectionChanged[\s\S]{0,400}_refreshingTabs/.test(mainPage));

// 4. Stale list selections are cleared so a repeat tap navigates again.
check('History selection cleared after navigate',
  /HistoryList_SelectionChanged[\s\S]{0,800}HistoryList\.SelectedIndex = -1/.test(mainPage));
check('Favorites selection cleared after navigate',
  /FavoritesList_SelectionChanged[\s\S]{0,800}FavoritesList\.SelectedIndex = -1/.test(mainPage));

// 5. Event-loop model: unguarded populate/select recurses, guarded settles.
function simulate({ guarded }) {
  let executions = 0;
  let navigations = 0;
  let guard = false;
  function handler() {
    if (guarded && guard) return;
    executions++;
    if (executions > 50) return; // runaway: stop the model, count it
    navigations++;
    populate(); // handler repopulates, as the buggy code does
  }
  function populate() {
    if (guarded) guard = true;
    try {
      handler(); // programmatic SelectedIndex fires the event
    } finally {
      if (guarded) guard = false;
    }
  }
  handler(); // the initial real user gesture
  return { executions, navigations };
}
const bad = simulate({ guarded: false });
const good = simulate({ guarded: true });
check('unguarded model recurses (proves the bug is real)', bad.executions > 1,
  `got ${bad.executions} executions`);
check('guarded model settles to exactly 1 execution', good.executions === 1,
  `got ${good.executions} executions`);

// 6. Both test projects are registered in the solution.
check('sln builds BrowserForWP.Core.Tests', sln.includes('BrowserForWP.Core.Tests'));
check('sln builds BrowserForWP.Crypto.Tests', sln.includes('BrowserForWP.Crypto.Tests'));

if (failures > 0) {
  console.log(`\n${failures} shell-guard failure(s).`);
  process.exit(1);
}
console.log('\nshell-guard checks, 0 failure(s)');
