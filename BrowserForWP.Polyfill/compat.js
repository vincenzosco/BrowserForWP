/*
 * BrowserForWP — on-device compatibility layer.
 *
 * Injected into every document before author scripts run, because Windows Phone
 * 8.1's engine is Trident (IE11): no ES6+, no Promise, no fetch, no Intl.
 *
 * SCOPE, STATED HONESTLY
 * ---------------------
 * This raises the FLOOR. It makes code that *feature-detects* correctly work,
 * and it supplies the missing small APIs that break otherwise-fine sites.
 *
 * It does NOT transpile anyone's ES6 source, and it cannot fix CSS layout modes
 * Trident never implemented (no CSS grid). A site that ships untranspiled ES6
 * will still fail. The compatibility probe in BrowserForWP.Core/Diagnostics
 * exists so that failure is *reported* rather than mysterious.
 *
 * CONSTRAINTS
 * -----------
 *   - ES5 only. No const/let, no arrow functions, no classes, no template
 *     literals. `node -e "new Function(src)"` plus the ES6 grep in
 *     docs/MAINTAINING.md guard this.
 *   - Nothing is installed if the page already provides it, so native (and
 *     therefore faster) implementations always win.
 *   - Every define is guarded, so a hostile page cannot have these throw during
 *     injection.
 */
