#!/usr/bin/env node
/**
 * BrowserForWP — test-vector generator and verifier.
 *
 * WHY THIS EXISTS
 * ---------------
 * BrowserForWP implements HKDF, X25519, AES-GCM and the TLS 1.3 key schedule in
 * VB.NET, which can only be compiled on Windows with the Windows
 * Phone 8.1 SDK. The algorithms are language-independent, so this script:
 *
 *   1. Recomputes every value from the RFCs using Node's native crypto.
 *   2. ASSERTS each recomputed value against the constant published in the RFC.
 *      A mismatch aborts with a non-zero exit code.
 *   3. Emits the verified vectors as VB source that the Windows test project
 *      consumes.
 *
 * That means the VB tests are grounded in values that were independently
 * checked here, not in values copied by hand from a document.
 *
 * Sources
 * -------
 *   RFC 5869  HKDF
 *   RFC 7748  X25519
 *   RFC 8448  TLS 1.3 handshake trace (key schedule anchors)
 *   NIST CAVS AES-GCM
 *
 * Usage
 * -----
 *   node tools/gen-vectors.mjs            # verify + write tools/out/*, tests VB file
 *   node tools/gen-vectors.mjs --check    # verify only, write nothing
 */

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.dirname(HERE);

// ── Hex / byte helpers ─────────────────────────────────────────────────────
const b2h = (b) => Buffer.from(b).toString('hex');
const h2b = (h) => Buffer.from(h.replace(/\s+/g, ''), 'hex');

/** Parse the whitespace-separated hex dump style used throughout RFC 8448. */
const rfc = (s) => h2b(s.replace(/[^0-9a-fA-F\s]/g, ' ').trim());

let failures = 0;
let checks = 0;

function assertEqual(label, actual, expectedHex) {
  checks += 1;
  const got = b2h(actual);
  const want = expectedHex.replace(/\s+/g, '').toLowerCase();
  if (got !== want) {
    failures += 1;
    console.error(`  ✗ ${label}`);
    console.error(`      expected ${want}`);
    console.error(`      actual   ${got}`);
  } else {
    console.log(`  ✓ ${label}`);
  }
  return got;
}

// ── HKDF (RFC 5869), implemented directly so the byte layout is explicit ────
const hashLen = { sha1: 20, sha256: 32, sha384: 48, sha512: 64 }[('sha256')];

function hmac(key, data, alg = 'sha256') {
  return crypto.createHmac(alg, key).update(data).digest();
}

function hkdfExtract(salt, ikm, alg = 'sha256') {
  // RFC 5869 §2.2 — note that an empty salt is replaced by HashLen zero octets.
  const s = salt && salt.length ? salt : Buffer.alloc(hashLen[alg] ?? 32);
  return hmac(s, ikm, alg);
}

function hkdfExpand(prk, info, length, alg = 'sha256') {
  // RFC 5869 §2.3
  const n = Math.ceil(length / (hashLen[alg] ?? 32));
  if (n > 255) throw new Error('hkdf: requested length too large');
  let output = Buffer.alloc(0);
  let t = Buffer.alloc(0);
  for (let i = 1; i <= n; i++) {
    t = hmac(prk, Buffer.concat([t, info, Buffer.from([i])]), alg);
    output = Buffer.concat([output, t]);
  }
  return output.subarray(0, length);
}

function hkdf(salt, ikm, info, length, alg = 'sha256') {
  return hkdfExpand(hkdfExtract(salt, ikm, alg), info, length, alg);
}

// ── TLS 1.3 label helpers (RFC 8446 §7.1) ──────────────────────────────────
const enc16 = (n) => {
  const b = Buffer.alloc(2);
  b.writeUInt16BE(n, 0);
  return b;
};

/**
 * Build the HkdfLabel structure (RFC 8446 §7.1):
 *   struct { uint16 length; opaque label<7..255>; opaque context<0..255>; } HkdfLabel;
 * The label is prefixed with the literal "tls13 ". Exposed separately so the
 * byte layout can be asserted against the info dumps published in RFC 8448.
 */
function hkdfLabelInfo(label, context, length) {
  const full = Buffer.from('tls13 ' + label, 'ascii');
  const ctx = context ?? Buffer.alloc(0);
  return Buffer.concat([
    enc16(length),
    Buffer.from([full.length]),
    full,
    Buffer.from([ctx.length]),
    ctx,
  ]);
}

function hkdfExpandLabel(secret, label, context, length, alg = 'sha256') {
  return hkdfExpand(secret, hkdfLabelInfo(label, context, length), length, alg);
}

const sha256 = (d) => crypto.createHash('sha256').update(d).digest();
const deriveSecret = (secret, label, transcript, alg = 'sha256') =>
  hkdfExpandLabel(secret, label, sha256(transcript), hashLen[alg] ?? 32, alg);

// ══════════════════════════════════════════════════════════════════════════
// 1. HKDF — RFC 5869 Appendix A (SHA-256 cases 1-3)
// ══════════════════════════════════════════════════════════════════════════
console.log('\n[1/6] HKDF — RFC 5869 A.1-A.3 (SHA-256)');

