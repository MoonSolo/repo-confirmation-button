@echo off
REM ExtractionConfirm Build Script
REM Compiles the mod in Release and stages the DLL for the Thunderstore package.
REM
REM The mod is self-contained: one DLL, no AssetBundle, no companion library.

setlocal enabledelayedexpansion

echo.
echo ===== ExtractionConfirm Build =====
echo.

REM Find dotnet executable
for /f "delims=" %%A in ('where dotnet') do set "DOTNET_PATH=%%A"

if not defined DOTNET_PATH (
    echo ERROR: dotnet command not found. Please ensure .NET SDK is installed and in PATH.
    pause
    exit /b 1
)

echo Using dotnet: !DOTNET_PATH!
echo.

REM Change to script directory using the working directory of the batch file
pushd "%~dp0."

REM Build in Release mode. Override the game location with -p:RepoDir="..."
echo Building ExtractionConfirm...
call "!DOTNET_PATH!" build -c Release

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Build failed!
    popd
    pause
    exit /b %ERRORLEVEL%
)

REM Stage the DLL so the Thunderstore package can be zipped as-is.
if not exist "Thunderstore\plugins" mkdir "Thunderstore\plugins"
copy /y "bin\Release\ExtractionConfirm.dll" "Thunderstore\plugins\" >nul

echo.
echo ===== Build Successful! =====
echo Output:    bin\Release\ExtractionConfirm.dll
echo Staged in: Thunderstore\plugins\ExtractionConfirm.dll
echo.
echo Install: drop the single DLL into REPO\BepInEx\plugins\.
echo No AssetBundle and no companion library are needed.
echo.

popd
pause
exit /b 0