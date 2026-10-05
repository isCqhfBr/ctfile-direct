@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
title 城通网盘 多账号加速下载

set "DIR=%~dp0"
set "URLFILE=%TEMP%\ctfile-url.txt"
set "NAMEFILE=%TEMP%\ctfile-name.txt"
set "SRCFILE=%TEMP%\ctfile-sources.json"
set "SAVEDIR=%USERPROFILE%\Downloads"

where node >nul 2>nul && set "NODE=node"
if not defined NODE if exist "%ProgramFiles%\nodejs\node.exe" set "NODE=%ProgramFiles%\nodejs\node.exe"
if not defined NODE if exist "%LOCALAPPDATA%\Programs\nodejs\node.exe" set "NODE=%LOCALAPPDATA%\Programs\nodejs\node.exe"
if not defined NODE if exist "%DIR%node-path.txt" for /f "usebackq delims=" %%p in ("%DIR%node-path.txt") do set "NODE=%%p"
if not defined NODE (
    echo [错误] 没找到 node，请先安装 Node.js 18+ 并确保它在 PATH 里。
    echo        下载地址: https://nodejs.org/
    echo.
    echo        已经装了但不在 PATH 里的话，把 node.exe 的完整路径
    echo        单独一行写进 %DIR%node-path.txt 即可。
    echo.
    pause
    exit /b 1
)

echo ============================================================
echo         城通网盘 多账号加速下载
echo ============================================================
echo.

set "LINK=%~1"
if "%LINK%"=="" set /p "LINK=请粘贴城通分享链接（或分享码），然后回车： "

if "%LINK%"=="" (
    echo 没有输入内容，已退出。
    pause
    exit /b 1
)

echo.
"%NODE%" "%DIR%resolve.mjs" "%LINK%"
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)

if not exist "%SRCFILE%" (
    echo 解析异常：没有生成直链来源文件。
    pause
    exit /b 1
)
clip < "%URLFILE%"

set "FN=ctfile-download.bin"
if exist "%NAMEFILE%" for /f "usebackq delims=" %%n in ("%NAMEFILE%") do set "FN=%%n"

echo.
set "ANS="
set /p "ANS=现在下载吗？(Y=多账号并发下载到「下载」文件夹 / 直接回车=否) "
if /i not "%ANS%"=="Y" goto :done

echo.
echo 保存到: %SAVEDIR%\%FN%
echo.
"%NODE%" "%DIR%下载.mjs" --sources "%SRCFILE%" "%SAVEDIR%\%FN%"

:done
echo.
pause
