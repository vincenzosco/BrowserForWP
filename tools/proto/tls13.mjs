#!/usr/bin/env node
// ═══════════════════════════════════════════════════════════════════════════
//  TLS 1.3 client prototype — BrowserForWP
//
//  This is a line-for-line prototype of BrowserForWP.Net/Tls13/*.vb.
//
//  WHY IT EXISTS
//  -------------
//  The Windows Phone 8.1 SDK is Windows-only, so Tls13Client.vb cannot be run
//  on macOS or Linux. Rather than ship an unverified handshake, the whole
//  protocol is implemented here first, against a REAL server, using Node only
//  for primitives (X25519, AES-GCM, SHA-256, HMAC) that the RFCs pin down.
//
//  The VB is then a transliteration. If this file passes and the VB fails, the
//  bug is in the translation, not the design — which is a far smaller search
//  space. Same method as tools/proto/w25519.mjs.
//
//  WHAT IT PROVES
//  --------------
//   1. ClientHello is well-formed enough that a real server answers it.
//   2. The key schedule derives the keys the server actually used, i.e. our
//      decryption of the server's encrypted records is byte-correct.
//   3. Our Finished verification accepts the server's Finished.
//   4. The server accepts OUR Finished — the handshake completes.
//   5. Application data flows both ways.
//
//  USAGE
//    node tools/proto/tls13.mjs               # handshake against a default host
//    node tools/proto/tls13.mjs example.com   # against a specific host
//    node tools/proto/tls13.mjs host 443 /    # custom path
//
//  Against the render server, which is NOT an HTTP server:
//    node tools/proto/tls13.mjs 34.132.106.149 8443 --handshake-only
//  That run answers the one question the deployment raises and no other: does
//  this client accept the certificate the server presents, for the host we
//  asked? It is the mirror of the phone's own check, and the certificate it
//  presents is a Let's Encrypt certificate FOR AN ADDRESS with no common name.
//
//  Exits non-zero on any failure. Prints every protocol step it takes.
// ═══════════════════════════════════════════════════════════════════════════

import net from 'node:net';
import crypto from 'node:crypto';
import fs from 'node:fs';

// ── Assertion bookkeeping (mirrors tools/gen-vectors.mjs) ───────────────────
let checks = 0;
let failures = 0;

function ok(label, detail = '') {
  checks++;
  console.log(`  \u2713 ${label}${detail ? '  ' + detail : ''}`);
}
function fail(label, detail = '') {
  checks++;
  failures++;
  console.log(`  \u2717 ${label}${detail ? '  ' + detail : ''}`);
}
function check(cond, label, detail = '') {
  cond ? ok(label, detail) : fail(label, detail);
}
function fmt(v) {
  if (typeof v === 'string') return v;
  if (typeof v === 'number') return String(v);
  return hex(v);
}
function eq(actual, expected, label) {
  const a = fmt(actual);
  const e = fmt(expected);
  check(a === e, label, a === e ? a : `got ${a}, want ${e}`);
}
// RFC 8446 §6: a two-byte alert is level || description.
const ALERT_NAMES = {
  0: 'close_notify', 10: 'unexpected_message', 20: 'bad_record_mac',
  22: 'record_overflow', 40: 'handshake_failure', 47: 'illegal_parameter',
  48: 'unknown_ca', 50: 'decode_error', 70: 'protocol_version',
  80: 'internal_error', 109: 'missing_extension', 110: 'unsupported_extension',
  112: 'unrecognized_name', 113: 'bad_certificate', 116: 'certificate_required',
  120: 'no_application_protocol',
};
function describeAlert(body) {
  if (body.length < 2) return `malformed (${hex(body)})`;
  const level = body[0];
  const desc = body[1];
  return `${level === 2 ? 'fatal' : 'warning'} ${desc} ` +
         `(${ALERT_NAMES[desc] || 'unknown'})`;
}
function hex(buf) {
  return Buffer.from(buf).toString('hex');
}
function step(msg) {
  console.log(`\n\u2500\u2500 ${msg}`);
}

// ═══════════════════════════════════════════════════════════════════════════
// 1. Primitives the RFCs define exactly. Everything else is ours.
// ═══════════════════════════════════════════════════════════════════════════

const EMPTY = Buffer.alloc(0);

function sha256(...chunks) {
  const h = crypto.createHash('sha256');
  for (const c of chunks) h.update(c);
  return h.digest();
}

function hmac(key, data) {
  return crypto.createHmac('sha256', key).update(data).digest();
}

// RFC 5869 §2.2: HKDF-Extract(salt, IKM) = HMAC(salt, IKM)
function hkdfExtract(salt, ikm) {
  return hmac(salt.length === 0 ? Buffer.alloc(32) : salt, ikm);
}

// RFC 5869 §2.3: HKDF-Expand(PRK, info, L)
function hkdfExpand(prk, info, length) {
  let out = Buffer.alloc(0);
  let t = Buffer.alloc(0);
  let counter = 1;
  while (out.length < length) {
    t = hmac(prk, Buffer.concat([t, info, Buffer.from([counter])]));
    out = Buffer.concat([out, t]);
    counter++;
  }
  return out.subarray(0, length);
}

// RFC 8446 §7.1: the HkdfLabel structure, including the "tls13 " prefix.
//   struct {
//       uint16 length = Length;
//       opaque label<7..255> = "tls13 " + Label;
//       opaque context<0..255> = Context;
//   } HkdfLabel;
function hkdfLabel(length, label, context) {
  const full = Buffer.from('tls13 ' + label, 'ascii');
  return Buffer.concat([
    Buffer.from([length >> 8, length & 0xff]),
    Buffer.from([full.length]),
    full,
    Buffer.from([context.length]),
    context,
  ]);
}

