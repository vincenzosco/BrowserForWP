#!/usr/bin/env node
// ═══════════════════════════════════════════════════════════════════════════
//  Static VB.NET structural checker — BrowserForWP
//
//  WHY THIS EXISTS
//  ---------------
//  The Windows Phone 8.1 SDK cannot be installed on this project's development
//  host (an Apple silicon Mac), so the sources are developed off-platform.
//
//  They are NOT, however, uncompiled. An ARM64 Windows 11 Parallels guest does
//  host the whole VS2013 toolchain and builds the solution for real; see
//  tools/vm-build.cmd and the round-by-round transcripts in docs/MAINTAINING.md.
//  An earlier revision of this header claimed the VM could not host VS2013. That
//  was wrong, and it is worth saying so plainly: the claim was inferred from
//  Microsoft's "Visual Studio does not support Arm processors" documentation
//  instead of from trying it, and the cost of not trying was three rounds of
//  compile errors that a two-minute build would have surfaced immediately.
//
//  This tool still earns its place, because the guest round trip is slow and its
//  output is in Italian. It catches the cheap, repetitive mistakes first, on any
//  machine:
//
//    1. Block balance          every Class/Sub/If/Try/... has its terminator
//    2. Implements completeness an `Implements IDisposable` needs matching members
//    3. Project/disk parity    every .vb on disk is in the .vbproj and vice versa
//    4. Namespace resolution   RootNamespace + declared Namespace actually produces
//                              the namespace that other projects import
//    5. Cross-project imports  every `Imports BrowserForWP.X` names a real namespace
//    6. Resource parity        en-US and it-IT define exactly the same keys
//    7. XAML handler wiring    every event handler named in XAML exists in VB
//    8. XAML single root child  a Page sets Content exactly once
//    9. Theme resources        every `{ThemeResource X}` in XAML exists on WP8.1
//   10. Char-range literals    ChrW cannot express a supplementary-plane code point
//   11. VB 12 syntax           no leading-dot line continuation (VS2015 and later)
//   12. Profile hazards        APIs absent from .NET for Windows Store apps
//   13. Comment hazards        '--' in XML comments, unescaped '<' in doc comments
//   14. Project flavour        the flavour GUID the IDE uses to resolve references
//   15. Privileged access      JIT, process creation, full-trust capabilities
//   16. Capability requirements  the code needs a capability the manifest lacks
//
//  Group 12's list is not a guess about what the profile removes: every entry in
//  it was paid for by a guest build that failed. FontStyles is the latest.
//
//  Groups 15 and 16 are the opposite and say so: none of their entries has ever
//  broken a build in this repository, because none of them has ever been written.
//  Group 15 is derived from docs/ARCHITECTURE.md Law 4 and group 16 from the
//  platform's capability list, so their provenance is reasoning rather than a
//  compiler, and a reader should weigh them accordingly. Of group 16's twelve
//  requirements exactly one is referenced by this codebase (the network one); the
//  other eleven are the guard, not a description of what the app does.
//
//  WHAT IT CANNOT DO
//  -----------------
//  It is not a compiler. It cannot type-check, resolve overloads, verify WinRT
//  API availability in general, or catch a wrong method signature. A clean run
//  means "no mechanical defects of these kinds", not "compiles".
//
//  Concretely, every one of the following compiled clean here and failed in the
//  guest, so treat a green run as a filter and not as a verdict:
//
//    * `Friend` members used from a referencing assembly (BC30390)
//    * a nested class named in another file's signature (BC30002)
//    * a local named the same as an enclosing type or member, which VB's
//      case-insensitivity turns into a shadowing error at the USE site
//      (BC30039 / BC30456 "is not a member of 'Integer'")
//
//  A XAML `{ThemeResource ...}` key that does not exist on WP8.1 was on this list
//  until it cost a round: `MainPage.xaml` asked for `TextControlBackground`, a
//  UWP-era name the phone does not define, and shipped. Group 9 now checks it
//  against the phone's own dictionaries, so it is no longer uncatchable.
//
//  USAGE
//    node tools/check-vb.mjs          # report, exit 1 on any finding
//    node tools/check-vb.mjs --quiet  # only print the summary
// ═══════════════════════════════════════════════════════════════════════════

import fs from 'node:fs';
import path from 'node:path';

const QUIET = process.argv.includes('--quiet');
const ROOT = process.cwd();

let findings = [];

function fail(check, file, message, line) {
  findings.push({ check, file, message, line });
}
function ok(msg) {
  if (!QUIET) console.log(`  \u2713 ${msg}`);
}
function heading(msg) {
  if (!QUIET) console.log(`\n\u2500\u2500 ${msg}`);
}

// ── File discovery ──────────────────────────────────────────────────────────
function walk(dir, predicate, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin' || entry.name === '.git') continue;
      walk(full, predicate, out);
    } else if (predicate(full)) {
      out.push(full);
    }
  }
  return out;
}

const rel = (p) => path.relative(ROOT, p).split(path.sep).join('/');

// ── VB source cleaning ──────────────────────────────────────────────────────
// Removes comments and blanks out string literals, so that a keyword or quote
// inside a comment or a string cannot be mistaken for code. VB doubles a quote
// to escape it, which is why this scans rather than using a regex.
function cleanLines(source) {
  const lines = source.split(/\r?\n/);
  return lines.map((raw) => {
    let out = '';
    let inString = false;
    for (let i = 0; i < raw.length; i++) {
      const ch = raw[i];
      if (inString) {
        if (ch === '"') {
          if (raw[i + 1] === '"') { i++; continue; }   // escaped quote
          inString = false;
        }
        continue;                                      // drop literal content
      }
      if (ch === '"') { inString = true; continue; }
      if (ch === "'") break;                           // comment to end of line
      out += ch;
    }
    return out;
  });
}

// ── 1. Block balance ────────────────────────────────────────────────────────
// A declaration is `Sub Name` / `Function Name`; a lambda is `Sub(` / `Function(`.
// Only declarations open a block, which is what keeps lambdas from producing
// false positives.
const BLOCK_RULES = [
  { open: /^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend|Shared|Partial|NotInheritable|MustInherit|Static)\s+)*Namespace\s+\S/, close: 'End Namespace', label: 'Namespace' },
  { open: /^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend|Shared|Partial|NotInheritable|MustInherit)\s+)*Class\s+\S/, close: 'End Class', label: 'Class' },
  { open: /^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend)\s+)*Module\s+\S/, close: 'End Module', label: 'Module' },
  { open: /^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend)\s+)*Structure\s+\S/, close: 'End Structure', label: 'Structure' },
  { open: /^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend)\s+)*Interface\s+\S/, close: 'End Interface', label: 'Interface' },
  { open: /^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend)\s+)*Enum\s+\S/, close: 'End Enum', label: 'Enum' },
  { open: /^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|NotOverridable|MustOverride|Async|Static|Protected Friend|Private Protected)\s+)*Sub\s+[A-Za-z_\[]/, close: 'End Sub', label: 'Sub' },
  { open: /^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|NotOverridable|MustOverride|Async|Static|Protected Friend|Private Protected)\s+)*Function\s+[A-Za-z_\[]/, close: 'End Function', label: 'Function' },
  { open: /^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|NotOverridable|MustOverride|Default|ReadOnly|WriteOnly|Async|Static)\s+)*Property\s+[A-Za-z_\[]/, close: 'End Property', label: 'Property' },
  { open: /^\s*Get\s*$/, close: 'End Get', label: 'Get' },
  { open: /^\s*Set\s*$/, close: 'End Set', label: 'Set' },
  { open: /^\s*Select\s+Case\b/, close: 'End Select', label: 'Select' },
  { open: /^\s*While\b/, close: 'End While', label: 'While' },
  { open: /^\s*For\s+(?:Each\s+)?\S/, close: 'Next', label: 'For' },
  { open: /^\s*Try\s*$/, close: 'End Try', label: 'Try' },
  { open: /^\s*Using\b/, close: 'End Using', label: 'Using' },
  { open: /^\s*With\b/, close: 'End With', label: 'With' },
  { open: /^\s*SyncLock\b/, close: 'End SyncLock', label: 'SyncLock' },
  { open: /^\s*Do\b/, close: 'Loop', label: 'Do' },
  { open: /^\s*Operator\b/, close: 'End Operator', label: 'Operator' },
];

