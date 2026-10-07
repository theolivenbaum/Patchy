@echo off
rem Uploads the newest Linux Flatpak bundle (built by scripts\remote\release-linux.bat)
rem under the stable "latest" name PatchyLinux.flatpak that latest_version.json points at,
rem then installs the signed Flatpak repository built beside it at rtsoft.com/flatpak
rem (what `flatpak update` reads; packaging\linux\README.md).
rem cd to the repo root (this script lives in scripts\release) so the relative paths
rem below resolve from any cwd.
cd /d "%~dp0..\.."
set "LINUX_BUNDLE="
for /f "delims=" %%F in ('dir /b /o:d build\package\Patchy-*.flatpak 2^>nul') do set "LINUX_BUNDLE=build\package\%%F"
if not defined LINUX_BUNDLE (
  echo No Patchy-*.flatpak found in build\package - run scripts\remote\release-linux.bat first.
  if /i not "%~1"=="nopause" pause
  exit /b 1
)
echo Uploading %LINUX_BUNDLE% as PatchyLinux.flatpak
copy /y "%LINUX_BUNDLE%" build\package\PatchyLinux.flatpak >nul
if errorlevel 1 (
  echo ERROR: could not stage build\package\PatchyLinux.flatpak from "%LINUX_BUNDLE%".
  if /i not "%~1"=="nopause" pause
  exit /b 1
)
rem upload-one-file.bat fails loudly on a bad transfer and verifies the bytes that
rem landed; see its header for why a plain scp is not enough.
call "%~dp0upload-one-file.bat" build\package\PatchyLinux.flatpak files
if errorlevel 1 (
  echo.
  echo Linux upload FAILED - https://rtsoft.com/files/PatchyLinux.flatpak was not
  echo updated, or was left in a bad state. Do not announce this release.
  if /i not "%~1"=="nopause" pause
  exit /b 1
)
echo Linux upload OK: https://rtsoft.com/files/PatchyLinux.flatpak

rem The repository. release-linux.ps1 refuses to finish without the tar, so a missing
rem one means the build did not complete; never publish a bundle whose repository is
rem a version behind.
set "LINUX_REPO_TAR="
for /f "delims=" %%F in ('dir /b /o:d build\package\Patchy-*-flatpak-repo.tar 2^>nul') do set "LINUX_REPO_TAR=build\package\%%F"
if not defined LINUX_REPO_TAR goto repo_missing
echo Uploading %LINUX_REPO_TAR% as PatchyFlatpakRepo.tar
copy /y "%LINUX_REPO_TAR%" build\package\PatchyFlatpakRepo.tar >nul
if errorlevel 1 goto repo_fail
ssh rtsoft@rtsoft.com "mkdir -p www/flatpak"
if errorlevel 1 goto repo_fail
call "%~dp0upload-one-file.bat" build\package\PatchyFlatpakRepo.tar flatpak
if errorlevel 1 goto repo_fail
rem The MIME types that make a clicked .flatpakref link open in a software center.
call "%~dp0upload-one-file.bat" packaging\linux\site\.htaccess flatpak
if errorlevel 1 goto repo_fail
rem Unpack beside the live copy, check the signed summary is there, then swap the
rem directory in with two renames so a client never reads a half-written repository.
rem The descriptor files move last: they only name the repository URL and key.
echo Installing the repository on the server ...
ssh rtsoft@rtsoft.com "cd www/flatpak && rm -rf incoming repo.old && mkdir incoming && tar -xf PatchyFlatpakRepo.tar -C incoming && test -s incoming/repo/summary && test -s incoming/repo/summary.sig && test -s incoming/patchy.flatpakrepo && test -s incoming/com.rtsoft.patchy.flatpakref && if [ -d repo ]; then mv repo repo.old; fi && mv incoming/repo repo && mv incoming/patchy.flatpakrepo incoming/com.rtsoft.patchy.flatpakref . && rm -rf incoming repo.old PatchyFlatpakRepo.tar"
if errorlevel 1 goto repo_fail
rem The claim "it shipped" needs evidence from the public URL, not the ssh session.
echo Verifying the live repository ...
curl -sfI https://rtsoft.com/flatpak/repo/summary >nul || goto repo_fail
curl -sfI https://rtsoft.com/flatpak/repo/summary.sig >nul || goto repo_fail
curl -sfI https://rtsoft.com/flatpak/com.rtsoft.patchy.flatpakref >nul || goto repo_fail
curl -sfI https://rtsoft.com/flatpak/patchy.flatpakrepo >nul || goto repo_fail
rem A convenience, not a correctness gate: flatpak itself ignores the type, so a host
rem without mod_mime only loses the click-to-install behavior in browsers.
curl -sI https://rtsoft.com/flatpak/com.rtsoft.patchy.flatpakref | findstr /i "application/vnd.flatpak.ref" >nul
if errorlevel 1 echo WARNING: the flatpakref is not served as application/vnd.flatpak.ref; check packaging\linux\site\.htaccess on the server.
echo Linux repository OK: https://rtsoft.com/flatpak/com.rtsoft.patchy.flatpakref
if /i not "%~1"=="nopause" pause
exit /b 0

:repo_missing
echo.
echo No Patchy-*-flatpak-repo.tar found in build\package - run scripts\remote\release-linux.bat first.
echo The bundle was uploaded but the Flatpak repository was NOT updated.
if /i not "%~1"=="nopause" pause
exit /b 1

:repo_fail
echo.
echo Flatpak repository upload FAILED - https://rtsoft.com/flatpak/repo was not updated,
echo or was left in a bad state. Installed copies will not see this release through
echo flatpak update. Do not announce this release.
if /i not "%~1"=="nopause" pause
exit /b 1
