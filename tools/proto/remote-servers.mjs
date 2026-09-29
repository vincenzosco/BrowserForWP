#!/usr/bin/env node
// The executable referee for BrowserForWP.Core/Engine/Remote/RemoteServers.vb.
//
// The rule it pins is the one the project asked for: a primary server by default,
// a secondary that takes over when the primary does not answer, and no third
// option — because "add your own" means replacing the secondary, and a list
// nobody can see the end of is a settings screen nobody finishes.
//
// The thirteen behavioural checks below are a transliteration of the VB's
// Normalize/Order; the four source checks assert the parts of the file a
// behaviour cannot show — that it holds no user-facing prose, that the two roles
// are constants rather than literals scattered through the logic, and that it is
// a rule and not a thing.
import fs from 'node:fs';

let checks = 0;
let failures = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) console.log(`  ✓ ${name}`);
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}

const SOURCE = 'BrowserForWP.Core/Engine/Remote/RemoteServers.vb';
const source = fs.existsSync(SOURCE) ? fs.readFileSync(SOURCE, 'utf8') : '';

// The prefixing rule is `has an authority`, not `contains a colon`. The first
// draft asked whether a scheme-looking token appeared before a colon at all, and
// `render.example.com:8443` matches that: a host and a port read as scheme
// "render.example.com" and path "8443", the scheme is neither https nor http, and
// the server a person just typed SILENTLY vanishes from the settings screen. So
// the test is `scheme://`, and a bare host:port is treated as what it is.
function hasAuthority(text) {
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(text);
}

function Normalize(raw) {
  const text = String(raw ?? '').trim();
  if (!text) return '';
  const withScheme = hasAuthority(text) ? text : `https://${text}`;
  let parsed;
  try { parsed = new URL(withScheme); } catch { return ''; }
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') return '';
  const path = parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/$/, '');
  return parsed.origin + path;
}

function Order(settings) {
  const out = [];
  if (!settings) return out;
  const first = Normalize(settings.primaryUrl);
  const second = Normalize(settings.secondaryUrl);
  if (first) out.push(first);
  if (second && second !== first) out.push(second);
  return out;
}

check('a url with no scheme becomes https',
  Normalize('render.example.com') === 'https://render.example.com');
check('a trailing slash is removed',
  Normalize('https://render.example.com/') === 'https://render.example.com');
check('a path is kept, without its trailing slash',
  Normalize('https://host/bfwp/') === 'https://host/bfwp');
check('a non-web scheme is refused',
  Normalize('ftp://host') === '' && Normalize('file:///tmp') === '');
check('nonsense is refused', Normalize('not a url at all') === '');
check('an empty setting is empty, not an error', Normalize('') === '');
check('a port is kept', Normalize('https://render.example.com:8443') === 'https://render.example.com:8443');
check('a bare host and port is a host and port, not a scheme',
  Normalize('render.example.com:8443') === 'https://render.example.com:8443',
  'otherwise a typed server silently disappears instead of being configured');
check('a bare host with a path keeps the path',
  Normalize('render.example.com/bfwp/') === 'https://render.example.com/bfwp');

check('the primary is tried first',
  Order({ primaryUrl: 'https://a', secondaryUrl: 'https://b' })[0] === 'https://a');
check('the secondary is the fallback',
  Order({ primaryUrl: 'https://a', secondaryUrl: 'https://b' })[1] === 'https://b');
check('with no secondary there is one entry',
  Order({ primaryUrl: 'https://a', secondaryUrl: '' }).length === 1);
check('two identical servers are one entry, not two attempts',
  Order({ primaryUrl: 'https://a', secondaryUrl: 'https://a' }).length === 1);
check('two spellings of one server are one entry, not two attempts',
  Order({ primaryUrl: 'https://a/', secondaryUrl: 'https://a' }).length === 1);
check('with no server configured there is nothing to try',
  Order({ primaryUrl: '', secondaryUrl: '' }).length === 0);

check(`${SOURCE} exists`, source.length > 0);
check('it declares the two roles as constants, not as literals in the logic',
  /Public Const Primary As String = "primary"/.test(source)
  && /Public Const Secondary As String = "secondary"/.test(source));
check('it is uninstantiable', /Private Sub New\(\)/.test(source));
check('it returns resource keys, not sentences (Core holds no user-facing prose)', (() => {
  const literals = [];
  for (const raw of source.split(/\r?\n/)) {
    if (/^\s*'/.test(raw)) continue;
    const body = raw.split("'")[0];
    // VB doubles a quote inside a literal; the scan below is on source text, so
    // a doubled quote would simply end the match early. None of the expected
    // literals contains one.
    for (const m of body.matchAll(/"([^"]*)"/g)) literals.push(m[1]);
  }
  // No literal in this file may contain a space: the two role keywords, the url
  // scheme, the scheme names and the resource keys are all single tokens.
  return literals.length > 0 && literals.every((l) => !/\s/.test(l));
})());

console.log(`\n${checks - failures}/${checks} remote-servers checks passed.`);
if (failures > 0) {
  console.log('The server-choice rule and its source contract do not hold.');
  process.exit(1);
}
console.log('The server-choice rule and its source contract hold.');