// `If` opens a block in two shapes, and the single-line form opens none:
//
//   If x Then                 -> block (Then ends the line)
//   If x AndAlso              -> block (Then is on a LATER line; the condition
//      y Then                     is continued without a `_`, which VB allows)
//   If x Then DoSomething()   -> no block
//
// Missing the continuation case desynchronises the stack exactly as badly as
// omitting `End If` from the terminator list does.
// True when a parenthesised group contains a comma at depth 1. `If(a, b, c)`
// always does; `If (x And y) = 0` never does. A bare `If\s*\(` test cannot
// tell those apart and wrongly rejects the second as the ternary operator.
function hasTopLevelComma(group) {
  let depth = 0;
  let inString = false;
  for (let i = 0; i < group.length; i++) {
    const ch = group[i];
    if (inString) {
      if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') { inString = true; continue; }
    if (ch === '(') depth++;
    else if (ch === ')') depth--;
    else if (ch === ',' && depth === 1) return true;
  }
  return false;
}

function isBlockIf(line) {
  if (!/^\s*If\b/.test(line)) return false;      // excludes ElseIf and #If
  if (/\bThen\s*$/.test(line)) return true;       // Then ends the line

  // Ternary conditional operator, which is an expression and has no End If.
  // Deliberately written WITHOUT a backslash-escaped paren in a pattern: that
  // is precisely where an over-escaped pattern silently stopped matching.
  const trimmedIf = line.trim();
  if (trimmedIf.startsWith('If(') && trimmedIf.endsWith(')')) {
    if (hasTopLevelComma(trimmedIf.slice(2))) return false;
  }

  if (!/\bThen\b/.test(line)) return true;        // Then arrives on a later line
  return false;                                   // single-line If
}

// The next line that is not blank, used to tell an auto-implemented property
// from one with a body.
function nextCodeLine(lines, from) {
  for (let i = from + 1; i < lines.length; i++) {
    if (lines[i].trim().length > 0) return lines[i].trim();
  }
  return '';
}

function checkBlockBalance(file, lines) {
  // Pairs of (opener, terminator). Inside a single-line statement no block is
  // opened, so `Next`/`Loop` etc. are matched against the innermost opener.
  const stack = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    if (trimmed.length === 0) continue;

    // Terminators are matched FIRST, so `End If` is never read as an opener.
    // `End If` must be present in this list: leaving it out does not merely
    // miss one case, it desynchronises the stack and reports every following
    // block as unbalanced. Longer strings are listed before their prefixes.
    const TERMINATORS = ['End Namespace', 'End Interface', 'End Structure',
      'End Module', 'End Class', 'End Enum', 'End Sub', 'End Function',
      'End Property', 'End Select', 'End SyncLock', 'End Operator',
      'End While', 'End Try', 'End Using', 'End With',
      'End Get', 'End Set', 'End If', 'Next', 'Loop'];

    // `Next` and `Loop` take an optional trailing clause — `Next i`,
    // `Loop While condition`, `Loop Until condition` — so an exact-string
    // comparison misses them and desynchronises the stack.
    const terminator = TERMINATORS.find((t) => {
      if (t === 'Next') return /^Next\b/.test(trimmed);
      if (t === 'Loop') return /^Loop\b/.test(trimmed);
      return trimmed === t || trimmed.startsWith(t);
    });

    if (terminator) {
      const top = stack.pop();
      if (!top) {
        fail('block-balance', file, `stray \`${terminator}\` with no open block`, i + 1);
      } else if (terminator !== top.close) {
        // `Next` closes For and `Loop` closes Do, so a direct comparison is
        // the correct test; treating them as wildcards would hide real errors.
        fail('block-balance', file,
          `\`${terminator}\` closes ${top.label} opened at line ${top.line}, which needs \`${top.close}\``,
          i + 1);
      }
      continue;
    }

    if (isBlockIf(line)) {
      stack.push({ label: 'If', close: 'End If', line: i + 1 });
      continue;
    }

    // Inside an Interface, and for MustOverride/Declare members, Sub/Function/
    // Property are signature-only and have no terminator at all.
    const insideInterface = stack.some((s) => s.label === 'Interface');

    for (const rule of BLOCK_RULES) {
      if (!rule.open.test(line)) continue;

      if (insideInterface &&
          ['Sub', 'Function', 'Property', 'Get', 'Set'].includes(rule.label)) {
        break;                                   // bodyless interface member
      }
      if ((rule.label === 'Sub' || rule.label === 'Function') &&
          /\b(?:MustOverride|Declare)\b/.test(line)) {
        break;                                   // bodyless by design
      }
      if (rule.label === 'Property') {
        // Auto-implemented properties are one line with no End Property:
        //   Public Property Foo As Integer
        // A property with a body is followed by Get/Set/Implements or by
        // End Property directly. Anything else has no block.
        const next = nextCodeLine(lines, i);
        const hasBody = /^(?:Get|Set)\s*$/.test(next) ||
                        /^End Property\s*$/.test(next) ||
                        /^Implements\b/.test(next);
        if (!hasBody) break;
      }

      stack.push({ label: rule.label, close: rule.close, line: i + 1 });
      break;
    }
  }

  for (const unclosed of stack) {
    fail('block-balance', file,
      `${unclosed.label} opened at line ${unclosed.line} is never closed (needs \`${unclosed.close}\`)`,
      unclosed.line);
  }
  if (stack.length === 0) ok(`${rel(file)}: blocks balanced`);
}

// ── 2/5. Namespaces, declarations and cross-project imports ─────────────────
function readProject(file) {
  // Strip XML comments BEFORE scanning for items. Without this, an `<Item>`
  // example written inside a comment is parsed as a real declaration -- which is
  // exactly what happened: a comment describing the old
  // `<Content Include="Polyfill\compat.js" />` was read as a live item and
  // reported as a missing file. The same mistake the .vb scanner already avoids.
  const xml = fs.readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  const rootNamespace = (xml.match(/<RootNamespace>([^<]*)</) || [, ''])[1].trim();
  const assemblyName = (xml.match(/<AssemblyName>([^<]*)</) || [, ''])[1].trim();
  // VB projects declare .vb files as <Compile>, but .xaml and .resw are
  // declared as <Page>/<ApplicationDefinition> and <PRIResource>/<Content>.
  // Comparing every file against <Compile> alone produces false alarms for
  // every XAML and resource file, so gather every declared item instead.
  const ITEM_TYPES = ['Compile', 'Page', 'ApplicationDefinition', 'PRIResource',
    'Content', 'EmbeddedResource', 'Resource', 'None', 'DesignTime',
    'DesignTimeSharedInput', 'None'];
  const itemPattern = new RegExp(
    `<(${ITEM_TYPES.join('|')})\\s+Include="([^"]+)"`, 'g');
  const declaredItems = [...xml.matchAll(itemPattern)]
    .map((m) => m[2].replace(/\\/g, '/'));
  const refs = [...xml.matchAll(/<ProjectReference Include="([^"]+)"/g)]
    .map((m) => m[1].replace(/\\/g, '/'));
  // The project FLAVOUR and the target platform are two separate statements, and
  // a project can make them disagree without either looking wrong on its own.
  // See checkProjectFlavor().
  const projectTypeGuids = (xml.match(/<ProjectTypeGuids>([^<]*)</) || [, ''])[1].trim();
  const targetPlatformIdentifier = [...xml.matchAll(/<TargetPlatformIdentifier>([^<]*)</g)]
    .map((m) => m[1].trim());
  return { rootNamespace, assemblyName, declaredItems, refs, projectTypeGuids,
    targetPlatformIdentifier };
}

const projectFiles = walk(ROOT, (f) => f.endsWith('.vbproj'));
const projects = projectFiles.map((f) => {
  const dir = path.dirname(f);
  return { file: f, dir, name: path.basename(f, '.vbproj'), ...readProject(f) };
});
const projectByName = new Map(projects.map((p) => [p.name, p]));

// Full namespace of a file = RootNamespace + the file's own Namespace blocks,
// NESTED ONES COMPOSED.
//
// They compose, and the first version of this function did not know that: it
// matched every `Namespace` line on its own and prefixed the root namespace, so
//
//     Namespace Engine
//         Namespace Remote
//
// produced BrowserForWP.Core.Engine and BrowserForWP.Core.Remote, and never
// BrowserForWP.Core.Engine.Remote. The compiler composes them, so a perfectly
// correct `Imports BrowserForWP.Core.Engine.Remote` was reported as matching no
// namespace in the solution -- a false alarm on code that compiles, which is the
// one thing an import checker must never do. Found when RemoteChannel.vb became
// the first file to import a nested namespace; RemoteProtocol.vb had declared it
// for a whole round before that, invisible.
function fileNamespaces(source, project) {
  const stack = [];
  const namespaces = [project.rootNamespace];
  for (const line of cleanLines(source)) {
    const trimmed = line.trim();
    const open = /^Namespace\s+([\w.]+)$/.exec(trimmed);
    if (open) {
      stack.push(open[1]);
      namespaces.push([project.rootNamespace, ...stack].filter(Boolean).join('.'));
      continue;
    }
    if (/^End Namespace$/.test(trimmed)) stack.pop();
  }
  return namespaces;
}

function checkNamespacesAndImports() {
  heading('Namespace resolution and cross-project imports');

  const knownNamespaces = new Set();
  const fileNamespaceMap = new Map();

  for (const project of projects) {
    const sources = walk(project.dir, (f) => f.endsWith('.vb'));
    for (const src of sources) {
      const text = fs.readFileSync(src, 'utf8');
      const namespaces = fileNamespaces(text, project);
      fileNamespaceMap.set(src, namespaces);
      for (const ns of namespaces) knownNamespaces.add(ns);
    }
  }

  for (const project of projects) {
    const sources = walk(project.dir, (f) => f.endsWith('.vb'));
    for (const src of sources) {
      const text = fs.readFileSync(src, 'utf8');
      const lines = cleanLines(text);
      lines.forEach((line, idx) => {
        const m = line.match(/^\s*Imports\s+([\w.]+)\s*$/);
        if (!m) return;
        const ns = m[1];
        // Only BrowserForWP namespaces are ours to resolve; System.* etc. are
        // the compiler's business.
        if (!ns.startsWith('BrowserForWP')) return;
        if (!knownNamespaces.has(ns)) {
          const hint = [...knownNamespaces]
            .filter((k) => k.startsWith(ns) || ns.startsWith(k))
            .sort();
          fail('imports', src,
            `Imports ${ns} does not match any namespace in this solution` +
            (hint.length ? ` (did you mean: ${hint.join(', ')}?)` : ''),
            idx + 1);
        }
      });
    }
  }
  if (findings.every((f) => f.check !== 'imports')) {
    ok(`${knownNamespaces.size} namespaces resolve; all BrowserForWP imports match one`);
  }
  return { knownNamespaces, fileNamespaceMap };
}

