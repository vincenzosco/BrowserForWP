# XAML theme resources: kill the key that is not there, and stop calling it noise

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** No XAML file here may reference a `{ThemeResource}` key Windows Phone 8.1
does not define, and the guest build must stop reporting an unresolved key as
allow-listed noise.

**Architecture:** `{ThemeResource X}` is resolved when a page *loads*, not when it
compiles, so an unknown `X` is invisible to `vm-build.cmd` — the compiler can only
notice it as an internal error (`WMC9999`, currently allow-listed). The fix has two
halves: replace the one bad key, and give the off-Windows checker an oracle so the
whole class is caught before the guest round trip.

**Tech Stack:** VB.NET / WinRT for Windows Phone 8.1, VS2013 (MSBuild 12) inside the
Parallels guest at `C:\Mac\Home\Documents\BrowserForWP`; Node.js static checkers on
the macOS host; `prlctl exec` to reach the guest.

## Global Constraints

- Windows Phone 8.1 is the target. Nothing here changes the platform laws, the
  TLS/Schannel ceiling, or the engine seam.
- **The oracle is the phone's dictionaries, never the desktop's.** Windows 8.1 ships
  its own `themeresources.xaml` / `generic.xaml` beside the phone's and they are
  *not* the same set. A desktop-only key compiles, ships, and then throws on the
  handset — which is exactly the defect being fixed.
- The check must run on macOS with no guest and no network. The guest is used only
  to *generate* the committed key snapshot, never to run the check.
- `tools/check-vb.mjs` is a filter, not a compiler. It must keep saying so.
- Every new tool or command is documented in `.agents/skills/browserforwp/SKILL.md`
  and `docs/MAINTAINING.md` before the turn ends.
- The loop is plan → failing check → implement → verify on the guest → commit → push.

## Evidence

Four facts, all measured, that converge:

1. The Visual Studio error list reports, at `MainPage.xaml:30`,
   `The resource "TextControlBackground" could not be resolved.`
2. `{ThemeResource TextControlBackground}` is used **once**, at
   `BrowserForWP/MainPage.xaml` line 30.
3. The WP8.1 design dictionaries define **523** keys and **none of them is
   `TextControlBackground`**. Only `TextControlBackgroundThemeOpacity` — a `Double`
   — is close, and a `Double` is not a `Brush`. Windows 8.1's *desktop* dictionary
   does not define the brush either: the name is UWP/Win10-era and was borrowed
   into a WP8.1 page. The phone's own TextBox background is
   `TextBoxBackgroundThemeBrush`, defined in both dictionaries
   (`themeresources.xaml:264`) and used by the phone's own TextBox style
   (`generic.xaml:2413`).
4. `tools/vm-build.cmd` allow-lists exactly one diagnostic, naming it in its own
   comments as non-fatal noise:
   `Xaml Internal Error error WMC9999: La chiave specificata non era presente nel
   dizionario` — *"the given key was not present in the dictionary"*, i.e. a
   `KeyNotFoundException` from a resource-key lookup. `tools/check-vb.mjs`'s header
   already lists "a XAML `{ThemeResource ...}` key that does not exist on WP8.1" as
   a defect it cannot catch.

Facts 1 and 4 are the same defect reported by two different tools: the designer
names the key, the compiler releases an internal error. The repository measured
WMC9999 (12/12 builds, byte-identical `App.xbf`/`MainPage.xbf`) and concluded "noise".
That measurement was real but its control was missing: identical hashes across
repeated builds of the *same* source prove reproducibility, not harmlessness. Nothing
ever compared a build **with** the bad key against one **without** it.

**Prediction to test (Task 4):** after the key is fixed, WMC9999 stops appearing. If
it does, the "known noise" entry is not noise and must be removed, not re-allow-listed.

## File Structure

| File | Change | Responsibility |
| --- | --- | --- |
| `docs/superpowers/plans/2026-09-28-xaml-theme-resources.md` | create | this plan |
| `tools/wp81-theme-keys.sh` | create | regenerate the oracle from the guest's WP8.1 design dictionaries |
| `tools/wp81-theme-keys.txt` | create (generated) | committed snapshot: the 523 keys, with provenance in a header |
| `tools/check-vb.mjs` | modify | new check group: every `{ThemeResource X}` exists |
| `BrowserForWP/MainPage.xaml` | modify | line 30, the one bad key |
| `tools/vm-build.cmd` | modify | the WMC9999 allow-list, per Task 4's measurement |
| `tools/wmc9999-probe.sh` | modify | add the control the original measurement lacked |
| `docs/MAINTAINING.md` | modify | correct the WMC9999 record; document the new tool |
| `.agents/skills/browserforwp/SKILL.md` | modify | same, in the skill's own tables |

