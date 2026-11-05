@echo off
setlocal
title CJ Auto Provisioner Builder
cd /d "%~dp0"

echo ================================================================
echo  [CJ Auto Provisioner] Standalone Executable Builder
echo ================================================================
echo.

set "OUTPUT_DIR=%~dp0dist"
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

set "CSC_EXE=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC_EXE%" (
    set "CSC_EXE=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if exist "%CSC_EXE%" (
    echo [1/2] Compiling ultra-light standalone executable via Windows csc.exe...
    "%CSC_EXE%" /nologo /target:exe /platform:anycpu /optimize+ /win32manifest:app.manifest /out:"%OUTPUT_DIR%\CJAutoProvisioner.exe" "Program.cs"
    if exist "%OUTPUT_DIR%\CJAutoProvisioner.exe" (
        echo  - [SUCCESS] Built: %OUTPUT_DIR%\CJAutoProvisioner.exe (Ultra-light ~20KB)
    )
) else (
    echo [1/2] csc.exe not found on system.
)

echo.
echo [2/2] Publishing .NET 8 standalone executable...
set "DOTNET_CMD=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_CMD=%ProgramFiles%\dotnet\dotnet.exe"
)

if not "%DOTNET_CMD%"=="" (
    "%DOTNET_CMD%" publish "CJAutoProvisioner.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%OUTPUT_DIR%\net8" >nul 2>nul
    if exist "%OUTPUT_DIR%\net8\CJAutoProvisioner.exe" (
        copy /y "%OUTPUT_DIR%\net8\CJAutoProvisioner.exe" "%OUTPUT_DIR%\CJAutoProvisioner_Net8.exe" >nul 2>nul
        echo  - [SUCCESS] Built: %OUTPUT_DIR%\CJAutoProvisioner_Net8.exe (.NET 8 Single-File)
    )
)

echo.
echo ================================================================
echo  Build complete!
echo  Recommended deployment file: %OUTPUT_DIR%\CJAutoProvisioner.exe
echo ================================================================
echo.
endlocal
