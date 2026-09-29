@echo off
rem ═══════════════════════════════════════════════════════════════════════════
rem  BrowserForWP — measure which VB keywords vbc 12 refuses as a NAME.
rem
rem  Run from the macOS host with:
rem
rem    prlctl exec "66a2f493-162c-4b3f-ba40-0a26020cc818" cmd /c ^
rem        "C:\Mac\Home\Documents\BrowserForWP\tools\keyword-probe.cmd"
rem
rem  See tools/keyword-probe/batch1.vb for what the measurement is for and how to
rem  read it. In one sentence: tools/check-vb.mjs group 17 holds a list of words
rem  it treats as illegal identifiers, and that list must come from the compiler
rem  rather than from the language reference, because `Out` is in the reference's
rem  reserved list and `Dim out(31) As Byte` compiles in this repository.
rem
rem  WHY BATCHES. vbc 12 is the pre-Roslyn compiler and it stops after about a
rem  hundred errors -- with no message. A single-file probe reported one error per
rem  candidate up to a point and then nothing, which reads as "legal" for words
rem  that were never compiled. Each batch therefore holds well under sixty
rem  declarations, and each batch ends with a SENTINEL whose line number must
rem  appear in the log. A missing sentinel means that batch is void, not clean.
rem
rem  This script exits 0 even when vbc reports errors: the errors ARE the
rem  measurement. What decides whether the measurement is usable is the sentinel
rem  check (must be REFUSED) and the control check (must be CLEAN) printed at the
rem  end of each batch.
rem ═══════════════════════════════════════════════════════════════════════════

setlocal enabledelayedexpansion

set "REPO=C:\Mac\Home\Documents\BrowserForWP"
set "VBC=C:\Program Files (x86)\MSBuild\12.0\Bin\vbc.exe"

cd /d "%REPO%" || (echo CANNOT_CD_TO_REPO & exit /b 1)

echo === Toolchain ===
if exist "%VBC%" (echo vbc12 OK) else (echo vbc12 MISSING & exit /b 1)
"%VBC%" /version /nologo

set "SENTINEL_BROKEN=0"

for %%b in (1 2 3) do (
  set "SRC=tools\keyword-probe\batch%%b.vb"
  set "LOG=%TEMP%\kwprobe-%%b.log"

  echo.
  echo ================= batch %%b =================
  for /f "tokens=1 delims=:" %%L in ('findstr /n /C:"Dim next As Integer" "!SRC!"') do set "SENTINEL_LINE=%%L"
  for /f "tokens=1 delims=:" %%L in ('findstr /n /C:"Dim out As Integer" "!SRC!"') do set "CONTROL_LINE=%%L"
  for /f %%C in ('findstr /C:"Dim " "!SRC!" ^| find /c /v ""') do set "DECLS=%%C"

  echo declarations in this batch: !DECLS! ^(must be well under 100: the cap is why this is batched^)
  echo sentinel line: !SENTINEL_LINE!   control line: !CONTROL_LINE!

  "%VBC%" /t:library /nologo /out:"%TEMP%\kwprobe-%%b.dll" "!SRC!" > "!LOG!" 2>&1
  rem Warnings are printed too, because BC42024 on a candidate is a signal that
  rem the candidate was compiled AND accepted. What is READ as the measurement
  rem is only the `error` lines; see tools/keyword-probe/batch1.vb.
  type "!LOG!"

  echo ---- batch %%b self-check ----
  rem Both checks require "error", not just the file(line) prefix. The first
  rem version of these two lines matched warnings as well, and EVERY legal word
  rem gets warning BC42024 ("local variable is never used"), so batch after batch
  rem reported its own controls as refused and the verdict was VOID. A checker
  rem that reads warnings as errors is wrong in the direction that hides real
  rem errors, which is the worse direction.
  findstr /R /C:"batch%%b.vb(!SENTINEL_LINE!) : error" "!LOG!" >nul
  if errorlevel 1 (
    echo SENTINEL NOT REFUSED -- this batch was truncated. Its "no error"
    echo answers mean "not compiled", not "legal". VOID.
    set "SENTINEL_BROKEN=1"
  ) else (
    echo sentinel refused as required: this batch reached its end
  )
  findstr /R /C:"batch%%b.vb(!CONTROL_LINE!) : error" "!LOG!" >nul
  if errorlevel 1 (
    echo controls clean: the words known to be legal are accepted
  ) else (
    echo CONTROL REFUSED -- the harness is broken, not the language. VOID.
    set "SENTINEL_BROKEN=1"
  )
)

echo.
if "!SENTINEL_BROKEN!"=="1" (
  echo === VERDICT: AT LEAST ONE BATCH IS VOID. The list is NOT usable. ===
) else (
  echo === VERDICT: all three batches self-checked. Refused line numbers are the measure. ===
)

endlocal & exit /b 0