// RFC 8446 §7.1: HKDF-Expand-Label(Secret, Label, Context, Length)
function expandLabel(secret, label, context, length) {
  return hkdfExpand(secret, hkdfLabel(length, label, context), length);
}

// RFC 8446 §7.1: Derive-Secret(Secret, Label, Messages)
function deriveSecret(secret, label, transcriptHash) {
  return expandLabel(secret, label, transcriptHash, 32);
}

// ═══════════════════════════════════════════════════════════════════════════
// 2. Constants (RFC 8446 §B)
// ═══════════════════════════════════════════════════════════════════════════

const CT = { CCS: 20, ALERT: 21, HANDSHAKE: 22, APP: 23 };
const HS = { CLIENT_HELLO: 1, SERVER_HELLO: 2, NEW_SESSION_TICKET: 4, EE: 8,
             CERTIFICATE: 11, CERT_VERIFY: 15, FINISHED: 20 };
const SUITE_AES128 = 0x1301;
const GROUP_X25519 = 0x001d;

// X25519 SPKI DER prefix: SEQUENCE { SEQUENCE { OID 1.3.101.110 }, BIT STRING }
const X25519_SPKI_PREFIX = Buffer.from('302a300506032b656e032100', 'hex');

// ═══════════════════════════════════════════════════════════════════════════
// 3. Byte reader/writer — the VB TlsBinary transliterates this
// ═══════════════════════════════════════════════════════════════════════════

class Writer {
  constructor() {
    this.parts = [];
  }
  u8(v) { this.parts.push(Buffer.from([v & 0xff])); return this; }
  u16(v) { this.parts.push(Buffer.from([(v >> 8) & 0xff, v & 0xff])); return this; }
  u24(v) { this.parts.push(Buffer.from([(v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff])); return this; }
  bytes(b) { this.parts.push(Buffer.from(b)); return this; }
  // Vector lengths are prefixed by the *length of the length field* in bytes.
  vec8(b) { this.u8(b.length); return this.bytes(b); }
  vec16(b) { this.u16(b.length); return this.bytes(b); }
  // An opaque vector with an explicit width ≤ 255 (e.g. a 1-byte extension).
  raw8(b, n) { this.parts.push(Buffer.from(b)); return this; }
  get length() { return this.parts.reduce((n, p) => n + p.length, 0); }
  toBuffer() { return Buffer.concat(this.parts); }
}

class Reader {
  constructor(buf) { this.buf = Buffer.from(buf); this.pos = 0; }
  get remaining() { return this.buf.length - this.pos; }
  need(n) {
    if (this.remaining < n) {
      throw new Error(`truncated: need ${n} bytes, have ${this.remaining}`);
    }
  }
  u8() { this.need(1); return this.buf[this.pos++]; }
  u16() { this.need(2); const v = this.buf.readUInt16BE(this.pos); this.pos += 2; return v; }
  u24() { this.need(3); const v = this.buf.readUIntBE(this.pos, 3); this.pos += 3; return v; }
  bytes(n) { this.need(n); const v = this.buf.subarray(this.pos, this.pos + n); this.pos += n; return v; }
  vec8() { return this.bytes(this.u8()); }
  vec16() { return this.bytes(this.u16()); }
  vec24() { return this.bytes(this.u24()); }
}

// ═══════════════════════════════════════════════════════════════════════════
// 4. Record layer (RFC 8446 §5.2) + AEAD (RFC 8446 §5.3, RFC 5116)
// ═══════════════════════════════════════════════════════════════════════════

class RecordLayer {
  constructor(key, iv) {
    this.key = key;
    this.iv = iv;
    this.seq = 0n;
    this.label = '?';
  }

  // Per-record nonce: the IV with the sequence number XORed into its low bytes.
  nonce() {
    const n = Buffer.from(this.iv);
    let s = this.seq;
    for (let i = n.length - 1; i >= 0 && s > 0n; i--) {
      n[i] ^= Number(s & 0xffn);
      s >>= 8n;
    }
    return n;
  }

  // Plaintext record -> encrypted record.
  //
  // RFC 8446 §5.2 — read the field order carefully, this is the classic
  // off-by-one in a hand-written TLS 1.3 stack:
  //
  //   struct {
  //       opaque content[TLSPlaintext.length];
  //       ContentType type;
  //       uint8 zeros[length_of_padding];
  //   } TLSInnerPlaintext;
  //
  // The content type goes AFTER the content, not before it. A leading type
  // byte is TLS 1.2/DTLS thinking and produces records a server silently
  // cannot interpret.
  seal(contentType, payload) {
    const inner = Buffer.concat([payload, Buffer.from([contentType])]);
    const aad = Buffer.from([CT.APP, 0x03, 0x03,
                             (inner.length + 16) >> 8, (inner.length + 16) & 0xff]);
    const c = crypto.createCipheriv('aes-128-gcm', this.key, this.nonce(),
                                    { authTagLength: 16 });
    c.setAAD(aad);
    const body = Buffer.concat([c.update(inner), c.final(), c.getAuthTag()]);
    this.seq++;
    return Buffer.concat([aad, body]);
  }

