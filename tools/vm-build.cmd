@echo off
rem ═══════════════════════════════════════════════════════════════════════════
rem  BrowserForWP — build the solution inside the Windows guest.
rem
rem  Run from the macOS host with:
rem
rem    prlctl exec "<VM-ID|name>" "cmd.exe" "/c" ^
rem        "C:\Mac\Home\Documents\BrowserForWP\tools\vm-build.cmd"
rem
rem  Note the quoting: prlctl exec takes the command and its arguments as
rem  SEPARATE argv entries. Passing "cmd /c ver" as one string fails silently,
rem  which is why this is a batch file rather than a one-liner.
rem
rem  Extra args are forwarded to MSBuild, e.g.
rem    tools\vm-build.cmd /t:Rebuild
rem
rem  THIS SCRIPT DECIDES PASS/FAIL. It does not just relay MSBuild's exit code:
rem  it also fails when a real compiler error appears in the log, and it names
rem  the one diagnostic that is allowed to appear (WMC9999) so that a future
rem  reader cannot mistake known noise for a clean build, or a clean exit code
rem  for a clean log. See docs/MAINTAINING.md for the evidence behind the
rem  allow-list.
rem ═══════════════════════════════════════════════════════════════════════════

setlocal

set "REPO=C:\Mac\Home\Documents\BrowserForWP"
set "MSB=C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe"
set "CFG=Debug"
set "PLAT=ARM"
set "LOG=%TEMP%\browserforwp-build.log"

cd /d "%REPO%" || (echo CANNOT_CD_TO_REPO & exit /b 1)

echo === MSBuild ===
"%MSB%" /version /nologo

echo === Toolchain check ===
if exist "%MSB%" (echo msbuild12 OK) else (echo msbuild12 MISSING)
if exist "C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1" (echo wp81sdk OK) else (echo wp81sdk MISSING)
if exist "C:\Program Files (x86)\Windows Kits\8.1" (echo win81sdk OK) else (echo win81sdk MISSING)

echo === Building BrowserForWP.sln /p:Configuration=%CFG% /p:Platform=%PLAT% ===
"%MSB%" BrowserForWP.sln /nologo /v:minimal /p:Configuration=%CFG% /p:Platform=%PLAT% %* > "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
type "%LOG%"

echo.
echo === Diagnostic summary ===
rem WMC9999 is allow-listed. The VS2013 XAML compiler emits it from its second
rem pass. It appeared in 12 of 12 measured builds, it never changes this script's
rem exit code, and App.xbf / MainPage.xbf are byte-identical across every one of
rem them -- asserted, not assumed, by tools/wmc9999-probe.sh, which exits
rem non-zero if the compiled XAML ever varies. Evidence: docs/MAINTAINING.md.
findstr /C:"WMC9999" "%LOG%" >nul && echo known-noise: WMC9999 ^(allowed, see docs/MAINTAINING.md^)

echo.
echo === Real compiler errors ===
rem Any of these means the build is broken, whatever MSBuild's exit code says.
findstr /R /C:"error BC" /C:"error MSB" /C:"error APPX" "%LOG%" >nul
if not errorlevel 1 (
  echo UNEXPECTED COMPILER ERRORS ^(see the log above, and "%LOG%"^)
  set "RC=1"
) else (
  echo none
)

echo.
echo === Warnings ===
findstr /R /C:"warning BC" /C:"warning MSB" /C:"warning APPX" "%LOG%" || echo none
rem No warnings are expected. The two ResourceLoader BC40000s are suppressed at
rem their call site in Localizer.vb, with the reason recorded there and in
rem docs/MAINTAINING.md. Any warning here is new and worth a look.

echo === BUILD_EXIT=%RC% ===

endlocal & exit /b %RC%
