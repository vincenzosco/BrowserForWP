#!/usr/bin/env node
// Executable mirror of the Core logic checks in
// tests/BrowserForWP.Core.Tests/CoreLogicTests.vb.
//
// WHY THIS FILE EXISTS: those VB test projects are now registered in
// BrowserForWP.sln and the guest build compiles them, but nothing EXECUTES them.
// A WP8.1 class library built for ARM cannot run on the desktop, and this
// project has no handset and no Windows Phone emulator. "Compiled" is therefore
// not "tested", and the difference matters: the CoreLogicTests search assertion
// named a URL that the lite-first default had replaced and nobody noticed, for
// exactly that reason.
//
// So the same assertions run here, against a faithful transliteration of the VB.
// Keep this file and tests/BrowserForWP.Core.Tests/CoreLogicTests.vb in step: if
// one gains a case, the other must too. Where a mirror already exists for a
// narrower slice (tools/proto/pinstore.mjs, useragents.mjs, trackerblock.mjs)
// those stay as they are; this file covers the Core logic they do not.
//
// Must print: core-logic checks, 0 failure(s). Exits 1 otherwise.

let failures = 0;
let checks = 0;

function check(name, ok, detail = '') {
  checks += 1;
  if (ok) {
    console.log(`  ✓ ${name}`);
  } else {
    console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`);
    failures += 1;
  }
}

// ── BrowserForWP.Core/Browser/AddressNormalizer.vb ─────────────────────────
const SEARCH_PREFIX = 'https://duckduckgo.com/?q=';
const HOST_WITH_OPTIONAL_PARTS = /^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*(:[0-9]{1,5})?([/?#].*)?$/;
const DOTTED_HOST = /^([A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,}$/;

function isNavigableHttpUrl(text) {
  let parsed;
  try {
    parsed = new URL(text);
  } catch {
    return false;
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;
  return parsed.hostname.length > 0;
}

function normalize(input) {
  const text = String(input ?? '').trim();
  if (text.length === 0) return { url: SEARCH_PREFIX, isSearch: true, isValid: true };

  const colon = text.indexOf(':');
  const slash = text.indexOf('/');
  if (colon > 0 && (slash < 0 || colon < slash)) {
    const scheme = text.slice(0, colon).toLowerCase();
    if ((scheme === 'http' || scheme === 'https') && isNavigableHttpUrl(text)) {
      return { url: text, isSearch: false, isValid: true };
    }
    return { url: SEARCH_PREFIX + encodeURIComponent(text), isSearch: true, isValid: false };
  }

  if (HOST_WITH_OPTIONAL_PARTS.test(text)) {
    const authority = text.split(/[/?#]/)[0];
    const hostOnly = authority.split(':')[0];
    if (DOTTED_HOST.test(hostOnly)) {
      return { url: 'https://' + text, isSearch: false, isValid: true };
    }
  }

  if (text.toLowerCase().startsWith('localhost')) {
    return { url: 'http://' + text, isSearch: false, isValid: true };
  }

  return { url: SEARCH_PREFIX + encodeURIComponent(text), isSearch: true, isValid: true };
}

const searchTarget = normalize('hello world');
check('search classified', searchTarget.isSearch === true);

const urlTarget = normalize('example.com');
check('bare host https', urlTarget.url === 'https://example.com', urlTarget.url);

const refusedTarget = normalize('javascript:alert(1)');
check('javascript refused', refusedTarget.isValid === false);

// Every scheme that must never reach the WebView. The VB test exercises
// javascript: only; these are the same branch and cost nothing to pin down.
for (const refused of ['javascript:alert(1)', 'data:text/html,x', 'file:///etc/passwd', 'ms-appx:///x']) {
  check(`scheme refused: ${refused.split(':')[0]}`, normalize(refused).isValid === false);
}
// The VB comment says localhost is "unambiguous, and is useful in development",
// but the branch only fires for a host with NO port: "localhost:8080" contains a
// colon before any slash, so the scheme probe reads "localhost" as the scheme,
// rejects it, and refuses the input. Recorded here as the measured behaviour
// instead of pretending the comment's promise holds. Left unfixed on purpose:
// an AppContainer cannot reach loopback on this platform anyway, and
// AddressNormalizer is security-relevant parsing that a merge review has no
// business rewriting without a failing device test to justify it.
check('localhost without port allowed', normalize('localhost/x').url === 'http://localhost/x', normalize('localhost/x').url);
check('localhost with port is refused (documented limit)', normalize('localhost:8080/x').isValid === false);
check('empty becomes search', normalize('   ').isSearch === true);

// ── BrowserForWP.Core/Browser/BrowserSession.vb — TabModel ─────────────────
class TabModel {
  constructor() {
    this._history = [];
    this._index = -1;
    this.title = '';
  }
  get url() {
    if (this._index < 0 || this._index >= this._history.length) return '';
    return this._history[this._index];
  }
  get canGoBack() {
    return this._index > 0;
  }
  get canGoForward() {
    return this._index >= 0 && this._index < this._history.length - 1;
  }
  pushHistory(url) {
    if (!url) return;
    if (this._index >= 0 && this._index < this._history.length && this._history[this._index] === url) return;
    if (this._index < this._history.length - 1) {
      this._history.splice(this._index + 1, this._history.length - this._index - 1);
    }
    this._history.push(url);
    this._index = this._history.length - 1;
  }
  replaceCurrent(url) {
    if (!url) return;
    if (this._index < 0) {
      this.pushHistory(url);
      return;
    }
    this._history[this._index] = url;
  }
  back() {
    if (!this.canGoBack) return this.url;
    this._index -= 1;
    return this.url;
  }
  forward() {
    if (!this.canGoForward) return this.url;
    this._index += 1;
    return this.url;
  }
  peekBack() {
    if (!this.canGoBack) return null;
    return this._history[this._index - 1];
  }
}

const tab = new TabModel();
tab.pushHistory('https://a.example/');
tab.pushHistory('https://b.example/');
check('tab can go back', tab.canGoBack === true);
tab.back();
check('tab back url', tab.url === 'https://a.example/', tab.url);

// Pushing after a back truncates the forward tail: the branch abandoned.
tab.pushHistory('https://c.example/');
check('push truncates forward tail', tab.url === 'https://c.example/' && tab.canGoForward === false);
check('no duplicate adjacent entry', (() => {
  const t = new TabModel();
  t.pushHistory('https://a.example/');
  t.pushHistory('https://a.example/');
  return t.peekBack() === null;
})());

// ── BrowserForWP.Core/Browser/UserAgents.vb ───────────────────────────────
const MobileDefault = 'Mozilla/5.0 (compatible; MSIE 10.0; Windows Phone 8.1; Trident/6.0; BrowserForWP/1.0 Mobile)';
const DesktopWindows = 'Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko';
const effectiveUserAgent = (desktop) => (desktop ? DesktopWindows : MobileDefault);

class BrowserSession {
  constructor() {
    this._tabs = [];
    this._activeIndex = -1;
    this.desktopMode = false;
    this.privateMode = false;
  }
  get effectiveUserAgent() {
    return effectiveUserAgent(this.desktopMode);
  }
  get activeTab() {
    if (this._tabs.length === 0) this.newTab();
    if (this._activeIndex < 0 || this._activeIndex >= this._tabs.length) this._activeIndex = this._tabs.length - 1;
    return this._tabs[this._activeIndex];
  }
  newTab() {
    const t = new TabModel();
    this._tabs.push(t);
    this._activeIndex = this._tabs.length - 1;
    return t;
  }
  activateTab(index) {
    if (index < 0 || index >= this._tabs.length) return;
    this._activeIndex = index;
  }
  closeActiveTab() {
    if (this._tabs.length === 0) return;
    this._tabs.splice(this._activeIndex, 1);
    if (this._tabs.length === 0) {
      this.newTab();
      return;
    }
    if (this._activeIndex >= this._tabs.length) this._activeIndex = this._tabs.length - 1;
    if (this._activeIndex < 0) this._activeIndex = 0;
  }
}

const session = new BrowserSession();
session.newTab();
check('session active tab', session.activeTab !== null);
check('default UA mobile', session.effectiveUserAgent === MobileDefault);
session.desktopMode = true;
check('desktop UA', session.effectiveUserAgent === DesktopWindows);
check('UA helper mobile', effectiveUserAgent(false) === MobileDefault);

// A session always keeps at least one tab, however many are closed.
const closing = new BrowserSession();
closing.newTab();
closing.closeActiveTab();
check('closing the last tab leaves one', closing.activeTab !== null);

// ── BrowserForWP.Core/Storage/AppSettings.vb ──────────────────────────────
const DefaultHomepage = 'https://lite.duckduckgo.com/lite/';
const DefaultSearchTemplate = 'https://lite.duckduckgo.com/lite/?q={q}';
const DefaultDohUrl = 'https://cloudflare-dns.com/dns-query';
const MaxSessionTabs = 6;

class AppSettings {
  constructor() {
    this.homepage = DefaultHomepage;
    this.dohUrl = DefaultDohUrl;
    this.desktopMode = false;
    this.languageOverride = null;
    this.nightMode = false;
    this.blockTrackers = true;
    this.restoreSession = false;
    this.liteRedirects = true;
    this.lastSessionTabs = '';
  }
  searchUrlFor(query) {
    return DefaultSearchTemplate.replace('{q}', encodeURIComponent(String(query ?? '')));
  }
  getSessionTabs() {
    if (!this.lastSessionTabs) return [];
    return this.lastSessionTabs
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.toLowerCase().startsWith('http://') || l.toLowerCase().startsWith('https://'))
      .slice(0, MaxSessionTabs);
  }
  setSessionTabs(urls) {
    const kept = [];
    for (const url of urls ?? []) {
      if (!url) continue;
      kept.push(String(url).trim().slice(0, 300));
      if (kept.length >= MaxSessionTabs) break;
    }
    this.lastSessionTabs = kept.join('\n');
  }
  saveToMap() {
    return {
      homepage: this.homepage || DefaultHomepage,
      dohUrl: this.dohUrl || DefaultDohUrl,
      desktopMode: this.desktopMode ? '1' : '0',
      languageOverride: this.languageOverride || '',
      nightMode: this.nightMode ? '1' : '0',
      blockTrackers: this.blockTrackers ? '1' : '0',
      restoreSession: this.restoreSession ? '1' : '0',
      liteRedirects: this.liteRedirects ? '1' : '0',
      lastSessionTabs: this.lastSessionTabs || '',
    };
  }
  loadFromMap(map) {
    if (!map) return;
    if ('homepage' in map) this.homepage = map.homepage || DefaultHomepage;
    if ('dohUrl' in map) this.dohUrl = map.dohUrl || DefaultDohUrl;
    if ('desktopMode' in map) this.desktopMode = map.desktopMode === '1';
    if ('languageOverride' in map) this.languageOverride = map.languageOverride || null;
    if ('nightMode' in map) this.nightMode = map.nightMode === '1';
    if ('blockTrackers' in map) this.blockTrackers = map.blockTrackers === '' ? true : map.blockTrackers === '1';
    if ('restoreSession' in map) this.restoreSession = map.restoreSession === '1';
    if ('liteRedirects' in map) this.liteRedirects = map.liteRedirects === '' ? true : map.liteRedirects === '1';
    if ('lastSessionTabs' in map) this.lastSessionTabs = map.lastSessionTabs || '';
  }
}

const appSettings = new AppSettings();
// The default is the LITE endpoint. This assertion is the one that was wrong in
// CoreLogicTests.vb: it named the heavy duckduckgo.com URL the lite default had
// replaced, and because nothing executed the VB, only the build compiled.
check('search url', appSettings.searchUrlFor('hello world') === 'https://lite.duckduckgo.com/lite/?q=hello%20world', appSettings.searchUrlFor('hello world'));
check('search default is lite', appSettings.searchUrlFor('hello world').startsWith('https://lite.duckduckgo.com/'));
check('homepage default is lite', appSettings.homepage === DefaultHomepage);
const hostMap = appSettings.saveToMap();
const reloaded = new AppSettings();
reloaded.loadFromMap(hostMap);
check('settings roundtrip', reloaded.homepage === appSettings.homepage);
check('single search default', appSettings.searchUrlFor('x') === DefaultSearchTemplate.replace('{q}', 'x'));
check('session tab cap', (() => {
  const s = new AppSettings();
  s.setSessionTabs(Array.from({ length: 25 }, (_, i) => `https://h${i}.example/`));
  return s.getSessionTabs().length === MaxSessionTabs;
})());
check('non-http tab dropped', (() => {
  const s = new AppSettings();
  s.setSessionTabs(['https://ok.example/', 'javascript:alert(1)', 'file:///x']);
  const back = s.getSessionTabs();
  return back.length === 1 && back[0] === 'https://ok.example/';
})());