  // Encrypted record -> { type, payload }. Tag failure throws.
  open(record) {
    if (record.length < 5 + 16) throw new Error('record too short');
    const aad = record.subarray(0, 5);
    const body = record.subarray(5);
    const d = crypto.createDecipheriv('aes-128-gcm', this.key, this.nonce(),
                                      { authTagLength: 16 });
    d.setAAD(aad);
    d.setAuthTag(body.subarray(body.length - 16));
    const inner = Buffer.concat([d.update(body.subarray(0, body.length - 16)), d.final()]);
    if (this.trace) {
      console.log(`    [${this.label}] seq=${this.seq} nonce=${hex(this.nonce())} ` +
                  `key=${hex(this.key)} aad=${hex(aad)} pt[0..16]=${hex(inner.subarray(0, 16))}`);
    }
    this.seq++;
    // Strip zero padding from the END, then read the content type that sits
    // immediately before it. Anything else is a decoding error.
    let end = inner.length;
    while (end > 0 && inner[end - 1] === 0) end--;
    if (end === 0) throw new Error('inner plaintext was all padding');
    const type = inner[end - 1];
    return { type, payload: inner.subarray(0, end - 1) };
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// 5. Key schedule (RFC 8446 §7.1)
// ═══════════════════════════════════════════════════════════════════════════

class KeySchedule {
  constructor() {
    this.emptyHash = sha256(EMPTY);
    // No PSK => the early secret is extracted from an all-zero IKM.
    this.earlySecret = hkdfExtract(Buffer.alloc(32), Buffer.alloc(32));
  }
  handshakeSecret(sharedSecret) {
    const derived = deriveSecret(this.earlySecret, 'derived', this.emptyHash);
    this.hs = hkdfExtract(derived, sharedSecret);
    return this.hs;
  }
  masterSecret() {
    const derived = deriveSecret(this.hs, 'derived', this.emptyHash);
    this.ms = hkdfExtract(derived, Buffer.alloc(32));
    return this.ms;
  }
  // traffic secret -> { key, iv, finishedKey }
  expand(secret, prefix) {
    return {
      secret,
      key: expandLabel(secret, prefix + 'key', EMPTY, 16),
      iv: expandLabel(secret, prefix + 'iv', EMPTY, 12),
      finishedKey: expandLabel(secret, 'finished', EMPTY, 32),
    };
  }
  // The schedule is a strict chain: early -> handshake -> master. Deriving a
  // later stage without the earlier one is a null-dereference waiting to
  // happen, so each accessor brings its stage up to date first.
  ensureMaster() {
    if (!this.ms) this.masterSecret();
    return this.ms;
  }

  clientHsTraffic(th) { return this.expand(deriveSecret(this.hs, 'c hs traffic', th), ''); }
  serverHsTraffic(th) { return this.expand(deriveSecret(this.hs, 's hs traffic', th), ''); }
  clientApTraffic(th) { return this.expand(deriveSecret(this.ensureMaster(), 'c ap traffic', th), ''); }
  serverApTraffic(th) { return this.expand(deriveSecret(this.ensureMaster(), 's ap traffic', th), ''); }
  exporterMaster(th) { return deriveSecret(this.ensureMaster(), 'exp master', th); }
}

// ═══════════════════════════════════════════════════════════════════════════
// 6. ClientHello (RFC 8446 §4.1.2)
// ═══════════════════════════════════════════════════════════════════════════

const EXT = { SERVER_NAME: 0, SUPPORTED_GROUPS: 10, SIG_ALGS: 13, ALPN: 16,
              SUPPORTED_VERSIONS: 43, KEY_SHARE: 51 };

function buildClientHello(host, pubKey, random, sessionId) {
  const extensions = [];

  // server_name — required for a virtual-hosted site to pick its cert.
  // ServerName = NameType(1) || HostName; the extension body wraps that in a
  // 2-byte list length. Omitting NameType is a decode_error on the wire.
  const sn = new Writer().u8(0).u16(host.length).bytes(Buffer.from(host, 'ascii'))
                         .toBuffer();
  extensions.push(new Writer().u16(EXT.SERVER_NAME).u16(sn.length + 2)
                              .vec16(sn).toBuffer());

  // supported_versions — the ONLY place 0x0304 (TLS 1.3) may appear.
  extensions.push(new Writer().u16(EXT.SUPPORTED_VERSIONS).u16(3)
                              .vec8(Buffer.from([0x03, 0x04])).toBuffer());

  // supported_groups, x25519 first.
  extensions.push(new Writer().u16(EXT.SUPPORTED_GROUPS).u16(4)
                              .vec16(Buffer.from([0x00, GROUP_X25519])).toBuffer());

  // signature_algorithms. 0x0403 = ecdsa_secp256r1_sha256,
  // 0x0804 = rsa_pss_rsae_sha256, 0x0401 = rsa_pkcs1_sha256 (cert chains only).
  extensions.push(new Writer().u16(EXT.SIG_ALGS).u16(8)
                              .vec16(Buffer.from([0x04, 0x03, 0x08, 0x04, 0x04, 0x01]))
                              .toBuffer());

  // ALPN: HTTP/1.1 ONLY.
  //
  // RFC 7301 §3.1 requires that a client be prepared to speak every protocol
  // it offers. We do not implement HTTP/2, so offering "h2" is a lie that real
  // servers act on: Google selects h2, then answers our HTTP/1.1 request with
  // an HTTP/2 GOAWAY (http2_handshake_failed). Offering only what we speak is
  // the correct fix, not a workaround.
  const protos = Buffer.concat([Buffer.from([8]), Buffer.from('http/1.1')]);
  extensions.push(new Writer().u16(EXT.ALPN).u16(protos.length + 2)
                              .vec16(protos).toBuffer());

  // key_share — one x25519 share. KeyShareEntry is
  // NamedGroup(2) || opaque key_exchange<1..2^16-1>, so the key carries its
  // own 2-byte length. Dropping that length is a decode_error on the wire.
  const entry = new Writer().u16(GROUP_X25519).u16(pubKey.length)
                            .bytes(pubKey).toBuffer();
  extensions.push(new Writer().u16(EXT.KEY_SHARE).u16(entry.length + 2)
                              .vec16(entry).toBuffer());

  const extBlock = Buffer.concat(extensions);

  const body = new Writer()
    .u16(0x0303)                       // legacy_version: always TLS 1.2
    .bytes(random)
    .vec8(sessionId)                   // legacy_session_id, non-empty => compat mode
    .vec16(Buffer.from([0x13, 0x01]))  // cipher_suites: TLS_AES_128_GCM_SHA256
    .vec8(Buffer.from([0x00]))         // legacy_compression_methods: null
    .vec16(extBlock)
    .toBuffer();

  const msg = new Writer().u8(HS.CLIENT_HELLO).u24(body.length).bytes(body).toBuffer();
  const record = new Writer().u8(CT.HANDSHAKE).u16(0x0301).u16(msg.length)
                             .bytes(msg).toBuffer();
  return { msg, record };
}

// ═══════════════════════════════════════════════════════════════════════════
// 7. Server message parsing
// ═══════════════════════════════════════════════════════════════════════════

function parseServerHello(payload) {
  const r = new Reader(payload);
  const legacyVersion = r.u16();
  const random = r.bytes(32);
  const sessionId = r.vec8();
  const suite = r.u16();
  const compression = r.u8();
  const extLen = r.u16();
  const exts = new Reader(r.bytes(extLen));

  let selectedVersion = null;
  let keyShare = null;
  let serverAlpn = null;

  while (exts.remaining > 0) {
    const type = exts.u16();
    const body = exts.vec16();
    if (type === EXT.SUPPORTED_VERSIONS) {
      selectedVersion = new Reader(body).u16();
    } else if (type === EXT.KEY_SHARE) {
      const ks = new Reader(body);
      const group = ks.u16();
      keyShare = { group, key: ks.vec16() };
    } else if (type === EXT.ALPN) {
      serverAlpn = new Reader(new Reader(body).vec16()).vec8().toString('ascii');
    }
  }
  return { legacyVersion, random, sessionId, suite, compression,
           selectedVersion, keyShare, serverAlpn,
           isHelloRetry: random.equals(Buffer.from(
             'cf21ad74e59a6111be1d8c021e65b891c2a211167abb8c5e079e09e2c8a8339c',
             'hex')) };
}

// RFC 8446 §4.3.1. In TLS 1.3 the negotiated ALPN lives here, NOT in
// ServerHello — reading it from ServerHello always yields "no ALPN".
function parseEncryptedExtensions(payload) {
  const r = new Reader(payload);
  const exts = new Reader(r.vec16());
  let alpn = null;
  while (exts.remaining > 0) {
    const type = exts.u16();
    const body = exts.vec16();
    if (type === EXT.ALPN) {
      alpn = new Reader(new Reader(body).vec16()).vec8().toString('ascii');
    }
  }
  return { alpn };
}

function parseCertificate(payload) {
  const r = new Reader(payload);
  r.u8();                              // certificate_request_context
  const listLen = r.u24();
  const list = new Reader(r.bytes(listLen));
  const certs = [];
  while (list.remaining > 0) {
    const der = list.vec24();
    const extLen = new Reader(list.vec16());   // per-cert extensions, ignored
    certs.push(der);
  }
  return certs;
}

// ═══════════════════════════════════════════════════════════════════════════
// 8. CertificateVerify (RFC 8446 §4.4.3) and Finished (RFC 8446 §4.4.4)
// ═══════════════════════════════════════════════════════════════════════════

const SIG_CONTEXT = Buffer.from('TLS 1.3, server CertificateVerify', 'ascii');

// The signed content is: 64 spaces || context || 0x00 || transcript hash.
function certVerifyContent(transcriptHash) {
  return Buffer.concat([Buffer.alloc(64, 0x20), SIG_CONTEXT,
                        Buffer.from([0x00]), transcriptHash]);
}

function verifyCertVerify(payload, transcriptHash, cert) {
  const r = new Reader(payload);
  const scheme = r.u16();
  const sig = r.vec16();
  const content = certVerifyContent(transcriptHash);
  const key = new crypto.X509Certificate(cert).publicKey;

  let okFlag = false;
  let algoName = '';
  if (scheme === 0x0403) {
    algoName = 'ecdsa_secp256r1_sha256';
    okFlag = crypto.verify('sha256', content, key, sig);
  } else if (scheme === 0x0804) {
    algoName = 'rsa_pss_rsae_sha256';
    okFlag = crypto.verify('sha256', content,
      { key, padding: crypto.constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 }, sig);
  } else if (scheme === 0x0401) {
    algoName = 'rsa_pkcs1_sha256';
    okFlag = crypto.verify('sha256', content,
      { key, padding: crypto.constants.RSA_PKCS1_PADDING }, sig);
  } else {
    throw new Error(`unsupported signature scheme 0x${scheme.toString(16)}`);
  }
  return { scheme, algoName, ok: okFlag };
}

function computeFinished(finishedKey, transcriptHash) {
  return hmac(finishedKey, transcriptHash);
}

// What the certificate is FOR: a name, or an address.
//
// The mirror of CertificateValidator.MatchSubjectAltName, and the reason it is
// two checks rather than one: a host that is an address appears in the SAN as an
// iPAddress entry, which no dNSName rule can ever match. The deployed server's
// certificate is exactly that case -- a Let's Encrypt certificate for a bare IP,
// under the `shortlived` profile, which issues NO common name -- so the name-only
// version of this function refused a certificate that is correct in every
// respect. Measured against it on 2026-09-29.
//
// Both sides are compared as BYTES: the address parsed out of the host, the entry
// read from the SAN. That leaves no rule about spelling an address (leading
// zeros, case, "::") able to disagree with the VB.
function ipLiteralToBytes(text) {
  let s = String(text).trim();
  if (s.length >= 2 && s[0] === '[' && s[s.length - 1] === ']') s = s.slice(1, -1);
  if (s.length === 0 || s.includes('%') || s.includes('/')) return null;
  return s.includes(':') ? ipv6ToBytes(s) : ipv4ToBytes(s);
}

function ipv4ToBytes(text) {
  const parts = text.split('.');
  if (parts.length !== 4) return null;
  const out = [];
  for (const part of parts) {
    if (part.length === 0 || part.length > 3) return null;
    // "010" is octal to some parsers and decimal to others: refused, not guessed.
    if (part.length > 1 && part[0] === '0') return null;
    if (!/^[0-9]+$/.test(part)) return null;
    const value = Number(part);
    if (value > 255) return null;
    out.push(value);
  }
  return Buffer.from(out);
}

function ipv6ToBytes(text) {
  const gapAt = text.indexOf('::');
  if (gapAt >= 0 && text.indexOf('::', gapAt + 1) >= 0) return null;
  const head = gapAt >= 0 ? text.slice(0, gapAt) : text;
  const tail = gapAt >= 0 ? text.slice(gapAt + 2) : '';
  const h = ipv6SideToBytes(head);
  const t = ipv6SideToBytes(tail);
  if (!h || !t) return null;
  const gap = 16 - h.length - t.length;
  if (gapAt >= 0) {
    // "::" stands for at least one group of zeros (RFC 4291 §2.2).
    if (gap <= 0) return null;
  } else if (gap !== 0) {
    return null;
  }
  return Buffer.concat([h, Buffer.alloc(gap), t]);
}

function ipv6SideToBytes(text) {
  if (text.length === 0) return Buffer.alloc(0);
  const out = [];
  const parts = text.split(':');
  for (let i = 0; i < parts.length; i++) {
    const part = parts[i];
    if (part.length === 0) return null;
    if (part.includes('.')) {
      // A dotted quad only as the LAST part, where it stands for two groups.
      if (i !== parts.length - 1) return null;
      const quad = ipv4ToBytes(part);
      if (!quad) return null;
      out.push(...quad);
    } else {
      if (part.length > 4 || !/^[0-9a-fA-F]+$/.test(part)) return null;
      const value = parseInt(part, 16);
      out.push((value >> 8) & 0xff, value & 0xff);
    }
  }
  return Buffer.from(out);
}

function formatIpBytes(bytes) {
  if (bytes.length === 4) return [...bytes].join('.');
  const groups = [];
  for (let i = 0; i < 16; i += 2) groups.push(bytes.readUInt16BE(i).toString(16).padStart(4, '0'));
  return groups.join(':');
}

function subjectAltNameEntries(sanText) {
  const names = [];
  const addresses = [];
  for (const raw of String(sanText || '').split(',')) {
    const entry = raw.trim();
    if (/^DNS:/i.test(entry)) {
      names.push(entry.slice(4).trim());
    } else if (/^IP Address:/i.test(entry)) {
      const bytes = ipLiteralToBytes(entry.slice(11).trim());
      if (bytes) addresses.push(bytes);
    }
  }
  return { names, addresses };
}

function matchSubjectAltName(san, host) {
  const hostBytes = ipLiteralToBytes(host);
  if (!hostBytes) {
    // A name: the RFC 6125 rules, unchanged.
    const lower = host.toLowerCase();
    for (const n of san.names) {
      const pat = n.toLowerCase();
      if (pat === lower) return { ok: true, matched: pat };
      if (pat.startsWith('*.')) {
        // Wildcards match exactly one label, and never a bare public suffix.
        const rest = lower.slice(lower.indexOf('.') + 1);
        if (lower.includes('.') && rest === pat.slice(2)) {
          return { ok: true, matched: pat };
        }
      }
    }
    return { ok: false, names: san.names };
  }
  for (const entry of san.addresses) {
    if (entry.length === hostBytes.length && entry.equals(hostBytes)) {
      return { ok: true, matched: formatIpBytes(entry) };
    }
  }
  // An address that is not in the SAN does NOT fall back to the name rules: a
  // dNSName is never an address, and a fallback would be a second chance for a
  // certificate that did not earn the first one.
  return { ok: false, names: san.names, addresses: san.addresses };
}

function hostnameMatches(cert, host) {
  const c = new crypto.X509Certificate(cert);
  return matchSubjectAltName(subjectAltNameEntries(c.subjectAltName || ''), host);
}

// ── What the certificate is for: the executable half ─────────────────────────
//
// One case here is the DEPLOYED certificate's SAN, verbatim as Node prints it
// (captured 2026-09-29 from 34.132.106.149:8443). The rest are the spellings the
// parse has to get right, including the ones that must be REFUSED because they
// mean more than one thing.
const HOST_MATCH_CASES = [
  { san: 'IP Address:34.132.106.149', host: '34.132.106.149', ok: true,
    why: "the deployed server's own SAN" },
  { san: 'IP Address:34.132.106.149', host: '34.132.106.150', ok: false,
    why: 'a different address' },
  { san: 'IP Address:34.132.106.149', host: 'host.example.com', ok: false,
    why: 'a name is not the address this SAN holds' },
  { san: 'DNS:example.com', host: '34.132.106.149', ok: false,
    why: 'an address never matches a dNSName entry' },
  { san: 'DNS:example.com, IP Address:34.132.106.149', host: '34.132.106.149', ok: true,
    why: 'a mixed SAN' },
  { san: 'IP Address:2001:db8::1', host: '2001:0db8:0000:0000:0000:0000:0000:0001', ok: true,
    why: 'two spellings, one address' },
  { san: 'IP Address:2001:db8::1', host: '[2001:db8::1]', ok: true,
    why: 'a url brackets an IPv6 host' },
  { san: 'IP Address:2001:db8::2', host: '2001:db8::1', ok: false,
    why: 'the last group differs' },
  { san: 'IP Address:0:0:0:0:0:ffff:c000:201', host: '::ffff:192.0.2.1', ok: true,
    why: 'a dotted-quad tail is two groups' },
  { san: 'IP Address:34.132.106.149', host: '034.132.106.149', ok: false,
    why: 'a leading zero means two things, so it is refused' },
  { san: 'IP Address:34.132.106.149', host: 'fe80::1%eth0', ok: false,
    why: 'a zone id is a scope, and an iPAddress entry has nowhere to put one' },
  { san: 'DNS:*.example.com', host: 'a.example.com', ok: true,
    why: 'the name rules are unchanged' },
  { san: 'DNS:*.example.com', host: 'a.b.example.com', ok: false,
    why: 'a wildcard is one label' },
  { san: 'DNS:*.example.com', host: 'example.com', ok: false,
    why: 'a wildcard is never the bare domain' },
  { san: '', host: '34.132.106.149', ok: false,
    why: 'no SAN at all fails for an address too, rather than passing by default' },
];

// The VB this file mirrors. A transliteration carries its own copy of every rule,
// so a branch deleted from the VB leaves this file green -- the failure mode that
// tools/proto/engine-choice.mjs shipped once. These read the source.
const VB_SOURCE_CONTRACTS = [
  ['BrowserForWP.Net/Tls13/X509Reader.vb',
   /TagContextIpAddress As Byte = &H87/,
   'the VB reader names the iPAddress tag'],
  ['BrowserForWP.Net/Tls13/X509Reader.vb',
   /name\.Tag = TagContextIpAddress/,
   'the VB reader collects iPAddress entries'],
  ['BrowserForWP.Net/Tls13/X509Reader.vb',
   /If name\.Length = 4 OrElse name\.Length = 16 Then/,
   'the VB reader keeps only real address lengths'],
  ['BrowserForWP.Net/Tls13/CertificateValidator.vb',
   /Dim match = MatchSubjectAltName\(info, hostName\)/,
   'the VB validator dispatches on the host'],
  ['BrowserForWP.Net/Tls13/CertificateValidator.vb',
   /If Not TryParseIpLiteral\(hostName, addressBytes\) Then/,
   'the VB validator parses a literal address'],
  ['BrowserForWP.Net/Tls13/CertificateValidator.vb',
   /SameBytes\(entry, addressBytes\)/,
   'the VB compares address BYTES, not spellings'],
];

function verifyWhatTheCertificateIsFor() {
  console.log('-- What the certificate is for (offline)');
  for (const c of HOST_MATCH_CASES) {
    const res = matchSubjectAltName(subjectAltNameEntries(c.san), c.host);
    const detail = c.ok
      ? (res.ok ? `matched ${res.matched}` : 'EXPECTED A MATCH, refused')
      : (res.ok ? `MATCHED ${res.matched}, should have been refused` : 'refused');
    check(res.ok === c.ok, `host match: ${c.host} against "${c.san || '(no SAN)'}"`,
          `${detail} -- ${c.why}`);
  }
  for (const [file, pattern, label] of VB_SOURCE_CONTRACTS) {
    const text = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
    check(pattern.test(text), label, text ? file : `${file} is missing`);
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// 9. The client
// ═══════════════════════════════════════════════════════════════════════════

class Tls13ClientProto {
  constructor(host) {
    this.host = host;
    this.buf = Buffer.alloc(0);
    this.transcript = [];
    this.handshakeBuf = Buffer.alloc(0);
  }

  transcriptHash() {
    const h = crypto.createHash('sha256');
    for (const m of this.transcript) h.update(m);
    return h.digest();
  }
  addTranscript(msg) { this.transcript.push(msg); }

  // A push-based byte queue. The naive version — awaiting a fresh 'data'
  // listener per read — deadlocks the moment the peer closes the connection,
  // and can miss a chunk that lands between two awaits. Attaching the listeners
  // once and parking a single waiter is both correct and simpler.
  attachSocketHandlers() {
    this.waiter = null;
    this.closed = null;
    this.sock.on('data', (d) => {
      this.buf = Buffer.concat([this.buf, d]);
      this.wake();
    });
    this.sock.on('end', () => this.closeWith(new Error('connection closed by peer')));
    this.sock.on('error', (e) => this.closeWith(e));
    this.sock.on('close', () => this.closeWith(new Error('socket closed')));
  }

  wake() {
    if (this.waiter) { const w = this.waiter; this.waiter = null; w(); }
  }

  closeWith(err) {
    if (!this.closed) this.closed = err;
    this.wake();
  }

  // Pull exactly n bytes off the socket.
  async read(n) {
    while (this.buf.length < n) {
      if (this.closed) {
        throw this.closed.message === 'socket closed' && n === 0
          ? this.closed
          : new Error(`${this.closed.message} (wanted ${n} more bytes, had ${this.buf.length})`);
      }
      await new Promise((resolve) => { this.waiter = resolve; });
    }
    const out = this.buf.subarray(0, n);
    this.buf = this.buf.subarray(n);
    return out;
  }

  async readRecord() {
    const header = await this.read(5);
    const length = header.readUInt16BE(3);
    const body = await this.read(length);
    if (this.trace) {
      console.log(`    [record] type=${header[0]} ver=0x${header.readUInt16BE(1).toString(16)}` +
                  ` len=${length}`);
    }
    return { type: header[0], version: header.readUInt16BE(1), body,
             raw: Buffer.concat([header, body]) };
  }

  async write(buf) {
    await new Promise((resolve, reject) =>
      this.sock.write(buf, (e) => e ? reject(e) : resolve()));
  }

  // Reassemble handshake messages, which are not aligned to record boundaries.
  async nextHandshakeMessage(recordLayer) {
    for (;;) {
      while (this.handshakeBuf.length >= 4) {
        const len = this.handshakeBuf.readUIntBE(1, 3);
        if (this.handshakeBuf.length < 4 + len) break;
        const msg = this.handshakeBuf.subarray(0, 4 + len);
        this.handshakeBuf = this.handshakeBuf.subarray(4 + len);
        return { type: msg[0], body: msg.subarray(4), raw: msg };
      }
      const rec = await this.readRecord();
      if (rec.type === CT.CCS) continue;     // compatibility mode, ignore
      if (rec.type === CT.ALERT) {
        throw new Error(`alert during handshake: ${describeAlert(rec.body)}`);
      }
      let opened;
      try {
        opened = recordLayer.open(rec.raw);
      } catch (e) {
        throw new Error(`record ${this.recordsOpened || 0} failed to decrypt ` +
                        `(${e.message}); keys or nonce are wrong`);
      }
      this.recordsOpened = (this.recordsOpened || 0) + 1;
      if (opened.type === CT.ALERT) {
        throw new Error(`encrypted alert: ${describeAlert(opened.payload)}`);
      }
      this.handshakeBuf = Buffer.concat([this.handshakeBuf, opened.payload]);
    }
  }

  async connect(port = 443) {
    step(`TCP connect ${this.host}:${port}`);
    this.sock = net.connect(port, this.host);
    await new Promise((resolve, reject) => {
      this.sock.once('connect', resolve);
      this.sock.once('error', reject);
    });
    ok('TCP connected');
    this.attachSocketHandlers();
    this.sock.setTimeout(20000, () => {
      this.closeWith(new Error('idle timeout after 20s'));
    });

    const { privateKey, publicKey } = crypto.generateKeyPairSync('x25519');
    this.x25519Private = privateKey;
    const rawPub = publicKey.export({ type: 'spki', format: 'der' }).subarray(-32);

    const random = crypto.randomBytes(32);
    const sessionId = crypto.randomBytes(32);
    const { msg, record } = buildClientHello(this.host, rawPub, random, sessionId);
    this.addTranscript(msg);

    step('ClientHello');
    ok(`sent ${record.length} bytes`,
       `SNI=${this.host} session_id=${sessionId.length}B`);
    await this.write(record);

    // Compatibility mode: a dummy CCS right after ClientHello keeps
    // middleboxes that assume TLS 1.2 from stalling the stream.
    await this.write(Buffer.from([CT.CCS, 0x03, 0x03, 0x00, 0x01, 0x01]));

    step('ServerHello');
    const shRec = await this.readRecord();
    if (shRec.type === CT.ALERT) {
      throw new Error(`server sent a plaintext alert: ${describeAlert(shRec.body)}`);
    }
    eq(shRec.type, CT.HANDSHAKE, 'ServerHello record type is handshake');
    const shReader = new Reader(shRec.body);
    const shType = shReader.u8();
    const shLen = shReader.u24();
    const shBody = shReader.bytes(shLen);
    eq(shType, HS.SERVER_HELLO, 'handshake type is server_hello');
    const sh = parseServerHello(shBody);
    check(sh.selectedVersion === 0x0304, 'server selected TLS 1.3',
          `0x${(sh.selectedVersion ?? 0).toString(16)}`);
    check(!sh.isHelloRetry, 'no HelloRetryRequest on the first flight');
    check(sh.suite === SUITE_AES128, 'negotiated TLS_AES_128_GCM_SHA256',
          `0x${sh.suite.toString(16)}`);
    check(sh.keyShare && sh.keyShare.group === GROUP_X25519,
          'server key_share is x25519');
    ok('server random', hex(sh.random).slice(0, 16) + '...');

    this.addTranscript(shRec.body.subarray(0, 4 + shLen));

    // ── X25519 shared secret ────────────────────────────────────────────────
    const spki = Buffer.concat([X25519_SPKI_PREFIX, sh.keyShare.key]);
    const peer = crypto.createPublicKey({ key: spki, format: 'der', type: 'spki' });
    const shared = crypto.diffieHellman({ privateKey: this.x25519Private,
                                          publicKey: peer });
    eq(shared.length, 32, 'X25519 shared secret is 32 bytes');

    // ── Key schedule ────────────────────────────────────────────────────────
    step('Key schedule (RFC 8446 §7.1)');
    const ks = new KeySchedule();
    ks.handshakeSecret(shared);
    const thSh = this.transcriptHash();
    const chs = ks.clientHsTraffic(thSh);
    const shs = ks.serverHsTraffic(thSh);
    ok('handshake traffic secrets derived', hex(thSh).slice(0, 16) + '...');

    const serverLayer = new RecordLayer(shs.key, shs.iv);
    const clientLayer = new RecordLayer(chs.key, chs.iv);
    serverLayer.label = 'server-hs';
    clientLayer.label = 'client-hs';
    serverLayer.trace = this.trace;
    clientLayer.trace = this.trace;

    // ── Encrypted flight ────────────────────────────────────────────────────
    step('EncryptedExtensions');
    let m = await this.nextHandshakeMessage(serverLayer);
    check(m.type === HS.EE, 'received EncryptedExtensions',
          `type=${m.type}`);
    const ee = parseEncryptedExtensions(m.body);
    this.negotiatedAlpn = ee.alpn;
    ok('negotiated ALPN', ee.alpn || '(none)');
    this.addTranscript(m.raw);

    step('Certificate');
    m = await this.nextHandshakeMessage(serverLayer);
    check(m.type === HS.CERTIFICATE, 'received Certificate');
    const certs = parseCertificate(m.body);
    ok(`${certs.length} certificate(s) in chain`,
       `leaf ${certs[0].length} bytes DER`);
    this.addTranscript(m.raw);

    const leaf = new crypto.X509Certificate(certs[0]);
    // `subject` is UNDEFINED -- not empty -- for a certificate with an empty
    // subject, and an empty subject is exactly what the `shortlived` profile
    // issues: no common name, the address in the SAN only. Reading it unguarded
    // crashed this prototype on the deployed server's own certificate, before a
    // single check ran, which is how the crash was found.
    ok('leaf subject', (leaf.subject ?? '(empty: no common name)').replace(/\n/g, ' ').slice(0, 60));
    ok('leaf issuer ', (leaf.issuer ?? '(empty)').replace(/\n/g, ' ').slice(0, 60));
    ok('leaf SAN    ', (leaf.subjectAltName ?? '(none)').slice(0, 80));
    check(leaf.validTo !== undefined &&
          Date.parse(leaf.validTo) > Date.now(), 'leaf certificate not expired',
          `valid until ${leaf.validTo}`);

    const hostMatch = hostnameMatches(certs[0], this.host);
    check(hostMatch.ok, 'the certificate is for this host',
          hostMatch.ok
            ? hostMatch.matched
            : `names=${(hostMatch.names || []).join('|')}`);

    step('CertificateVerify');
    m = await this.nextHandshakeMessage(serverLayer);
    check(m.type === HS.CERT_VERIFY, 'received CertificateVerify');
    const verify = verifyCertVerify(m.body, this.transcriptHash(), certs[0]);
    check(verify.ok, 'server proved possession of the cert private key',
          verify.algoName);
    this.addTranscript(m.raw);

    step('Finished (server)');
    m = await this.nextHandshakeMessage(serverLayer);
    check(m.type === HS.FINISHED, 'received Finished');
    const expectServerFin = computeFinished(shs.finishedKey, this.transcriptHash());
    eq(m.body, expectServerFin, 'server Finished verify_data matches');
    this.addTranscript(m.raw);

    // Application secrets are derived over the transcript up to server Finished.
    const thFin = this.transcriptHash();
    const cap = ks.clientApTraffic(thFin);
    const sap = ks.serverApTraffic(thFin);
    const exporter = ks.exporterMaster(thFin);
    ok('application traffic secrets derived', hex(exporter).slice(0, 16) + '...');

    // ── Client Finished ─────────────────────────────────────────────────────
    step('Finished (client)');
    const clientFin = computeFinished(chs.finishedKey, thFin);
    const finMsg = new Writer().u8(HS.FINISHED).u24(clientFin.length)
                               .bytes(clientFin).toBuffer();
    this.addTranscript(finMsg);
    await this.write(clientLayer.seal(CT.HANDSHAKE, finMsg));
    ok('sent Finished under handshake keys');

    this.readLayer = new RecordLayer(sap.key, sap.iv);
    this.writeLayer = new RecordLayer(cap.key, cap.iv);

    console.log('\n\u2500\u2500 Handshake complete');
    ok('TLS 1.3 established',
       `suite=0x${sh.suite.toString(16)} alpn=${this.negotiatedAlpn || '(none)'}`);
    check(this.negotiatedAlpn === null || this.negotiatedAlpn === 'http/1.1',
          'server agreed to a protocol we can actually speak');
  }

  async readApplicationData() {
    for (;;) {
      const rec = await this.readRecord();
      if (rec.type === CT.CCS) continue;
      const opened = this.readLayer.open(rec.raw);
      if (opened.type === CT.ALERT) {
        throw new Error(`peer alert: ${hex(opened.payload)}`);
      }
      if (opened.type === CT.APP) return opened.payload;
      // Post-handshake messages (NewSessionTicket) are skipped.
    }
  }

  async send(request) {
    await this.write(this.writeLayer.seal(CT.APP, Buffer.from(request, 'utf8')));
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// 10. Driver
// ═══════════════════════════════════════════════════════════════════════════

async function main() {
  const handshakeOnly = process.argv.includes('--handshake-only');
  const argv = process.argv.slice(2).filter((a) => !a.startsWith('--'));
  const host = argv[0] || 'cloudflare.com';
  const port = Number(argv[1] || 443);
  const path = argv[2] || '/';

  console.log('TLS 1.3 client prototype — BrowserForWP');
  verifyWhatTheCertificateIsFor();
  console.log(`\nTarget: https://${host}:${port}${path}`);
  if (handshakeOnly) console.log('(--handshake-only: the HTTP request is skipped)');

  const client = new Tls13ClientProto(host);
  client.trace = !!process.env.TRACE;
  try {
    await client.connect(port);
  } catch (e) {
    fail('handshake', e.message);
    if (process.env.TRACE) console.log(e.stack);
    console.log(`\n${checks} checks, ${failures} failure(s)`);
    process.exit(1);
  }

  // The render server is not an HTTP server: it answers with sealed frames, so the
  // request below would earn a closed connection and a failed status-line check.
  // What this prototype has to prove there is the handshake, the
  // CertificateVerify, and whether the certificate is FOR the host we asked --
  // which is every certificate check the phone's own TLS stack performs. An early
  // exit with its own summary keeps that run honest instead of red for the wrong
  // reason.
  if (handshakeOnly) {
    ok('stopped before the HTTP request', '--handshake-only');
    console.log(`\n${checks} checks, ${failures} failure(s)`);
    client.sock.destroy();
    process.exit(failures === 0 ? 0 : 1);
  }

  step('HTTP request over the established channel');
  const request =
    `GET ${path} HTTP/1.1\r\n` +
    `Host: ${host}\r\n` +
    `User-Agent: BrowserForWP/0.1 (TLS 1.3 prototype)\r\n` +
    `Accept: */*\r\n` +
    `Connection: close\r\n\r\n`;
  await client.send(request);
  ok('sent GET request as application data');

  let response = Buffer.alloc(0);
  try {
    for (let i = 0; i < 40; i++) {
      response = Buffer.concat([response, await client.readApplicationData()]);
      if (response.length > 2048) break;
    }
  } catch (e) {
    ok('read terminated', e.message);
  }

  const text = response.toString('utf8');
  const statusLine = text.split('\r\n')[0] || '';
  check(/^HTTP\/1\.[01] \d{3}/.test(statusLine),
        'got a well-formed HTTP status line', statusLine);
  check(response.length > 0, `received ${response.length} bytes of plaintext`);

  console.log(`\n${checks} checks, ${failures} failure(s)`);
  client.sock.destroy();
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error('\nunexpected failure:', e);
  process.exit(1);
});