---

### Task 1: The oracle — the keys WP8.1 actually defines

**Files:**
- Create: `tools/wp81-theme-keys.sh`
- Create (generated): `tools/wp81-theme-keys.txt`

**Interfaces:**
- Produces: `tools/wp81-theme-keys.txt`, one key per line, `#` comments allowed.
  Task 2 reads it and nothing else.

- [ ] **Step 1: Write the generator**

`tools/wp81-theme-keys.sh` reads the two design dictionaries out of the guest and
writes the sorted union of their `x:Key="..."` names. Read the committed file for
the full script; the load-bearing parts are these:

```bash
VM="{66a2f493-162c-4b3f-ba40-0a26020cc818}"
DESIGN='C:\Program Files (x86)\Windows Phone Kits\8.1\Include\abi\Xaml\Design'

dump() {
  for f in themeresources.xaml generic.xaml; do
    prlctl exec "$VM" "cmd.exe" "/c" "type \"$DESIGN\\$f\""
  done
}

dump | LC_ALL=C tr -d '\r' \
     | grep -o 'x:Key="[^"]*"' \
     | sed 's/^x:Key="//; s/"$//' \
     | LC_ALL=C sort -u > "$tmp"
```

The script refuses to write when it reads fewer than 200 keys, because a silently
truncated snapshot would turn the checker into a rubber stamp.

- [ ] **Step 2: Run it**

Run: `bash tools/wp81-theme-keys.sh`
Expected: `wrote .../tools/wp81-theme-keys.txt (523 keys)`

- [ ] **Step 3: Prove the oracle separates the two keys**

Run:
```bash
grep -x "TextControlBackground" tools/wp81-theme-keys.txt || echo ABSENT
grep -n "TextBoxBackgroundThemeBrush" tools/wp81-theme-keys.txt
```
Expected: `ABSENT`, then line 439. `grep -x` matters: without it,
`TextControlBackgroundThemeOpacity` matches and the check looks satisfiable.

---

### Task 2: The check, and it must fail first

**Files:**
- Modify: `tools/check-vb.mjs`

**Interfaces:**
- Consumes: `tools/wp81-theme-keys.txt` (Task 1).
- Produces: finding tag `theme-resource`; one new group, taking the checker from 62
  to 63 groups.

- [ ] **Step 1: Write the group**

Inserted after `checkXamlRoot()` and numbered 9, with groups 9–12 renumbered to
10–13 so the XAML checks stay together:

```js
// ── 9. XAML theme-resource keys ───────────────────────────────────────────
// `{ThemeResource X}` is resolved when the page LOADS, not when it compiles, so
// an unknown key is not something the build can report as an error. The VS2013
// XAML compiler does collide with it, but only as the internal error
// WMC9999 "the given key was not present in the dictionary", which
// tools/vm-build.cmd has always allow-listed as non-fatal noise. The designer
// names it properly: "The resource \"X\" could not be resolved".
//
// `{StaticResource X}` is deliberately NOT checked: the markup compiler resolves
// it at compile time, so it is already a build error when the key is missing.

function checkXamlThemeResources() {
  heading('XAML theme-resource keys (WP8.1)');
  checksRun++;
  let anyBad = false;

  const platform = loadWp81ThemeKeys();
  if (!platform) {
    fail('theme-resource', WP81_THEME_KEYS_FILE, '... regenerate with bash tools/wp81-theme-keys.sh');
    return;
  }

  for (const xaml of walk(ROOT, (f) => f.endsWith('.xaml'))) {
    const body = fs.readFileSync(xaml, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
    const local = new Set();
    for (const m of body.matchAll(/x:Key="([^"]+)"/g)) local.add(m[1]);   // app-defined keys are legal
    body.split(/\r?\n/).forEach((line, idx) => {
      for (const m of line.matchAll(/\{ThemeResource\s+([A-Za-z_][\w.]*)\s*\}/g)) {
        if (platform.has(m[1]) || local.has(m[1])) continue;
        fail('theme-resource', xaml, `{ThemeResource ${m[1]}} — ...`, idx + 1);
        anyBad = true;
      }
    });
  }
  if (!anyBad) ok('every {ThemeResource} key exists in the WP8.1 dictionaries');
}
```

Also in `tools/check-vb.mjs`:

- the header's numbered list gains `9. Theme resources`, and 9–12 become 10–13;
- the "WHAT IT CANNOT DO" bullet claiming it cannot catch a WP8.1 theme-resource key
  is **deleted** — it now can, and a header that understates the tool is the same
  class of error as one that overstates it;
- `No mechanical defects found in the twelve checked categories.` becomes
  `thirteen`;
