@echo off
setlocal enabledelayedexpansion

set "ARG_WIM=%~1"
if not "!ARG_WIM!"=="" (
    set "TARGET_WIM=!ARG_WIM!"
) else (
    set "TARGET_WIM=default.wim"
)

set "TARGET_WIM=%TARGET_WIM:"=%"
echo TARGET_WIM with percent: [%TARGET_WIM%]
echo TARGET_WIM with exclam: [!TARGET_WIM!]
if not exist "%TARGET_WIM%" (
    echo [ERROR] NOT EXIST WITH PERCENT
)
if not exist "!TARGET_WIM!" (
    echo [ERROR] NOT EXIST WITH EXCLAM
)

