#!/usr/bin/env node
// Rules for turning an HTTP response into document text, mirrored from
// BrowserForWP/Diagnostics/NetDocumentFetcher.vb and
// BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb -- plus, since the tree-wide
// audit round, the two untrusted-input rules in HttpClient13 (a url that would
// splice a second request, a negative Content-Length or chunk size that would size
// an array as count - 1) and the DoH resolver's very deliberate A/AAAA asymmetry.
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

// The two negative assertions below read whole files, comments included -- and
// both files document the rule they are checked against. Without this, the
// comment explaining WHY no Accept-Encoding is sent fails the check that
// asserts it is not sent, and the seam fails for naming the library it must not
// reference. The check would forbid documenting the rule it enforces.
//
// Comments only, never string literals: a real "Accept-Encoding" is a string
// literal in code, so stripping strings would make the check unfalsifiable.
// A ' inside a string does not start a comment.
function stripVbComments(src) {
  let out = '';
  for (let i = 0; i < src.length; i += 1) {
    const c = src[i];
    if (c === '"') {
      out += c;
      i += 1;
      while (i < src.length) {
        out += src[i];
        if (src[i] === '"') {
          if (src[i + 1] === '"') { out += src[i + 1]; i += 2; continue; }
          break;
        }
        i += 1;
      }
      continue;
    }
    if (c === "'") {
      while (i < src.length && src[i] !== '\n') i += 1;
      out += '\n';
      continue;
    }
    out += c;
  }
  return out;
}

// Negative control for the stripper. Without this pair the two checks below
// would pass even if the stripper deleted the whole file -- which is precisely
// the vacuous-negative failure this repository has already been bitten by.
const probeComment = stripVbComments("' no Accept-Encoding here\nDim x As Integer = 1\n");
const probeCode = stripVbComments('Dim h As String = "Accept-Encoding"\n');
check('the stripper drops the header when only a comment names it', !/Accept-Encoding/i.test(probeComment));
check('the stripper keeps the header when code names it', /Accept-Encoding/i.test(probeCode));

// Content-Type: "text/html; charset=UTF-8" -> media type + charset.
function parseContentType(header) {
  if (!header) return { type: '', charset: '' };
  const parts = String(header).split(';');
  const type = parts[0].trim().toLowerCase();
  let charset = '';
  for (let i = 1; i < parts.length; i += 1) {
    const p = parts[i].trim();
    if (p.toLowerCase().startsWith('charset=')) charset = p.slice(8).trim().replace(/^"|"$/g, '');
  }
  return { type, charset: charset.toLowerCase() };
}
check('plain html', parseContentType('text/html').type === 'text/html');
check('html with charset', (() => {
  const r = parseContentType('text/html; charset=UTF-8');
  return r.type === 'text/html' && r.charset === 'utf-8';
})());
check('quoted charset', parseContentType('text/html; charset="ISO-8859-1"').charset === 'iso-8859-1');
check('empty header is empty', parseContentType('').type === '');
check('missing header does not throw', parseContentType(undefined).charset === '');

// Only these two media types are documents for this engine.
function isHtmlType(type) {
  return type === 'text/html' || type === 'application/xhtml+xml';
}
check('text/html is a document', isHtmlType('text/html') === true);
check('application/xhtml+xml is a document', isHtmlType('application/xhtml+xml') === true);
check('image/png is not', isHtmlType('image/png') === false);
check('empty is not', isHtmlType('') === false);

// A header lookup must be case-insensitive: servers send any casing they like.
function headerValue(headers, name) {
  if (!headers) return '';
  const wanted = name.toLowerCase();
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() === wanted) return headers[key];
  }
  return '';
}
check('header lookup is case-insensitive', headerValue({ 'content-type': 'text/html' }, 'Content-Type') === 'text/html');
check('missing header yields empty', headerValue({}, 'Content-Type') === '');

