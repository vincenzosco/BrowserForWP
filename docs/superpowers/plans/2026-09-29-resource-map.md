# The resource map name Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the app read its own UI strings instead of throwing `ResourceMap Not Found` once per string and painting the raw keys.

**Architecture:** `Localizer` (in `BrowserForWP.Localization`) asks WinRT for a resource map by name. The name it asks for is wrong, and nothing in the repository can notice, because the map names only exist inside the built `resources.pri`. So the fix has three parts: ask for a map that the build actually produces, stop retrying a load that has already failed (one exception, not one per string), and add a check that compares the name in the source against the names the `.resw` files become — which is the question nobody was asking.

**Tech Stack:** Visual Basic .NET (Windows Phone 8.1), WinRT `ResourceLoader`/`ResourceContext`, Node.js for the repository's checks (`tools/check-vb.mjs`), Parallels CLI + the guest's MSBuild for the build and the emulator.

## Global Constraints

- The repository's loop is Plan → Implement → Verify → Commit → Push; a commit that is not pushed is not done.
- VB is compiled by the guest only (`tools/vm-build.cmd`, `prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}"`), which is also the arbiter of pass/fail.
- No new dependencies, no new project, no manifest change.
- Doc comments are parsed as XML: a literal `<` or `>` must be escaped (`&lt;`/`&gt;`) or the comment is discarded with BC42304 and `tools/check-vb.mjs` fails.
- Every `.vb`/`.xaml`/`.resw` change must keep `node tools/check-vb.mjs` at `16 check groups run, 0 finding(s)`.
- Two languages exist (`Strings/en-US/Resources.resw`, `Strings/it-IT/Resources.resw`) and their key sets must stay identical.
- Do not weaken a check to make a symptom go away. If a step's expected result does not appear, the step is wrong.

---

### Task 1: Ask for the map the build produces

**Files:**
- Modify: `BrowserForWP.Localization/Localizer.vb:24-31` (the `ResourceMap` constant and its comment)
- Modify: `tools/check-vb.mjs` (`checkResourceParity`, group 6 — the only place in the repository that reads the `.resw` files)
- Evidence: `BrowserForWP/bin/{Debug,x86/Debug,ARM/Debug}/resources.pri` (build output, not committed)

**Interfaces:**
- Consumes: nothing.
- Produces: the constant `Localizer.ResourceMap` has the value `"Resources"`, and `tools/check-vb.mjs` fails when that constant is not one of the map names the `.resw` files become.

**The measurement this task rests on** (already taken, 2026-09-29): the resource maps inside the built PRI are

```
'Resources'   'Files'   'Polyfill'   'Assets'
```

with no `Strings` and no `Strings/Resources`, on all three platforms built (`Debug`/AnyCPU, `x86/Debug`, `ARM/Debug`) — extracted from the real file with

```python
import re
data = open('BrowserForWP/bin/Debug/resources.pri','rb').read()
print([m.group().decode('utf-16-le') for m in re.finditer(rb'(?:[ -~]\x00){3,}', data)][:6])
# ['IT-IT', 'EN-US', '100', '240', 'ms-appx://c6af10f5-...', 'Resources']
```

So the strings live in a map named after the `.resw` FILE, `Resources`, and `Localizer` asking for `"Strings/Resources"` is a map that does not exist. The language folder (`Strings/en-US/`) is a qualifier, not part of the map name.

- [ ] **Step 1: Write the failing check**

In `tools/check-vb.mjs`, inside `checkResourceParity()` — after the parity loop, before the function's closing brace — add the map-name check. It uses the `reswFiles` already collected at the top of that function:

```js
  // ── The map NAME, which is a different question from parity ──────────────
  //
  // Parity says the two languages agree. It said nothing about whether the code
  // asks for a map that exists, and that gap shipped: Localizer asked for
  // "Strings/Resources" while the built resources.pri holds the strings in a map
  // named after the .resw FILE, so every lookup threw ResourceMap Not Found and
  // the UI painted raw keys like "EngineLabel".
  //
  // The map name is inferred from the file name here because the repository
  // cannot read resources.pri (it is a build output, per platform). The
  // inference was MEASURED, not assumed: maps in the built PRI are exactly
  // Resources, Files, Polyfill, Assets. See docs/MAINTAINING.md, Round 18. If
  // somebody makes the build index these files under another name, this check
  // fails and sends them to that measurement.
  const mapNames = reswFiles.map((f) => path.basename(f, '.resw'));
  const localizerPath = path.join(ROOT, 'BrowserForWP.Localization', 'Localizer.vb');
  const localizer = fs.readFileSync(localizerPath, 'utf8');
  const mapConstant = localizer.match(/Private Const ResourceMap As String = "([^"]+)"/);
  if (!mapConstant) {
    fail('resw', 'BrowserForWP.Localization/Localizer.vb',
         'no ResourceMap constant found; the resource map name is not readable from the source');
  } else if (!mapNames.includes(mapConstant[1])) {
    fail('resw', 'BrowserForWP.Localization/Localizer.vb',
         `asks for map "${mapConstant[1]}", but the .resw files become the map(s) ${mapNames.map((m) => `"${m}"`).join(', ')}`);
  } else {
    ok(`the resource map the code asks for ("${mapConstant[1]}") is the map the .resw files become`);
  }
```

- [ ] **Step 2: Run the check and watch it fail on the current code**

Run: `node tools/check-vb.mjs 2>&1 | tail -6`
Expected: `1 finding(s)` and a line naming `Localizer.vb` asking for `"Strings/Resources"` while the `.resw` files become `"Resources"`. This is the defect in one line — the check is worth having only if it can see it.

- [ ] **Step 3: Fix the constant**

In `BrowserForWP.Localization/Localizer.vb`, replace the constant and its comment:

```vb
        ''' <summary>
        ''' Resource map name. The .resw files are at
        ''' BrowserForWP/Strings/&lt;tag&gt;/Resources.resw, and the map the build
        ''' produces for them is named after the FILE -- "Resources" -- with the
        ''' language folder acting as a qualifier rather than as a path.
        '''
        ''' MEASURED, not assumed (2026-09-29): the resource maps inside the built
        ''' resources.pri are exactly 'Resources', 'Files', 'Polyfill' and
        ''' 'Assets', on Debug/AnyCPU as well as x86/Debug and ARM/Debug. This
        ''' constant used to read "Strings/Resources", which is a map that does not
        ''' exist: every lookup threw ResourceMap Not Found, the error was swallowed
        ''' by design, and the UI painted raw keys such as "EngineLabel".
        '''
        ''' The angle brackets must be escaped: a doc comment is parsed as XML, so an
        ''' unescaped &lt;tag&gt; opens an element and the closing &lt;/summary&gt;
        ''' then mismatches (BC42304).
        ''' </summary>
        Private Const ResourceMap As String = "Resources"
```

- [ ] **Step 4: Run the check again**

Run: `node tools/check-vb.mjs 2>&1 | tail -3`
Expected: `16 check groups run, 0 finding(s).`

- [ ] **Step 5: Mutation-test the new check**

Break the constant on purpose and require the check to refuse it:

```bash
# Copy FIRST, and restore from the copy -- never with `git checkout`. A mutation
# test has to be reversible against the WORKING TREE, and `git checkout` restores
# the last COMMIT: in this repository's execution of the sibling step it silently
# deleted an hour of uncommitted work. That was a defect in the plan, and it is
# written down here rather than quietly fixed.
cp BrowserForWP.Localization/Localizer.vb /tmp/Localizer.vb.keep
sed -i '' 's/Private Const ResourceMap As String = "Resources"/Private Const ResourceMap As String = "Strings\/Resources"/' BrowserForWP.Localization/Localizer.vb
node tools/check-vb.mjs 2>&1 | grep -c "Strings/Resources"   # must be >= 1
cp /tmp/Localizer.vb.keep BrowserForWP.Localization/Localizer.vb
node tools/check-vb.mjs 2>&1 | grep "check groups run"      # back to 0 findings
```