// ── BrowserForWP.Core/Storage/HistoryStore.vb ─────────────────────────────
const HistoryMaxEntries = 50;
class HistoryStore {
  constructor() {
    this._entries = [];
  }
  get count() {
    return this._entries.length;
  }
  add(url, title) {
    if (!url) return;
    this._entries.push({ url, title: title ?? '', ticks: 0 });
    while (this._entries.length > HistoryMaxEntries) this._entries.shift();
  }
  list() {
    return this._entries.slice().reverse();
  }
  serialize() {
    return this._entries
      .map((e) => `${e.ticks}|${e.url.replace(/\|/g, '/')}|${e.title.replace(/\|/g, ' ')}`)
      .join('\n');
  }
  parse(saved) {
    this._entries = [];
    if (!saved) return;
    for (const rawLine of saved.split('\n')) {
      if (!rawLine) continue;
      const pipe = rawLine.indexOf('|');
      if (pipe <= 0) continue;
      const ticksPart = rawLine.slice(0, pipe);
      const rest = rawLine.slice(pipe + 1);
      const second = rest.indexOf('|');
      const urlPart = second < 0 ? rest : rest.slice(0, second);
      const titlePart = second < 0 ? '' : rest.slice(second + 1);
      if (!/^-?\d+$/.test(ticksPart)) continue;
      if (!urlPart) continue;
      this._entries.push({ url: urlPart, title: titlePart, ticks: Number(ticksPart) });
    }
    while (this._entries.length > HistoryMaxEntries) this._entries.shift();
  }
}