(function (globalScope) {
    'use strict';

    // globalScope arrives as an argument (window) because `this` inside a
    // strict IIFE is undefined — `var globalScope = this` silently broke every
    // section below the first globalScope dereference (no Promise, no fetch,
    // no timing hook). Caught by executing the bundle, not by reading it.
    if (!globalScope) { return; }

    /**
     * Install `value` as `name` on `target` only when it is missing.
     * Falls back to plain assignment when defineProperty is unavailable or the
     * property is non-configurable.
     */
    function define(target, name, value) {
        if (!target || target[name]) { return; }
        try {
            Object.defineProperty(target, name, {
                value: value,
                writable: true,
                configurable: true,
                enumerable: false
            });
        } catch (e) {
            try { target[name] = value; } catch (e2) { /* frozen: nothing to do */ }
        }
    }

    // ── Object ────────────────────────────────────────────────────────────
    define(Object, 'assign', function (target) {
        if (target === null || target === undefined) {
            throw new TypeError('Cannot convert undefined or null to object');
        }
        var out = Object(target);
        for (var i = 1; i < arguments.length; i++) {
            var source = arguments[i];
            if (!source) { continue; }
            for (var key in source) {
                if (Object.prototype.hasOwnProperty.call(source, key)) { out[key] = source[key]; }
            }
        }
        return out;
    });

    define(Object, 'values', function (obj) {
        var out = [];
        if (obj === null || obj === undefined) { return out; }
        for (var key in obj) {
            if (Object.prototype.hasOwnProperty.call(obj, key)) { out.push(obj[key]); }
        }
        return out;
    });

    define(Object, 'entries', function (obj) {
        var out = [];
        if (obj === null || obj === undefined) { return out; }
        for (var key in obj) {
            if (Object.prototype.hasOwnProperty.call(obj, key)) { out.push([key, obj[key]]); }
        }
        return out;
    });

    define(Object, 'is', function (a, b) {
        if (a === b) { return a !== 0 || 1 / a === 1 / b; }
        return a !== a && b !== b;
    });

    // ── Array ─────────────────────────────────────────────────────────────
    define(Array, 'from', function (arrayLike, mapFn, thisArg) {
        var out = [];
        if (arrayLike === null || arrayLike === undefined) { return out; }
        if (typeof arrayLike.length !== 'number') {
            // Iterables other than array-likes are not supported on this engine.
            return out;
        }
        for (var i = 0; i < arrayLike.length; i++) {
            out.push(mapFn ? mapFn.call(thisArg, arrayLike[i], i) : arrayLike[i]);
        }
        return out;
    });

    define(Array, 'of', function () { return Array.prototype.slice.call(arguments); });

    define(Array, 'isArray', function (value) {
        return Object.prototype.toString.call(value) === '[object Array]';
    });

    define(Array.prototype, 'find', function (predicate, thisArg) {
        for (var i = 0; i < this.length; i++) {
            if (predicate.call(thisArg, this[i], i, this)) { return this[i]; }
        }
        return undefined;
    });

    define(Array.prototype, 'findIndex', function (predicate, thisArg) {
        for (var i = 0; i < this.length; i++) {
            if (predicate.call(thisArg, this[i], i, this)) { return i; }
        }
        return -1;
    });

    define(Array.prototype, 'includes', function (value, fromIndex) {
        return this.indexOf(value, fromIndex || 0) !== -1;
    });

    define(Array.prototype, 'fill', function (value, start, end) {
        var len = this.length;
        var begin = start === undefined ? 0 : (start < 0 ? Math.max(len + start, 0) : Math.min(start, len));
        var stop = end === undefined ? len : (end < 0 ? Math.max(len + end, 0) : Math.min(end, len));
        for (var i = begin; i < stop; i++) { this[i] = value; }
        return this;
    });

    // ── String ────────────────────────────────────────────────────────────
    define(String.prototype, 'startsWith', function (search, position) {
        var start = position || 0;
        return this.substr(start, String(search).length) === String(search);
    });

    define(String.prototype, 'endsWith', function (search, length) {
        var target = String(search);
        var end = (length === undefined || length > this.length) ? this.length : length;
        return this.substring(end - target.length, end) === target;
    });

    define(String.prototype, 'includes', function (search, position) {
        return this.indexOf(String(search), position || 0) !== -1;
    });

    define(String.prototype, 'repeat', function (count) {
        var n = Number(count) || 0;
        if (n < 0 || n === Infinity) { throw new RangeError('invalid count value'); }
        var pattern = String(this);
        var out = '';
        for (var i = 0; i < n; i++) { out += pattern; }
        return out;
    });

    define(String.prototype, 'padStart', function (targetLength, padString) {
        var pad = padString === undefined ? ' ' : String(padString);
        var out = String(this);
        if (out.length >= targetLength || pad.length === 0) { return out; }
        var needed = targetLength - out.length;
        while (pad.length < needed) { pad += pad; }
        return pad.slice(0, needed) + out;
    });

    define(String.prototype, 'trimEnd', function () { return String(this).replace(/\s+$/, ''); });
    define(String.prototype, 'trimStart', function () { return String(this).replace(/^\s+/, ''); });

    // ── Number / Math ─────────────────────────────────────────────────────
    define(Number, 'isNaN', function (value) { return typeof value === 'number' && value !== value; });
    define(Number, 'isFinite', function (value) { return typeof value === 'number' && isFinite(value); });
    define(Number, 'isInteger', function (value) { return typeof value === 'number' && isFinite(value) && Math.floor(value) === value; });
    define(Number, 'parseFloat', function (value) { return parseFloat(value); });
    define(Number, 'parseInt', function (value, radix) { return parseInt(value, radix); });
    define(Number, 'MAX_SAFE_INTEGER', 9007199254740991);

    define(Math, 'sign', function (value) {
        var n = Number(value);
        if (n !== n) { return n; }
        return n === 0 ? n : (n > 0 ? 1 : -1);
    });
    define(Math, 'trunc', function (value) { return value < 0 ? Math.ceil(value) : Math.floor(value); });
    define(Math, 'log2', function (value) { return Math.log(value) / Math.LN2; });
    define(Math, 'log10', function (value) { return Math.log(value) / Math.LN10; });

    // ── Element / DOM shims ───────────────────────────────────────────────
    // classList is present on IE11 for elements, but missing on SVG and some
    // document fragments, which is where sites tend to trip.
    if (globalScope.Element && !globalScope.Element.prototype.classList) {
        globalScope.Element.prototype.classList = {
            add: function (el) {
                return function () {
                    var list = (el.className || '').split(/\s+/);
                    for (var i = 0; i < arguments.length; i++) {
                        if (list.indexOf(arguments[i]) === -1) { list.push(arguments[i]); }
                    }
                    el.className = list.join(' ').replace(/^\s+|\s+$/g, '');
                };
            }(this),
            remove: function (el) {
                return function () {
                    var list = (el.className || '').split(/\s+/);
                    for (var i = 0; i < arguments.length; i++) {
                        var at = list.indexOf(arguments[i]);
                        if (at !== -1) { list.splice(at, 1); }
                    }
                    el.className = list.join(' ');
                };
            }(this),
            contains: function (el) {
                return function (name) {
                    return (' ' + el.className + ' ').indexOf(' ' + name + ' ') !== -1;
                };
            }(this)
        };
    }

    define(globalScope, 'requestAnimationFrame', function (callback) {
        return globalScope.setTimeout(function () { callback(Date.now()); }, 16);
    });
    define(globalScope, 'cancelAnimationFrame', function (handle) {
        globalScope.clearTimeout(handle);
    });

    // ── Promise ───────────────────────────────────────────────────────────
    // Only supplied when absent, and deliberately minimal: it is enough for
    // feature-detection paths and simple chains. It is not a full A+ impl.
    if (!globalScope.Promise) {
        var PENDING = 0, FULFILLED = 1, REJECTED = 2;

        var PromiseShim = function (executor) {
            var self = this;
            self._state = PENDING;
            self._value = undefined;
            self._handlers = [];

            function settle(state, value) {
                if (self._state !== PENDING) { return; }
                self._state = state;
                self._value = value;
                var queued = self._handlers;
                self._handlers = [];
                for (var i = 0; i < queued.length; i++) {
                    (function (run) { setTimeout(run, 0); }(queued[i]));
                }
            }

            function resolve(value) {
                if (value && typeof value.then === 'function') {
                    value.then(resolve, reject);
                    return;
                }
                settle(FULFILLED, value);
            }

            function reject(reason) { settle(REJECTED, reason); }

            try {
                executor(resolve, reject);
            } catch (e) {
                reject(e);
            }
        };

        PromiseShim.prototype.then = function (onFulfilled, onRejected) {
            var self = this;
            return new PromiseShim(function (resolve, reject) {
                function run() {
                    var handler = self._state === FULFILLED ? onFulfilled : onRejected;
                    if (typeof handler !== 'function') {
                        (self._state === FULFILLED ? resolve : reject)(self._value);
                        return;
                    }
                    try {
                        resolve(handler(self._value));
                    } catch (e) {
                        reject(e);
                    }
                }
                if (self._state === PENDING) { self._handlers.push(run); } else { setTimeout(run, 0); }
            });
        };

        PromiseShim.prototype['catch'] = function (onRejected) { return this.then(undefined, onRejected); };

        PromiseShim.resolve = function (value) { return new PromiseShim(function (r) { r(value); }); };
        PromiseShim.reject = function (reason) { return new PromiseShim(function (_, r) { r(reason); }); };
        PromiseShim.all = function (items) {
            return new PromiseShim(function (resolve, reject) {
                var results = [], remaining = items.length;
                if (remaining === 0) { resolve(results); return; }
                for (var i = 0; i < items.length; i++) {
                    (function (index) {
                        PromiseShim.resolve(items[index]).then(function (value) {
                            results[index] = value;
                            remaining -= 1;
                            if (remaining === 0) { resolve(results); }
                        }, reject);
                    }(i));
                }
            });
        };
        PromiseShim.race = function (items) {
            return new PromiseShim(function (resolve, reject) {
                for (var i = 0; i < items.length; i++) { PromiseShim.resolve(items[i]).then(resolve, reject); }
            });
        };

        globalScope.Promise = PromiseShim;
    }

    // ── fetch ─────────────────────────────────────────────────────────────
    // Implemented over XMLHttpRequest. Note this is a *shim for pages that
    // feature-detect*: it is same-origin by default, exactly like XHR.
    if (!globalScope.fetch && globalScope.XMLHttpRequest && globalScope.Promise) {
        globalScope.fetch = function (input, options) {
            options = options || {};
            var url = typeof input === 'string' ? input : (input && input.url);

            return new globalScope.Promise(function (resolve, reject) {
                var xhr = new globalScope.XMLHttpRequest();
                xhr.open(options.method || 'GET', url, true);
                if (options.credentials === 'include') { xhr.withCredentials = true; }

                var headers = options.headers || {};
                for (var name in headers) {
                    if (Object.prototype.hasOwnProperty.call(headers, name)) {
                        try { xhr.setRequestHeader(name, headers[name]); } catch (e) { /* forbidden header */ }
                    }
                }

                xhr.onload = function () {
                    var body = xhr.responseText;
                    var response = {
                        ok: xhr.status >= 200 && xhr.status < 300,
                        status: xhr.status,
                        statusText: xhr.statusText,
                        url: url,
                        headers: {
                            get: function (key) { return xhr.getResponseHeader(key); },
                            has: function (key) { return xhr.getResponseHeader(key) !== null; }
                        },
                        text: function () { return globalScope.Promise.resolve(body); }
                    };
                    response.json = function () {
                        return new globalScope.Promise(function (res, rej) {
                            try { res(JSON.parse(body)); } catch (e) { rej(e); }
                        });
                    };
                    resolve(response);
                };
                xhr.onerror = function () { reject(new TypeError('Network request failed')); };
                xhr.ontimeout = function () { reject(new TypeError('Network request timed out')); };
                if (options.timeout) { xhr.timeout = options.timeout; }

                xhr.send(options.body === undefined ? null : options.body);
            });
        };
    }

    // ── URL / URLSearchParams ─────────────────────────────────────────────
    if (!globalScope.URLSearchParams) {
        var URLSearchParamsShim = function (init) {
            this._pairs = [];
            if (typeof init === 'string') {
                var query = init.charAt(0) === '?' ? init.substring(1) : init;
                if (query) {
                    var parts = query.split('&');
                    for (var i = 0; i < parts.length; i++) {
                        if (!parts[i]) { continue; }
                        var eq = parts[i].indexOf('=');
                        var key = eq === -1 ? parts[i] : parts[i].substring(0, eq);
                        var value = eq === -1 ? '' : parts[i].substring(eq + 1);
                        this._pairs.push([decodeURIComponent(key.replace(/\+/g, ' ')), decodeURIComponent(value.replace(/\+/g, ' '))]);
                    }
                }
            }
        };

        URLSearchParamsShim.prototype.append = function (key, value) { this._pairs.push([String(key), String(value)]); };
        URLSearchParamsShim.prototype.set = function (key, value) {
            this['delete'](key);
            this.append(key, value);
        };
        URLSearchParamsShim.prototype.get = function (key) {
            for (var i = 0; i < this._pairs.length; i++) {
                if (this._pairs[i][0] === key) { return this._pairs[i][1]; }
            }
            return null;
        };
        URLSearchParamsShim.prototype.getAll = function (key) {
            var out = [];
            for (var i = 0; i < this._pairs.length; i++) {
                if (this._pairs[i][0] === key) { out.push(this._pairs[i][1]); }
            }
            return out;
        };
        URLSearchParamsShim.prototype.has = function (key) { return this.get(key) !== null; };
        URLSearchParamsShim.prototype['delete'] = function (key) {
            var kept = [];
            for (var i = 0; i < this._pairs.length; i++) {
                if (this._pairs[i][0] !== key) { kept.push(this._pairs[i]); }
            }
            this._pairs = kept;
        };
        URLSearchParamsShim.prototype.toString = function () {
            var out = [];
            for (var i = 0; i < this._pairs.length; i++) {
                out.push(encodeURIComponent(this._pairs[i][0]) + '=' + encodeURIComponent(this._pairs[i][1]));
            }
            return out.join('&');
        };

        globalScope.URLSearchParams = URLSearchParamsShim;
    }

    // ── Map / Set / WeakMap / Symbol ──────────────────────────────────
    // Minimal SameValueZero collections for feature-detecting pages. Iteration
    // order is insertion order. WeakMap holds references for page lifetime:
    // true weak semantics need engine support Trident does not have.
    function sameVal(a, b) {
        return a === b || (a !== a && b !== b);
    }
    function keyIndex(keys, key) {
        for (var i = 0; i < keys.length; i++) {
            if (sameVal(keys[i], key)) { return i; }
        }
        return -1;
    }

    if (!globalScope.Map) {
        var MapShim = function () { this._k = []; this._v = []; };
        MapShim.prototype.set = function (k, v) {
            var at = keyIndex(this._k, k);
            if (at === -1) { this._k.push(k); this._v.push(v); }
            else { this._v[at] = v; }
            return this;
        };
        MapShim.prototype.get = function (k) {
            var at = keyIndex(this._k, k);
            return at === -1 ? undefined : this._v[at];
        };
        MapShim.prototype.has = function (k) { return keyIndex(this._k, k) !== -1; };
        MapShim.prototype['delete'] = function (k) {
            var at = keyIndex(this._k, k);
            if (at === -1) { return false; }
            this._k.splice(at, 1); this._v.splice(at, 1); return true;
        };
        MapShim.prototype.clear = function () { this._k = []; this._v = []; };
        MapShim.prototype.size = function () { return this._k.length; };
        MapShim.prototype.forEach = function (fn, self) {
            for (var i = 0; i < this._k.length; i++) { fn.call(self, this._v[i], this._k[i], this); }
        };
        globalScope.Map = MapShim;
    }

    if (!globalScope.Set) {
        var SetShim = function () { this._v = []; };
        SetShim.prototype.add = function (v) {
            if (keyIndex(this._v, v) === -1) { this._v.push(v); }
            return this;
        };
        SetShim.prototype.has = function (v) { return keyIndex(this._v, v) !== -1; };
        SetShim.prototype['delete'] = function (v) {
            var at = keyIndex(this._v, v);
            if (at === -1) { return false; }
            this._v.splice(at, 1); return true;
        };
        SetShim.prototype.clear = function () { this._v = []; };
        SetShim.prototype.size = function () { return this._v.length; };
        SetShim.prototype.forEach = function (fn, self) {
            for (var i = 0; i < this._v.length; i++) { fn.call(self, this._v[i], this._v[i], this); }
        };
        globalScope.Set = SetShim;
    }

    if (!globalScope.WeakMap) {
        var WeakMapShim = function () { this._k = []; this._v = []; };
        WeakMapShim.prototype.set = function (k, v) {
            var at = keyIndex(this._k, k);
            if (at === -1) { this._k.push(k); this._v.push(v); }
            else { this._v[at] = v; }
            return this;
        };
        WeakMapShim.prototype.get = function (k) {
            var at = keyIndex(this._k, k);
            return at === -1 ? undefined : this._v[at];
        };
        WeakMapShim.prototype.has = function (k) { return keyIndex(this._k, k) !== -1; };
        WeakMapShim.prototype['delete'] = function (k) {
            var at = keyIndex(this._k, k);
            if (at === -1) { return false; }
            this._k.splice(at, 1); this._v.splice(at, 1); return true;
        };
        globalScope.WeakMap = WeakMapShim;
    }

    // Symbol stub: unique-string factory plus the well-known keys pages test
    // for. typeof checks for real symbols still fail — that needs the engine.
    if (!globalScope.Symbol) {
        var symCtr = 0;
        var SymbolShim = function (desc) {
            symCtr += 1;
            return '@@symbol:' + (desc || '') + '#' + symCtr;
        };
        SymbolShim.iterator = '@@symbol:iterator#0';
        SymbolShim.toStringTag = '@@symbol:toStringTag#0';
        SymbolShim.hasInstance = '@@symbol:hasInstance#0';
        SymbolShim.species = '@@symbol:species#0';
        globalScope.Symbol = SymbolShim;
    }

    // ── DOM helpers ─────────────────────────────────────────────────────
    if (globalScope.Element && globalScope.Element.prototype) {
        var elProto = globalScope.Element.prototype;
        if (!elProto.matches && elProto.msMatchesSelector) {
            elProto.matches = function (sel) { return this.msMatchesSelector(sel); };
        }
        if (!elProto.closest) {
            elProto.closest = function (sel) {
                var node = this;
                while (node) {
                    try {
                        if (node.matches && node.matches(sel)) { return node; }
                    } catch (e) { return null; }
                    node = node.parentElement || node.parentNode;
                    if (node && node.nodeType !== 1) { node = node.parentNode; }
                }
                return null;
            };
        }
        if (!elProto.remove) {
            elProto.remove = function () {
                if (this.parentNode) { this.parentNode.removeChild(this); }
            };
        }
    }
    if (!globalScope.CustomEvent && globalScope.document && globalScope.document.createEvent) {
        globalScope.CustomEvent = function (type, params) {
            params = params || {};
            var evt = globalScope.document.createEvent('CustomEvent');
            evt.initCustomEvent(type, !!params.bubbles, !!params.cancelable, params.detail);
            return evt;
        };
    }
    if (globalScope.NodeList && globalScope.NodeList.prototype && !globalScope.NodeList.prototype.forEach) {
        globalScope.NodeList.prototype.forEach = function (fn, self) {
            for (var i = 0; i < this.length; i++) { fn.call(self, this[i], i, this); }
        };
    }

    // ── fetch hardening ─────────────────────────────────────────────────
    // Adds blob()/arrayBuffer() to the XHR-backed shim above, derived from
    // text(): binary fidelity is best-effort on this engine. Headers keep
    // get()/has() only — a no-op forEach would silently break iteration logic,
    // so it is deliberately not faked.
    if (globalScope.fetch && globalScope.Blob) {
        var nativeFetch = globalScope.fetch;
        globalScope.fetch = function (input, options) {
            return nativeFetch(input, options).then(function (resp) {
                if (!resp.blob) {
                    resp.blob = function () {
                        return resp.text().then(function (t) {
                            return new globalScope.Blob([t]);
                        });
                    };
                }
                if (!resp.arrayBuffer) {
                    resp.arrayBuffer = function () {
                        return resp.text().then(function (t) {
                            var bytes = new Array(t.length);
                            for (var i = 0; i < t.length; i++) { bytes[i] = t.charCodeAt(i) & 255; }
                            return bytes;
                        });
                    };
                }
                return resp;
            });
        };
    }

    // ── Observer stubs (eager, not spec-true) ───────────────────────────
    // Firing immediately with isIntersecting:true makes lazy-load libraries
    // load everything instead of never loading anything on this engine.
    function eagerObserver(callback) {
        this._cb = callback;
        this._targets = [];
    }
    eagerObserver.prototype.observe = function (target) {
        this._targets.push(target);
        var self = this;
        globalScope.setTimeout(function () {
            try {
                self._cb([{ target: target, isIntersecting: true, intersectionRatio: 1 }], self);
            } catch (e) { /* page callback threw */ }
        }, 0);
    };
    eagerObserver.prototype.unobserve = function (target) {
        for (var i = 0; i < this._targets.length; i++) {
            if (this._targets[i] === target) { this._targets.splice(i, 1); break; }
        }
    };
    eagerObserver.prototype.disconnect = function () { this._targets = []; };
    if (!globalScope.IntersectionObserver) {
        globalScope.IntersectionObserver = function (cb) { eagerObserver.call(this, cb); };
        globalScope.IntersectionObserver.prototype = eagerObserver.prototype;
    }
    if (!globalScope.ResizeObserver) {
        globalScope.ResizeObserver = function (cb) { eagerObserver.call(this, cb); };
        globalScope.ResizeObserver.prototype = eagerObserver.prototype;
    }

    // ── Timing hook ───────────────────────────────────────────────────────
    // Lets the host page prove the shim ran, which the compatibility probe
    // reads back. Harmless if the page never looks at it.
    try {
        globalScope.__browserForWPCompat = {
            version: 2,
            installedAt: Date.now()
        };
    } catch (e) { /* ignore */ }
}(typeof window !== 'undefined' ? window : this));