Expected: the first run prints at least one line mentioning `Strings/Resources`; the last prints `0 finding(s)`. A check nobody has seen fail is decoration, and this repository has shipped decoration twice.

- [ ] **Step 6: Commit**

```bash
git add BrowserForWP.Localization/Localizer.vb tools/check-vb.mjs
git commit -m "fix(l10n): ask for the resource map the build creates, and check the name"
```

---

### Task 2: One attempt, one exception

**Files:**
- Modify: `BrowserForWP.Localization/Localizer.vb` (the `Loader` property, `[Get]`, and `ApplyLanguageQualifier`)
- Modify: `tools/check-vb.mjs` (`checkResourceParity`, a shape contract next to the map-name check)

**Interfaces:**
- Consumes: `Localizer.ResourceMap` from Task 1.
- Produces: `Localizer.Loader` returns `Nothing` after a failed construction instead of retrying; `Private Shared _loaderUnavailable As Boolean`; `Private Shared Function TryCreateLoader() As ResourceLoader`.

**Why this is a separate task:** Task 1 makes the map exist, so this changes nothing the user sees today. It changes what a FUTURE failure costs. Measured in the emulator on 2026-09-29: the wrong map name produced ~50 `ResourceMap Not Found` first-chance exceptions for one app launch, one per string, because `Loader` had no memory of having failed. A swallowed error that repeats once per frame is not a diagnostic, it is a flood.

- [ ] **Step 1: Write the shape contract**

In `tools/check-vb.mjs`, directly after the map-name check from Task 1:

```js
  // The loader is created ONCE, and a failure is remembered. Shape, not
  // behaviour: the VB cannot run off-device, so this asserts that the code still
  // HAS the memory that stops a broken map from throwing once per string. The
  // behaviour itself was seen in the emulator (MAINTAINING, Round 18).
  if (!/_loaderUnavailable As Boolean/.test(localizer)) {
    fail('resw', 'BrowserForWP.Localization/Localizer.vb',
         'no _loaderUnavailable field: a failed resource-map load will be retried on every string');
  } else if (!/If _loader Is Nothing AndAlso Not _loaderUnavailable Then/.test(localizer)) {
    fail('resw', 'BrowserForWP.Localization/Localizer.vb',
         'the Loader property does not consult _loaderUnavailable, so a failed load repeats');
  } else {
    ok('a failed resource-map load is remembered rather than retried per string');
  }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `node tools/check-vb.mjs 2>&1 | tail -4`
Expected: `2 finding(s)` — the map-name one is already fixed, so this is the new one, naming the missing `_loaderUnavailable`.

- [ ] **Step 3: Implement the memory**

In `BrowserForWP.Localization/Localizer.vb`, add the field next to the others:

```vb
        Private Shared _override As String
        Private Shared _current As String
        Private Shared _loader As ResourceLoader

        ''' <summary>
        ''' Set when the resource map could not be loaded. Remembered so that the
        ''' cost of a broken map is ONE exception and not one per string: measured
        ''' in the emulator, the wrong map name produced about fifty
        ''' ResourceMap Not Found exceptions for a single app launch, because
        ''' constructing the loader was retried on every lookup.
        ''' </summary>
        Private Shared _loaderUnavailable As Boolean
```

Replace the `Loader` property and add the guarded factory:

```vb
        ''' <summary>
        ''' Created lazily, and rebuilt whenever the language qualifier changes, so
        ''' a runtime language switch is reflected without an app restart. Nothing
        ''' after a failure: the map either exists for the life of the process or
        ''' it does not.
        ''' </summary>
        Private Shared ReadOnly Property Loader As ResourceLoader
            Get
                If _loader Is Nothing AndAlso Not _loaderUnavailable Then
                    _loader = TryCreateLoader()
                End If
                Return _loader
            End Get
        End Property

        ''' <summary>
        ''' The only place a loader is constructed, so the only place the
        ''' "unavailable" flag is set. Nothing means the map is not there.
        ''' </summary>
        Private Shared Function TryCreateLoader() As ResourceLoader
            Try
                _loaderUnavailable = False
                Return New ResourceLoader(ResourceMap)
            Catch ex As Exception
                _loaderUnavailable = True
                Return Nothing
            End Try
        End Function
```

In `[Get]`, tolerate `Nothing` explicitly, so the intent is readable at the call site:

```vb
        Public Shared Function [Get](key As String) As String
            If String.IsNullOrEmpty(key) Then Return String.Empty

            ' Nothing means the map could not be loaded, which has already been
            ' reported once and is not worth reporting again per string.
            '
            ' The local is `resolver` and NOT `loader`: VB is case-insensitive, so
            ' `Dim loader = Loader` declares a local that shadows the property it is
            ' reading, and the compiler reports BC30980, BC30574 and BC30512. The
            ' first version of this task did exactly that and did not build -- see
            ' the note under this block.
            Dim resolver As ResourceLoader = Loader
            If resolver IsNot Nothing Then
                Try
                    Dim value = resolver.GetString(key)
                    If value IsNot Nothing Then Return value
                Catch ex As Exception
                    ' A malformed key must not take the UI down; the key itself is
                    ' a better diagnostic than a crash.
                End Try
            End If
            Return key
        End Function
```

**Adjustment, made while executing this plan (2026-09-29).** The block above was
first written as `Dim loader = Loader`, and the guest compiler refused all four
configurations with

```
Localizer.vb(114,26): error BC30980: Impossibile dedurre il tipo di 'loader' da
  un'espressione contenente 'loader'
Localizer.vb(117,33): error BC30574: Option Strict On non consente l'associazione tardiva
Localizer.vb(118,56): error BC30512: Option Strict On non consente conversioni implicite da 'Object' a 'String'
```

A local that differs from the property only in case is the same name to VB, so it
shadows what it is reading. The name is `resolver` now. Nothing in this repository's
checks could have caught it -- a transliterated mirror does not know about case
insensitivity -- which is the whole reason the guest build is the arbiter.

In `ApplyLanguageQualifier`, construct through the same factory so the flag cannot drift out of step:

```vb
        Private Shared Sub ApplyLanguageQualifier(tag As String)
            Try
                Dim context = ResourceContext.GetForCurrentView()
                context.QualifierValues("Language") = tag
                _loader = TryCreateLoader()   ' discard the old resolution
            Catch ex As Exception
                ' If the qualifier cannot be set we keep whatever the manifest
                ' resolved, which is still a valid language rather than a failure.
            End Try
        End Sub
```

- [ ] **Step 4: Run the check and the compiler**

Run: `node tools/check-vb.mjs 2>&1 | tail -3`
Expected: `16 check groups run, 0 finding(s).`

Run: `prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd"`
Expected: `=== BUILD_EXIT=0 ===` with no `error BC` lines. Do the same for `/p:Configuration=Release`, `/p:Platform=x86` and `/p:Platform=ARM` — four solution builds, since the library is compiled into each.

- [ ] **Step 5: Mutation-test the contract**

```bash
cp BrowserForWP.Localization/Localizer.vb /tmp/Localizer.vb.keep
sed -i '' 's/If _loader Is Nothing AndAlso Not _loaderUnavailable Then/If _loader Is Nothing Then/' BrowserForWP.Localization/Localizer.vb
node tools/check-vb.mjs 2>&1 | grep -c "does not consult _loaderUnavailable"   # must be >= 1
cp /tmp/Localizer.vb.keep BrowserForWP.Localization/Localizer.vb
node tools/check-vb.mjs 2>&1 | grep "check groups run"                          # back to 0 findings
diff /tmp/Localizer.vb.keep BrowserForWP.Localization/Localizer.vb              # identical
```

Expected: the mutated run reports the finding, the restored run prints
`16 check groups run, 0 finding(s)`, and `diff` says nothing. The `diff` is part of
the step because "I restored it" is a claim about the file, and the file is
checkable.

- [ ] **Step 6: Commit**

```bash
git add BrowserForWP.Localization/Localizer.vb tools/check-vb.mjs
git commit -m "fix(l10n): remember a failed resource-map load instead of retrying per string"
```

---

### Task 3: See the strings in the emulator, and write down what was seen

**Files:**
- Modify: `docs/MAINTAINING.md` (a new `### Round 18` section before `## The loop`, plus the `.resw` row of "Where the tests actually are")
- Modify: `.agents/skills/browserforwp/SKILL.md` (the `check-vb.mjs` row: group 6 now also checks the map name)
- Evidence to produce: a screenshot of the running app, and the build/deploy transcript

**Interfaces:**
- Consumes: the fixed `Localizer.vb` from Tasks 1 and 2.
- Produces: nothing at runtime. This task is the verification for both, because the VB cannot run on the host and the map only exists inside a package.

**Why a real run and not a unit test:** the map name is not a property of the source, it is a property of the PRI the build writes. The only place it can be observed end to end is a device or an emulator, and the emulator is running.

- [ ] **Step 1: Build the app project for the emulator's platform**

```bash
VM="{66a2f493-162c-4b3f-ba40-0a26020cc818}"
prlctl exec "$VM" "cmd.exe" "/c" "cd /d C:\Mac\Home\Documents\BrowserForWP && \"C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe\" BrowserForWP\BrowserForWP.vbproj /nologo /v:minimal /p:Configuration=Debug /p:Platform=AnyCPU"
prlctl exec "$VM" "cmd.exe" "/c" "dir /s /b C:\Mac\Home\Documents\BrowserForWP\BrowserForWP\bin\Debug\*.appx"
```

Expected: the build's last lines include `Your package has been successfully created.`, and the `dir` lists a `.appx` (the earlier Round 17 build produced `bin\Release\BrowserForWP_1.0.0.0_Bundle\BrowserForWP_1.0.0.0_AnyCPU.appx`; the Debug one is the same shape under `bin\Debug`). If no `.appx` is produced, this task cannot proceed and the plan stops here and says so — an emulator run needs a package.

- [ ] **Step 2: Install and launch it into a running emulator**

```bash
prlctl exec "$VM" "cmd.exe" "/c" "\"C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe\" /EnumerateDevices"
# pick the index of the emulator that is already running (the user's earlier run used
# the AnyCPU layout, so it exists); then:
prlctl exec "$VM" "cmd.exe" "/c" "\"C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe\" /installlaunch <path-to-appx> /targetdevice:<index>"
```

Expected: `Completato.` and the phone screen shows the browser. If the emulator is not running, start one from the list (`xde.exe` under `C:\Program Files (x86)\Microsoft XDE\8.1\`) — and if that fails, stop and ask the owner to press F5 in Visual Studio, which is how the defect was found in the first place.

- [ ] **Step 3: Photograph the screen**

```bash
prlctl exec "$VM" "powershell.exe" "-command" "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; \$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; \$bmp=New-Object System.Drawing.Bitmap \$b.Width,\$b.Height; \$g=[System.Drawing.Graphics]::FromImage(\$bmp); \$g.CopyFromScreen(\$b.Location,[System.Drawing.Point]::Empty,\$b.Size); \$bmp.Save('C:\Mac\Home\Documents\BrowserForWP\emulator.png')"
```

Expected: `emulator.png` in the repository root (delete it before committing; it is evidence, not an asset).

- [ ] **Step 4: Read the evidence, and write down what it says**

Look at the screenshot and answer ONE question: **are the labels words, or are they keys like `EngineLabel`, `RemoteUseServer`, `KeyBarClose`?** Words mean the fix worked and the strings are being read. Keys mean it did not, and the next step is Step 5, not a commit.

Record, in the Round 18 section and in the emulator-proven table, exactly what was seen: the language shown, three labels read off the screen, and whether the app was the installed package or a debugger launch. Do not write "verified" for anything not visible in the picture.

- [ ] **Step 5: If the labels are still keys**

Then `"Resources"` is not the name the runtime resolves either, and the next measurement is the runtime's own view: enumerate the app package's maps from inside the app (`ResourceManager.Current.MainResourceMap` of the app's `ResourceContext`) via the existing diagnostics surface, or try the parameterless `New ResourceLoader()`, which is defined to read the default map. Do NOT commit a guess: change the constant, rebuild, and repeat Steps 2-4. Record which value worked and why the other did not.

- [ ] **Step 6: Commit the documentation**

```bash
rm -f emulator.png
git add docs/MAINTAINING.md .agents/skills/browserforwp/SKILL.md
git commit -m "docs: a resource map that does not exist, measured on a real device"
```

---

**Adjustment, made while executing this task (2026-09-29).** The emulator steps
above were written on an assumption that does not hold on this host, and the run
happened on the handset instead:

- **No emulator is possible.** The WP8.1 emulator is a Hyper-V VM and Parallels on
  Apple silicon cannot nest it: `AppDeployCmd /installlaunch ... /targetdevice:3`
  answers `PrlJob_GetResult: Invalid argument`. `XDE.exe /?` opens a GUI window and
  blocks. The owner confirmed the constraint directly.
- **The deploy that produced the evidence was F5 to Device**, which is how the
defect was originally found.
- **The screen was read with `prlctl capture` + OCR, not with a handset screenshot.**
  `prlctl exec` runs in session 0, where a `CopyFromScreen` capture is blank (measured:
  1024x768, 3 KB), so the guest display was captured with `prlctl capture` (2940x1846)
  and read with a 30-line Swift/Vision script that was first validated against an
  image whose text was known. That is how the Output window was inspected without
  asking anyone to transcribe it.
- **The XAML designer cannot answer this question at all** (it does not run
  code-behind; all 106 strings come from `Localizer.Get`), and it is crashing with
  `System.Runtime.Remoting.RemotingException` on this host anyway.
- **The result:** no `ResourceMap Not Found` in the debug output, and the labels read
  as words ("Cerca o digita un indirizzo", "Vai") on the device.

The section this task would have written also came out with a different shape: the
evidence is about the *app*, so it closes none of the blank rows in "The remote
engine, verified by hand" -- which is stated there rather than left to inference.

---

## Self-Review

**Spec coverage.** The pasted debugger output contains exactly one defect class: `System.Exception ... ResourceMap Not Found` from `BrowserForWP.Localization.DLL`, about fifty times. Task 1 removes the cause (the map does not exist under the name the code asks for), Task 2 removes the amplifier (the failure is retried per string), Task 3 verifies both on a real runtime and records the language actually displayed. Nothing in the output suggests a second defect; if the screenshot shows one, that is a new plan, not a fifth task in this one.

**Placeholders.** None: every step has the code, the command, and the expected output. Two steps name a value the executor must fill in (`<path-to-appx>`, `<index>`) and both are outputs of the command immediately above them.

**Type consistency.** `ResourceMap` (String constant), `_loaderUnavailable` (Boolean field), `TryCreateLoader()` (`Function ... As ResourceLoader`, returns `Nothing` on failure), `Loader` (property of type `ResourceLoader`) are used with those names and types in every task that mentions them, and the check-vb patterns in Task 2 match the code introduced in Task 2 exactly.

**What this plan cannot promise.** That `"Resources"` is the name the runtime resolves. It is the name the PRI contains, which is strong evidence and not the same thing. Task 3 Step 5 exists for that gap, and the plan does not commit until a real runtime has shown strings.
