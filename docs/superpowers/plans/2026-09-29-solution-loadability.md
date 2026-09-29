# Plan — a solution an IDE can load, and the comment that broke a project

Date: 2026-09-29
Status: implemented

## The report

Opening `BrowserForWP.sln` in a tool that is not Visual Studio — a solution and
project selector on macOS — shows

```
Solution 'BrowserForWP' (0 projects)
BrowserForWP              (unavailable)
BrowserForWP.Core         (unavailable)
BrowserForWP.Crypto       (unavailable)
BrowserForWP.Crypto.Tests (unavailable)
BrowserForWP.Localization (unavailable)
BrowserForWP.Net          (unavailable)
```

`(0 projects)` with every project `(unavailable)` is the shape of a solution the
loader could not use at all, not of seven broken projects. The projects are
demonstrably fine: six configurations build `BUILD_EXIT=0` in the guest.

## What was ruled out, and how

- **Not the project files.** Seven `.vbproj` files, each structurally identical to
  the templates under `Common7\IDE\ProjectTemplates\VisualBasic\` in the guest,
  each carrying the Windows Phone 8.1 flavour GUID that group 14 of
  `tools/check-vb.mjs` requires. Round 6 fixed that and it is still right.
- **Not the report's tool.** The report came from a selector on macOS, which
  cannot be run here. The component it hands the file to can: `devenv.com` loads a
  solution through the IDE's own project system, not through MSBuild. That is the
  oracle this round uses, and Round 6 had none, which is where it went wrong.

## Root cause 1 — the `.sln` names a project *factory*, not a flavour

Round 6 changed the seven `Project` lines to `{76F1466A-...}`, the Windows Phone
8.1 flavour, reasoning from a registry query that neither flavour GUID is
registered under `HKLM\...\VisualStudio\12.0\Projects` and therefore the field is
inert. The registry shows which factories **exist**; it does not say what the
loader does with a name that is not among them. Measured, one field apart:

```
devenv.com BrowserForWP.sln /build "Debug|ARM"
  with {76F1466A-...}   ->  Build: 0 succeeded or up-to-date, 0 failed, 0 skipped
  with {F184B08F-...}   ->  Build: 7 succeeded, 0 failed, 0 up-to-date, 0 skipped
```

Nothing is loaded in the first case — the same `(0 projects)` the report shows.
The rule is therefore two rules, one per file, and confusing them produces a
solution no IDE will open:

| File | Carries | Because |
| --- | --- | --- |
| `.vbproj` | the **flavour** `{76F1466A-8B6D-4E39-A767-685A06062A39}` | the IDE's project system compares flavours before resolving a reference |
| `.sln` | the **factory** `{F184B08F-C81C-45F6-A57F-5ABD9991F28F}` | the only factory registered in this VS2013, and the loader resolves the entry through whatever factory the GUID names |

The same field also decides whether a host that is not Windows can find the
project: `BrowserForWP\BrowserForWP.vbproj` is a file *name* on macOS, not a path.
The `.sln` now uses `/` separators, which Windows accepts unchanged.

## Root cause 2 — the IDE reads the project file as text

With the solution loading, the project system took six of the seven projects and
refused `BrowserForWP.Crypto` with

```
BrowserForWP.Crypto.vbproj : error  : The application for the project is not installed.
```

again without a diagnostic code, so again invisible to every build. Bisected on
the guest, one `devenv.com /build` run per row against a one-project solution:

| Variant of `BrowserForWP.Crypto.vbproj` | Loads? |
| --- | --- |
| untouched | no |
| byte-identical copy under another file name | no |
| every XML comment removed | yes |
| comment block N removed, each N in turn | only N = 2 loads |
| comments intact, the two mentions inside block 2 reworded | yes |
| block 2 removed, one mention added to a leading comment | no |

The last two rows are the finding: the IDE's Windows Phone project factory
locates the flavour property by **scanning the project file as text**, not by
parsing it as XML, so the first occurrence of that name is the one that counts.
In this file the comment's mention came first and the real element second — the
flavour read as empty and the project was refused. `MSBuild` parses XML, which is
why six configurations stayed green throughout.

## The change

1. `BrowserForWP.sln`: all seven `Project` lines to `{F184B08F-...}`, paths with
   `/`. Nothing else in the file, so the platform mapping is untouched.
2. `BrowserForWP.Crypto/BrowserForWP.Crypto.vbproj`: the comment says "the flavour
   property" and records why the property is not named in prose.
3. `tools/check-vb.mjs` group 14 grows the two new rules — a registered factory
   GUID and `/` separators in the `.sln`, and the property's name nowhere ahead of
   its element, comments included. The group's comment carries the measurements so
   the next reader can re-derive both instead of trusting them.
4. `docs/MAINTAINING.md`: the Round 6 paragraph that drew the wrong conclusion
   about the `.sln` is marked corrected; Round 12 records this round. SKILL.md
   gains the two rules and a verification-table row for the real oracle.

## Verification

- `node tools/check-vb.mjs` — 0 findings, and both new rules shown red by a
  deliberate mutation (`{76F1466A-...}` back into the `.sln`: 8 findings; one
  mention added to a leading comment: the third rule fires).
- All 20 referees in `tools/proto/` green, unchanged by this round.
- Six configurations `BUILD_EXIT=0`, only the two deliberate `BC40000` warnings.
  The two `Any CPU` ones are project builds — that platform's name contains a
  space, which `prlctl exec` cannot pass to `cmd.exe`.
- `devenv.com BrowserForWP.sln /build "Debug|ARM"` — `Build: 7 succeeded, 0 failed`,
  no `not installed`, only the allow-listed `WMC9999`.

## What this does not prove

Not that the reporting tool now shows the projects. It cannot be run here. What is
measured is `devenv.com`, the same project system that tool hands the file to; the
solution's text is now the text that system loads, and that is the claim.

## Also noticed

`devenv` rewrites the projects it opens, adding a BOM, CRLF line endings and a
`<Folder Include="My Project\" />` item to whichever `.vbproj` files it touched.
Those edits have to be reverted after a measuring run, or a round that meant to
change only the solution will also rewrite four project files.