// ── 3. Project file vs disk parity ──────────────────────────────────────────
function checkProjectParity() {
  heading('Project file / disk parity');
  let anyBad = false;

  for (const project of projects) {
    const onDisk = walk(project.dir, (f) => f.endsWith('.vb') || f.endsWith('.resw') || f.endsWith('.xaml'))
      .map((f) => path.relative(project.dir, f).split(path.sep).join('/'))
      .sort();

    // No exceptions. An earlier version of this tool ignored App.xaml, which was
    // wrong: App.xaml belongs in <ApplicationDefinition>, and a missing one means
    // no generated entry point at all. Silencing that finding hid nothing here,
    // but the habit is what hides the next real defect.
    const declared = new Set(project.declaredItems);
    const missing = onDisk.filter((f) => !declared.has(f));

    // EVERY declared item must exist, not just source files. An earlier version
    // of this check only covered .vb/.xaml/.resw, and that hole let through a
    // `<Content Include="Polyfill\compat.js">` pointing at a file that was never
    // created -- a defect that fails the build with an unattributed "cannot find
    // the path specified" and then cascades into dozens of misleading errors.
    // Do not narrow this again.
    const phantom = project.declaredItems.filter((f) => {
      if (f.includes('*') || f.includes('?')) return false;   // MSBuild wildcard
      // A Link item is resolved by MSBuild relative to the project; the Include
      // path may legitimately point outside the project directory.
      return !fs.existsSync(path.join(project.dir, f));
    });

    for (const f of missing) {
      fail('project-parity', project.file, `file exists on disk but is not in <Compile Include>: ${f}`);
      anyBad = true;
    }
    for (const f of phantom) {
      fail('project-parity', project.file, `<Compile Include> names a file that does not exist: ${f}`);
      anyBad = true;
    }
  }
  if (!anyBad) ok('every .vb/.xaml/.resw is declared, and every declaration exists');
}

// ── 14. Project flavour (Windows Phone 8.1 vs Windows Store) ────────────────
// The first GUID in ProjectTypeGuids is the project FLAVOUR. It is read by the
// IDE's project system, not by MSBuild, and a Windows Phone 8.1 app may only
// resolve references to projects of the same flavour.
//
// This solution's libraries carried {BC8A1FFA-...}, the WINDOWS STORE flavour,
// while declaring TargetPlatformIdentifier WindowsPhoneApp. Each file therefore
// looked right on its own, and the guest build stayed green: MSBuild compares
// neither GUID. The IDE, which does, reported
//
//     The referenced component 'BrowserForWP.Core' could not be found.
//
// once per reference, naming projects that were present, correct and already
// built. Two things identify the message as the project system's and not the
// compiler's: it carries no diagnostic code (every BC and MSB failure does), and
// the English string occurs nowhere under MSBuild, the Windows Phone 8.1 SDK or
// the Windows Kits on the guest -- so no build here can reproduce or refute it.
// That is also why tools/vm-build.cmd never saw it.
//
// The authoritative values are the VS2013 templates on the guest:
//
//   ProjectTemplates\VisualBasic\Windows Phone 8.1\1033\
//       WindowsPhoneClassLibrary\ClassLibrary.vbproj      {76F1466A-...}
//   ProjectTemplates\VisualBasic\Windows Phone 8.1\1033\
//       WindowsPhoneBlankApplication\Application.vbproj    {76F1466A-...}
//   ProjectTemplates\VisualBasic\Windows Store\1033\
//       ClassLibrary_WindowsStoreApps\ClassLibrary.vbproj  {BC8A1FFA-...}
//
// The app template and the Windows Phone 8.1 class library template agree, which
// is the whole rule: a WindowsPhoneApp project uses the 76F1466A flavour.
//
// The .sln records a project type GUID per project as well, and it is checked
// too -- but it is a DIFFERENT field with a DIFFERENT rule, and the two must not
// be confused. VS writes the flavour GUID there when it creates the entry; the
// loader, however, resolves a project through the factory that GUID names, and
// in this VS2013 installation
//
//   HKLM\SOFTWARE[\WOW6432Node]\Microsoft\VisualStudio\12.0\Projects
//
// registers neither the phone ({76F1466A-...}) nor the Store ({BC8A1FFA-...})
// flavour, only {F184B08F-...}, the plain VB language GUID. A .sln naming an
// unregistered factory loads as an EMPTY solution: every project listed with
// "(unavailable)" beside it and a count of zero.
//
// Measured on the guest, one field apart, everything else identical:
//
//   devenv.com BrowserForWP.sln /build "Debug|ARM"
//     .sln says {76F1466A-...}  ->  Build: 0 succeeded or up-to-date, 0 failed,
//                                   0 skipped   (no project was loaded at all)
//     .sln says {F184B08F-...}  ->  Build: 6 succeeded, 0 failed
//
// MSBuild never reads the field, so tools/vm-build.cmd and all six
// configurations are green either way: it is invisible to the build and decisive
// for the IDE. That is exactly why it needs a check rather than a note.
//
// The paths get the same treatment. `BrowserForWP/BrowserForWP.vbproj` instead of
// `BrowserForWP\BrowserForWP.vbproj`, because on a host that is not Windows a
// backslash is an ordinary character in a file name: the tool asking for the
// project cannot find it even once the GUID resolves.

const WP81_FLAVOR_GUID = '{76F1466A-8B6D-4E39-A767-685A06062A39}';
const WINDOWS_STORE_FLAVOR_GUID = '{BC8A1FFA-BEE3-4634-8014-F334798102B3}';
const VB_PROJECT_TYPE_GUID = '{F184B08F-C81C-45F6-A57F-5ABD9991F28F}';

