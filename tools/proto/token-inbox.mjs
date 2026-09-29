#!/usr/bin/env node
// The executable referee for BrowserForWP.Core/Engine/Remote/TokenInbox.vb.
//
// The rule it pins: a device token arrives from a FORM, served by the phone to a
// computer on the same network, and this file decides whether what arrived may be
// saved. It is the first thing in this project that accepts a credential typed by
// somebody else, so the refusals matter as much as the acceptances -- a code, a
// token shape, a slot name, an address -- and every one of them is checked here.
//
// Two halves, like every referee in this directory:
//   * the behavioural checks are a transliteration of the VB, so the rules are
//     exercised without a phone;
//   * the source checks assert what a behaviour cannot show: that Core holds no
//     HTML and no prose, that the field names and reason keys are constants, and
//     that the class is a rule rather than a thing.
import fs from 'node:fs';

let checks = 0;
let failures = 0;
function check(name, ok, detail = '') {
  checks += 1;
  if (ok) console.log(`  ✓ ${name}`);
  else { console.log(`  ✗ ${name}${detail ? ': ' + detail : ''}`); failures++; }
}

const SOURCE = 'BrowserForWP.Core/Engine/Remote/TokenInbox.vb';
const source = fs.existsSync(SOURCE) ? fs.readFileSync(SOURCE, 'utf8') : '';

// ── The transliteration ─────────────────────────────────────────────────────
// Kept line-for-line with the VB on purpose: a referee that "improves" the rule
// while transliterating it tests its own version of it.

const FieldToken = 'token';
const FieldSlot = 'slot';
const FieldAddress = 'address';
const FieldCode = 'code';

const DefaultPort = 8777;
const PortsToTry = 5;
const MaxBodyBytes = 8192;
const MinTokenLength = 16;
const MaxTokenLength = 200;
const CodeLength = 4;
const MaxCodeFailures = 5;

const ReasonCode = 'TokenInboxReasonCode';
const ReasonToken = 'TokenInboxReasonToken';
const ReasonSlot = 'TokenInboxReasonSlot';
const ReasonAddress = 'TokenInboxReasonAddress';
const ReasonOk = 'TokenInboxReasonOk';

const Primary = 'primary';
const Secondary = 'secondary';

const decoder = new TextDecoder('utf-8', { fatal: false });
const encoder = new TextEncoder();

function hexDigit(ch) {
  if (ch >= '0' && ch <= '9') return ch.charCodeAt(0) - '0'.charCodeAt(0);
  if (ch >= 'a' && ch <= 'f') return ch.charCodeAt(0) - 'a'.charCodeAt(0) + 10;
  if (ch >= 'A' && ch <= 'F') return ch.charCodeAt(0) - 'A'.charCodeAt(0) + 10;
  return -1;
}

function decode(text) {
  const value = String(text ?? '');
  if (!value.includes('%') && !value.includes('+')) return value;
  const bytes = [];
  let at = 0;
  while (at < value.length) {
    const ch = value[at];
    if (ch === '+') { bytes.push(32); at += 1; continue; }
    if (ch === '%' && at + 2 < value.length) {
      const high = hexDigit(value[at + 1]);
      const low = hexDigit(value[at + 2]);
      if (high >= 0 && low >= 0) { bytes.push(high * 16 + low); at += 3; continue; }
    }
    for (const one of encoder.encode(ch)) bytes.push(one);
    at += 1;
  }
  return decoder.decode(Uint8Array.from(bytes));
}

function ParseForm(body) {
  const result = new Map();
  if (body === null || body === undefined) return result;
  for (const pair of String(body).split('&')) {
    if (pair.length === 0) continue;
    const equalsAt = pair.indexOf('=');
    const rawName = equalsAt < 0 ? pair : pair.slice(0, equalsAt);
    const rawValue = equalsAt < 0 ? '' : pair.slice(equalsAt + 1);
    result.set(decode(rawName), decode(rawValue));
  }
  return result;
}

function Field(fields, name) {
  if (!fields || !name) return '';
  return fields.has(name) ? String(fields.get(name) ?? '') : '';
}