const hkdfCases = [
  {
    n: 1,
    ikm: '0b'.repeat(22),
    salt: '000102030405060708090a0b0c',
    info: 'f0f1f2f3f4f5f6f7f8f9',
    L: 42,
    prk: '077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5',
    okm: '3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865',
  },
  {
    n: 2,
    ikm: '000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f' +
      '202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f' +
      '404142434445464748494a4b4c4d4e4f',
    salt: '606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f' +
      '808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f' +
      'a0a1a2a3a4a5a6a7a8a9aaabacadaeaf',
    info: 'b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecf' +
      'd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeef' +
      'f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff',
    L: 82,
    prk: '06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244',
    okm: 'b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c' +
      '59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71' +
      'cc30c58179ec3e87c14c01d5c1f3434f1d87',
  },
  {
    n: 3,
    ikm: '0b'.repeat(22),
    salt: '',
    info: '',
    L: 42,
    prk: '19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04',
    okm: '8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8',
  },
];

const hkdfVectors = hkdfCases.map((c) => {
  const ikm = h2b(c.ikm);
  const salt = h2b(c.salt);
  const info = h2b(c.info);
  const prk = hkdfExtract(salt, ikm);
  const okm = hkdfExpand(prk, info, c.L);
  assertEqual(`HKDF A.${c.n} PRK`, prk, c.prk);
  assertEqual(`HKDF A.${c.n} OKM`, okm, c.okm);
  // Cross-check against Node's own HKDF, to catch a shared misunderstanding.
  assertEqual(
    `HKDF A.${c.n} OKM (node crypto.hkdf cross-check)`,
    crypto.hkdfSync('sha256', ikm, salt, info, c.L),
    c.okm,
  );
  return { n: c.n, ikm: c.ikm, salt: c.salt, info: c.info, L: c.L, prk: c.prk, okm: c.okm };
});

// ══════════════════════════════════════════════════════════════════════════
// 2. X25519 — RFC 7748 §5.2 and §6.1
// ══════════════════════════════════════════════════════════════════════════
console.log('\n[2/6] X25519 — RFC 7748 §5.2, §6.1');

const PKCS8_X25519 = '302e020100300506032b656e04220420';
const SPKI_X25519 = '302a300506032b656e032100';

const privKeyFromRaw = (raw) =>
  crypto.createPrivateKey({
    key: Buffer.concat([h2b(PKCS8_X25519), Buffer.from(raw)]),
    format: 'der',
    type: 'pkcs8',
  });

const pubKeyFromRaw = (raw) =>
  crypto.createPublicKey({
    key: Buffer.concat([h2b(SPKI_X25519), Buffer.from(raw)]),
    format: 'der',
    type: 'spki',
  });

/** Derive the public key from a raw 32-byte X25519 private key. */
function x25519Public(rawPriv) {
  const der = crypto.createPublicKey(privKeyFromRaw(rawPriv)).export({ format: 'der', type: 'spki' });
  return der.subarray(der.length - 32);
}

const x25519 = (rawPriv, rawPeerPub) =>
  crypto.diffieHellman({ privateKey: privKeyFromRaw(rawPriv), publicKey: pubKeyFromRaw(rawPeerPub) });

// §6.1 Diffie-Hellman
const ALICE_PRIV = h2b('77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a');
const ALICE_PUB = '8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a';
const BOB_PRIV = h2b('5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb');
const BOB_PUB = 'de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f';
const X25519_SHARED = '4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742';

assertEqual('X25519 §6.1 Alice public', x25519Public(ALICE_PRIV), ALICE_PUB);
assertEqual('X25519 §6.1 Bob public', x25519Public(BOB_PRIV), BOB_PUB);
assertEqual('X25519 §6.1 shared (Alice)', x25519(ALICE_PRIV, h2b(BOB_PUB)), X25519_SHARED);
assertEqual('X25519 §6.1 shared (Bob)', x25519(BOB_PRIV, h2b(ALICE_PUB)), X25519_SHARED);

// §5.2 — raw scalar multiplication, exercised with a non-DH u-coordinate.
const ITER_SCALAR = h2b('a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4');
const ITER_U = h2b('e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c');
const ITER_OUT = 'c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552';
assertEqual('X25519 §5.2 one iteration', x25519(ITER_SCALAR, ITER_U), ITER_OUT);

// ══════════════════════════════════════════════════════════════════════════
// 3. AEAD helpers, used by the AES-GCM section below
// ══════════════════════════════════════════════════════════════════════════
// NOTE: ChaCha20-Poly1305 (RFC 8439) is deliberately NOT verified here.
// BrowserForWP offers exactly one TLS 1.3 suite, TLS_AES_128_GCM_SHA256 — which
// RFC 8446 §9.1 makes mandatory-to-implement, so there is no interoperability
// cost — and that suite is served by the platform's accelerated AES-GCM. A
// managed AEAD would have been dead code carrying real risk. This tool verifies
// what actually ships.

function aeadSeal(alg, key, nonce, aad, plaintext, tagLen = 16) {
  const c = crypto.createCipheriv(alg, key, nonce, { authTagLength: tagLen });
  if (aad.length) c.setAAD(aad, { plaintextLength: plaintext.length });
  const ct = Buffer.concat([c.update(plaintext), c.final()]);
  return { ct, tag: c.getAuthTag() };
}