function checkProjectFlavor() {
  heading('Project flavour (WindowsPhoneApp vs Windows Store) and solution loadability');
  let anyBad = false;

  for (const project of projects) {
    const guids = project.projectTypeGuids.toUpperCase();
    const phoneTarget = project.targetPlatformIdentifier.includes('WindowsPhoneApp');

    if (guids.includes(WINDOWS_STORE_FLAVOR_GUID)) {
      const also = phoneTarget
        ? ' This project also declares TargetPlatformIdentifier WindowsPhoneApp, so the ' +
          'two statements contradict each other.'
        : '';
      fail('project-flavor', project.file,
        `ProjectTypeGuids carries the Windows Store flavour ${WINDOWS_STORE_FLAVOR_GUID}.` +
        also +
        ' The IDE reads the flavour, so a Windows Phone 8.1 app cannot resolve a reference ' +
        'to this project: its error list says the referenced component could not be found, ' +
        'naming a project that is present, correct and already built. No build catches this ' +
        `because MSBuild never reads ProjectTypeGuids. Use ${WP81_FLAVOR_GUID}, the value the ` +
        'Windows Phone 8.1 class library template writes.');
      anyBad = true;
    } else if (phoneTarget && !guids.includes(WP81_FLAVOR_GUID)) {
      fail('project-flavor', project.file,
        'declares TargetPlatformIdentifier WindowsPhoneApp but does not carry the Windows ' +
        `Phone 8.1 flavour GUID ${WP81_FLAVOR_GUID} in ProjectTypeGuids. The IDE keys on the ` +
        'flavour GUID, so the app cannot resolve this project as a reference.');
      anyBad = true;
    }
  }
  // The project file's own text, and this is the trap worth a check of its own: the
  // IDE's Windows Phone project factory locates ProjectTypeGuids by SCANNING THE
  // FILE AS TEXT, not by parsing it as XML. The first occurrence of the name is the
  // one that counts, so a mention inside a comment ahead of the real element shadows
  // it, the flavour reads as empty, and the IDE refuses the whole project with
  //
  //     The application for the project is not installed.
  //
  // -- a message with no diagnostic code, from a component MSBuild never runs, which
  // is why every configuration can stay green while the project cannot be opened.
  // This is not hypothetical: it is what BrowserForWP.Crypto did, and the file was
  // otherwise correct. Measured on the guest, one word apart: with the two mentions
  // taken out of that comment the project loads and builds; with one mention added
  // to a leading comment of a file that loaded a moment earlier, it stops.
  for (const project of projects) {
    const text = fs.readFileSync(project.file, 'utf8');
    const at = text.indexOf('<ProjectTypeGuids>');
    if (at < 0) continue;                       // absence is reported by the loop above
    const hidden = text.slice(0, at).indexOf('ProjectTypeGuids');
    if (hidden >= 0) {
      const lineOf = (n) => text.slice(0, n).split(/\r?\n/).length;
      fail('project-flavor', project.file,
        `the name ProjectTypeGuids appears at line ${lineOf(hidden)}, ahead of the ` +
        `<ProjectTypeGuids> element at line ${lineOf(at)}. The IDE finds that element by ` +
        'scanning this file as text, so the earlier mention is the one it reads: the ' +
        'flavour then looks empty and the IDE refuses the project with "The application ' +
        'for the project is not installed.", without a diagnostic code and without any ' +
        'build noticing. Name the property only where it is declared -- in a comment, ' +
        'say "the flavour property" instead.');
      anyBad = true;
    }
  }

  // The solution file, by the other rule. See the comment above: the .vbproj
  // carries the flavour the IDE's project system reads, the .sln carries the
  // factory the solution loader can actually resolve, and only the VB GUID is
  // registered here. A .sln that names the flavour GUID is not wrong-looking
  // copy -- it is an empty solution.
  const slnFile = path.join(ROOT, 'BrowserForWP.sln');
  if (fs.existsSync(slnFile)) {
    const byPath = new Map(projects.map((p) => [rel(p.file), p]));
    fs.readFileSync(slnFile, 'utf8').split(/\r?\n/).forEach((raw, idx) => {
      const m = /^Project\("\{([^"]+)\}"\) = "([^"]+)", "([^"]+)", "\{([^"]+)\}"/.exec(raw);
      if (!m) return;                                     // not a project line
      const declaredPath = m[3].replace(/\\/g, '/');
      if (!declaredPath.endsWith('.vbproj')) return;      // solution folders
      const project = byPath.get(declaredPath);
      if (project === undefined) return;                  // reported by group 3
      const slnGuid = `{${m[1].toUpperCase()}}`;
      if (slnGuid !== VB_PROJECT_TYPE_GUID) {
        fail('project-flavor', slnFile,
          `the solution declares ${m[2]} with project type ${slnGuid}. The .sln is loaded, not ` +
          `compiled, and no build reads this field: only ${VB_PROJECT_TYPE_GUID} (VB) is ` +
          `registered as a project factory in this VS2013 installation, while the flavour ` +
          `${WP81_FLAVOR_GUID} this project correctly carries in its .vbproj is not. A solution ` +
          'naming an unregistered factory opens with every project "(unavailable)" and a count ' +
          `of zero. Use ${VB_PROJECT_TYPE_GUID} here and leave the flavour to the .vbproj, ` +
          'which is where the IDE project system reads it.', idx + 1);
        anyBad = true;
      }
      if (m[3].includes('\\')) {
        fail('project-flavor', slnFile,
          `the solution points at ${m[3]}, with a backslash between the directory and the ` +
          'file. On Windows that is a separator; on any other host it is an ordinary ' +
          'character in a file name, so a tool that is not Visual Studio cannot find the ' +
          `project even with the right GUID. Write ${declaredPath}.`, idx + 1);
        anyBad = true;
      }
    });
  }
  if (!anyBad) {
    ok('every .vbproj carries the Windows Phone 8.1 flavour GUID');
    ok('the .sln names a registered project factory and reads on any host');
  }
}

// ── 4. Implements completeness ──────────────────────────────────────────────
function checkImplements() {
  heading('Implements completeness');
  let anyBad = false;

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const text = fs.readFileSync(src, 'utf8');
      const lines = cleanLines(text);

      lines.forEach((line, idx) => {
        const m = line.match(/^\s*(?:(?:Public|Private|Friend|Partial|NotInheritable)\s+)*Class\s+(\w+)[^\n]*\bImplements\s+([\w.,\s]+)$/);
        if (!m) return;
        const classLine = idx;
        const interfaces = m[2].split(',').map((s) => s.trim()).filter(Boolean);

        // Scan forward to this class's `End Class` to collect implemented members.
        const implemented = new Set();
        for (let i = classLine + 1; i < lines.length; i++) {
          if (/^\s*End Class\s*$/.test(lines[i])) break;
          const impl = lines[i].match(/\bImplements\s+([\w.]+)\s*$/);
          if (impl) implemented.add(impl[1]);
        }

        for (const iface of interfaces) {
          const short = iface.split('.').pop();
          const members = IMPLEMENTED_MEMBERS_BY_INTERFACE[short];
          if (!members) continue;      // not a framework interface we know
          for (const member of members) {
            if (!implemented.has(`${iface}.${member}`) && !implemented.has(member)) {
              fail('implements', src,
                `class ${m[1]} declares Implements ${iface} but never implements ${member}`,
                classLine + 1);
              anyBad = true;
            }
          }
        }
      });
    }
  }
  if (!anyBad) ok('every declared framework interface has its members implemented');
}

// Only interfaces with an exactly-known member set can be verified this way.
// Adding an entry is how you extend this check; guessing is worse than skipping.
const IMPLEMENTED_MEMBERS_BY_INTERFACE = {
  IDisposable: ['Dispose'],
};

// ── 6. Resource parity ─────────────────────────────────────────────────────
function checkResourceParity() {
  heading('Localization resource parity');

  const reswFiles = walk(ROOT, (f) => f.endsWith('Resources.resw'));
  if (reswFiles.length === 0) {
    fail('resw', '(project)', 'no Resources.resw files found');
    return;
  }

  const byLanguage = new Map();
  for (const file of reswFiles) {
    const lang = path.basename(path.dirname(file));
    const xml = fs.readFileSync(file, 'utf8');
    const keys = [...xml.matchAll(/<data\s+name="([^"]+)"/g)].map((m) => m[1]).sort();
    byLanguage.set(lang, { keys, file });
  }

  const languages = [...byLanguage.keys()].sort();
  const reference = byLanguage.get(languages[0]);

  let anyBad = false;
  for (const lang of languages.slice(1)) {
    const current = byLanguage.get(lang);
    const missing = reference.keys.filter((k) => !current.keys.includes(k));
    const extra = current.keys.filter((k) => !reference.keys.includes(k));
    for (const k of missing) {
      fail('resw', current.file, `key "${k}" present in ${languages[0]} but missing here`);
      anyBad = true;
    }
    for (const k of extra) {
      fail('resw', current.file, `key "${k}" is here but missing in ${languages[0]}`);
      anyBad = true;
    }
  }
  if (!anyBad) {
    ok(`${languages.join(' / ')} define identical key sets (${reference.keys.length} keys each)`);
  }

  // ── The map NAME, which is a different question from parity ──────────────
  //
  // Parity says the two languages agree. It said nothing about whether the code
  // asks for a map that exists, and that gap shipped: Localizer asked for
  // "Strings/Resources" while the built resources.pri holds the strings in a map
  // named after the .resw FILE, so every lookup threw ResourceMap Not Found and
  // the UI painted raw keys such as "EngineLabel".
  //
  // The map name is inferred from the file name here because the repository
  // cannot read resources.pri -- it is a build output, one per platform, and not
  // committed. The inference was MEASURED, not assumed: the maps in the built PRI
  // are exactly Resources, Files, Polyfill and Assets, on Debug/AnyCPU as well as
  // x86/Debug and ARM/Debug (docs/MAINTAINING.md, Round 18). If the build is ever
  // made to index these files under another name, this check fails and sends the
  // reader to that measurement.
  // Both languages are the SAME map, resolved per language qualifier, so the two
  // .resw files collapse to one name.
  const mapNames = [...new Set(reswFiles.map((f) => path.basename(f, '.resw')))];
  const localizerPath = path.join(ROOT, 'BrowserForWP.Localization', 'Localizer.vb');
  const localizer = fs.readFileSync(localizerPath, 'utf8');
  const mapConstant = localizer.match(/Private Const ResourceMap As String = "([^"]+)"/);
  if (!mapConstant) {
    fail('resw', 'BrowserForWP.Localization/Localizer.vb',
         'no ResourceMap constant found; the resource map name is not readable from this file');
  } else if (!mapNames.includes(mapConstant[1])) {
    fail('resw', 'BrowserForWP.Localization/Localizer.vb',
         `asks for map "${mapConstant[1]}", but the .resw files become the map(s) ` +
         mapNames.map((m) => `"${m}"`).join(', '));
  } else {
    ok(`the map the code asks for ("${mapConstant[1]}") is the map the .resw files become`);
  }
}

// ── 7. XAML handler wiring ─────────────────────────────────────────────────
function checkXamlHandlers() {
  heading('XAML event handler wiring');
  let anyBad = false;

  const xamlFiles = walk(ROOT, (f) => f.endsWith('.xaml'));
  for (const xaml of xamlFiles) {
    const codeBehind = `${xaml}.vb`;
    if (!fs.existsSync(codeBehind)) continue;

    const xamlText = fs.readFileSync(xaml, 'utf8');
    const handlers = [...xamlText.matchAll(/\b(?:Click|Tapped|SelectionChanged|Checked|Unchecked|Loaded|TextChanged|KeyDown|LostFocus|GotFocus|SizeChanged|ValueChanged|Completed|Holding|PointerPressed|PointerReleased)="([A-Za-z_]\w*)"/g)]
      .map((m) => m[1]);
    if (handlers.length === 0) continue;

    const vbText = fs.readFileSync(codeBehind, 'utf8');
    for (const handler of new Set(handlers)) {
      const declared = new RegExp(
        String.raw`^\s*(?:(?:Public|Private|Protected|Friend|Shared|Async)\s+)*Sub\s+${handler}\s*\(`,
        'm');
      if (!declared.test(vbText)) {
        fail('xaml-handler', xaml, `event handler "${handler}" is referenced but not defined in ${path.basename(codeBehind)}`);
        anyBad = true;
      }
    }
  }
  if (!anyBad) ok('every event handler named in XAML is defined in its code-behind');
}