- `checkXamlThemeResources();` joins the run list after `checkXamlRoot();`.

- [ ] **Step 2: Run it to watch it fail**

Run: `node tools/check-vb.mjs; echo "EXIT=$?"`
Expected: exit 1, `63 check group(s) run, 1 finding(s)`, and a finding naming
`BrowserForWP/MainPage.xaml:30` and `TextControlBackground`.

- [ ] **Step 3: Commit the oracle and the failing check**

```bash
git add tools/wp81-theme-keys.sh tools/wp81-theme-keys.txt tools/check-vb.mjs\
        docs/superpowers/plans/2026-09-28-xaml-theme-resources.md
git commit -m "test(xaml): check ThemeResource keys against the WP8.1 dictionaries"
```

---

### Task 3: Fix the key

**Files:**
- Modify: `BrowserForWP/MainPage.xaml:30`

- [ ] **Step 1: Change the one attribute**

In the address-bar container `Border` in the `Grid.Row="0"` header grid:

```xml
                <!--
                  TextBoxBackgroundThemeBrush, not TextControlBackground: the
                  latter is a UWP/Win10 name that Windows Phone 8.1 does not
                  define. ThemeResource is resolved at page load, so a missing
                  key does not fail the build — the page throws instead.
                -->
                <Border Grid.Column="0"
                        Background="{ThemeResource TextBoxBackgroundThemeBrush}"
                        CornerRadius="4" Padding="4,0">
```

- [ ] **Step 2: Run the check to watch it pass**

Run: `node tools/check-vb.mjs --quiet | tail -1`
Expected: `No mechanical defects found in the thirteen checked categories.`
(or `63 check group(s) run, 0 finding(s).` from the non-quiet form)

- [ ] **Step 3: Commit**

```bash
git add BrowserForWP/MainPage.xaml
git commit -m "fix(xaml): use a theme key Windows Phone 8.1 actually defines"
```

---

### Task 4: Measure WMC9999 instead of assuming it

**Files:**
- Modify: `tools/wmc9999-probe.sh`
- Modify: `tools/vm-build.cmd` (only if the measurement says so)

- [ ] **Step 1: Rebuild in the guest and count**

Run:
```bash
prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /t:Rebuild"
```
Expected: `=== BUILD_EXIT=0 ===`, no `BC` errors, and — the prediction —
**no `known-noise: WMC9999` line at all**.

- [ ] **Step 2: Record the outcome for whichever way it went**

If WMC9999 is gone, the diagnostic was never noise: it was this key, and the
allow-list hid it. Then:

- `tools/vm-build.cmd` drops the allow-list and treats a `WMC9999` line as an
  unexpected diagnostic like any other (the check already greps the log for it);
- `tools/wmc9999-probe.sh` gains the control its first version lacked — build once
  with a deliberately bad key and once without, and assert the XBF hashes differ
  *between those two*, which is what "cannot affect the shipped package" requires;
- `docs/MAINTAINING.md`'s WMC9999 section is corrected rather than deleted: the
  determinism was measured correctly and the conclusion drawn from it was wrong.

If WMC9999 survives, it really is independent noise; say so, keep the allow-list,
and record that the key fix did not silence it. **Do not re-allow-list it without
this measurement.**

- [ ] **Step 3: Commit**

```bash
git add tools/vm-build.cmd tools/wmc9999-probe.sh docs/MAINTAINING.md
git commit -m "build(xaml): stop allow-listing the unresolved-key diagnostic"
```

---

### Task 5: Documentation

**Files:**
- Modify: `docs/MAINTAINING.md`
- Modify: `.agents/skills/browserforwp/SKILL.md`

- [ ] **Step 1: Correct the WMC9999 record**

Replace the "WMC9999 is benign noise" conclusion with what was measured and what it
did and did not show, keeping the raw numbers.

- [ ] **Step 2: Document the new tool and the new check group**

In `SKILL.md`: a row in the verification table (`Any .xaml` →
`node tools/check-vb.mjs` already covers it; add the oracle to the tool inventory
and the `wp81-theme-keys.sh` regeneration command), the group-count change, and a
line in the platform-constraint section: a `{ThemeResource}` key is resolved at load
time, so it is a runtime failure on the handset, and the oracle is the *phone's*
dictionary.

In `MAINTAINING.md`: `wp81-theme-keys.sh` in the tool list, the check group in the
checker inventory, and the guest path to the dictionaries.

- [ ] **Step 3: Commit**

```bash
git add docs/MAINTAINING.md .agents/skills/browserforwp/SKILL.md
git commit -m "docs: correct the WMC9999 record and document the theme-key oracle"
```

---

### Task 6: Full verification, then push

- [ ] **Step 1: Local suite**