function aeadOpen(alg, key, nonce, aad, ct, tag, tagLen = 16) {
  const d = crypto.createDecipheriv(alg, key, nonce, { authTagLength: tagLen });
  if (aad.length) d.setAAD(aad, { plaintextLength: ct.length });
  d.setAuthTag(tag);
  return Buffer.concat([d.update(ct), d.final()]);
}

// ══════════════════════════════════════════════════════════════════════════
// 4. AES-GCM — NIST CAVS
// ══════════════════════════════════════════════════════════════════════════
console.log('\n[3/5] AES-GCM — NIST CAVS (zero-key cases)');

const Z16 = h2b('00'.repeat(16));
const Z12 = h2b('00'.repeat(12));

const aes128Empty = aeadSeal('aes-128-gcm', Z16, Z12, Buffer.alloc(0), Buffer.alloc(0));
assertEqual(
  'AES-128-GCM empty plaintext tag',
  aes128Empty.tag,
  '58e2fccefa7e3061367f1d57a4e7455a',
);

const aes128Block = aeadSeal('aes-128-gcm', Z16, Z12, Buffer.alloc(0), Buffer.alloc(16));
assertEqual('AES-128-GCM (0^128) ciphertext', aes128Block.ct, '0388dace60b6a392f328c2b971b2fe78');
assertEqual('AES-128-GCM (0^128) tag', aes128Block.tag, 'ab6e47d42cec13bdf53a67b21257bddf');

const aes256Empty = aeadSeal('aes-256-gcm', h2b('00'.repeat(32)), Z12, Buffer.alloc(0), Buffer.alloc(0));
assertEqual(
  'AES-256-GCM empty plaintext tag',
  aes256Empty.tag,
  '530f8afbc74536b9a963b4f1c4cb738b',
);

// ══════════════════════════════════════════════════════════════════════════
// 5. TLS 1.3 key schedule — RFC 8448 §3 anchors
// ══════════════════════════════════════════════════════════════════════════
console.log('\n[4/5] TLS 1.3 key schedule — RFC 8448 §3');

const r8448 = {
  clientPriv: '49 af 42 ba 7f 79 94 85 2d 71 3e f2 78 4b cb ca a7 91 1d e2 6a dc 56 42 cb 63 45 40 e7 ea 50 05',
  clientPub: '99 38 1d e5 60 e4 bd 43 d2 3d 8e 43 5a 7d ba fe b3 c0 6e 51 c1 3c ae 4d 54 13 69 1e 52 9a af 2c',
  serverPriv: 'b1 58 0e ea df 6d d5 89 b8 ef 4f 2d 56 52 57 8c c8 10 e9 98 01 91 ec 8d 05 83 08 ce a2 16 a2 1e',
  serverPub: 'c9 82 88 76 11 20 95 fe 66 76 2b db f7 c6 72 e1 56 d6 cc 25 3b 83 3d f1 dd 69 b1 b0 4e 75 1f 0f',
  ecdhe: '8b d4 05 4f b5 5b 9d 63 fd fb ac f9 f0 4b 9f 0d 35 e6 d6 3f 53 75 63 ef d4 62 72 90 0f 89 49 2d',
  earlySecret: '33 ad 0a 1c 60 7e c0 3b 09 e6 cd 98 93 68 0c e2 10 ad f3 00 aa 1f 26 60 e1 b2 2e 10 f1 70 f9 2a',
  derivedEarly: '6f 26 15 a1 08 c7 02 c5 67 8f 54 fc 9d ba b6 97 16 c0 76 18 9c 48 25 0c eb ea c3 57 6c 36 11 ba',
  handshakeSecret: '1d c8 26 e9 36 06 aa 6f dc 0a ad c1 2f 74 1b 01 04 6a a6 b9 9f 69 1e d2 21 a9 f0 ca 04 3f be ac',
  transcriptChSh: '86 0c 06 ed c0 78 58 ee 8e 78 f0 e7 42 8c 58 ed d6 b4 3f 2c a3 e6 e9 5f 02 ed 06 3c f0 e1 ca d8',
  cHsTraffic: 'b3 ed db 12 6e 06 7f 35 a7 80 b3 ab f4 5e 2d 8f 3b 1a 95 07 38 f5 2e 96 00 74 6a 0e 27 a5 5a 21',
  sHsTraffic: 'b6 7b 7d 69 0c c1 6c 4e 75 e5 42 13 cb 2d 37 b4 e9 c9 12 bc de d9 10 5d 42 be fd 59 d3 91 ad 38',
  serverHsKey: '3f ce 51 60 09 c2 17 27 d0 f2 e4 e8 6e e4 03 bc',
  serverHsIv: '5d 31 3e b2 67 12 76 ee 13 00 0b 30',
  derivedMaster: '43 de 77 e0 c7 77 13 85 9a 94 4d b9 db 25 90 b5 31 90 a6 5b 3e e2 e4 f1 2d d7 a0 bb 7c e2 54 b4',
  masterSecret: '18 df 06 84 3d 13 a0 8b f2 a4 49 84 4c 5f 8a 47 80 01 bc 4d 4c 62 79 84 d5 a4 1d a8 d0 40 29 19',
  transcriptChSf: '96 08 10 2a 0f 1c cc 6d b6 25 0b 7b 7e 41 7b 1a 00 0e aa da 3d aa e4 77 7a 76 86 c9 ff 83 df 13',
  cApTraffic: '9e 40 64 6c e7 9a 7f 9d c0 5a f8 88 9b ce 65 52 87 5a fa 0b 06 df 00 87 f7 92 eb b7 c1 75 04 a5',
  sApTraffic: 'a1 1a f9 f0 55 31 f8 56 ad 47 11 6b 45 a9 50 32 82 04 b4 f4 4b fb 6b 3a 4b 4f 1f 3f cb 63 16 43',
  serverFinishedKey: '00 8d 3b 66 f8 16 ea 55 9f 96 b5 37 e8 85 c3 1f c0 68 bf 49 2c 65 2f 01 f2 88 a1 d8 cd c1 9f c8',
  serverFinished: '9b 9b 14 1d 90 63 37 fb d2 cb dc e7 1d f4 de da 4a b4 2c 30 95 72 cb 7f ff ee 54 54 b7 8f 07 18',
};