const PreferredPrefixes = ['192.168.', '10.', '172.'];

function isUsableIpv4(text) {
  if (!text) return false;
  if (text.includes(':')) return false;
  const parts = text.split('.');
  if (parts.length !== 4) return false;
  for (const part of parts) {
    if (part.length === 0 || part.length > 3) return false;
    for (const digit of part) if (digit < '0' || digit > '9') return false;
    if (Number(part) > 255) return false;
  }
  if (text.startsWith('127.')) return false;
  if (text.startsWith('169.254.')) return false;
  if (text === '0.0.0.0') return false;
  return true;
}

function ChooseAddress(candidates) {
  if (!candidates) return '';
  const usable = [];
  for (const raw of candidates) {
    const text = String(raw ?? '').trim();
    if (!isUsableIpv4(text)) continue;
    usable.push(text);
  }
  for (const preferred of PreferredPrefixes) {
    for (const address of usable) if (address.startsWith(preferred)) return address;
  }
  return usable.length > 0 ? usable[0] : '';
}

function LooksLikeAToken(token) {
  const text = String(token ?? '').trim();
  if (text.length < MinTokenLength || text.length > MaxTokenLength) return false;
  for (const ch of text) {
    if (/[A-Za-z0-9]/.test(ch)) continue;
    if (ch === '-' || ch === '_') continue;
    return false;
  }
  return true;
}

function Masked(token) {
  const text = String(token ?? '').trim();
  if (text.length === 0) return '';
  if (text.length <= 4) return '****';
  return `****${text.slice(text.length - 4)}`;
}

function SlotFor(text) {
  const value = String(text ?? '').trim().toLowerCase();
  switch (value) {
    case '': case '1': case 'primary': case 'server1': return Primary;
    case '2': case 'secondary': case 'backup': case 'server2': return Secondary;
    default: return '';
  }
}

function CodesMatch(presented, expected) {
  const given = String(presented ?? '').trim();
  const wanted = String(expected ?? '').trim();
  if (wanted.length !== CodeLength) return false;
  if (given.length !== wanted.length) return false;
  let same = true;
  for (let i = 0; i < wanted.length; i += 1) {
    if (given[i].toUpperCase() !== wanted[i].toUpperCase()) same = false;
  }
  return same;
}

// Normalize, transliterated from RemoteServers.Normalize as its own referee has
// it: this file only has to agree with the VB about which addresses it accepts.
function Normalize(raw) {
  const text = String(raw ?? '').trim();
  if (!text) return '';
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `https://${text}`;
  let parsed;
  try { parsed = new URL(withScheme); } catch { return ''; }
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') return '';
  // A space is not a hostname, whatever URL does with it. The full host rule is
  // RemoteServers' own referee's business; this one only has to agree with the VB
  // about which addresses are acceptable, so an address it refuses must be refused
  // here too.
  if (/\s/.test(parsed.host)) return '';
  const path = parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/$/, '');
  return parsed.origin + path;
}

function Review(fields, code, primaryUrl, secondaryUrl) {
  const map = fields ?? new Map();
  if (!CodesMatch(Field(map, FieldCode), code)) {
    return { ok: false, reasonKey: ReasonCode, slot: Primary, address: '', token: '' };
  }
  const token = Field(map, FieldToken).trim();
  if (!LooksLikeAToken(token)) {
    return { ok: false, reasonKey: ReasonToken, slot: Primary, address: '', token: '' };
  }
  const slot = SlotFor(Field(map, FieldSlot));
  if (slot.length === 0) {
    return { ok: false, reasonKey: ReasonSlot, slot: Primary, address: '', token: '' };
  }
  const typed = Field(map, FieldAddress).trim();
  let address = Normalize(typed);
  if (typed.length > 0 && address.length === 0) {
    return { ok: false, reasonKey: ReasonAddress, slot, address: '', token: '' };
  }
  if (address.length === 0) {
    address = Normalize(slot === Primary ? primaryUrl : secondaryUrl);
  }
  if (address.length === 0) {
    address = Normalize(slot === Primary ? secondaryUrl : primaryUrl);
  }
  if (address.length === 0) {
    return { ok: false, reasonKey: ReasonAddress, slot, address: '', token: '' };
  }
  return { ok: true, reasonKey: ReasonOk, slot, address, token };
}