```bash
node tools/check-vb.mjs --quiet | tail -1
node tools/gen-vectors.mjs --check | tail -1
node tools/check-polyfill.mjs | tail -1
python3 tools/make_logo.py >/dev/null && git status --short BrowserForWP/Assets
```
Expected: `0 finding(s)`, `nothing written`, `is valid ES5`, and no asset diff.

- [ ] **Step 2: Every solution configuration, not just the ARM default**

`vm-build.cmd` forwards extra arguments to MSBuild and the later `-p:` wins, so each
of the six configurations can be built explicitly. `Debug|Any CPU` and `Debug|x86`
also have to be exercised because the IDE's active configuration comes from the
`.suo`, which is not in version control.

```bash
for spec in 'Debug|Any CPU' 'Debug|ARM' 'Debug|x86' 'Release|Any CPU' 'Release|ARM' 'Release|x86'; do
  cfg="${spec%%|*}"; plat="${spec##*|}"
  printf '### %s\n' "$spec"
  prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" \
    "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd /p:Configuration=$cfg /p:Platform=\"$plat\"" \
    2>&1 | grep -iE 'BUILD_EXIT|error |UNEXPECTED|Real compiler errors|WMC9999'
done
```
Expected: `BUILD_EXIT=0` and `none` six times.

- [ ] **Step 3: Push**

```bash
git push origin HEAD
git ls-remote origin refs/heads/main
```

## Outcome (recorded, not rewritten)

### The fix, and the check that guards it

Tasks 1–3 were carried out as written. The oracle reads **523** keys. The check took
`tools/check-vb.mjs` from 62 to 63 groups, failed on the unmodified tree exactly where
the IDE failed (`BrowserForWP/MainPage.xaml:30`, `TextControlBackground`), and passes
once the key is `TextBoxBackgroundThemeBrush`. The guest build reached `BUILD_EXIT=0`
with `Real compiler errors: none` and only the two deliberate `BC40000` warnings.

### Task 4's prediction was FALSIFIED, and that is the useful result

WMC9999 **survives** the fix. One build with the bad key (`TextControlBackground`)
and one without it both print:

```
Xaml Internal Error error WMC9999: La chiave specificata non era presente nel dizionario.
```

So Task 4's "it is gone" branch does not apply, the allow-list stays, and the commit
that branch prescribed was not made. **WMC9999 is genuinely independent of the XAML
theme keys**, and there is more evidence for that than the single pair of builds:
`tools/wmc9999-probe.sh` recorded `WMC9999=1` in 12 of 12 runs, every one of them taken
*with* `TextControlBackground` in `MainPage.xaml`. The diagnostic is now measured in both
source states, which is the control the original probe lacked.

### What the investigation found instead — a corrected record, not a new defect

`docs/MAINTAINING.md` documents this exact line of XAML, and its record is inverted.
**Round 4 is what put the wrong key there.** It swapped
`TextBoxBackgroundThemeBrush` *out* for `TextControlBackground`, and gave as its evidence:
"Verified by swapping the key to TextControlBackground: the diagnostic disappears, and
returns when the old key is restored."

Four measurements now say that observation was not caused by the swap:

1. the phone's own dictionaries **define** `TextBoxBackgroundThemeBrush`
   (`themeresources.xaml:264`) and WP8.1's own `TextBox` style **uses** it
   (`generic.xaml:2413`), while
2. `TextControlBackground` is defined in neither the 523 phone keys nor the Windows
   8.1 desktop dictionaries — the name is UWP/Win10-era, so it was borrowed in from
   the wrong platform, and
3. the probe's own 12 of 12 runs, recorded later in the same document, still showed
   the diagnostic **with the swapped key in place** — the swap's stated evidence is
   self-refuted by the measurement it sits beside, and
4. today's build, with the key removed again, shows the diagnostic anyway.

An intermittent diagnostic was read as a source-code signal, a working key was
replaced with a name that does not exist on the platform, and the false conclusion was
written down as "verified". The page then could not resolve its address-bar brush, which
is the error the IDE reports — and the only item in that 119-line report that this
repository could actually fix.

**The rule this leaves behind:** a diagnostic that comes and goes is not evidence for a
source change. Find the control — one build with the change, one without — before
concluding anything from it, and never rewrite a platform name on the strength of a log
line whose presence varies.

## What this plan does not do

It does not run the app on a handset, which is still the only way to confirm the
address bar renders as it did before, or to close `IeModeProbe`'s `documentMode`
check. It does not make `{StaticResource}` keys checkable off-Windows; they are
already build errors. And it cannot empty the reader's Visual Studio error list:
those window-global diagnostics are regenerated by an IDE build, so the fix has to be
verified there by rebuilding, not by reading this plan.
