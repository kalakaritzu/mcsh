@echo off
setlocal

set ROOT=%~dp0
set PUBLISH=%ROOT%publish
set INSTALLER=%ROOT%installer

echo.
echo   McSH Launcher -- Build Installer
echo.

:: 1. Publish self-contained single-file EXE
echo   [1/2] Publishing...
dotnet publish "%ROOT%McSH.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%PUBLISH%" --nologo -v quiet
if errorlevel 1 ( echo   ERROR: Publish failed. & exit /b 1 )

:: 2. Build MSI
echo   [2/2] Building MSI...
wix build "%INSTALLER%\McSH.wxs" -ext WixToolset.UI.wixext -b "%INSTALLER%" -o "%INSTALLER%\McSH.msi"
if errorlevel 1 ( echo   ERROR: WiX build failed. & exit /b 1 )

echo.
echo   Done: installer\McSH.msi
echo.
endlocal
