# Run modern sites — Plan

**Goal:** Raise the share of modern pages that render usably in Trident: bigger ES5 shim set, injection at `DOMContentLoaded` (not only after load), automatic reading-mode fallback for badly-missing pages, and lite-version redirects — all inside the 40KB shim budget, with Proxy/Intl/ES6-syntax impossibilities stated, not attempted.

**Global Constraints (verbatim from the request and the platform):**
- Target: `TargetPlatformVersion 8.1`, `WindowsPhoneApp`, VB 12 / VS2013. Windows Phone 8.1 cannot host Chromium or Firefox, cannot replace Trident, and cannot exceed TLS 1.2 through the OS.
- No backend. Every byte of logic runs on-device. No `127.0.0.1` proxy feeding the WebView.
- API surface is ".NET for Windows Store apps": no `SHA256`/`HMACSHA256`/`RNGCryptoServiceProvider`, no `Encoding.ASCII`, no `RegexOptions.Compiled`, `VerifySignature` never `Verify`.
- VB 12: no implicit line continuation after `.`; use a `With` block. VB is case-insensitive: no local shadows a type, member, keyword, or `Page.Tag`. (`Dim parsedUri As Uri` is the safe pattern; never `Dim uri As Uri` next to the `Uri` type.)
- Never hand-edit `X25519.vb` or `BrowserForWP.Net/Tls13/` (untouched this round).
- `en-US` is the default and fallback language: new keys `ReaderFallback`, `LiteRedirects` go in both files.
- Layer discipline: shim text is data (Core string constants stay in `compat.js` itself); page-script execution stays in `TridentEngine`; navigation policy in the app.
- Honesty floor: `Proxy`, `Intl`, CSS grid, and ES6 *syntax* (arrows, classes, async/await, modules) cannot be polyfilled — a shim cannot parse syntax the engine chokes on. The probe keeps reporting those truthfully.

## File Structure

| File | Why |
|---|---|
| `tools/proto/modern-sites.mjs` NEW | Failing-first: shim markers + ≤40KB size, redirect-rule mirror, delivery guards. FAIL pre, PASS post. |
| `BrowserForWP.Polyfill/compat.js` | +Map/Set/WeakMap/Symbol-stub, DOM helpers, fetch hardening, eager observer stubs. ES5 only. |
| `BrowserForWP.Core/Browser/LiteRedirects.vb` NEW | Pure host→lite-URL rules (wikipedia m-subdomain, mbasic facebook, old reddit). |
| `BrowserForWP.Core/Storage/AppSettings.vb` | `LiteRedirects As Boolean` (default True) + save/load key. |
| `BrowserForWP/MainPage.xaml` | `LiteRedirectsToggle` in Settings (Checked/Unchecked). No overlay changes. |
| `BrowserForWP/MainPage.xaml.vb` | `DOMContentLoaded` hook + handler, silent probe → auto-reader at ≥8 missing, lite redirect in both navigation paths, toggle handlers, 2 new labels. |
| `BrowserForWP/Strings/*/Resources.resw` | `ReaderFallback`, `LiteRedirects` in both languages. |
| `README.md`, `README.it.md` | Correct the stale "injection step is not written yet" line (injection shipped last round; this round moves it earlier). |

## Task 1 — failing check first (commit: test)

`node tools/proto/modern-sites.mjs` → FAIL pre-fix: `compat.js` lacks `Map`/`Symbol`/`closest`/`CustomEvent`/`arrayBuffer`/`IntersectionObserver`; `MainPage.xaml.vb` lacks `DOMContentLoaded`; `LiteRedirects.vb` absent; resw lacks the 2 keys; redirect mirror cases unimplemented. Mirror cases: `en.wikipedia.org/wiki/X` → `en.m.wikipedia.org/wiki/X` (path kept); `www.facebook.com/a?b=c` → `mbasic.facebook.com/a?b=c`; `reddit.com/r/x` → `old.reddit.com/r/x`; `example.com` → no redirect; `en.m.wikipedia.org` → no redirect (no loop).

## Task 2 — shim expansions (commit: feat polyfill)

Append five guarded ES5 sections to `compat.js` before the timing hook (exact code below abridged to the load-bearing parts; full text goes in the commit):

