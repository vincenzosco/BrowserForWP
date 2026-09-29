#!/usr/bin/env node
// Mirror of BrowserForWP.Net/Tls13/PinStore.vb NormalizeHost + Verify.
// Must print: pinstore checks, 0 failure(s).
let failures = 0;
function check(name, got, want) {
  if (got === want) console.log(`  ✓ ${name}`);
  else { console.log(`  ✗ ${name}: got ${JSON.stringify(got)} want ${JSON.stringify(want)}`); failures++; }
}
function NormalizeHost(h) {
  if (!h) return "";
  let c = h.trim().toLowerCase();
  const ci = c.indexOf(":");
  if (ci >= 0) c = c.slice(0, ci);
  while (c.endsWith(".")) c = c.slice(0, -1);
  return c;
}
check("lower", NormalizeHost("Example.COM"), "example.com");
check("port stripped", NormalizeHost("example.com:443"), "example.com");
check("trailing dot", NormalizeHost("example.com."), "example.com");
check("empty", NormalizeHost(""), "");
const pins = new Map();
pins.set(NormalizeHost("Example.com"), "abc123");
function Verify(host, presented) {
  const k = NormalizeHost(host);
  if (!pins.has(k)) return true;
  if (!presented) return false;
  return pins.get(k) === presented;
}
check("no pin passes", Verify("other.com", null), true);
check("match passes", Verify("example.com", "abc123"), true);
check("mismatch fails", Verify("example.com", "zzz"), false);
if (failures) process.exit(1);
console.log("pinstore checks, 0 failure(s)");