const history = new HistoryStore();
history.add('https://a.example/', 'A');
const savedHistory = history.serialize();
const history2 = new HistoryStore();
history2.parse(savedHistory);
check('history roundtrip', history2.count === 1);
check('history capped at 50', (() => {
  const h = new HistoryStore();
  for (let i = 0; i < 150; i += 1) h.add(`https://h${i}.example/`, 'T');
  return h.count === HistoryMaxEntries;
})());
check('history pipe stripped', (() => {
  const h = new HistoryStore();
  h.add('https://a.example/?x=1|2', 'ti|tle');
  // ticks|url|title: three fields, because the pipes inside url and title are
  // replaced (/ in the url, a space in the title) before serialization.
  const parts = h.serialize().split('|');
  return parts.length === 3 && parts[1] === 'https://a.example/?x=1/2' && parts[2] === 'ti tle';
})());

// ── BrowserForWP.Core/Storage/FavoritesStore.vb ───────────────────────────
const FavoritesMaxEntries = 50;
class FavoritesStore {
  constructor() {
    this._items = new Map();
  }
  get count() {
    return this._items.size;
  }
  add(url, title) {
    if (!url) return false;
    if (this._items.has(url)) return false;
    if (this._items.size >= FavoritesMaxEntries) return false;
    this._items.set(url, title || url);
    return true;
  }
  remove(url) {
    if (!url) return false;
    return this._items.delete(url);
  }
  contains(url) {
    return Boolean(url) && this._items.has(url);
  }
  serialize() {
    return [...this._items.entries()]
      .map(([url, title]) => `${url.replace(/\|/g, '/')}|${title.replace(/\|/g, ' ')}`)
      .join('\n');
  }
  parse(saved) {
    this._items.clear();
    if (!saved) return;
    for (const rawLine of saved.split('\n')) {
      if (!rawLine) continue;
      const pipe = rawLine.indexOf('|');
      if (pipe <= 0) continue;
      const urlPart = rawLine.slice(0, pipe);
      if (!urlPart) continue;
      this._items.set(urlPart, rawLine.slice(pipe + 1));
    }
  }
}