// ── 8. XAML single root child ──────────────────────────────────────────────
// A ContentControl (Page, UserControl, Window) can have exactly ONE Content.
// Two direct children is the error "The property 'Content' can only be set
// once", and the damage is much larger than the message suggests: the XAML
// compiler rejects the file, so the generated .g.vb is never produced, so every
// x:Name field becomes "not declared" in the code-behind and the project loses
// its generated entry point. One structural mistake, dozens of errors — which is
// exactly why it is worth a dedicated check.
function countRootChildren(xml, rootTag) {
  const tagPattern = /<(\/?)([A-Za-z][\w.:]*)((?:"[^"]*"|'[^']*'|[^>])*?)(\/?)>/g;
  let depth = 0;
  let children = 0;
  let match;
  let seenRoot = false;
  while ((match = tagPattern.exec(xml)) !== null) {
    const isClosing = match[1] === '/';
    const name = match[2];
    const isSelfClosing = match[4] === '/';

    if (isClosing) { depth--; continue; }
    if (!seenRoot) { seenRoot = true; depth = 1; continue; }   // the root itself
    if (isSelfClosing) {
      if (depth === 1) children++;
      continue;
    }
    if (depth === 1) children++;
    depth++;
  }
  return children;
}

function checkXamlRoot() {
  heading('XAML single root child');
  let anyBad = false;

  for (const xaml of walk(ROOT, (f) => f.endsWith('.xaml'))) {
    const body = fs.readFileSync(xaml, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
    const root = /<([A-Za-z][\w.:]*)/.exec(body);
    if (!root) continue;

    const children = countRootChildren(body, root[1]);
    if (children > 1) {
      fail('xaml-root', xaml,
        `root <${root[1]}> has ${children} direct children; a ContentControl can hold only one. ` +
        'Wrap them in a single container — otherwise the XAML compiler rejects the file, the ' +
        'generated .g.vb is never created, and every x:Name field becomes "not declared".');
      anyBad = true;
    }
  }
  if (!anyBad) ok('every XAML file has exactly one root child');
}

// ── 9. XAML theme-resource keys ───────────────────────────────────────────
// A `{ThemeResource X}` is resolved when the page LOADS, not when it compiles,
// so an unknown X is not an error that any build here can report. Nothing in the
// toolchain cross-checks the key against the platform. The XAML designer does,
// and names it -- "The resource "X" could not be resolved" -- but a design-time
// error list is a per-machine window and not part of the build.
//
// That is exactly how MainPage.xaml came to ask for `TextControlBackground`, a
// UWP-era name Windows Phone 8.1 does not define, and ship. docs/MAINTAINING.md's
// Round 4 record had put that key there *deliberately*, because a session in
// which WMC9999 came and went made the swap look like the thing that silenced it.
// It was not: the diagnostic is independent of the key. It was present in 12 of 12
// recorded probe runs *with* that key in the file, and it is still present now
// that the key is gone. So the key needed an oracle of its own, not a diagnostic
// to interpret.
//
// The oracle is tools/wp81-theme-keys.txt, the 523 keys Windows Phone 8.1 itself
// defines, regenerated by tools/wp81-theme-keys.sh. It is the PHONE's dictionary
// and not the desktop's: Windows 8.1 ships a themeresources.xaml and a
// generic.xaml too, the two sets differ, and a desktop-only key compiles,
// packages, and then cannot resolve on the handset.
//
// `{StaticResource X}` is deliberately not checked: the markup compiler resolves
// it at compile time, so a missing key is already a build error, not a silent one.

const WP81_THEME_KEYS_FILE = path.join(ROOT, 'tools', 'wp81-theme-keys.txt');

function loadWp81ThemeKeys() {
  if (!fs.existsSync(WP81_THEME_KEYS_FILE)) return null;
  const keys = new Set();
  for (const raw of fs.readFileSync(WP81_THEME_KEYS_FILE, 'utf8').split(/\r?\n/)) {
    const key = raw.trim();
    if (key !== '' && !key.startsWith('#')) keys.add(key);
  }
  return keys.size > 0 ? keys : null;
}

function checkXamlThemeResources() {
  heading('XAML theme-resource keys (WP8.1)');
  let anyBad = false;

  const platform = loadWp81ThemeKeys();
  if (platform === null) {
    fail('theme-resource', WP81_THEME_KEYS_FILE,
      'the WP8.1 theme-resource key list is missing or empty, so this group cannot ' +
      'run at all. Regenerate it on the host with:  bash tools/wp81-theme-keys.sh');
    return;
  }

  for (const xaml of walk(ROOT, (f) => f.endsWith('.xaml'))) {
    // A key the app declares itself is legal, ThemeResource or not.
    const body = fs.readFileSync(xaml, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
    const appDefined = new Set();
    for (const m of body.matchAll(/x:Key="([^"]+)"/g)) appDefined.add(m[1]);

    body.split(/\r?\n/).forEach((raw, idx) => {
      for (const m of raw.matchAll(/\{ThemeResource\s+([A-Za-z_][\w.]*)\s*\}/g)) {
        const key = m[1];
        if (platform.has(key) || appDefined.has(key)) continue;
        fail('theme-resource', xaml,
          `{ThemeResource ${key}} -- Windows Phone 8.1 does not define this key. ` +
          'ThemeResource is resolved at page load, so no build here can reject it and the ' +
          'reference silently cannot resolve on the handset. Use a key from ' +
          'tools/wp81-theme-keys.txt, or declare your own in App.xaml. A Windows 8.1 or ' +
          'UWP name is a different set and will not resolve on this platform.',
          idx + 1);
        anyBad = true;
      }
    });
  }
  if (!anyBad) ok('every {ThemeResource} key exists in the WP8.1 dictionaries');
}

// ── 10. Char-range literals ───────────────────────────────────────────────
// ChrW and Chr return a Char, which is 16 bits. A supplementary-plane code point
// such as U+1F512 (128274) does not fit, and the compiler says so in a way that
// is easy to misread: "Value '128274' cannot be converted to 'Char'". The fix is
// Char.ConvertFromUtf32, which returns a String.
function checkCharLiterals() {
  heading('Char-range literals');
  let anyBad = false;

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = cleanLines(fs.readFileSync(src, 'utf8'));
      lines.forEach((line, idx) => {
        const pattern = /\bChrW?\s*\(\s*(?:&H([0-9A-Fa-f]+)|(\d+))\s*\)/g;
        let match;
        while ((match = pattern.exec(line)) !== null) {
          const value = match[1] ? parseInt(match[1], 16) : parseInt(match[2], 10);
          if (value > 0xffff) {
            fail('char-range', src,
              `${match[0]} is ${value}, above &HFFFF. A Char is 16 bits, so this cannot compile. ` +
              'Use Char.ConvertFromUtf32(...) — but check the handset fonts cover the glyph.',
              idx + 1);
            anyBad = true;
          }
        }
      });
    }
  }
  if (!anyBad) ok('no ChrW/Chr call exceeds the 16-bit range of a Char');
}

// ── 11. VB 12 syntax ──────────────────────────────────────────────────────
// Visual Studio 2013 ships VB 12. Implicit line continuation AFTER a '.'
// arrived in VB 14 (VS2015), so the JavaScript-style fluent chain
//
//     Dim w = New TlsWriter().
//         U8(1).
//         U16(2).
//         ToArray()
//
// is not merely unconventional here, it does not parse. The damage is worse
// than one bad line: the compiler reports BC30203 on the trailing dot and then
// "<name> is not declared" for every method in the chain, so a 4-line mistake
// produces twenty errors that all name the wrong thing. Rewrite as a `With`
// block, which reads the same and compiles.
function checkVb12Syntax() {
  heading('VB 12 syntax (no VS2015-only constructs)');
  let anyBad = false;

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = cleanLines(fs.readFileSync(src, 'utf8'));
      lines.forEach((line, idx) => {
        // A code line ending in '.' is a chain continued on the next line.
        // Comments and string contents are already gone, so a sentence in a
        // comment or a '.' inside a literal cannot trigger this.
        if (/\.\s*$/.test(line)) {
          fail('vb12', src,
            'line ends with ".", so the next line continues a method chain. VB 12 ' +
            '(VS2013) has no implicit line continuation after a period; this is BC30203 ' +
            'plus a phantom "not declared" for every following method. Use a With block ' +
            'and one call per line.',
            idx + 1);
          anyBad = true;
        }
      });
    }
  }
  if (!anyBad) ok('no leading-dot method chains (VB 12 cannot continue a line after ".")');
}

// ── 12. Fine .NET-for-Windows-Store-apps profile hazards ──────────────────
// The WP8.1 app and library projects compile against the .NET for Windows
// Store apps profile, not full .NET. A handful of members that are unconditional
// on the desktop simply are not there, and the compiler error names the member
// as missing from a type that obviously has it, which reads like a typo.
const PROFILE_HAZARDS = [
  [/\bEncoding\.(ASCII|UTF7|UTF32)\b/,
    'not in the Store profile (it needs ASCIIEncoding/UTF7Encoding, which the ' +
    'profile removes). Encode ASCII explicitly, or use Encoding.UTF8 after ' +
    'validating the input is ASCII.'],
  [/\bRegexOptions\.Compiled\b/,
    'RegexOptions.Compiled is not supported in the Store profile.'],
  [/\b(CryptographicException|RNGCryptoServiceProvider|RandomNumberGenerator|SHA256Managed|HMACSHA256|SHA256|HMACSHA1)\b/,
    'System.Security.Cryptography is not in the Store profile. Use ' +
    'Windows.Security.Cryptography.Core through WinRtCrypto, and InvalidOperationException ' +
    'or ArgumentException for failures.'],
  [/\bCryptographicEngine\.Verify\b/,
    'the WinRT type exposes VerifySignature / VerifySignatureWithHashInput, not Verify.'],
  [/\bControlChars\b/,
    'Microsoft.VisualBasic.ControlChars is not in the Store profile (BC30451), even ' +
    'though Microsoft.VisualBasic.Strings (AscW, ChrW) is. Test whitespace with ' +
    'Char.IsWhiteSpace, or use the numeric Char code.'],
  [/\bFontStyles\b/,
    'System.Windows.FontStyles is WPF. The WinRT profile has no FontStyles helper at ' +
    'all (BC30451): XAML markup resolves FontStyle="Italic" through the enum, and code ' +
    'must name Windows.UI.Text.FontStyle.Italic / .Normal. Found the hard way by the ' +
    'guest build, one round after this group was written.'],
];

