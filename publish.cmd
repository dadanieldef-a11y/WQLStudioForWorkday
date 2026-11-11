@echo off
REM ── Package WQL Studio for colleagues ────────────────────────────────────
REM Publishes the Server project self-contained for win-x64 (the .NET 8 runtime
REM is bundled, so target machines need nothing installed), then zips it. Send
REM the zip; colleagues unzip and run WQLStudio.exe — a browser opens
REM on http://localhost:3000 automatically.
REM
REM Requires the .NET 8 SDK on THIS machine only (to build). Run from anywhere:
REM   publish.cmd
setlocal
cd /d "%~dp0"

set "OUT=releases\win-x64"
set "ZIP=releases\WQLStudio.zip"

echo(
echo Cleaning previous output...
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%ZIP%" del /q "%ZIP%"

echo(
echo Publishing self-contained win-x64 (Release)...
dotnet publish "src\ExtendExplorer.Server" -c Release -r win-x64 --self-contained true -o "%OUT%"
if errorlevel 1 (
  echo(
  echo Publish FAILED - see the errors above.
  exit /b 1
)

echo(
echo Zipping to %ZIP% ...
powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Zip FAILED.
  exit /b 1
)

echo(
echo ============================================================
echo Unzip and run  WQLStudio.exe
echo A browser opens on http://localhost:3000 automatically.
echo ============================================================
endlocal
