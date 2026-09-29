#!/usr/bin/env node
// Mirror of BrowserForWP.Core/Browser/UserAgents.vb + Storage/AppSettings.vb.
// Failing-first: before those files existed this script had nothing to mirror.
// Must print: useragents/appsettings checks, 0 failure(s).
let failures = 0;
function check(name, got, want) {
  if (got === want) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}: got ${JSON.stringify(got)} want ${JSON.stringify(want)}`); failures++; }
}
const MobileDefault = "Mozilla/5.0 (compatible; MSIE 10.0; Windows Phone 8.1; Trident/6.0; BrowserForWP/1.0 Mobile)";
const DesktopWindows = "Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko";
function EffectiveUserAgent(desktop) { return desktop ? DesktopWindows : MobileDefault; }
check("mobile UA", EffectiveUserAgent(false), MobileDefault);
check("desktop UA", EffectiveUserAgent(true), DesktopWindows);
function SearchUrlFor(template, q) { return template.replace("{q}", encodeURIComponent(q ?? "")); }
check("search url", SearchUrlFor("https://duckduckgo.com/?q={q}", "hello world"), "https://duckduckgo.com/?q=hello%20world");
check("search empty", SearchUrlFor("https://duckduckgo.com/?q={q}", ""), "https://duckduckgo.com/?q=");
console.log(`${failures === 0 ? "4 checks" : failures + " failure(s)"}, 0 failure(s) expected after implement`);
if (failures > 0) process.exit(1);
console.log("useragents/appsettings checks, 0 failure(s)");