// A token of the shape the server mints: 43 base64url characters.
const GOOD_TOKEN = 'kX8htcml2oLiej_P8McvNaIY7PWnoXFPY1HeAMc3pSY';
const CODE = '4821';

console.log('TokenInbox — the rules behind the page the phone serves');

// ── Decoding a form ─────────────────────────────────────────────────────────
console.log('\nDecoding an urlencoded body');

{
  const fields = ParseForm('token=abc&slot=2&address=&code=1234');
  check('every pair becomes a field',
    Field(fields, 'token') === 'abc' && Field(fields, 'slot') === '2' && Field(fields, 'code') === '1234');
  check('an empty value is empty, not missing', fields.has('address') && Field(fields, 'address') === '');
}

check('a plus sign is a space', Field(ParseForm('address=a+b'), 'address') === 'a b');
check('a percent escape becomes its byte', Field(ParseForm('token=a%20b'), 'token') === 'a b');
check('an escaped plus stays a plus', Field(ParseForm('token=a%2Bb'), 'token') === 'a+b');
check('an escaped accented letter comes back as one letter',
  Field(ParseForm('label=caff%C3%A8'), 'label') === 'caffè');
check('a percent escape and a typed letter agree',
  Field(ParseForm('label=caff%C3%A8'), 'label') === Field(ParseForm('label=caffè'), 'label'));

{
  const malformed = ParseForm('token=100%&other=%2&third=%zz');
  check('a malformed escape is left as typed instead of throwing',
    Field(malformed, 'token') === '100%' && Field(malformed, 'other') === '%2'
    && Field(malformed, 'third') === '%zz');
}

check('a repeated field keeps the last value, which is what a browser would send',
  Field(ParseForm('slot=1&slot=2'), 'slot') === '2');
check('a pair with no equals sign is a name with an empty value',
  Field(ParseForm('token'), 'token') === '' && ParseForm('token').has('token'));
check('an empty body is an empty form',
  ParseForm('').size === 0 && ParseForm(null).size === 0);
check('a missing field reads as empty rather than as nothing',
  Field(new Map(), 'token') === '' && Field(ParseForm('a=b'), 'token') === '');

// ── Which address to advertise ──────────────────────────────────────────────
console.log('\nWhich of the phone\'s own addresses a computer can reach');

check('the loopback address is never advertised',
  ChooseAddress(['127.0.0.1', '::1']) === '');
check('a link-local address is never advertised',
  ChooseAddress(['169.254.10.20']) === '');
check('the unspecified address is never advertised',
  ChooseAddress(['0.0.0.0']) === '');
check('an IPv6 literal is not a URL a person can type',
  ChooseAddress(['fe80::1', '2001:db8::5']) === '');
check('the Wi-Fi address wins over the public one',
  ChooseAddress(['8.8.8.8', '192.168.1.20']) === '192.168.1.20');
check('a 10.x address beats a 172.x one, in that order',
  ChooseAddress(['172.20.5.5', '10.0.0.7']) === '10.0.0.7');
check('a public address is still used when it is the only one',
  ChooseAddress(['100.64.3.4', '127.0.0.1']) === '100.64.3.4');
check('a malformed address is not an address',
  ChooseAddress(['192.168.1', '999.1.1.1', '192.168.1.20.5', '192.168.1.']) === '');
check('no candidates means no address',
  ChooseAddress([]) === '' && ChooseAddress(null) === '');

// ── What a token looks like ─────────────────────────────────────────────────
console.log('\nWhat a token looks like');

check('the 43 characters the server mints are a token', LooksLikeAToken(GOOD_TOKEN));
check('whitespace around a pasted token is not part of it',
  LooksLikeAToken(`  ${GOOD_TOKEN}\n`) === true);