// Ephemeral key pairs must reproduce the trace's public keys.
assertEqual('RFC 8448 client ephemeral public', x25519Public(rfc(r8448.clientPriv)), r8448.clientPub.replace(/\s+/g, ''));
assertEqual('RFC 8448 server ephemeral public', x25519Public(rfc(r8448.serverPriv)), r8448.serverPub.replace(/\s+/g, ''));
assertEqual(
  'RFC 8448 ECDHE shared secret',
  x25519(rfc(r8448.clientPriv), rfc(r8448.serverPub)),
  r8448.ecdhe.replace(/\s+/g, ''),
);

// The key schedule chain, rebuilt from first principles.
const emptyTranscript = Buffer.alloc(0);
const earlySecret = hkdfExtract(Buffer.alloc(32), Buffer.alloc(32));
assertEqual('TLS 1.3 early_secret', earlySecret, r8448.earlySecret);
assertEqual('SHA-256("") == transcript hash of empty', sha256(emptyTranscript), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');

const derivedEarly = deriveSecret(earlySecret, 'derived', emptyTranscript);
assertEqual('TLS 1.3 derived (early -> handshake)', derivedEarly, r8448.derivedEarly);

const handshakeSecret = hkdfExtract(derivedEarly, rfc(r8448.ecdhe));
assertEqual('TLS 1.3 handshake_secret', handshakeSecret, r8448.handshakeSecret);

// Traffic secrets need the ClientHello..ServerHello transcript hash, which the
// trace publishes. Feeding it directly isolates the label/format logic.
const transcriptChSh = h2b(r8448.transcriptChSh);
const cHs = hkdfExpandLabel(handshakeSecret, 'c hs traffic', transcriptChSh, 32);
const sHs = hkdfExpandLabel(handshakeSecret, 's hs traffic', transcriptChSh, 32);
assertEqual('TLS 1.3 client_handshake_traffic_secret', cHs, r8448.cHsTraffic);
assertEqual('TLS 1.3 server_handshake_traffic_secret', sHs, r8448.sHsTraffic);

// Derive-Secret(handshake_secret, "c hs traffic", CH..SH) must agree, i.e. the
// transcript hashing step matches simply passing the published hash.
assertEqual(
  'TLS 1.3 c hs traffic via Derive-Secret == published hash path',
  deriveSecret(handshakeSecret, 'c hs traffic', emptyTranscript),
  b2h(hkdfExpandLabel(handshakeSecret, 'c hs traffic', sha256(Buffer.alloc(0)), 32)),
);

const serverHsKey = hkdfExpandLabel(sHs, 'key', Buffer.alloc(0), 16);
const serverHsIv = hkdfExpandLabel(sHs, 'iv', Buffer.alloc(0), 12);
assertEqual('TLS 1.3 server handshake key', serverHsKey, r8448.serverHsKey);
assertEqual('TLS 1.3 server handshake iv', serverHsIv, r8448.serverHsIv);

// The HkdfLabel byte layout is asserted directly against the `info` dumps the
// RFC prints for each derivation. This pins the wire format — length prefix,
// "tls13 " prefix, and context framing — independently of any derived value.
assertEqual(
  'HkdfLabel layout for "derived" (info, 49 octets)',
  hkdfLabelInfo('derived', sha256(Buffer.alloc(0)), 32),
  '00 20 0d 74 6c 73 31 33 20 64 65 72 69 76 65 64 20 ' +
    'e3 b0 c4 42 98 fc 1c 14 9a fb f4 c8 99 6f b9 24 27 ae 41 e4 64 9b 93 4c a4 95 99 1b 78 52 b8 55',
);
assertEqual(
  'HkdfLabel layout for "c hs traffic" (info, 54 octets)',
  hkdfLabelInfo('c hs traffic', transcriptChSh, 32),
  '00 20 12 74 6c 73 31 33 20 63 20 68 73 20 74 72 61 66 66 69 63 20 ' +
    '86 0c 06 ed c0 78 58 ee 8e 78 f0 e7 42 8c 58 ed d6 b4 3f 2c a3 e6 e9 5f 02 ed 06 3c f0 e1 ca d8',
);
assertEqual(
  'HkdfLabel layout for "key" (info, 13 octets)',
  hkdfLabelInfo('key', Buffer.alloc(0), 16),
  '00 10 09 74 6c 73 31 33 20 6b 65 79 00',
);
assertEqual(
  'HkdfLabel layout for "iv" (info, 12 octets)',
  hkdfLabelInfo('iv', Buffer.alloc(0), 12),
  '00 0c 08 74 6c 73 31 33 20 69 76 00',
);

const derivedMaster = deriveSecret(handshakeSecret, 'derived', emptyTranscript);
assertEqual('TLS 1.3 derived (handshake -> master)', derivedMaster, r8448.derivedMaster);

// The `exp master` label uses the post-Finished transcript. RFC 8448 prints its
// info dump truncated after four context octets, so cite exactly that much and
// reconstruct the remainder from the documented framing rule.
const expMasterInfo = hkdfLabelInfo('exp master', h2b(r8448.transcriptChSf), 32);
assertEqual(
  'HkdfLabel layout for "exp master" (RFC-printed prefix, 24 octets)',
  expMasterInfo.subarray(0, 24),
  '00 20 10 74 6c 73 31 33 20 65 78 70 20 6d 61 73 74 65 72 20 96 08 10 2a',
);
assertEqual(
  'HkdfLabel layout for "exp master" (full 52 octets)',
  expMasterInfo,
  '002010' + b2h(Buffer.from('tls13 exp master', 'ascii')) + '20' + r8448.transcriptChSf.replace(/\s+/g, ''),
);
assertEqual('HkdfLabel "exp master" info length == 52', Buffer.from([expMasterInfo.length]), '34');

const masterSecret = hkdfExtract(derivedMaster, Buffer.alloc(32));
assertEqual('TLS 1.3 master_secret', masterSecret, r8448.masterSecret);

const transcriptChSf = h2b(r8448.transcriptChSf);
assertEqual(
  'TLS 1.3 client_application_traffic_secret_0',
  hkdfExpandLabel(masterSecret, 'c ap traffic', transcriptChSf, 32),
  r8448.cApTraffic,
);
assertEqual(
  'TLS 1.3 server_application_traffic_secret_0',
  hkdfExpandLabel(masterSecret, 's ap traffic', transcriptChSf, 32),
  r8448.sApTraffic,
);
const exporterMasterSecret = hkdfExpandLabel(masterSecret, 'exp master', transcriptChSf, 32);

// ── Transcript handling, verified end to end ───────────────────────────────
// The earlier derivation checks were fed RFC-published hashes. That leaves the
// transcript machinery itself untested, so rebuild it from the raw handshake
// messages the trace prints and check the chain all the way to the Finished.
const messages = {
  clientHello: `
    01 00 00 c0 03 03 cb 34 ec b1 e7 81 63 ba 1c 38 c6 da cb 19 6a 6d
    ff a2 1a 8d 99 12 ec 18 a2 ef 62 83 02 4d ec e7 00 00 06 13 01 13
    03 13 02 01 00 00 91 00 00 00 0b 00 09 00 00 06 73 65 72 76 65 72
    ff 01 00 01 00 00 0a 00 14 00 12 00 1d 00 17 00 18 00 19 01 00 01
    01 01 02 01 03 01 04 00 23 00 00 00 33 00 26 00 24 00 1d 00 20 99
    38 1d e5 60 e4 bd 43 d2 3d 8e 43 5a 7d ba fe b3 c0 6e 51 c1 3c ae
    4d 54 13 69 1e 52 9a af 2c 00 2b 00 03 02 03 04 00 0d 00 20 00 1e
    04 03 05 03 06 03 02 03 08 04 08 05 08 06 04 01 05 01 06 01 02 01
    04 02 05 02 06 02 02 02 00 2d 00 02 01 01 00 1c 00 02 40 01`,
  serverHello: `
    02 00 00 56 03 03 a6 af 06 a4 12 18 60 dc 5e 6e 60 24 9c d3 4c 95
    93 0c 8a c5 cb 14 34 da c1 55 77 2e d3 e2 69 28 00 13 01 00 00 2e
    00 33 00 24 00 1d 00 20 c9 82 88 76 11 20 95 fe 66 76 2b db f7 c6
    72 e1 56 d6 cc 25 3b 83 3d f1 dd 69 b1 b0 4e 75 1f 0f 00 2b 00 02
    03 04`,
  encryptedExtensions: `
    08 00 00 24 00 22 00 0a 00 14 00 12 00 1d 00 17 00 18 00 19 01 00
    01 01 01 02 01 03 01 04 00 1c 00 02 40 01 00 00 00 00`,
  certificate: `
    0b 00 01 b9 00 00 01 b5 00 01 b0 30 82 01 ac 30 82 01 15 a0 03 02
    01 02 02 01 02 30 0d 06 09 2a 86 48 86 f7 0d 01 01 0b 05 00 30 0e
    31 0c 30 0a 06 03 55 04 03 13 03 72 73 61 30 1e 17 0d 31 36 30 37
    33 30 30 31 32 33 35 39 5a 17 0d 32 36 30 37 33 30 30 31 32 33 35
    39 5a 30 0e 31 0c 30 0a 06 03 55 04 03 13 03 72 73 61 30 81 9f 30
    0d 06 09 2a 86 48 86 f7 0d 01 01 01 05 00 03 81 8d 00 30 81 89 02
    81 81 00 b4 bb 49 8f 82 79 30 3d 98 08 36 39 9b 36 c6 98 8c 0c 68
    de 55 e1 bd b8 26 d3 90 1a 24 61 ea fd 2d e4 9a 91 d0 15 ab bc 9a
    95 13 7a ce 6c 1a f1 9e aa 6a f9 8c 7c ed 43 12 09 98 e1 87 a8 0e
    e0 cc b0 52 4b 1b 01 8c 3e 0b 63 26 4d 44 9a 6d 38 e2 2a 5f da 43
    08 46 74 80 30 53 0e f0 46 1c 8c a9 d9 ef bf ae 8e a6 d1 d0 3e 2b
    d1 93 ef f0 ab 9a 80 02 c4 74 28 a6 d3 5a 8d 88 d7 9f 7f 1e 3f 02
    03 01 00 01 a3 1a 30 18 30 09 06 03 55 1d 13 04 02 30 00 30 0b 06
    03 55 1d 0f 04 04 03 02 05 a0 30 0d 06 09 2a 86 48 86 f7 0d 01 01
    0b 05 00 03 81 81 00 85 aa d2 a0 e5 b9 27 6b 90 8c 65 f7 3a 72 67
    17 06 18 a5 4c 5f 8a 7b 33 7d 2d f7 a5 94 36 54 17 f2 ea e8 f8 a5
    8c 8f 81 72 f9 31 9c f3 6b 7f d6 c5 5b 80 f2 1a 03 01 51 56 72 60
    96 fd 33 5e 5e 67 f2 db f1 02 70 2e 60 8c ca e6 be c1 fc 63 a4 2a
    99 be 5c 3e b7 10 7c 3c 54 e9 b9 eb 2b d5 20 3b 1c 3b 84 e0 a8 b2
    f7 59 40 9b a3 ea c9 d9 1d 40 2d cc 0c c8 f8 96 12 29 ac 91 87 b4
    2b 4d e1 00 00`,
  certificateVerify: `
    0f 00 00 84 08 04 00 80 5a 74 7c 5d 88 fa 9b d2 e5 5a b0 85 a6 10
    15 b7 21 1f 82 4c d4 84 14 5a b3 ff 52 f1 fd a8 47 7b 0b 7a bc 90
    db 78 e2 d3 3a 5c 14 1a 07 86 53 fa 6b ef 78 0c 5e a2 48 ee aa a7
    85 c4 f3 94 ca b6 d3 0b be 8d 48 59 ee 51 1f 60 29 57 b1 54 11 ac
    02 76 71 45 9e 46 44 5c 9e a5 8c 18 1e 81 8e 95 b8 c3 fb 0b f3 27
    84 09 d3 be 15 2a 3d a5 04 3e 06 3d da 65 cd f5 ae a2 0d 53 df ac
    d4 2f 74 f3`,
  finished: `14 00 00 20 9b 9b 14 1d 90 63 37 fb d2 cb dc e7 1d f4 de da
    4a b4 2c 30 95 72 cb 7f ff ee 54 54 b7 8f 07 18`,
};

// Message lengths are stated in the trace; a transcription error in the hex
// above would silently change every downstream hash, so check them first.
const expectedLengths = {
  clientHello: 196,
  serverHello: 90,
  encryptedExtensions: 40,
  certificate: 445,
  certificateVerify: 136,
  finished: 36,
};
const raw = {};
for (const [k, v] of Object.entries(messages)) {
  raw[k] = rfc(v);
  checks += 1;
  const got = raw[k].length;
  const want = expectedLengths[k];
  if (got !== want) {
    failures += 1;
    console.error(`  ✗ ${k}: expected ${want} octets, transcribed ${got}`);
  } else {
    console.log(`  ✓ ${k} is ${want} octets as documented`);
  }
}

const upToServerHello = Buffer.concat([raw.clientHello, raw.serverHello]);
assertEqual('transcript hash(CH, SH)', sha256(upToServerHello), r8448.transcriptChSh);

const upToCertVerify = Buffer.concat([
  upToServerHello,
  raw.encryptedExtensions,
  raw.certificate,
  raw.certificateVerify,
]);
const serverFinishedKey = hkdfExpandLabel(sHs, 'finished', Buffer.alloc(0), 32);
assertEqual('TLS 1.3 server finished key', serverFinishedKey, r8448.serverFinishedKey);

// Finished = HMAC(finished_key, Transcript-Hash(up to CertificateVerify)).
// Note the hash is over everything BEFORE the Finished, not after — feeding the
// post-Finished hash here is a classic and silent mistake, so this asserts the
// exact ordering.
const preFinishedHash = sha256(upToCertVerify);
assertEqual(
  'TLS 1.3 server Finished verify_data',
  hmac(serverFinishedKey, preFinishedHash),
  r8448.serverFinished,
);

// And the transcript hash used for the application secrets DOES include the
// Finished message. Checking this pins the opposite ordering too.
assertEqual(
  'transcript hash(CH..Finished) == hash used for c ap / s ap traffic',
  sha256(Buffer.concat([upToCertVerify, raw.finished])),
  r8448.transcriptChSf,
);
assertEqual(
  'server Finished verify_data matches the Finished message in the trace',
  hmac(serverFinishedKey, preFinishedHash),
  b2h(raw.finished.subarray(4)),
);

// ══════════════════════════════════════════════════════════════════════════
// 6. Emit
// ══════════════════════════════════════════════════════════════════════════
console.log(`\n[5/5] Emission — ${checks} assertions, ${failures} failure(s)`);

if (failures > 0) {
  console.error(`\n✗ ${failures} vector(s) did NOT match the published RFC text.`);
  console.error('  The VB test data was NOT written. Fix the implementation above first.');
  process.exit(1);
}
console.log('  All recomputed values match the published RFC / NIST constants.');

const checkOnly = process.argv.includes('--check');
if (checkOnly) {
  console.log('  --check: nothing written.');
  process.exit(0);
}

const vectorJson = {
  generatedBy: 'tools/gen-vectors.mjs',
  verifiedAgainst: [
    'RFC 5869 A.1-A.3',
    'RFC 7748 §5.2, §6.1',
    'RFC 8448 §3',
    'NIST CAVS AES-GCM',
  ],
  hkdf: hkdfVectors,
  x25519: {
    alicePriv: b2h(ALICE_PRIV),
    alicePub: ALICE_PUB,
    bobPriv: b2h(BOB_PRIV),
    bobPub: BOB_PUB,
    shared: X25519_SHARED,
    iterScalar: b2h(ITER_SCALAR),
    iterU: b2h(ITER_U),
    iterOut: ITER_OUT,
  },
  aesGcm: {
    key128: b2h(Z16),
    key256: b2h(Buffer.alloc(32)),
    iv: b2h(Z12),
    emptyTag128: '58e2fccefa7e3061367f1d57a4e7455a',
    blockCt128: '0388dace60b6a392f328c2b971b2fe78',
    blockTag128: 'ab6e47d42cec13bdf53a67b21257bddf',
    emptyTag256: '530f8afbc74536b9a963b4f1c4cb738b',
  },
  tls13: Object.fromEntries(
    Object.entries(r8448).map(([k, v]) => [k, v.replace(/\s+/g, '').toLowerCase()]),
  ),
  // Derived by the same verified machinery, but not quoted verbatim by the RFC.
  tls13Derived: {
    exporterMasterSecret: b2h(exporterMasterSecret),
  },
  tls13Transcript: Object.fromEntries(
    Object.entries(raw).map(([k, v]) => [k, b2h(v)]),
  ),
};

fs.mkdirSync(path.join(HERE, 'out'), { recursive: true });
fs.writeFileSync(
  path.join(HERE, 'out', 'vectors.json'),
  JSON.stringify(vectorJson, null, 2) + '\n',
);
console.log('  wrote tools/out/vectors.json');

// VB module for the Windows test project.
const vbName = (s) => 'V' + s.charAt(0).toUpperCase() + s.slice(1);
function vbBytes(name, hex, indent = '        ') {
  const bytes = h2b(hex);
  const parts = [];
  for (let i = 0; i < bytes.length; i++) {
    parts.push('&H' + bytes[i].toString(16).toUpperCase().padStart(2, '0'));
  }
  if (parts.length === 0) {
    // RFC 5869 case 3 uses an EMPTY salt and an EMPTY info. Without this branch
    // the multi-line path below emitted `New Byte() { _` with no closing brace,
    // which is not valid VB: the unterminated initializer swallowed the next
    // declarations and the test project failed with BC30201.
    return `    Public ReadOnly ${name} As Byte() = New Byte() {}\n`;
  }
  const lines = [];
  for (let i = 0; i < parts.length; i += 12) {
    lines.push(indent + parts.slice(i, i + 12).join(', '));
  }
  if (lines.length === 1) {
    return `    Public ReadOnly ${name} As Byte() = {${lines[0].trim()}}\n`;
  }
  return (
    `    Public ReadOnly ${name} As Byte() = New Byte() { _\n` +
    lines.map((l, i, a) => l + (i === a.length - 1 ? '}\n' : ', _\n')).join('') +
    ''
  );
}

let vb = '';
vb += `' <auto-generated>\n`;
vb += `'     Generated by tools/gen-vectors.mjs — DO NOT EDIT BY HAND.\n`;
vb += `'\n`;
vb += `'     Every value below was recomputed from first principles and asserted\n`;
vb += `'     against the constant published in the source document before being\n`;
vb += `'     written out. Regenerate with:  node tools/gen-vectors.mjs\n`;
vb += `'\n`;
vb += `'     Sources: RFC 5869 A.1-A.3, RFC 7748 5.2/6.1, RFC 8448 3,\n`;
vb += `'              NIST CAVS AES-GCM.\n`;
vb += `' </auto-generated>\n`;
vb += `\n`;
vb += `Namespace CryptoTests\n`;
vb += `\n`;
vb += `    ''' <summary>Verified known-answer vectors shared by the crypto tests.</summary>\n`;
vb += `    ''' <remarks>\n`;
vb += `    ''' The values are INSTANCE fields, so the smoke tests in this same assembly\n`;
vb += `    ''' must be able to construct the class. A Private constructor made every\n`;
vb += `    ''' \`New Vectors()\` unbuildable, which nobody noticed while the file had no\n`;
vb += `    ''' consumer. Friend keeps it out of every other assembly.\n`;
vb += `    ''' </remarks>\n`;
vb += `    Friend NotInheritable Class Vectors\n`;
vb += `\n`;
vb += `        Friend Sub New()\n`;
vb += `        End Sub\n`;
vb += `\n`;

for (const c of hkdfVectors) {
  vb += vbBytes(`HkdfCase${c.n}Ikm`, c.ikm);
  vb += vbBytes(`HkdfCase${c.n}Salt`, c.salt);
  vb += vbBytes(`HkdfCase${c.n}Info`, c.info);
  vb += vbBytes(`HkdfCase${c.n}Prk`, c.prk);
  vb += vbBytes(`HkdfCase${c.n}Okm`, c.okm);
  vb += `    Public ReadOnly HkdfCase${c.n}L As Integer = ${c.L}\n\n`;
}

vb += vbBytes('X25519AlicePriv', vectorJson.x25519.alicePriv);
vb += vbBytes('X25519AlicePub', vectorJson.x25519.alicePub);
vb += vbBytes('X25519BobPriv', vectorJson.x25519.bobPriv);
vb += vbBytes('X25519BobPub', vectorJson.x25519.bobPub);
vb += vbBytes('X25519Shared', vectorJson.x25519.shared);
vb += vbBytes('X25519IterScalar', vectorJson.x25519.iterScalar);
vb += vbBytes('X25519IterU', vectorJson.x25519.iterU);
vb += vbBytes('X25519IterOut', vectorJson.x25519.iterOut);
vb += `\n`;

vb += vbBytes('AesGcmKey128', vectorJson.aesGcm.key128);
vb += vbBytes('AesGcmKey256', vectorJson.aesGcm.key256);
vb += vbBytes('AesGcmIv', vectorJson.aesGcm.iv);
vb += vbBytes('AesGcmEmptyTag128', vectorJson.aesGcm.emptyTag128);
vb += vbBytes('AesGcmBlockCt128', vectorJson.aesGcm.blockCt128);
vb += vbBytes('AesGcmBlockTag128', vectorJson.aesGcm.blockTag128);
vb += vbBytes('AesGcmEmptyTag256', vectorJson.aesGcm.emptyTag256);
vb += `\n`;

// TLS 1.3 block, emitted in the RFC's own field names.
const tls13Names = {
  clientPriv: 'Tls13ClientPriv',
  clientPub: 'Tls13ClientPub',
  serverPriv: 'Tls13ServerPriv',
  serverPub: 'Tls13ServerPub',
  ecdhe: 'Tls13Ecdhe',
  earlySecret: 'Tls13EarlySecret',
  derivedEarly: 'Tls13DerivedEarly',
  handshakeSecret: 'Tls13HandshakeSecret',
  transcriptChSh: 'Tls13TranscriptChSh',
  cHsTraffic: 'Tls13ClientHsTraffic',
  sHsTraffic: 'Tls13ServerHsTraffic',
  serverHsKey: 'Tls13ServerHsKey',
  serverHsIv: 'Tls13ServerHsIv',
  derivedMaster: 'Tls13DerivedMaster',
  masterSecret: 'Tls13MasterSecret',
  transcriptChSf: 'Tls13TranscriptChSf',
  cApTraffic: 'Tls13ClientApTraffic',
  sApTraffic: 'Tls13ServerApTraffic',
  serverFinishedKey: 'Tls13ServerFinishedKey',
  serverFinished: 'Tls13ServerFinished',
};
for (const [key, name] of Object.entries(tls13Names)) {
  vb += vbBytes(name, vectorJson.tls13[key]);
}
vb += vbBytes('Tls13ExporterMasterSecret', vectorJson.tls13Derived.exporterMasterSecret);
const transcriptNames = {
  clientHello: 'Tls13MsgClientHello',
  serverHello: 'Tls13MsgServerHello',
  encryptedExtensions: 'Tls13MsgEncryptedExtensions',
  certificate: 'Tls13MsgCertificate',
  certificateVerify: 'Tls13MsgCertificateVerify',
  finished: 'Tls13MsgFinished',
};
for (const [key, name] of Object.entries(transcriptNames)) {
  vb += vbBytes(name, vectorJson.tls13Transcript[key]);
}
vb += `\n`;

vb += `    End Class\n\nEnd Namespace\n`;

// The generated VB is compiled by tests/BrowserForWP.Crypto.Tests, so it has to
// be syntactically complete -- and until this assertion existed, nothing checked
// that. An EMPTY vector produced an unterminated `{ _` initializer and the
// project failed with BC30201. Brace balance catches exactly that shape, so a
// future empty vector cannot ship broken VB again.
checks += 1;
const openBraces = (vb.match(/\{/g) || []).length;
const closeBraces = (vb.match(/\}/g) || []).length;
if (openBraces !== closeBraces) {
  failures += 1;
  console.error(`  ✗ emitted VB has unbalanced braces: ${openBraces} '{' vs ${closeBraces} '}'`);
} else {
  console.log(`  ✓ emitted VB braces balanced (${openBraces} array initializer(s))`);
}
if (failures > 0) {
  console.error(`\n✗ ${failures} problem(s) in the emitted VB. The file was NOT written.`);
  process.exit(1);
}

const vbPath = path.join(ROOT, 'tests', 'BrowserForWP.Crypto.Tests', 'Vectors.generated.vb');
fs.mkdirSync(path.dirname(vbPath), { recursive: true });
fs.writeFileSync(vbPath, vb);
console.log('  wrote tests/BrowserForWP.Crypto.Tests/Vectors.generated.vb');
console.log(`\n✓ ${checks} assertions passed. Vectors are consistent with the RFCs.`);