// Charset selection. The VB compiles either way -- Encoding.GetEncoding is in
// this profile -- but on WinRT the available encodings are the Unicode ones, so
// a codepage name can throw at RUN time. The rule is therefore "ask, and fall
// back", not "assume": asserted here so the mirror states it independently of
// the VB it checks.
function decodeCharset(charset, platformEncodings) {
  if (charset === 'iso-8859-1' || charset === 'latin1' || charset === 'windows-1252') {
    if (platformEncodings.indexOf('iso-8859-1') >= 0) return 'iso-8859-1';
  }
  return 'utf-8';
}
check('a codepage the platform has is honoured', decodeCharset('iso-8859-1', ['iso-8859-1']) === 'iso-8859-1');
check('a codepage the platform lacks falls back to utf-8', decodeCharset('iso-8859-1', []) === 'utf-8');
check('an unknown charset falls back to utf-8', decodeCharset('klingon', ['iso-8859-1']) === 'utf-8');
check('no charset falls back to utf-8', decodeCharset('', ['iso-8859-1']) === 'utf-8');

const fetcherRaw = readIfPresent('BrowserForWP/Diagnostics/NetDocumentFetcher.vb');
const fetcher = stripVbComments(fetcherRaw);
check('NetDocumentFetcher.vb exists', fetcherRaw.length > 0);
check('the fetcher sends no Accept-Encoding', fetcherRaw.length > 0 && !/Accept-Encoding/i.test(fetcher));
check('the fetcher resolves through DoH', fetcher.includes('DohResolver'));
check('the fetcher uses the TLS 1.3 client', fetcher.includes('HttpClient13'));
check('the fetcher returns errors instead of throwing', fetcher.includes('ErrorMessage ='));
check('decoding asks the platform instead of assuming', fetcher.includes('TryGetEncoding'));

// The pin rule. Until now nothing but the TLS probe used this transport, so a
// stored pin was documented as protecting "the app's TLS 1.3 path" -- and that
// was accurate. A page LOAD now travels the same path, so the pin has to be
// enforced here too, or the documentation becomes false the moment it matters.
// Chain trust cannot substitute: a pin is what defends against a trusted CA.
function pinDecision(leafDer, host, pins) {
  const pin = (pins || {})[host] || '';
  if (!pin) return { blocked: false };
  return leafDer === pin ? { blocked: false } : { blocked: true, reason: 'pin mismatch' };
}
check('an unpinned host is fetched', pinDecision('abc', 'example.com', {}).blocked === false);
check('a matching pin is fetched', pinDecision('abc', 'example.com', { 'example.com': 'abc' }).blocked === false);
check('a mismatched pin is refused', pinDecision('abc', 'example.com', { 'example.com': 'zzz' }).blocked === true);
check('no pin table refuses nothing', pinDecision('abc', 'example.com', null).blocked === false);
check('the fetch enforces a stored pin', fetcher.includes('VerifyPin'));
check('the fetch has the leaf certificate to check it against', fetcher.includes('SessionInfo'));

const seamRaw = readIfPresent('BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb');
const seam = stripVbComments(seamRaw);
check('IDocumentFetcher.vb exists', seamRaw.length > 0);
check('the seam does not reference Net', seamRaw.length > 0 && !seam.includes('BrowserForWP.Net'));

// ── The request is BUILT BY CONCATENATION, and the url is untrusted input ──────
// HttpClient13 assembles "GET <path> HTTP/1.1" and every header with string
// concatenation, and the url reaches it from the address bar, from a redirect
// Location header and from a setting written by hand. A CR, LF or NUL inside it is
// therefore not a bad character: it is a second request, or a header of the
// sender's choosing, smuggled into the first one. ParsedUrl.Parse refuses them, and
// this mirrors that rule so the JS and the VB cannot drift apart.
function parseUrl(url) {
  if (!url) throw new Error('url required');
  for (const ch of url) {
    const code = ch.codePointAt(0);
    if (code === 13 || code === 10 || code === 0) {
      throw new Error('url contains a control character (0x' + code.toString(16) + ')');
    }
  }
  const schemeEnd = url.indexOf('://');
  const scheme = schemeEnd > 0 ? url.slice(0, schemeEnd).toLowerCase() : 'https';
  if (scheme !== 'https') throw new Error('only https is supported, got ' + scheme);
  const rest = schemeEnd > 0 ? url.slice(schemeEnd + 3) : url;
  const pathStart = rest.indexOf('/');
  const authority = pathStart < 0 ? rest : rest.slice(0, pathStart);
  const path = pathStart < 0 ? '/' : rest.slice(pathStart);
  const colon = authority.indexOf(':');
  const host = colon >= 0 ? authority.slice(0, colon) : authority;
  const port = colon >= 0 ? Number(authority.slice(colon + 1)) : 443;
  if (!host) throw new Error('no host in ' + url);
  return { scheme, host, port, path };
}
const thrownBy = (fn) => { try { fn(); return ''; } catch (e) { return e.message; } };
check('a normal url parses', parseUrl('https://example.com/a?b=1').host === 'example.com');
check('https with a port parses', parseUrl('https://example.com:8443/x').port === 8443);
check('plain http is refused, not upgraded',
  thrownBy(() => parseUrl('http://example.com/')).includes('only https'));
