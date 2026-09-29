# Plan — the four "referenced component could not be found" warnings

Date: 2026-09-28
Status: implemented, with one correction: the rule this plan gives for the `.sln`
entries (`{76F1466A-...}` there too) was wrong and was replaced in Round 12 — the
`.sln` names a project *factory*, and `{F184B08F-...}` is the only one this VS2013
registers. See `docs/MAINTAINING.md` Round 12 and
`docs/superpowers/plans/2026-09-29-solution-loadability.md`. Everything this plan
says about the `.vbproj` flavour GUID stands.

## The report

Four warnings in the Visual Studio error list, all attributed to the `BrowserForWP`
app project, none of them with a diagnostic code:

```
Warning 1  The referenced component 'BrowserForWP.Core' could not be found.        BrowserForWP
Warning 2  The referenced component 'BrowserForWP.Crypto' could not be found.      BrowserForWP
Warning 3  The referenced component 'BrowserForWP.Net' could not be found.         BrowserForWP
Warning 4  The referenced component 'BrowserForWP.Localization' could not be found. BrowserForWP
```

## What was ruled out, and how

The four `<ProjectReference>` items in `BrowserForWP/BrowserForWP.vbproj` are
well-formed: correct `Include` paths, correct `<Project>` GUIDs (checked against
each library's own `<ProjectGuid>`), `<Name>` equal to each `<AssemblyName>`. The
referenced projects all exist in `BrowserForWP.sln` with `ActiveCfg` and
`Build.0` for every one of the six solution configurations, and their `bin`
output exists on disk for all six (`bin\Debug`, `bin\Release`, `bin\ARM\Debug`,
`bin\ARM\Release`, `bin\x86\Debug`, `bin\x86\Release`). A guest rebuild of the
whole solution is green in all six configurations.

So nothing is missing. The message is not about absence.

It is also not a compiler diagnostic. Every other failure this project has met
carried a code (`BC30456`, `MSB4078`, `APPX1621`). This one carries none, which
places it in the project system rather than in `vbc`. That is consistent with a
search of the guest: the string "referenced component" does not occur anywhere
under `C:\Program Files (x86)\MSBuild`, under the Windows Phone 8.1 SDK, or under
`C:\Program Files (x86)\Windows Kits\8.1`. Nothing in the build toolchain can
emit it, which is why `tools/vm-build.cmd` has always been green while the IDE
shows the warning.

## Root cause

The four libraries declare the **Windows Store apps** project flavour, while
declaring themselves Windows Phone 8.1 libraries.

The two authoritative VS2013 project templates on the guest settle the question:

| Template | `ProjectTypeGuids` (first GUID) |
| --- | --- |
| `ProjectTemplates\VisualBasic\Windows Phone 8.1\1033\WindowsPhoneClassLibrary\ClassLibrary.vbproj` | `{76F1466A-8B6D-4E39-A767-685A06062A39}` |
| `ProjectTemplates\VisualBasic\Windows Phone 8.1\1033\WindowsPhoneBlankApplication\Application.vbproj` | `{76F1466A-8B6D-4E39-A767-685A06062A39}` |
| `ProjectTemplates\VisualBasic\Windows Store\1033\ClassLibrary_WindowsStoreApps\ClassLibrary.vbproj` | `{BC8A1FFA-BEE3-4634-8014-F334798102B3}` |

A Windows Phone 8.1 app and a Windows Phone 8.1 class library share the flavour
GUID `{76F1466A-...}`. `{BC8A1FFA-...}` is the *Windows Store* flavour.

Our app is correct (`{76F1466A-...}`). Our six libraries
(`BrowserForWP.Core`, `BrowserForWP.Crypto`, `BrowserForWP.Localization`,
`BrowserForWP.Net`, and both test libraries) carry `{BC8A1FFA-...}`, the Store
flavour, while also declaring

```xml
<TargetPlatformIdentifier>WindowsPhoneApp</TargetPlatformIdentifier>
```

The two statements contradict each other: the project system reads the flavour
from `ProjectTypeGuids`, sees a Windows Store library, and refuses to resolve it
as a reference from a Windows Phone 8.1 app. `TargetPlatformIdentifier` is an
MSBuild property and does not enter into that decision, which is why the guest
build never noticed.

The contradiction was introduced in the round that "fixed" an earlier cascade by
adding `<TargetPlatformIdentifier>WindowsPhoneApp</TargetPlatformIdentifier>` to
the libraries. The fix was already in place via the conditional fallback
`PropertyGroup` at the foot of each file, so it changed nothing; the real
mismatch, the Store flavour GUID, has been there since the projects were first
written. `docs/MAINTAINING.md` recorded the false cause; that record is corrected
by this change.

## The second statement of the same thing, and how it was judged

`BrowserForWP.sln` records a project type GUID per project as well, and it
disagreed with the project files: `{BC8A1FFA-...}` (Windows Store) for the six
libraries, and `{F184B08F-...}` (the plain VB language GUID) for the app.

Before touching it, the question was whether the IDE takes the project flavour
from the `.sln` entry or from the project file. The registration is the
discriminator, and it was read on the guest:

```
reg query HKLM\SOFTWARE[\WOW6432Node]\Microsoft\VisualStudio\12.0 /s /f "<guid>"
```

| GUID | Registered as a project factory in this VS2013 install? |
| --- | --- |
| `{F184B08F-C81C-45F6-A57F-5ABD9991F28F}` | **yes** (VB, under `Projects` and `LocalData`) |
| `{76F1466A-8B6D-4E39-A767-685A06062A39}` | no |
| `{BC8A1FFA-BEE3-4634-8014-F334798102B3}` | no |

Neither flavour GUID selects a project factory, so the `.sln` entry is a hint and
the loader resolves these projects through the project file — which is why the
project files were the cause, and why no MSBuild build can see any of this.

That makes the `.sln` fix a consistency change rather than the cure. It is still
made, because a solution that calls a Windows Phone project a Windows Store one is
exactly the trap that produced the defect, and the next reader would otherwise be
free to "fix" the project files back the wrong way. Both files now say
`{76F1466A-...}`, and group 14 enforces that they agree.

## The change

One line per project in seven project files: swap the Store flavour GUID for the
Windows Phone 8.1 one, in the four libraries the app references and in the two
test libraries that carry the same defect. The same swap in the seven `Project`
entries of `BrowserForWP.sln`, including the app's, so the two files agree.
Correct the four comments that state the false cause, and correct the
`docs/MAINTAINING.md` round record that repeated it.

## Verification

* New group 14 in `tools/check-vb.mjs`: a `.vbproj` that declares
  `TargetPlatformIdentifier=WindowsPhoneApp` must use `{76F1466A-...}`, the
  Windows Store flavour GUID must not appear in any project here, and the `.sln`
  entry for a project must carry the same flavour as that project's own file.
  RED against the reproduced pre-fix state — **13 findings**: six `.vbproj`
  (`BrowserForWP.Core`, `.Crypto`, `.Localization`, `.Net` and both test
  libraries) and seven `.sln` lines, `BrowserForWP.sln:5,7,9,11,13,15,17`. GREEN
  after: 64 groups, 0 findings.
* The template evidence above is re-readable on the guest; the group's message
  cites both paths so the next reader can check it without guessing.
* Guest rebuild of all six solution configurations stays green, including the two
  `Any CPU` ones (built as project builds, since that solution platform's name
  contains a space): `BUILD_EXIT=0` six times, with only the two deliberate
  `BC40000` `ResourceLoader` warnings.

## What this does not prove

The diagnostic itself cannot be reproduced off the IDE, because the project
system is the component that emits it. The evidence for the fix is the template
comparison and the registration check, both objective and re-runnable, not a
before/after screenshot of the error list. The error list is the user's to
confirm: the rule "a Windows Phone 8.1 project uses the 76F1466A flavour" is now
enforced and provable, while "the IDE stops warning" is a prediction about a
component no command here reaches.

This does not affect `TargetPlatformIdentifier`'s MSBuild use: the explicit line
and the conditional fallback are retained, and every project still declares
`WindowsPhoneApp`.
