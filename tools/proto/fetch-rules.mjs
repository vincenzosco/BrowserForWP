#!/usr/bin/env node
// Rules for turning an HTTP response into document text, mirrored from
// BrowserForWP/Diagnostics/NetDocumentFetcher.vb and
// BrowserForWP.Core/Engine/Native/IDocumentFetcher.vb.
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

console.log(`\n${checks - failures}/${checks} fetch-rule checks passed.`);
if (failures > 0) {
  console.log(`${failures} fetch-rule failure(s).`);
  process.exit(1);
}