```js
// Map/Set over parallel arrays (SameValueZero: NaN equals NaN).
if (!globalScope.Map) {
    var MapShim = function () { this._k = []; this._v = []; };
    function sameVal(a, b) { return a === b || (a !== a && b !== b); }
    function idxOf(shim, key) {
        for (var i = 0; i < shim._k.length; i++) {
            if (sameVal(shim._k[i], key)) { return i; }
        }
        return -1;
    }
    MapShim.prototype.set = function (k, v) {
        var at = idxOf(this, k);
        if (at === -1) { this._k.push(k); this._v.push(v); }
        else { this._v[at] = v; }
        return this;
    };
    MapShim.prototype.get = function (k) { var at = idxOf(this, k); return at === -1 ? undefined : this._v[at]; };
    MapShim.prototype.has = function (k) { return idxOf(this, k) !== -1; };
    MapShim.prototype['delete'] = function (k) {
        var at = idxOf(this, k);
        if (at === -1) { return false; }
        this._k.splice(at, 1); this._v.splice(at, 1); return true;
    };
    MapShim.prototype.clear = function () { this._k = []; this._v = []; };
    // size as a getter would need defineProperty-on-prototype support checks;
    // expose a method too so both shapes work:
    MapShim.prototype.size = function () { return this._k.length; };
    MapShim.prototype.forEach = function (fn, self) {
        for (var i = 0; i < this._k.length; i++) { fn.call(self, this._v[i], this._k[i], this); }
    };
    globalScope.Map = MapShim;
}
// Set mirrors Map with value-only entries (same helpers, _v unused).
// WeakMap: array-pair store; documented as ref-holding for page lifetime.
// Symbol stub: unique-string factory + iterator/toStringTag/hasInstance keys.
if (!globalScope.Symbol) {
    var symCtr = 0;
    var SymbolShim = function (desc) { symCtr += 1; return '@@symbol:' + (desc || '') + '#' + symCtr; };
    SymbolShim.iterator = '@@symbol:iterator#0';
    SymbolShim.toStringTag = '@@symbol:toStringTag#0';
    SymbolShim.hasInstance = '@@symbol:hasInstance#0';
    globalScope.Symbol = SymbolShim;
}
// DOM: matches via msMatchesSelector, closest loop, remove, CustomEvent,
// NodeList.forEach — each installed only when absent.
// fetch: add blob()/arrayBuffer() (Blob guarded) and headers.forEach().
// Observers: IntersectionObserver/ResizeObserver stubs that fire EAGERLY
// (isIntersecting:true on next tick) so lazy-load libraries load everything
// instead of never loading anything. Documented as eager, not spec-true.
```

Verify: `node tools/check-polyfill.mjs` → `is valid ES5`; `wc -c` ≤ 40960; `node tools/proto/modern-sites.mjs` shim section passes.

## Task 3 — delivery: earlier injection, auto-reader, lite redirects (commit: feat shell)

LiteRedirects.vb (full file, `Namespace Browser`):

```vb
Public NotInheritable Class LiteRedirects

    Private Sub New()
    End Sub

    ''' <summary>Lite URL for a page URL, or Nothing to leave it alone.</summary>
    Public Shared Function RedirectUrl(pageUrl As String) As String
        If String.IsNullOrEmpty(pageUrl) Then
            Return Nothing
        End If
        Dim parsedUri As Uri = Nothing
        If Not Uri.TryCreate(pageUrl, UriKind.Absolute, parsedUri) Then
            Return Nothing
        End If
        Dim hostKey As String = parsedUri.Host.Trim().ToLowerInvariant()
        Dim pathAndQuery As String = parsedUri.PathAndQuery
        Dim hostLabels As String() = hostKey.Split("."c)
        Dim alreadyMobile As Boolean = False
        For Each hostLabel In hostLabels
            If hostLabel = "m" Then
                alreadyMobile = True
                Exit For
            End If
        Next
        If hostKey.EndsWith(".wikipedia.org") AndAlso Not alreadyMobile Then
            Return "https://" & hostKey.Insert(hostKey.IndexOf("."c), ".m") & pathAndQuery
        End If
        If hostKey = "facebook.com" OrElse hostKey = "www.facebook.com" OrElse hostKey = "m.facebook.com" Then
            Return "https://mbasic.facebook.com" & pathAndQuery
        End If
        If hostKey = "reddit.com" OrElse hostKey = "www.reddit.com" Then
            Return "https://old.reddit.com" & pathAndQuery
        End If
        Return Nothing
    End Function
End Class
```

(`"en.wikipedia.org".Insert(2, ".m")` → `en.m.wikipedia.org`; path kept; already-m hosts untouched so no loop.)

MainPage.xaml.vb: hook `tridentView.View.DOMContentLoaded` in `OnNavigatedTo` (unhook in `OnNavigatedFrom`); handler runs `InjectPolyfillAsync` + night apply (both idempotent); `OnNavigationCompleted` keeps its injection call (covers SPA late loads), then runs the silent `CompatibilityProbe` and at `MissingFeatures.Count >= 8` tries `EnterReadingModeAsync`, showing `ReaderFallback` on success; `NavigateFromAddressBar` + `OnNavigationStarting` apply `LiteRedirects.RedirectUrl` first (when the setting is on), then the tracker check; `LiteRedirectsToggle` pair + label/state in `ApplyLocalizedStrings`.

resw: `ReaderFallback` ("Showing simplified article" / "Articolo semplificato"), `LiteRedirects` ("Prefer light site versions" / "Preferisci versioni leggere").

README en+it: replace the "injection step is not written yet" sentence with: injection runs at `DOMContentLoaded` and again on completion; the bundle raises the floor but cannot parse ES6 syntax or supply `Proxy`/`Intl`/grid.

Verify: `modern-sites.mjs` → `0 failure(s)`; `check-vb.mjs` → `0 finding(s)`; guest build + handset steps (DOMContentLoaded fires, reader fallback triggers on a heavy page, facebook/reddit/wiki redirect) — guest/handset unavailable here, report honestly.

## Commits + push

1. `test(modern-sites): add failing shim, delivery and redirect checks`
2. `feat(polyfill): add collections, DOM, fetch and observer shims`
3. `feat(shell): earlier injection, auto-reader fallback, lite redirects`
Then `git push origin HEAD` + `ls-remote` match check.
