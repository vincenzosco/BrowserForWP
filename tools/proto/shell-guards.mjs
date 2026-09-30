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

// 7. The remote engine's reporting chain, which ends at this shell.
//
// `RemoteEngine` reports through events, and MainPage is the subscriber. A message
// the client has no arm for used to be dropped with nothing said, and a handler
// that threw while a connection was closing used to be discarded by an empty Catch
// inside the read loop -- which is the END of the report chain: asking the same
// handler again asks the one that just threw, and a read loop owns no screen. So
// the shell is where a report that cannot be applied becomes visible, and the
// three links below are the whole of it.
const channel = fs.readFileSync(
  path.join(ROOT, 'BrowserForWP', 'Engine', 'RemoteChannel.vb'), 'utf8');
const engine = fs.readFileSync(
  path.join(ROOT, 'BrowserForWP', 'Engine', 'RemoteEngine.vb'), 'utf8');

// One method's own text, from its signature to the terminator the CALLER names,
// because the member indent differs between the app (4, and the method is a `Sub`)
// and the libraries (8, and it is a `Function`). A `[\s\S]{0,n}` window reaching
// into the NEXT method would credit that method's guard to this one -- measured in
// tools/check-vb.mjs's hop group, where exactly that made a check green on a
// deleted guard.
function subBody(source, signature, endMark = '\n    End Sub') {
  const at = source.indexOf(signature);
  if (at < 0) return '';
  const end = source.indexOf(endMark, at);
  return end < 0 ? '' : source.slice(at, end);
}

check('the read loop refuses a sealed frame that is not a server type',
  /If Not RemoteProtocol\.IsServerMessage\(frame\.Type\) Then/.test(channel)
  && /frame\.Type\.ToString\("X2"\)/.test(channel),
  'without this an unknown type is dropped by the handler, and the byte it carried is never named');

// The engine's own bottom arm. The read loop above is the FIRST place a type is
// refused and this is the second, which matters because the first version of this
// Select Case had no Else at all: an unknown type fell out of the bottom of the
// handler, the screen did not change, and nothing said why. Deleting ONLY this arm
// leaves the read loop's guard intact, so nothing else here would notice.
const serverHandler = subBody(engine, 'Private Async Function OnServerMessage(',
  '\n        End Function');
check('the engine names an unknown type instead of falling out of its Select',
  /Case Else/.test(serverHandler)
  && /RaiseEvent Navigated\(Me, Failed\("ErrorPageFailed"/.test(serverHandler)
  && /messageType\.ToString\("X2"\)/.test(serverHandler),
  'a Select Case with no Else is where an unknown message used to disappear in silence');

const closeGuard = (() => {
  const at = channel.indexOf('onClosed(reason)');
  if (at < 0) return '';
  const end = channel.indexOf('End Try', at);
  return end < 0 ? '' : channel.slice(at, end);
})();
check('a close callback that threw is kept and carried, not discarded',
  /Catch ex As Exception/.test(closeGuard)
  && /_callbackFailure = ex\.Message/.test(closeGuard)
  && /onClosed\(reason & "/.test(closeGuard),
  'an empty Catch here loses the reason the connection ended, which is the one thing the shell was being told');

const navigated = subBody(mainPage, 'Private Sub OnRemoteNavigated(');
check('the shell guards the engine report, so it lands on this screen',
  /Try[\s\S]*ApplyRemoteNavigation\(e\)/.test(navigated)
  && /Catch ex As Exception/.test(navigated)
  && /EngineReasonReportFailed/.test(navigated)
  && /ErrorText\.Visibility = Visibility\.Visible/.test(navigated),
  'the last link of a report chain is the only one with a screen; a report that can vanish is the defect');

if (failures > 0) {
  console.log(`\n${failures} shell-guard failure(s).`);
  process.exit(1);
}
console.log('\nshell-guard checks, 0 failure(s)');