const favorites = new FavoritesStore();
check('fav add', favorites.add('https://a.example/', 'A') === true);
check('fav dup rejected', favorites.add('https://a.example/', 'A') === false);
check('fav contains', favorites.contains('https://a.example/') === true);
check('fav remove', favorites.remove('https://a.example/') === true && favorites.contains('https://a.example/') === false);
check('favorites capped at 50', (() => {
  const f = new FavoritesStore();
  for (let i = 0; i < 150; i += 1) f.add(`https://f${i}.example/`, 'T');
  return f.count === FavoritesMaxEntries;
})());

// ── BrowserForWP.Net/Tls13/PinStore.vb ────────────────────────────────────
const PinMaxPins = 25;
function normalizeHost(hostName) {
  if (!hostName) return '';
  let clean = String(hostName).trim().toLowerCase();
  const colon = clean.indexOf(':');
  if (colon >= 0) clean = clean.slice(0, colon);
  while (clean.endsWith('.')) clean = clean.slice(0, -1);
  return clean;
}
class PinStore {
  constructor() {
    this._pins = new Map();
  }
  get count() {
    return this._pins.size;
  }
  add(host, pin) {
    const clean = normalizeHost(host);
    if (!clean) throw new Error('host required');
    if (!pin) throw new Error('pin required');
    if (!this._pins.has(clean) && this._pins.size >= PinMaxPins) throw new Error('pin store full');
    this._pins.set(clean, pin);
  }
  remove(host) {
    const clean = normalizeHost(host);
    if (!clean) return false;
    return this._pins.delete(clean);
  }
  tryGet(host) {
    const clean = normalizeHost(host);
    if (!clean) return null;
    return this._pins.has(clean) ? this._pins.get(clean) : null;
  }
  contains(host) {
    return this.tryGet(host) !== null;
  }
  verify(host, presented) {
    const expected = this.tryGet(host);
    if (expected === null) return true;
    if (!presented) return false;
    return expected === presented;
  }
}