function checkProfileHazards() {
  heading('NETFX_CORE profile hazards');
  let anyBad = false;

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = cleanLines(fs.readFileSync(src, 'utf8'));
      lines.forEach((line, idx) => {
        for (const [pattern, message] of PROFILE_HAZARDS) {
          if (pattern.test(line)) {
            fail('profile', src, `${line.trim()} — ${message}`, idx + 1);
            anyBad = true;
          }
        }
      });
    }
  }
  if (!anyBad) ok('no use of APIs missing from the .NET for Windows Store apps profile');
}

// ── 13. Comment hazards ──────────────────────────────────────────────────
// Three ways a comment can break a build.
//
//  * '--' is illegal inside an XML comment. The project file then fails to
//    load at all (MSB4025), and in a solution build the symptom is the far less
//    informative MSB4078 "project file is not supported by MSBuild". The
//    temptation is to draw a rule line out of dashes in a header comment.
//
//  * An XML doc comment is parsed as XML. `<0..2^24-1>`, copied straight from
//    an RFC's grammar, is an invalid tag name and the whole doc comment is
//    discarded with a warning (BC42304). Escape it as &lt;...&gt;.
//
//  * A PLAIN APOSTROPHE COMMENT INSIDE A ''' DOC BLOCK. A doc comment is a run
//    of `'''` lines; a line starting with a single `'` ends the block, so the
//    `''' </summary>` that follows becomes a SECOND doc comment whose closing tag
//    never matched anything. VB reports BC42301 plus BC42304 and throws the
//    documentation away. It is a warning, so it does not fail a build -- and that
//    is exactly the harm: two cheap warnings that train a reader to skim the
//    warning list, which is where the next real one will be. Found twice in one
//    round (SealedChannel.vb, then RemoteProtocol.vb), the second time by the
//    guest build rather than by this group.
const XML_ISH = ['.vbproj', '.xaml', '.appxmanifest', '.resw', '.sln', '.xml'];

function checkCommentHazards() {
  heading('Comment hazards (XML comments, doc comments)');
  let anyBad = false;

  // Doubled dashes inside an XML comment, in any XML-ish file.
  for (const file of walk(ROOT, (f) => XML_ISH.some((e) => f.endsWith(e)))) {
    const source = fs.readFileSync(file, 'utf8');
    const lines = source.split(/\r?\n/);
    let inComment = false;
    lines.forEach((raw, idx) => {
      let cursor = 0;
      for (;;) {
        if (!inComment) {
          const open = raw.indexOf('<!--', cursor);
          if (open === -1) break;
          inComment = true;
          cursor = open + 4;
        }
        const close = raw.indexOf('-->', cursor);
        const bodyEnd = close === -1 ? raw.length : close;
        if (raw.slice(cursor, bodyEnd).includes('--')) {
          fail('comment', file,
            'XML comments cannot contain "--". MSBuild refuses to load the whole ' +
            'project (MSB4025); from a solution build this surfaces as the ' +
            'unrelated-looking MSB4078 "project file is not supported by MSBuild".',
            idx + 1);
          anyBad = true;
        }
        if (close === -1) break;
        inComment = false;
        cursor = close + 3;
      }
    });
  }

  // Inside a ''' doc comment, every '<' must open or close a tag the doc-comment
  // parser knows, the tags must nest, and they must balance by the end of the
  // block.
  //
  // The first version of this check only looked for '<' followed by a DIGIT, to
  // catch `<0..2^24-1>` copied out of an RFC. That is one way to reach BC42304,
  // not the rule, and the narrow version let a second way through: prose that
  // mentions a closing tag, like
  //
  //     ''' block. It leaves the </summary> below attached to nothing.
  //
  // closes the element early, so the block's OWN closing tag has nothing to close
  // and the compiler discards the whole comment. That was written into
  // RemoteProtocol.vb by the very edit that fixed the apostrophe defect -- the
  // third time in one round that a doc-comment mistake surfaced as a warning
  // nobody was reading.
  // `paramref` and `typeparamref` belong here and were missing from the first
  // draft: Tls13Client.vb uses `<paramref name="count"/>` in a summary, the
  // compiler accepts it, and the check reported it as an unknown tag. The guest
  // build is the arbiter for a tag list too, not the fact that a list looks
  // complete while it is being written.
  const DOC_TAGS = new Set(['summary', 'remarks', 'returns', 'param', 'paramref',
    'typeparam', 'typeparamref', 'value', 'exception', 'example', 'code', 'c', 'see',
    'seealso', 'para', 'list', 'listheader', 'item', 'term', 'description',
    'include', 'permission', 'inheritdoc', 'br']);

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = fs.readFileSync(src, 'utf8').split(/\r?\n/);
      let stack = [];
      let openedAt = 0;

      const flush = () => {
        if (stack.length > 0) {
          fail('comment', src,
            `a ''' doc block opens <${stack[0]}> and never closes it: BC42304 ` +
            'discards the whole comment. Every tag opened here needs its closing ' +
            'tag, written as XML.', openedAt);
          anyBad = true;
        }
        stack = [];
        openedAt = 0;
      };

      lines.forEach((raw, idx) => {
        const text = raw.trim();
        if (!text.startsWith("'''")) {
          // A blank line, or a plain ' comment (reported by the check above), does
          // not end the block for this purpose. Anything else does.
          if (text !== '' && !text.startsWith("'")) flush();
          return;
        }

        const body = text.slice(3);
        for (let at = body.indexOf('<'); at !== -1; at = body.indexOf('<', at + 1)) {
          const match = /^<(\/?)([A-Za-z][A-Za-z0-9]*)[^>]*?(\/?)>/.exec(body.slice(at));
          const name = match ? match[2].toLowerCase() : '';
          if (!match || !DOC_TAGS.has(name)) {
            fail('comment', src,
              'a doc comment is parsed as XML, and this "<" does not open or close ' +
              `a tag the parser knows (it accepts ${[...DOC_TAGS].join(', ')}). ` +
              'Escape a literal one as &lt;...&gt;, or the whole comment is ' +
              'discarded with BC42304.',
              idx + 1);
            anyBad = true;
            continue;
          }
          if (match[3] === '/') continue;                  // <br/> closes nothing
          if (match[1] === '/') {
            if (stack.length === 0 || stack[stack.length - 1] !== name) {
              fail('comment', src,
                `</${name}> here closes nothing: the doc block has ` +
                (stack.length === 0
                  ? 'no open tag'
                  : `<${stack[stack.length - 1]}> open`) +
                '. A doc comment is parsed as XML, so a closing tag written in ' +
                "prose ends the element early and the block's own closing tag is " +
                'then unmatched -- BC42304, and the documentation is discarded.',
                idx + 1);
              anyBad = true;
              continue;
            }
            stack.pop();
          } else {
            if (stack.length === 0) openedAt = idx + 1;
            stack.push(name);
          }
        }
      });
      flush();
    }
  }

  // A plain `'` comment line stranded inside a `'''` doc block.
  //
  // The trigger is deliberately narrow, because the obvious rule ("a ' line
  // inside an open doc block") also fires on a perfectly ordinary trailing
  // comment after a finished doc comment, which would make this group noisy. So
  // a line is flagged only when a `'''` line is the nearest comment line BEFORE
  // it AND another comment line follows it: the comment run was clearly meant to
  // be one block, and the single apostrophe splits it. When code follows instead,
  // the doc comment above had already ended and the line is just a comment.
  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = fs.readFileSync(src, 'utf8').split(/\r?\n/);
      const significant = (from, step) => {
        for (let i = from; i >= 0 && i < lines.length; i += step) {
          const text = lines[i].trim();
          if (text !== '') return text;
        }
        return '';
      };
      lines.forEach((raw, idx) => {
        const text = raw.trim();
        if (!text.startsWith("'") || text.startsWith("'''")) return;
        const before = significant(idx - 1, -1);
        const after = significant(idx + 1, 1);
        if (before.startsWith("'''") && after.startsWith("'")) {
          fail('comment', src,
            "a plain ' comment inside a ''' doc block: the doc comment ends here, so " +
            "the closing tag below belongs to a second doc comment that never " +
            "opened. BC42301 plus BC42304, and the documentation is discarded. Use " +
            "''' on every line of the block.",
            idx + 1);
          anyBad = true;
        }
      });
    }
  }

  if (!anyBad) {
    ok('no "--" in XML comments, no unmatched or unknown tag in a doc comment, ' +
      "no plain comment inside a ''' block");
  }
}

