#!/usr/bin/env node
// Prototype and referee for BoxTreeBuilder.vb and DocumentDumper.vb.
// The rule it exists to pin down: an inline run inside a block container gets its
// own anonymous block box, which is what lets Phase 2 lay out only blocks and
// inline runs and never worry about the mixture.
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

// A hand-built, already-styled tree keeps this prototype about ONE rule: how
// elements and text become boxes. Styling has its own referee in csscascade.mjs.
function style(display) { return { display, isBlock: display === 'block', isHidden: display === 'none' }; }
function el(tag, st, children) { return { tagName: tag, children: children || [], style: st, text: '' }; }
function text(s) { return { tagName: '#text', text: s, children: [], style: style('inline') }; }

function build(node) {
  if (node.style && node.style.isHidden) return null;
  if (node.tagName === '#text') {
    if (!node.text.trim()) return null;   // whitespace-only text carries no box
    return { kind: 'text', tagName: '#text', text: node.text, style: node.style, children: [] };
  }
  const isBlock = node.style.isBlock;
  const children = [];
  for (const child of node.children) {
    const built = build(child);
    if (built === null) continue;
    if (!isBlock) { children.push(built); continue; }
    if (built.kind === 'block') children.push(built);
    else {
      const last = children[children.length - 1];
      if (last && last.anonymous) last.children.push(built);
      else children.push({ kind: 'block', tagName: '#anonymous', text: '', style: style('block'), children: [built], anonymous: true });
    }
  }
  return { kind: isBlock ? 'block' : 'inline', tagName: node.tagName, text: '', style: node.style, children };
}

check('hidden element is dropped entirely', build(el('script', style('none'), [text('var x')])) === null);
check('whitespace-only text produces no box',
  build(el('p', style('block'), [text('   ')])).children.length === 0);
check('a block root is a block box', (() => {
  const b = build(el('body', style('block'), [el('p', style('block'), [])]));
  return b.kind === 'block' && b.tagName === 'body';
})());
check('nested blocks stay nested', (() => {
  const b = build(el('body', style('block'), [el('div', style('block'), [el('p', style('block'), [])])]));
  return b.children[0].tagName === 'div' && b.children[0].children[0].tagName === 'p';
})());
check('a bare inline child is wrapped in an anonymous block', (() => {
  const b = build(el('body', style('block'), [text('hello')]));
  return b.children.length === 1 && b.children[0].tagName === '#anonymous'
    && b.children[0].kind === 'block' && b.children[0].children[0].kind === 'text';
})());
check('consecutive inline children share one anonymous block', (() => {
  const b = build(el('body', style('block'), [text('a'), el('em', style('inline'), [text('b')])]));
  return b.children.length === 1 && b.children[0].children.length === 2;
})());
check('an inline run is split around a block child', (() => {
  const b = build(el('body', style('block'), [
    text('before'), el('p', style('block'), [text('middle')]), text('after'),
  ]));
  return b.children.length === 3
    && b.children[0].kind === 'block' && b.children[0].anonymous === true
    && b.children[1].tagName === 'p'
    && b.children[2].anonymous === true;
})());
check('an inline container keeps inline children inline', (() => {
  const b = build(el('span', style('inline'), [text('x')]));
  return b.kind === 'inline' && b.children[0].kind === 'text' && b.children[0].anonymous === undefined;
})());
check('a document with only text still produces a block', (() => {
  const b = build(el('html', style('block'), [text('only text')]));
  return b.kind === 'block' && b.children.length === 1;
})());
check('a hidden child does not break the surrounding run', (() => {
  const b = build(el('div', style('block'), [text('a'), el('script', style('none'), []), text('b')]));
  return b.children.length === 1 && b.children[0].children.length === 2;
})());

function dump(node, indent = 0) {
  const pad = '  '.repeat(indent);
  const label = node.tagName === '#text' ? `#text "${node.text}"` : node.tagName;
  const lines = [`${pad}${node.kind[0]} ${label}`];
  for (const c of node.children) lines.push(dump(c, indent + 1));
  return lines.join('\n');
}
check('dump marks the box kind',
  dump(build(el('body', style('block'), [el('p', style('block'), [text('a')])]))).startsWith('b body'));
check('dump shows text content', dump(build(el('body', style('block'), [text('a')]))).includes('#text "a"'));
check('dump indents children',
  dump(build(el('body', style('block'), [el('p', style('block'), [])]))).includes('\n  b p'));

// ── wiring parity (Task 8) ──────────────────────────────────────────────────
const xaml = readIfPresent('BrowserForWP/MainPage.xaml');
const main = readIfPresent('BrowserForWP/MainPage.xaml.vb');
const enRes = readIfPresent('BrowserForWP/Strings/en-US/Resources.resw');
const itRes = readIfPresent('BrowserForWP/Strings/it-IT/Resources.resw');