check('pin host normalize', normalizeHost('Example.COM:443') === 'example.com', normalizeHost('Example.COM:443'));
check('pin host trailing dot', normalizeHost('example.com.') === 'example.com');
const pins = new PinStore();
pins.add('Example.com', 'abc123');
check('pin match', pins.verify('example.com', 'abc123') === true);
check('pin mismatch', pins.verify('example.com', 'zzz') === false);
check('no pin passes', pins.verify('other.com', null) === true);
check('pin remove', pins.remove('example.com') === true && pins.contains('example.com') === false);
check('pins capped at 25', (() => {
  const p = new PinStore();
  for (let i = 0; i < 150; i += 1) {
    // Add THROWS once the store is full (InvalidOperationException in VB),
    // rather than silently dropping the pin. Mirrored, not softened.
    try {
      p.add(`h${i}.example`, `p${i}`);
    } catch {
      break;
    }
  }
  return p.count === PinMaxPins;
})());

// ── BrowserForWP.Net/Tls13/CertificateValidator.vb — MatchHostname ────────
function matchHostname(dnsNames, hostName) {
  if (!hostName) throw new Error('host required');
  if (!dnsNames || dnsNames.length === 0) return { isMatch: false, matchedName: null };
  const host = hostName.toLowerCase();
  for (const name of dnsNames) {
    if (name == null) continue;
    const pattern = name.trim().toLowerCase();
    if (pattern.length === 0) continue;
    if (pattern === host) return { isMatch: true, matchedName: pattern };
    if (pattern.startsWith('*.')) {
      const suffix = pattern.slice(2);
      const firstDot = host.indexOf('.');
      if (firstDot > 0 && host.slice(firstDot + 1) === suffix) return { isMatch: true, matchedName: pattern };
    }
  }
  return { isMatch: false, matchedName: null };
}

const hostNames = ['*.example.com'];
check('wildcard one label', matchHostname(hostNames, 'a.example.com').isMatch === true);
check('wildcard not two labels', matchHostname(hostNames, 'a.b.example.com').isMatch === false);
check('wildcard never bare domain', matchHostname(hostNames, 'example.com').isMatch === false);
check('exact SAN matches', matchHostname(['example.com'], 'example.com').isMatch === true);
check('no SAN fails closed', matchHostname([], 'example.com').isMatch === false);

// ── BrowserForWP.Localization/LanguageCatalog.vb ──────────────────────────
const SupportedTags = ['en-US', 'it-IT'];
function catalogNormalize(candidate) {
  if (!candidate) return null;
  const primary = candidate.split('-')[0].toLowerCase();
  if (!primary || primary === 'iv') return null;
  for (const tag of SupportedTags) {
    if (tag.split('-')[0].toLowerCase() === primary) return tag;
  }
  return null;
}
function catalogMatch(requested) {
  if (requested) {
    for (const candidate of requested) {
      const tag = catalogNormalize(candidate);
      if (tag) return tag;
    }
  }
  return 'en-US';
}