check('a url with CR is refused',
  thrownBy(() => parseUrl('https://a/\rX: 1')).includes('control character'));
check('a url with LF is refused',
  thrownBy(() => parseUrl('https://a/\nX: 1')).includes('control character'));
check('a url with NUL is refused',
  thrownBy(() => parseUrl('https://a/\u0000')).includes('control character'));

const httpRaw = readIfPresent('BrowserForWP.Net/Http/HttpClient13.vb');
const httpClient = stripVbComments(httpRaw);
check('the VB refuses the same three characters',
  /charCode = 13 OrElse charCode = 10 OrElse charCode = 0/.test(httpClient),
  'and NOT via Microsoft.VisualBasic.ControlChars, which the Store profile removes');
check('the VB refuses a negative Content-Length',
  /If length < 0 Then/.test(httpClient) && httpClient.includes('negative Content-Length'),
  'the ceiling cannot see it, and ReadExactlyAsync sizes an array as count - 1');
check('the VB refuses a negative chunk size',
  /If size < 0 Then/.test(httpClient) && httpClient.includes('negative chunk size'),
  'hex parsing accepts a sign, so "-1" reaches the same array sizing');
check('the header block still has its 64 KiB ceiling',
  httpClient.includes('response header block exceeds 64 KiB'));
check('the body still has its size cap', httpClient.includes('response exceeds the size cap'));

// ── The DoH resolver's two queries are NOT symmetric, and the asymmetry is the
// point. AAAA may fail: plenty of networks are v4-only and a host with no IPv6
// record is normal. The A query is what the caller wanted. A host with no A record
// is answered by a SUCCESSFUL response carrying zero answers (ParseResponse returns
// an empty list, no exception), so the only way that call throws is that the QUERY
// failed -- HTTP 500, a dead transport, an unparseable answer. Swallowing it used to
// end the method at "no address records", replacing a real reason with a false one.
const dohRaw = readIfPresent('BrowserForWP.Net/Dns/DohResolver.vb');
const doh = stripVbComments(dohRaw);
// Exact shape, not a window: everything between the AAAA block's LAST `End Try`
// and the A call must contain no `Catch`, which is the same statement as "the A
// call is not inside a Try". A plain "is there a Catch within N characters" test is
// useless here, because the AAAA block's own Catch sits directly above it.
const beforeA = doh.slice(0, doh.indexOf('QueryAsync(host, TypeA)'));
const lastEndTry = beforeA.lastIndexOf('End Try');
check('the A query is outside the AAAA query\'s Try/Catch',
  beforeA.length > 0 && lastEndTry >= 0 && !/Catch/.test(beforeA.slice(lastEndTry)),
  'a failed A query must carry its own reason to a caller that falls back to the OS');
check('an answer with no records is still a refusal',
  doh.includes('no address records for '));
// The phrase lives in a COMMENT, and `doh` is the stripped source -- so this one
// reads the raw file on purpose. The first draft used the stripped text and the
// check could never have passed.
check('the AAAA query keeps its own leniency, and says why',
  /Catch[\s\S]{0,240}No IPv6 records is a normal answer/.test(dohRaw));

console.log(`\n${checks - failures}/${checks} fetch-rule checks passed.`);
if (failures > 0) {
  console.log(`${failures} fetch-rule failure(s).`);
  process.exit(1);
}