// ── wiring parity ─────────────────────────────────────────────────────────
// The parse-page demo was removed to simplify diagnostics: rendering over the
// app's own transport is what the engine picker chooses, not a diagnostics
// button. The pipeline stays owned by the engine layer, and the shell must not
// grow a second caller through it.
check('the parse demo button is gone', !xaml.includes('ParsePageButton'));
check('the parse handler is gone', !main.includes('ParsePageButton_Click'));
check('the result block is gone', !xaml.includes('ParseResult'));
check('the shell has no second tree-builder caller', !main.includes('BoxTreeBuilder.'));
check('the shell has no second dumper caller', !main.includes('DocumentDumper.Dump('));
for (const key of ['ParseThisPage', 'ParseFailed', 'ParseBoxCount']) {
  check(`${key} gone from en-US`, !enRes.includes(`name="${key}"`));
  check(`${key} gone from it-IT`, !itRes.includes(`name="${key}"`));
}
// ParseNoDocument stays: the native navigation-failure path still reports it.
check('ParseNoDocument stays in en-US', enRes.includes('name="ParseNoDocument"'));
check('ParseNoDocument stays in it-IT', itRes.includes('name="ParseNoDocument"'));

// The IE-mode instrument is required by this plan's Task 1: without a control in
// the diagnostics view, IeModeProbe ships unreachable and the record in
// MAINTAINING.md promises a measurement path that does not exist.
check('the diagnostics view has an IE mode button', xaml.includes('IeModeButton'));
check('the IE mode result block exists', xaml.includes('IeModeResult'));
check('the IE mode handler exists', main.includes('IeModeButton_Click'));
check('the IE mode handler uses the probe', main.includes('IeModeProbe'));
for (const key of ['IeModeCheck', 'IeModeReport']) {
  check(`${key} in en-US`, enRes.includes(`name="${key}"`));
  check(`${key} in it-IT`, itRes.includes(`name="${key}"`));
}

// ── Collecting the page's own stylesheet ────────────────────────────────────
// Transliterated from BoxTreeBuilder.PageCss, which is where this belongs: it
// was Private Shared inside MainPage until a second caller appeared (the native
// engine), and the tree builder is the thing that consumes its output.
function pageCss(html) {
  if (!html) return '';
  let out = '';
  const lowered = html.toLowerCase();
  let from = 0;
  for (;;) {
    const openAt = lowered.indexOf('<style', from);
    if (openAt < 0) break;
    const bodyStart = lowered.indexOf('>', openAt);
    if (bodyStart < 0) break;
    const closeAt = lowered.indexOf('</style', bodyStart);
    if (closeAt < 0) break;
    out += html.substring(bodyStart + 1, closeAt) + '\n';
    from = closeAt + 1;
  }
  return out;
}
check('page css: no style element gives an empty sheet', pageCss('<p>hi</p>') === '');
check('page css: one style element is collected',
  pageCss('<style>p{color:red}</style>') === 'p{color:red}\n');
check('page css: every style element is collected, in document order',
  pageCss('<style>a{}</style><p>x</p><style>b{}</style>') === 'a{}\nb{}\n');
check('page css: the tag is found in any case',
  pageCss('<STYLE>p{}</STYLE>') === 'p{}\n');
check('page css: attributes on the tag do not defeat it',
  pageCss('<style type="text/css">p{}</style>') === 'p{}\n');
check('page css: an unterminated element yields nothing, not the rest of the page',
  pageCss('<style>p{}</p>') === '');

// ── VB file parity (BoxTreeBuilder) ─────────────────────────────────────────
const builder = readIfPresent('BrowserForWP.Core/Engine/Native/BoxTreeBuilder.vb');
check('BoxTreeBuilder.vb exists', builder.length > 0);
check('BoxTreeBuilder exposes BuildPage', builder.includes('BuildPage'));
check('BoxTreeBuilder now owns the page stylesheet collector',
  builder.includes('Function PageCss('));
check('MainPage no longer owns it',
  main.length === 0 || !main.includes('Function InlineStyleText('));
check('MainPage has no second tree-builder caller',
  main.length === 0 || !main.includes('BoxTreeBuilder.'));
check('DocumentDumper.vb exists', readIfPresent('BrowserForWP.Core/Diagnostics/DocumentDumper.vb').length > 0);

console.log(`\n${checks - failures}/${checks} boxtree checks passed.`);
if (failures > 0) {
  console.log(`${failures} boxtree failure(s).`);
  process.exit(1);
}
