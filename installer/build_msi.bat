@echo off
:: ============================================================================
:: VideoConverter One-Click MSI Installer Builder
:: Double-click to compile VideoConverter_Setup.msi
:: ============================================================================

title VideoConverter MSI Builder
cd /d "%~dp0"

echo Building VideoConverter_Setup.msi...
wix build Package.wxs -o VideoConverter_Setup.msi

if %ERRORLEVEL% EQU 0 (
    echo.
    echo SUCCESS: VideoConverter_Setup.msi built successfully in installer folder!
) else (
    echo.
    echo ERROR: Failed to build MSI.
)

pause
