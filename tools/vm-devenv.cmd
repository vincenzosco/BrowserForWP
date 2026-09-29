@echo off
rem ═══════════════════════════════════════════════════════════════════════════
rem  BrowserForWP — the IDE's own loader, which is the only oracle for two
rem  things MSBuild cannot see.
rem
rem  Run from the macOS host with:
rem
rem    prlctl exec "{66a2f493-162c-4b3f-ba40-0a26020cc818}" "cmd.exe" "/c" ^
rem        "C:\Mac\Home\Documents\BrowserForWP\tools\vm-devenv.cmd"
rem
rem  WHY THIS IS A BATCH FILE AND NOT A COMMAND LINE. It used to be one, in
rem  docs/MAINTAINING.md, and it cannot be: `cmd.exe /c` reaches the guest with
rem  the argument already split by the host shell, so the quotes around
rem  `C:\Program Files (x86)\...` and around `"Debug|ARM"` are gone before MSBuild
rem  or devenv sees them. The first form tried in Round 14 was
rem  `cmd /c "... && ""<path>"" ..."` and the guest answered `"""C:\Program" non
rem  e riconosciuto come comando interno o esterno`. A file has no quoting to lose.
rem
rem  WHAT IT CATCHES, and why MSBuild cannot:
rem    * BrowserForWP.sln naming a project FACTORY that VS2013 has not registered
rem      loads nothing at all -- "Build: 0 succeeded or up-to-date". Six
rem      configurations of BUILD_EXIT=0 will be green while the IDE shows an
rem      empty solution (Round 12).
rem    * A .vbproj whose FLAVOUR property is named anywhere ahead of its own
rem      element -- including inside a comment -- because the project factory
rem      locates it by scanning the file as TEXT. The IDE says
rem      "The application for the project is not installed." (Round 12, again.)
rem
rem  PASS: "Rebuild All: 7 succeeded, 0 failed, 0 skipped" and no "not installed"
rem  line. It exits 1 on either failure. (The wording is "Rebuild All", not
rem  "Build", because of /Rebuild below -- measured, not guessed.)
rem
rem  /Rebuild, AND NOT /build, and that is the whole reason the count is exact: a
rem  plain /build prints no "Build started" line for a project it decides is already
rem  current, so a run of a clean tree reports "6 succeeded, 0 failed, 1 up-to-date"
rem  -- a correct tree failing an oracle that counted. /Rebuild touches all seven,
rem  and the first version of this file (which counted "Build started" lines and
rem  demanded 7) is what found that out.
rem
rem  NOTE: devenv REWRITES the projects it opens (a BOM, CRLF, a
rem  <Folder Include="My Project\" /> item). Run `git checkout -- '*.vbproj'`
rem  afterwards, or a measurement changes four project files.
rem ═══════════════════════════════════════════════════════════════════════════

setlocal

set "REPO=C:\Mac\Home\Documents\BrowserForWP"
set "DEVENV=C:\Program Files (x86)\Microsoft Visual Studio 12.0\Common7\IDE\devenv.com"
set "LOG=%TEMP%\browserforwp-devenv.log"
set "RC=0"

cd /d "%REPO%" || (echo CANNOT_CD_TO_REPO & exit /b 1)

if exist "%DEVENV%" (echo devenv12 OK) else (echo devenv12 MISSING & exit /b 1)

echo === devenv.com BrowserForWP.sln /rebuild "Debug|ARM" ===
"%DEVENV%" BrowserForWP.sln /rebuild "Debug|ARM" > "%LOG%" 2>&1
type "%LOG%"

echo.
echo === Diagnostic summary ===
rem WMC9999 is the VS2013 XAML compiler's second pass, allow-listed here for the
rem same reason vm-build.cmd allows it: it appeared in 12 of 12 measured builds and
rem the compiled XAML is byte-identical across all of them.
findstr /C:"WMC9999" "%LOG%" >nul && echo known-noise: WMC9999 ^(allowed, see docs/MAINTAINING.md^)

echo.
echo === Project load ===
rem The failure this script exists for. A project the IDE refuses puts this line
rem in the log while the build reports a cheerful zero.
rem
rem `findstr ... && ( ) else ( )` is NOT valid cmd: `else` may only follow an `if`,
rem and the first version of this file died with "else non atteso" instead of
rem reporting anything. Same shape as vm-build.cmd, which learned it earlier.
findstr /C:"not installed" "%LOG%" >nul
if not errorlevel 1 (
  echo PROJECT_NOT_LOADED: a project was refused by the IDE's project system
  set "RC=1"
) else (
  echo every project was accepted by the project system
)

echo.
echo === Build ===
rem The summary line, once, from /rebuild. A solution with the wrong factory GUID
rem in it prints "Build: 0 succeeded or up-to-date, 0 failed, 0 skipped" instead,
rem and a project the project system refuses is not built at all.
findstr /R /C:"Rebuild All: 7 succeeded, 0 failed, 0 skipped" "%LOG%" >nul
if not errorlevel 1 (
  echo seven projects loaded and built
) else (
  echo UNEXPECTED PROJECT COUNT: expected "Rebuild All: 7 succeeded, 0 failed, 0 skipped" ^(see "%LOG%"^)
  set "RC=1"
)

echo === DEVENV_EXIT=%RC% ===

endlocal & exit /b %RC%
