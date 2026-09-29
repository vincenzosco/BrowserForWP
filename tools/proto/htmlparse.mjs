#!/usr/bin/env node
// Prototype and referee for BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb and
// HtmlTreeBuilder.vb. Change this first, watch it pass, then port.
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

const VOID = ['area','base','br','col','embed','hr','img','input','link','meta','param','source','track','wbr'];
const RAW_TEXT = ['script','style'];
const HEAD_CONTENT = ['title','meta','link','base','style','script','noscript'];

function tokenize(html) {
  const tokens = [];
  let pos = 0;
  let text = '';
  const flush = () => { if (text.length) { tokens.push({ kind: 'text', text }); text = ''; } };

  while (pos < html.length) {
    const ch = html[pos];
    if (ch !== '<') { text += ch; pos += 1; continue; }

    if (html.startsWith('<!--', pos)) {
      const end = html.indexOf('-->', pos + 4);
      pos = end < 0 ? html.length : end + 3;
      continue;
    }
    if (html.startsWith('<!', pos)) {
      const end = html.indexOf('>', pos);
      if (end < 0) break;
      pos = end + 1;
      continue;
    }
    if (html.startsWith('</', pos)) {
      const end = html.indexOf('>', pos);
      if (end < 0) break;
      flush();
      const name = html.slice(pos + 2, end).trim().toLowerCase();
      if (name) tokens.push({ kind: 'endtag', name });
      pos = end + 1;
      continue;
    }
    if (pos + 1 < html.length && /[A-Za-z]/.test(html[pos + 1])) {
      flush();
      let scan = pos + 1;
      while (scan < html.length && html[scan] !== '>') scan += 1;
      const inside = html.slice(pos + 1, scan);
      pos = scan < html.length ? scan + 1 : html.length;
      const tag = parseStartTag(inside);
      if (!tag) continue;
      tokens.push(tag);
      if (!tag.selfClosing && RAW_TEXT.includes(tag.name)) {
        const closeAt = html.toLowerCase().indexOf(`</${tag.name}`, pos);
        const rawEnd = closeAt < 0 ? html.length : closeAt;
        const raw = html.slice(pos, rawEnd);
        if (raw.length) tokens.push({ kind: 'text', text: raw, raw: true });
        pos = rawEnd;
      }
      continue;
    }
    text += ch;
    pos += 1;
  }
  flush();
  return tokens;
}