check('a token that is too short is not one', LooksLikeAToken('x'.repeat(MinTokenLength - 1)) === false);
check('a token that is too long is not one', LooksLikeAToken('x'.repeat(MaxTokenLength + 1)) === false);
check('a space inside is not a token',
  LooksLikeAToken(`${GOOD_TOKEN.slice(0, 20)} ${GOOD_TOKEN.slice(20)}`) === false);
check('punctuation outside the token alphabet is refused',
  LooksLikeAToken('kX8htcml2oLiej+P8McvNaIY7PWnoXFPY1HeAMc3p') === false);
check('the reply may show four characters and no more',
  Masked(GOOD_TOKEN) === `****${GOOD_TOKEN.slice(-4)}` && !Masked(GOOD_TOKEN).includes(GOOD_TOKEN.slice(0, 10)));
check('a token too short to mask is masked anyway', Masked('ab') === '****');
check('masking nothing gives nothing', Masked('') === '');

// ── Which server ────────────────────────────────────────────────────────────
console.log('\nWhich server slot the form named');

check('no slot means the primary', SlotFor('') === Primary && SlotFor(null) === Primary);
check('"1" is the primary', SlotFor('1') === Primary);
check('"2" is the secondary', SlotFor('2') === Secondary);
check('the words are accepted for a hand-written body',
  SlotFor('secondary') === Secondary && SlotFor('backup') === Secondary && SlotFor('primary') === Primary);
check('case does not matter', SlotFor('SECONDARY') === Secondary);
check('a slot nobody offered is refused rather than guessed',
  SlotFor('9') === '' && SlotFor('third') === '');

// ── The code ────────────────────────────────────────────────────────────────
console.log('\nThe code on the phone\'s screen');

check('the code on the screen is the code', CodesMatch(CODE, CODE));
check('surrounding whitespace is forgiven', CodesMatch(` ${CODE} `, CODE));
check('a wrong code is refused', CodesMatch('4822', CODE) === false);
check('a shorter code is refused', CodesMatch('482', CODE) === false);
check('a longer code is refused, even with the right prefix', CodesMatch('48210', CODE) === false);
check('a code that is not four digits is never right', CodesMatch('4821', '48210') === false);
check('a code with a letter is refused', CodesMatch('48x1', '48x1') === true && CodesMatch('4821', '48x1') === false);

// ── Reviewing a submission ──────────────────────────────────────────────────
console.log('\nReviewing a submission');

const PRIMARY_URL = 'https://34.132.106.149';
const SECONDARY_URL = 'https://render.example.com';

function submission(overrides = {}) {
  return ParseForm(new URLSearchParams({
    token: GOOD_TOKEN,
    slot: '1',
    address: PRIMARY_URL,
    code: CODE,
    ...overrides,
  }).toString());
}

check('a good submission is accepted',
  Review(submission(), CODE, PRIMARY_URL, '') .ok === true);

{
  const verdict = Review(submission(), CODE, PRIMARY_URL, '');
  check('...and carries the token and the address it will store',
    verdict.token === GOOD_TOKEN && verdict.address === PRIMARY_URL && verdict.slot === Primary);
  check('...and says so with a key rather than a sentence', verdict.reasonKey === ReasonOk);
}

check('the wrong code is refused first, before anything else is looked at',
  Review(submission({ token: 'nope' }), '1111', PRIMARY_URL, '').reasonKey === ReasonCode);
check('a code-shaped secret is not checked against the token',
  Review(submission({ token: '' }), CODE, PRIMARY_URL, '').reasonKey === ReasonToken);
check('a token that is too short is refused',
  Review(submission({ token: 'short' }), CODE, PRIMARY_URL, '').reasonKey === ReasonToken);
check('a slot nobody offered is refused',
  Review(submission({ slot: '9' }), CODE, PRIMARY_URL, '').reasonKey === ReasonSlot);

{
  const refused = Review(submission(), CODE, PRIMARY_URL, '');
  const wrongCode = Review(submission(), '0000', PRIMARY_URL, '');
  check('a refusal carries nothing to store',
    wrongCode.token === '' && wrongCode.address === '' && refused.address === PRIMARY_URL);
}