// ── 15. Privileged access: the APIs and capabilities Law 4 closes ────────
// Two families, one question — is anything here asking for privilege the
// platform cannot grant?
//
//  * APIs that would try to leave the container: a JIT, a helper process, a
//    hand-loaded native binary, writable+executable pages.
//  * Capabilities that would ASK for that privilege. Privilege is declared at
//    package time, so the manifest is the only place such a request could
//    appear, and this group refuses it here rather than at certification.
//
// `tools/proto/sandbox-escape.mjs` is the decision record behind this group, and
// it is not a duplicate: that one pins the EXACT capability set the package
// declares — so adding even a benign capability is a finding there — and asserts
// that the reasoning is still written down. This one is the cheap guard that
// runs on every `.vb` edit and on any manifest change.
//
// Comments and string literals are stripped by cleanLines before any of this, on
// purpose: an earlier checker in this repository read a comment and so forbade
// documenting the very rule it enforced. Do not "fix" that by scanning raw text.
const PRIVILEGED_APIS = [
  [/\bReflection\.Emit\b/,
    'System.Reflection.Emit is not in the .NET for Windows Store apps profile ' +
    '(BC30002/BC30451), and there is no JIT to reach for anyway: an AppContainer ' +
    'denies writable+executable pages. See docs/ARCHITECTURE.md Law 4.'],
  [/\b(CreateProcess|CreateProcessAsUser|CreateProcessWithLogonW|CreateProcessWithTokenW)\b/,
    'an AppContainer app cannot create a process, and a child of one is created ' +
    'in the same container, so the helper would be sandboxed too. A P/Invoke to ' +
    'this cannot succeed. See docs/ARCHITECTURE.md Law 4.'],
  [/\bDiagnostics\.Process\b|\bProcess\.Start\b/,
    'System.Diagnostics.Process is not in the Store profile, and an AppContainer ' +
    'cannot start a process. See docs/ARCHITECTURE.md Law 4.'],
  [/\bShellExecute(Ex)?\b/,
    'shell activation is not reachable from an AppContainer. Use ' +
    'Windows.System.Launcher, which goes through the shell contracts.'],
  [/\bLoadLibrary(Ex)?\b/,
    'an AppContainer cannot load an arbitrary native binary. That is Law 1\'s ' +
    'reason for there being no Chromium or Gecko port for this OS.'],
  [/\bVirtualAlloc(Ex)?\b|\bVirtualProtect(Ex)?\b/,
    'writable+executable memory is exactly what an AppContainer refuses, so this ' +
    'is the hand-rolled route to a JIT and it cannot work here. Law 4.'],
];

// Forbidden by name rather than by allow-list. The allow-list itself lives in
// tools/proto/sandbox-escape.mjs, where a new capability is meant to be noticed.
const PRIVILEGED_CAPABILITIES = [
  ['runFullTrust',
    'a restricted Windows 10 capability. It is not grantable to a WP8.1 package ' +
    'and it requires a Microsoft signature, so requesting it is not a fix for ' +
    'Law 4.'],
  ['codeGeneration',
    'the Windows 10 capability that lifts the dynamic-code ban. It does not exist ' +
    'for an 8.1 package. Law 4.'],
  ['allowElevation',
    'requests elevation for a desktop-bridge process; no such thing exists in the ' +
    'WP8.1 manifest schema. Law 4.'],
  ['packageManagement',
    'installing or managing packages needs a privilege this platform does not ' +
    'grant an app.'],
];

function checkPrivilegedAccess() {
  heading('Privileged access (JIT, process creation, full-trust capabilities)');
  let anyBad = false;

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = cleanLines(fs.readFileSync(src, 'utf8'));
      lines.forEach((line, idx) => {
        for (const [pattern, message] of PRIVILEGED_APIS) {
          if (pattern.test(line)) {
            fail('privilege', src, `${line.trim()} — ${message}`, idx + 1);
            anyBad = true;
          }
        }
      });
    }
  }

  for (const manifest of walk(ROOT, (f) => f.endsWith('.appxmanifest'))) {
    fs.readFileSync(manifest, 'utf8').split(/\r?\n/).forEach((line, idx) => {
      for (const [name, message] of PRIVILEGED_CAPABILITIES) {
        const re = new RegExp(`<(?:[a-zA-Z]+:)?Capability\\s+Name=["']${name}["']`);
        if (re.test(line)) {
          fail('privilege', manifest,
            `<Capability Name="${name}" /> — ${message}`, idx + 1);
          anyBad = true;
        }
      }
    });
  }

  if (!anyBad) ok('no JIT, no process creation, no privileged capability anywhere');
}

// ── 16. Capability requirements: what the code needs the manifest to declare ─
// The other direction from group 15. That one refuses privilege the code must
// never ask for; this one refuses a RUNTIME failure — an API whose capability is
// missing throws UnauthorizedAccessException on the device, months after the
// build was green. The manifest is the only place a capability can live, so this
// checks the two files against each other.
//
// It is deliberately ONE-DIRECTIONAL, and the reason is specific to this product.
// The reverse rule ("every declared capability must be used") is wrong here: a
// browser declares capabilities that no line of its own code references, because
// it is hosted page content that asks — a page calling navigator.geolocation
// needs THIS app to have declared `location`. A checker enforcing the reverse
// rule would delete working permissions from a browser. So unused capabilities
// are reported in the passing line, never as findings.
//
// The requirement table is the platform's capability list, not a guess: each
// entry names the API family that requires it. Exactly one of them fires on this
// codebase today.
const CAPABILITY_REQUIREMENTS = [
  { capability: 'internetClient',
    api: /\b(StreamSocket|WebView|NetworkInformation|HttpClient|DatagramSocket)\b/,
    note: 'Outbound network access. `internetClientServer` is a superset and '
      + 'satisfies this, which is what this package declares.' },
  { capability: 'location',
    api: /\b(Geolocator|Geoposition|Geocoordinate|Geofence|GeofenceMonitor|CivicAddressResolver)\b/,
    note: 'Geolocation, including the location a hosted page asks for through the '
      + 'WebView.' },
  { capability: 'webcam',
    api: /\b(MediaCapture|MediaCaptureInitializationSettings|CameraCaptureUI|CameraStream)\b/,
    note: 'Video capture. `MediaCapture` spans camera and microphone, so it is '
      + 'listed under both capabilities: which one is needed depends on the '
      + 'initialisation profile.' },
  { capability: 'microphone',
    api: /\b(MediaCapture|MediaCaptureInitializationSettings|SpeechRecognizer|AudioCapture|AudioGraph)\b/,
    note: 'Audio capture and speech.' },
  { capability: 'proximity',
    api: /\b(ProximityDevice|PeerFinder|PeerInformation|ProximityMessage)\b/,
    note: 'NFC and near-field peer discovery.' },
  { capability: 'contacts',
    api: /\b(ContactManager|ContactStore|ContactPicker|ContactList|KnownContactField)\b/,
    note: 'Reading or picking contacts.' },
  { capability: 'appointments',
    api: /\b(AppointmentStore|AppointmentManager|AppointmentCalendar|AppointmentPicker)\b/,
    note: 'Reading or writing the calendar.' },
  { capability: 'phoneCall',
    api: /\b(PhoneCallManager|PhoneCallStore|PhoneLine)\b/,
    note: 'Placing calls or reading the call state.' },
  { capability: 'userAccountInformation',
    api: /\bUserInformation\b/,
    note: 'Reading the user name and picture. Also needs a company account at '
      + 'submission time, so it cannot be added quietly.' },
  { capability: 'musicLibrary',
    api: /\bMusicLibrary\b/,
    note: 'The music library. KnownFolders members are checked by name because '
      + 'KnownFolders alone names no particular library.' },
  { capability: 'photosLibrary',
    api: /\bPicturesLibrary\b/,
    note: 'The pictures library.' },
  { capability: 'videosLibrary',
    api: /\bVideosLibrary\b/,
    note: 'The videos library.' },
];

// Declaring a superset satisfies a subset. Only the one relation this product
// relies on is encoded, plus the one the platform documents; anything more
// speculative is left out rather than guessed.
const CAPABILITY_IMPLIES = {
  internetClientServer: ['internetClient'],
  enterpriseAuthentication: ['internetClient'],
};

function checkCapabilityRequirements() {
  heading('Capability requirements (code vs Package.appxmanifest)');

  const declared = new Set();
  for (const manifest of walk(ROOT, (f) => f.endsWith('.appxmanifest'))) {
    const source = fs.readFileSync(manifest, 'utf8');
    for (const m of source.matchAll(/<Capability\s+Name="([^"]+)"\s*\/>/g)) {
      declared.add(m[1]);
    }
  }
  const satisfied = new Set(declared);
  for (const c of declared) {
    for (const implied of (CAPABILITY_IMPLIES[c] || [])) satisfied.add(implied);
  }

  // First site per capability, so a missing capability is one finding rather
  // than one per call site.
  const needed = new Map();
  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      cleanLines(fs.readFileSync(src, 'utf8')).forEach((line, idx) => {
        for (const req of CAPABILITY_REQUIREMENTS) {
          if (!req.api.test(line)) continue;
          if (!needed.has(req.capability)) {
            needed.set(req.capability, { src, line: idx + 1, text: line.trim() });
          }
        }
      });
    }
  }

  let anyBad = false;
  for (const [capability, site] of needed) {
    if (satisfied.has(capability)) continue;
    fail('capability', site.src,
      `${site.text} — this needs the "${capability}" capability and no `
      + `Package.appxmanifest declares it. ${CAPABILITY_REQUIREMENTS.find(
        (r) => r.capability === capability).note}`,
      site.line);
    anyBad = true;
  }

  if (!anyBad) {
    const unused = [...declared].filter((c) => !needed.has(c)
      && !(CAPABILITY_IMPLIES[c] || []).some((i) => needed.has(i)));
    ok(`every capability the code needs is declared (${[...satisfied].sort().join(', ')}); `
      + 'declared but not referenced by this code: '
      + (unused.length ? unused.sort().join(', ') : 'none'));
  }
}