function parseStartTag(inside) {
  let s = inside;
  let selfClosing = false;
  if (s.endsWith('/')) { selfClosing = true; s = s.slice(0, -1); }
  const m = /^\s*([A-Za-z][A-Za-z0-9-]*)/.exec(s);
  if (!m) return null;
  const name = m[1].toLowerCase();
  const attributes = [];
  const rest = s.slice(m[0].length);
  const ATTR = /\s*([A-Za-z_:][A-Za-z0-9_:.-]*)(?:\s*=\s*("([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g;
  let a;
  while ((a = ATTR.exec(rest)) !== null) {
    if (a[1] === undefined) break;
    const value = a[3] !== undefined ? a[3] : a[4] !== undefined ? a[4] : a[5] !== undefined ? a[5] : '';
    attributes.push({ name: a[1].toLowerCase(), value });
    if (ATTR.lastIndex === a.index) break;
  }
  return { kind: 'starttag', name, attributes, selfClosing };
}

function makeNode(tag) {
  return { tagName: tag, attributes: {}, children: [], text: '', parent: null };
}

function tree(tokens) {
  const root = makeNode('html');
  let head = null;
  let body = null;
  let inHead = false;
  const stack = [root];
  const top = () => stack[stack.length - 1];
  const ensureHead = () => {
    if (!head) { head = makeNode('head'); head.parent = root; root.children.unshift(head); }
    return head;
  };
  const ensureBody = () => {
    if (!body) { body = makeNode('body'); body.parent = root; root.children.push(body); }
    return body;
  };

  for (const t of tokens) {
    if (t.kind === 'text') {
      // Text directly under <html> belongs to neither section and is dropped.
      if (top() === root) continue;
      // Script and style bodies are data, not document text: the box tree must
      // never see them, or a stray "<div>" inside a string would appear as text.
      if (RAW_TEXT.includes(top().tagName)) continue;
      if (!t.text.trim()) continue;
      const node = makeNode('#text');
      node.text = t.text.replace(/\s+/g, ' ');
      node.parent = top();
      top().children.push(node);
      continue;
    }
    if (t.kind === 'endtag') {
      for (let i = stack.length - 1; i >= 1; i -= 1) {
        if (stack[i].tagName === t.name) { stack.length = i; break; }
      }
      continue;
    }
    if (t.name === 'html') continue;
    if (t.name === 'head') { ensureHead(); inHead = true; stack.length = 1; stack.push(head); continue; }
    if (t.name === 'body') { ensureBody(); inHead = false; stack.length = 1; stack.push(body); continue; }

    if (!body && HEAD_CONTENT.includes(t.name)) {
      const headParent = ensureHead();
      const el = makeNode(t.name);
      el.parent = headParent;
      for (const a of t.attributes) el.attributes[a.name] = a.value;
      headParent.children.push(el);
      if (!VOID.includes(t.name) && !t.selfClosing) stack.push(el);
      continue;
    }

    ensureBody();
    // Leaving the head section: the insertion point moves to the body, whatever
    // was still open in the head.
    if (inHead || top() === root) { inHead = false; stack.length = 1; stack.push(body); }

    const parent = top();
    const el = makeNode(t.name);
    el.parent = parent;
    for (const a of t.attributes) el.attributes[a.name] = a.value;
    parent.children.push(el);
    if (!VOID.includes(t.name) && !t.selfClosing) stack.push(el);
  }
  return root;
}

function textOf(root) {
  let out = '';
  const walk = (n) => { if (n.tagName === '#text') { out += n.text; return; } for (const c of n.children) walk(c); };
  walk(root);
  return out;
}
function find(root, tag) {
  if (!root) return null;
  if (root.tagName === tag) return root;
  for (const c of root.children) { const hit = find(c, tag); if (hit) return hit; }
  return null;
}

// ── tokenizer ───────────────────────────────────────────────────────────────
check('plain text is one token', tokenize('hello').length === 1);
check('comment is skipped', tokenize('<!-- x -->').length === 0);
check('doctype is skipped', tokenize('<!DOCTYPE html>').length === 0);
check('start tag', tokenize('<p>')[0].name === 'p');
check('end tag', tokenize('</p>')[0].kind === 'endtag');
check('attributes lowercase', tokenize('<A HREF="x" ID=y>')[0].attributes[0].name === 'href');
check('quoted value', tokenize('<a href="http://e.com/">')[0].attributes[0].value === 'http://e.com/');
check('single-quoted value', tokenize("<a href='x'>")[0].attributes[0].value === 'x');
check('unquoted value', tokenize('<a href=x>')[0].attributes[0].value === 'x');
check('valueless attribute', tokenize('<input disabled>')[0].attributes[0].value === '');
check('self-closing flag', tokenize('<br/>')[0].selfClosing === true);
check('script body is raw text', tokenize('<script>if (a<b) {}</script>')[1].raw === true);
check('less-than in text survives', (() => {
  const t = tokenize('a < b');
  return t.length === 1 && t[0].text === 'a < b';
})());

// ── tree builder ────────────────────────────────────────────────────────────
check('implicit html/head/body', (() => {
  const t = tree(tokenize('<title>x</title><p>y</p>'));
  return t.tagName === 'html' && find(t, 'head') !== null && find(t, 'body') !== null;
})());
check('title goes to head, p to body', (() => {
  const t = tree(tokenize('<title>x</title><p>y</p>'));
  return find(find(t, 'head'), 'title') !== null && find(find(t, 'body'), 'p') !== null;
})());
check('head is created only once', (() => {
  const t = tree(tokenize('<title>a</title><meta charset="utf-8"><p>b</p>'));
  return t.children.filter((c) => c.tagName === 'head').length === 1;
})());
check('explicit head wins', (() => {
  const t = tree(tokenize('<head><title>a</title></head><body><p>b</p></body>'));
  return find(find(t, 'head'), 'title') !== null && find(find(t, 'body'), 'p') !== null;
})());
check('a block after head content lands in the body, not the head', (() => {
  const t = tree(tokenize('<title>a</title><p>b</p>'));
  return find(find(t, 'head'), 'p') === null;
})());
check('nesting by end tag', (() => {
  const t = tree(tokenize('<div><p>a</p></div>'));
  const d = find(t, 'div');
  return d.children.length === 1 && d.children[0].tagName === 'p';
})());
check('void element does not nest', (() => {
  const t = tree(tokenize('<p>a<br>b</p>'));
  const p = find(t, 'p');
  const br = find(p, 'br');
  // '<p>a<br>b</p>' has THREE children: text, br, text. The earlier version of
  // this assertion expected two (text, br) and so failed against a correct
  // implementation -- it had dropped the 'b' that proves the point. What it
  // means to test is that a void element never goes on the open-element stack,
  // so the text after <br> lands in <p> as a sibling and <br> stays childless.
  return p.children.length === 3 &&
    br !== null && br.children.length === 0 && br.parent === p &&
    p.children[0].tagName === '#text' &&
    p.children[1].tagName === 'br' &&
    p.children[2].tagName === '#text';
})());
check('whitespace-only text is dropped', (() => {
  const t = tree(tokenize('<p>\n   \n</p>'));
  return find(t, 'p').children.length === 0;
})());
check('text whitespace collapses', (() => {
  const t = tree(tokenize('<p>a   b</p>'));
  return find(find(t, 'p'), '#text').text === 'a b';
})());
check('stray end tag is ignored', find(tree(tokenize('</div><p>a</p>')), 'p') !== null);
check('unclosed element is auto-closed at the end', (() => {
  const t = tree(tokenize('<div><p>a'));
  return find(t, 'p') !== null;
})());
check('declared subset is recognised end to end', (() => {
  const t = tree(tokenize('<article><h1>T</h1><p>a<em>b</em></p></article>'));
  return find(t, 'article') !== null && find(t, 'h1') !== null && find(t, 'em') !== null;
})());
check('script text is not document text', (() => {
  const t = tree(tokenize('<body><p>a</p><script>var x=1;</script>'));
  return !textOf(find(t, 'body')).includes('var x');
})());
check('style text is not document text', (() => {
  const t = tree(tokenize('<body><p>a</p><style>p{color:red}</style>'));
  return !textOf(find(t, 'body')).includes('color:red');
})());

// ── VB file parity ──────────────────────────────────────────────────────────
const tokenizer = readIfPresent('BrowserForWP.Core/Engine/Native/HtmlTokenizer.vb');
check('HtmlTokenizer.vb exists', tokenizer.length > 0);
check('HtmlTokenizer.vb handles raw text', tokenizer.includes('script'));
check('HtmlTokenizer.vb has no VB14 fluent chain', tokenizer.length > 0 && !/\)\.\s*\n\s*\w+\(/.test(tokenizer));

console.log(`\n${checks - failures}/${checks} htmlparse checks passed.`);
if (failures > 0) {
  console.log(`${failures} htmlparse failure(s).`);
  process.exit(1);
}