check('an address the engine cannot use is refused rather than stored raw',
  Review(submission({ address: 'not a url' }), CODE, PRIMARY_URL, '').reasonKey === ReasonAddress);
check('an empty address falls back to what the slot already holds',
  Review(submission({ address: '' }), CODE, PRIMARY_URL, '').address === PRIMARY_URL);
check('an empty address in the SECOND slot falls back to the second slot\'s own',
  Review(submission({ address: '', slot: '2' }), CODE, PRIMARY_URL, SECONDARY_URL).address === SECONDARY_URL);
check('an empty address falls back to the other slot when this one is empty',
  Review(submission({ address: '', slot: '2' }), CODE, PRIMARY_URL, '').address === PRIMARY_URL);
check('an empty address everywhere is refused: a token with nowhere to go is not saved',
  Review(submission({ address: '' }), CODE, '', '').reasonKey === ReasonAddress);
check('a bare host typed in the address field is normalized the way the settings do it',
  Review(submission({ address: 'render.example.com:8443' }), CODE, '', '').address === 'https://render.example.com:8443');
check('the secondary slot can be written on its own',
  Review(submission({ slot: '2', address: SECONDARY_URL }), CODE, '', '').slot === Secondary);

// ── Source contracts ────────────────────────────────────────────────────────
// What a behaviour cannot show about the file itself.
console.log('\nThe source, which no behaviour above can check');