check('lang it', catalogMatch(['it-IT']) === 'it-IT');
check('lang fallback', catalogMatch(['xx']) === 'en-US');
check('lang primary subtag', catalogMatch(['it']) === 'it-IT');
check('lang order authoritative', catalogMatch(['en-US', 'it-IT']) === 'en-US');
check('en-US never removed', SupportedTags[0] === 'en-US');

// ── The engine-choice rule (mirror of CoreLogicTests.vb) ──────────────────
// tools/proto/engine-choice.mjs refuses this exhaustively; these are the rows
// the compiled half must agree on too, and the third is the one that matters.
function chooseEngine(setting, measured, missing) {
  const wanted = setting === 'trident' || setting === 'remote' ? setting : 'auto';
  if (wanted === 'remote') return 'remote';
  if (wanted === 'trident') return 'trident';
  if (!measured) return 'trident';
  return missing >= 8 ? 'remote' : 'trident';
}
check('engine choice: explicit remote wins with no measurement',
  chooseEngine('remote', false, 0) === 'remote');
check('engine choice: explicit trident wins over a broken probe',
  chooseEngine('trident', true, 99) === 'trident');
check('engine choice: auto never switches on an absent measurement',
  chooseEngine('auto', false, 99) === 'trident');
check('engine choice: auto switches at the threshold',
  chooseEngine('auto', true, 8) === 'remote');

// ── The three engine shapes behind EngineCapabilities.NeedsPolyfillLayer ──
const needsPolyfillLayer = (scripting, modernJs) => scripting && !modernJs;
check('capabilities: an IE11-shaped engine needs the polyfill layer',
  needsPolyfillLayer(true, false) === true);
check('capabilities: a modern engine does not',
  needsPolyfillLayer(true, true) === false);
check('capabilities: an engine with no script host does not',
  needsPolyfillLayer(false, false) === false);

// ── The remote render servers: a primary, a secondary, and nothing else ────
// Mirror of RemoteServers.vb. The port case is the one that cost a round: a
// "has a scheme" test that only looked for a colon read `render.example.com:8443`
// as scheme "render.example.com", refused it, and made a typed server disappear
// from the settings screen with no message at all. A colon is not a scheme.
const normalizeServer = (raw) => {
  const text = String(raw ?? '').trim();
  if (!text) return '';
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `https://${text}`;
  let parsed;
  try { parsed = new URL(withScheme); } catch { return ''; }
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') return '';
  return parsed.origin + (parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/$/, ''));
};
const orderServers = (primary, secondary) => {
  const first = normalizeServer(primary);
  const second = normalizeServer(secondary);
  const out = [];
  if (first) out.push(first);
  if (second && second !== first) out.push(second);
  return out;
};
const tokenFor = (url, secondaryUrl, primaryToken, secondaryToken) =>
  normalizeServer(url) === normalizeServer(secondaryUrl) && secondaryToken
    ? secondaryToken
    : primaryToken;

const orderedServers = orderServers('render.example.com', 'https://backup.example.com/');
check('servers: two configured', orderedServers.length === 2);
check('servers: the primary is normalized first',
  orderedServers[0] === 'https://render.example.com');
check('servers: the secondary is normalized',
  orderedServers[1] === 'https://backup.example.com');
check('servers: a non-web scheme is refused', normalizeServer('file:///tmp') === '');
check('servers: a bare host and port is a host and port',
  normalizeServer('render.example.com:8443') === 'https://render.example.com:8443');
check('servers: the secondary falls back to the primary token',
  tokenFor('https://backup.example.com', 'https://backup.example.com', 'primary-token', '') === 'primary-token');

// ── Summary ───────────────────────────────────────────────────────────────
if (failures > 0) {
  console.log(`\n${failures} core-logic failure(s) out of ${checks}.`);
  process.exit(1);
}
console.log(`\ncore-logic checks, 0 failure(s) (${checks} assertions).`);
console.log('This is the executable half of tests/BrowserForWP.Core.Tests; the VB project compiles but cannot run off-device.');