// ── 17. Reserved words used as names ──────────────────────────────────────
// A VB keyword is not an identifier, and the compiler's reaction to finding one
// is not "that name is a keyword". `Dim next As UInteger = _outSequence + 1UI`
// produced BC30201 "expression expected" on that line and then eleven BC30451
// "'header' is not declared" on the lines after it -- every one of them naming
// something that plainly IS declared. The guest build caught it; this file did
// not, which is the reason this group exists. Nothing here is exotic: `next`,
// `error`, `step`, `date`, `set` and `in` are all words a person reaches for.
//
// THE LIST IS MEASURED, NOT QUOTED. It was first written from the language
// reference, and the reference disagrees with the compiler: `Out` is in the
// reference's reserved list and `Dim out(31) As Byte` compiles (it is in
// BrowserForWP.Crypto/X25519.vb, and that project builds in all six
// configurations). Twelve more words looked legal for a worse reason -- see
// below. So the list below comes from tools/keyword-probe, which compiles one
// `Dim <word> As Integer` per candidate and reads the compiler's answer.
//
// MEASURED 2026-09-29 with vbc 12.0.40629.0: 117 candidates, 113 refused with
// BC30183 ("keyword not valid as an identifier"; `rem` gives BC30203 instead,
// because Rem starts a comment), and FOUR ACCEPTED:
//
//     out   async   await   custom
//
// Those four are exactly the trap this comment exists for. `async` and `await`
// are contextual keywords in VB and legal as names here; `custom` is a modifier
// only inside a property; `out` is interop-era and simply not reserved. Adding
// any of them would flag compiling code, which is how a build gate stops being
// read.
//
// AND A SECOND TRAP, IN THE OTHER DIRECTION. The first run of the probe was one
// file, and `mustinherit` through `custom` came back "legal" -- because vbc 12
// is pre-Roslyn and GIVES UP after about a hundred errors, silently. They had
// never been compiled. The probe now batches and each batch ends with a sentinel
// whose refusal proves the batch reached its end. The first list would have
// missed fourteen real keywords.
const VB_RESERVED_AS_NAME = new Set([
  'next', 'error', 'date', 'step', 'to', 'in', 'of', 'on', 'not', 'then', 'is', 'as',
  'me', 'new', 'single', 'static', 'string', 'object', 'char', 'decimal', 'boolean',
  'byte', 'short', 'long', 'integer', 'double', 'set', 'get', 'if', 'for', 'do', 'or',
  'and', 'using', 'rem', 'lib', 'let', 'stop', 'resume', 'when', 'while',
  'select', 'shared', 'partial', 'private', 'public', 'friend', 'protected', 'property',
  'event', 'exit', 'loop', 'each', 'option', 'module', 'class', 'structure', 'interface',
  'imports', 'inherits', 'implements', 'handles', 'return', 'continue', 'try', 'catch',
  'finally', 'throw', 'call', 'typeof', 'with', 'xor', 'mod', 'like', 'alias', 'declare',
  'delegate', 'enum', 'erase', 'goto', 'gosub', 'overloads', 'overridable', 'overrides',
  'paramarray', 'raiseevent', 'readonly', 'redim', 'shadows', 'synclock', 'true', 'false',
  'nothing', 'wend', 'writeonly', 'widening', 'narrowing', 'byval', 'byref', 'optional',
  'default', 'mustinherit', 'mustoverride', 'notinheritable', 'notoverridable',
  'directcast', 'trycast', 'gettype', 'addressof', 'else', 'elseif', 'end', 'endif',
]);

// The names a declaration on this line introduces. Only DECLARATION sites are
// read, so a keyword used correctly in an expression (every `For ... Next` in
// the repository, every `Not x`, every `If ... Then`) is not a finding -- a
// group that flagged those would fire on essentially every file.
function declaredNames(line) {
  const names = [];
  const patterns = [
    [/\b(?:Dim|Static|Const|ReDim)\s+(\w+)/g, new Set()],
    [/\bFor\s+Each\s+(\w+)/g, new Set()],
    [/\bFor\s+(\w+)\s*(?:As|=)/g, new Set()],
    [/\bCatch\s+(\w+)/g, new Set()],
    [/\bUsing\s+(\w+)/g, new Set()],
    [/\b(?:ByVal|ByRef|Optional)\s+(\w+)/g, new Set()],
    // `Sub New` is a constructor, not a name called New.
    [/\b(?:Function|Sub)\s+(?!New\b)(\w+)/g, new Set()],
    [/\b(?:Property|Class|Structure|Interface|Enum|Module|Event)\s+(\w+)/g, new Set()],
  ];
  for (const [pattern, skip] of patterns) {
    const re = new RegExp(pattern.source, pattern.flags);
    let match;
    while ((match = re.exec(line)) !== null) {
      if (!skip.has(match[1].toLowerCase())) names.push(match[1]);
    }
  }
  return names;
}

function checkReservedNames() {
  heading('reserved words used as names');
  let anyBad = false;

  // A self-test, in both directions. A regex that silently stops matching turns
  // this group into decoration, and a regex that matches too much fires on
  // `Public Sub New()`, which every class in the repository has.
  const planted = ['Dim next As UInteger = 1', 'For Each error In items', 'Private Sub Step(x As Integer)'];
  // The four measured-legal words are in here on purpose. They are the ones a
  // reference-based list gets wrong, so a future edit that "restores" them fails
  // this self-test rather than silently flagging code that compiles.
  const innocent = ['Dim nextIndex As Integer = 1', 'Public Sub New()', 'If x Is Nothing Then',
    'Dim fromDate As Date', 'For i As Integer = 0 To 10', 'Dim out As Integer',
    'Dim async As Integer', 'Dim await As Integer', 'Dim custom As Integer'];
  const missed = planted.filter((line) =>
    !declaredNames(line).some((n) => VB_RESERVED_AS_NAME.has(n.toLowerCase())));
  const falseAlarm = innocent.filter((line) =>
    declaredNames(line).some((n) => VB_RESERVED_AS_NAME.has(n.toLowerCase())));
  if (missed.length > 0 || falseAlarm.length > 0) {
    fail('reserved', ROOT,
      `the detector itself is broken: ${missed.length} planted declaration(s) not found, ` +
      `${falseAlarm.length} innocent line(s) flagged. Fix this group before trusting it.`);
    anyBad = true;
  } else {
    ok('the detector finds planted declarations and spares legal ones (self-test)');
  }

  for (const project of projects) {
    for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
      const lines = cleanLines(fs.readFileSync(src, 'utf8'));
      lines.forEach((line, idx) => {
        for (const name of declaredNames(line)) {
          if (!VB_RESERVED_AS_NAME.has(name.toLowerCase())) continue;
          fail('reserved', src,
            `'${name}' is a VB keyword and cannot be the name of anything. Rename it ` +
            '(VB is case-insensitive, so any casing is the same word). Expect BC30201 '
            + 'on this line and phantom "is not declared" errors on the lines that use it.',
            idx + 1);
          anyBad = true;
        }
      });
    }
  }
  if (!anyBad) ok('no declaration introduces a VB keyword as a name');
}

// ── Run ────────────────────────────────────────────────────────────────────
console.log('VB.NET structural checker — BrowserForWP');
console.log('(This is NOT a compiler. See the header for exactly what it proves.)');

heading('Block balance');
// A LIST rather than a sequence of calls, so the number printed below is the
// number of groups that actually ran.
//
// The counter this replaced was incremented in a loop over files inside several
// groups, so "N check group(s) run" read 71, then 72, then 73 across three rounds
// in which exactly ONE group was added. A count that moves for reasons the reader
// cannot see is worse than no count, and both docs quoted it.
const GROUPS = [
  // 1 — block balance, per file.
  () => {
    for (const project of projects) {
      for (const src of walk(project.dir, (f) => f.endsWith('.vb'))) {
        checkBlockBalance(src, cleanLines(fs.readFileSync(src, 'utf8')));
      }
    }
  },
  checkProjectParity,
  checkProjectFlavor,
  checkNamespacesAndImports,
  checkImplements,
  checkResourceParity,
  checkXamlHandlers,
  checkXamlRoot,
  checkXamlThemeResources,
  checkCharLiterals,
  checkVb12Syntax,
  checkProfileHazards,
  checkCommentHazards,
  checkPrivilegedAccess,
  checkCapabilityRequirements,
  checkReservedNames,
];

for (const group of GROUPS) group();

console.log(`\n${GROUPS.length} check groups run, ${findings.length} finding(s).`);
if (findings.length > 0) {
  console.log('\nFindings:');
  for (const f of findings) {
    console.log(`  [${f.check}] ${rel(f.file)}${f.line ? ':' + f.line : ''}`);
    console.log(`      ${f.message}`);
  }
  process.exit(1);
}
console.log('\nNo mechanical defects found in the seventeen checked categories.');
console.log('This still does NOT mean the project compiles. Build it for real:');
console.log('');
console.log('  prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \\');
console.log('      "C:\\Mac\\Home\\Documents\\BrowserForWP\\tools\\vm-build.cmd"');
console.log('');
console.log('See docs/MAINTAINING.md for the toolchain layout in that guest.');