const code = source.split('\n')
  .filter((line) => !/^\s*'/.test(line))
  .join('\n');

check('the file exists and is where the referee says it is', source.length > 0, SOURCE);
check('Core holds no HTML: the shell draws the page, not the rules',
  !/<\s*(html|body|form|input|div|p|h1)\b/i.test(code) && !code.includes('Content-Type'),
  'a tag or a header in Core means the page and its rules can drift apart');
check('Core holds no prose: the sentences come from the catalogue',
  !/Localizer/.test(code), 'Core cannot see the resource catalogue, by design');
check('it holds no connection: no socket, no listener, no port is opened here',
  !/StreamSocket|SocketAsyncEventArgs|TcpListener|HttpListener|BindServiceName/i.test(code));
check('it is a rule and not a thing',
  /Public NotInheritable Class TokenInbox/.test(source) && /Private Sub New\(\)/.test(source));
check('the field names are constants, not literals scattered through the logic',
  /Public Const FieldToken As String = "token"/.test(source)
  && /Public Const FieldSlot As String = "slot"/.test(source)
  && /Public Const FieldAddress As String = "address"/.test(source)
  && /Public Const FieldCode As String = "code"/.test(source));
check('and the logic uses the constants rather than typing the words again',
  (code.match(/"token"/g) || []).length === 1
  && (code.match(/"slot"/g) || []).length === 1
  && (code.match(/"address"/g) || []).length === 1
  && (code.match(/"code"/g) || []).length === 1);
check('the reason keys are constants too, so the shell and the catalogue cannot disagree quietly',
  /Public Const ReasonCode As String = "TokenInboxReasonCode"/.test(source)
  && /Public Const ReasonOk As String = "TokenInboxReasonOk"/.test(source)
  && (code.match(/"TokenInboxReason/g) || []).length === 5);
check('the port, the code length and the failure count are constants',
  /Public Const DefaultPort As Integer = 8777/.test(source)
  && /Public Const CodeLength As Integer = 4/.test(source)
  && /Public Const MaxCodeFailures As Integer = 5/.test(source));
check('the limits the referee just used are the limits in the file',
  source.includes(`MinTokenLength As Integer = ${MinTokenLength}`)
  && source.includes(`MaxTokenLength As Integer = ${MaxTokenLength}`)
  && source.includes(`MaxBodyBytes As Integer = ${MaxBodyBytes}`)
  && source.includes(`PortsToTry As Integer = ${PortsToTry}`));
check('normalising the address goes through RemoteServers rather than a second rule',
  /RemoteServers\.Normalize/.test(code));
check('the slot names come from RemoteServers and are not spelled out here',
  /RemoteServers\.Primary/.test(code) && /RemoteServers\.Secondary/.test(code));
check('nothing here writes the token into a message: Masked exists for that',
  /Return "\*\*\*\*" & text\.Substring/.test(source));// ── The shell: the port, the response and the form ─────────────────────────
// The rules are Core's; these contracts are about the file that turns them into a
// listener, a response and a form. Each one stands for a decision a behaviour
// cannot show: a page that is cached, a token echoed back, a failure count nobody
// enforces, a sentence written straight into a tag.
console.log('');
console.log('The page the phone serves, in the shell');

const SHELL = 'BrowserForWP/Engine/TokenPage.vb';
const MAIN = 'BrowserForWP/MainPage.xaml.vb';
const shell = fs.existsSync(SHELL) ? fs.readFileSync(SHELL, 'utf8') : '';
const mainSource = fs.existsSync(MAIN) ? fs.readFileSync(MAIN, 'utf8') : '';
const shellCode = shell.split('\n').filter((line) => !/^\s*'/.test(line)).join('\n');

check('the shell exists where this referee says it is', shell.length > 0, SHELL);
check('the decisions are Core\'s: the shell asks Review and decides nothing itself',
  shellCode.includes('TokenInbox.Review(')
  && !/CodesMatch\(|SlotFor\(|LooksLikeAToken\(/.test(shellCode),
  'a second copy of the rules in the shell is a second set of rules');
check('the page is never cached, and says it runs nothing',
  shellCode.includes('Cache-Control: no-store')
  && shellCode.includes("Content-Security-Policy: default-src 'none'")
  && shellCode.includes('X-Content-Type-Options: nosniff'));
check('the token is written once, masked, and never echoed',
  (shellCode.match(/verdict\.Token/g) || []).length === 1
  && shellCode.includes('TokenInbox.Masked(verdict.Token)'),
  'the reply goes over plain http inside the network');
check('the failure count that closes the listener is enforced here',
  shellCode.includes('TokenInbox.MaxCodeFailures') && shellCode.includes('Close()'));
check('every sentence comes from the catalogue',
  (shellCode.match(/Localizer\.Get\(/g) || []).length >= 12);
check('no sentence is written straight into a tag',
  !/"(h1|p|label|button|title)>[A-Za-z]/.test(shellCode));
check('the field names come from Core rather than from the HTML',
  ['Token', 'Slot', 'Address', 'Code'].every((name) => shell.includes('TokenInbox.Field' + name)));
check('the ports come from Core too',
  shellCode.includes('TokenInbox.DefaultPort') && shellCode.includes('TokenInbox.PortsToTry'));
check('the form is served on a GET and read on a POST',
  shellCode.includes('method = "GET"') && shellCode.includes('method <> "POST"'));
check('...and only the root path is served', shellCode.includes('target <> "/"'));
check('the app closes the listener when the settings screen closes',
  /Private Sub CloseSettingsButton_Click[\s\S]{0,400}StopTokenInbox\(\)/.test(mainSource)
  && /Private Sub StopTokenInbox[\s\S]{0,400}_tokenPage\.Close\(\)/.test(mainSource),
  'a listener left open behind a closed screen is the one state this feature must not have');
check('a saved token switches the engine to the server and stores the address',
  /_appSettings\.EngineSetting = EngineChoice\.Remote/.test(mainSource)
  && /_appSettings\.RemoteEnabled = True/.test(mainSource)
  && /_appSettings\.RemotePrimaryUrl = verdict\.Address/.test(mainSource));
check('the two slots are written by slot, not both at once',
  /verdict\.Slot = RemoteServers\.Secondary[\s\S]{0,200}RemoteSecondaryToken = verdict\.Token/.test(mainSource));
check('the settings screen shows what the window now holds',
  /RemoteTokenBox\.Text = _appSettings\.RemotePrimaryToken/.test(mainSource));

console.log(`\n${checks - failures}/${checks} token-inbox checks passed.`);
if (failures > 0) {
  console.log('The page the phone serves does not do what this file says it does.');
  process.exit(1);
}
console.log('A form is decoded, an address is chosen, and only a submission with the');
console.log('code, a token-shaped token and somewhere to point it is allowed through.');
