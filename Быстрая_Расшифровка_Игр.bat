@echo off
chcp 65001 >nul
title STORM DS DECRYPTOR 1.5.0 - Быстрая Расшифровка Игр
setlocal enabledelayedexpansion

cd /d "%~dp0"

if "%~1"=="" (
    echo ============================================================
    echo   STORM DS DECRYPTOR 1.5.0 ^(STORM TEAM^)
    echo ============================================================
    echo.
    echo Перетащите файлы .nds, .dsi, .srl, .app, .cia, .tad, .zip, .7z, .rar
    echo или папку с играми на этот файл, либо нажмите любую клавишу
    echo для запуска графического интерфейса.
    echo.
    echo ============================================================
    pause >nul
    start "" "STORM DS DECRYPTOR.exe"
    exit /b
)

echo ============================================================
echo   STORM DS DECRYPTOR 1.5.0 ^(STORM TEAM^)
echo   Многопоточная расшифровка образов Nintendo DS / DSi / DSiWare
echo ============================================================
echo.

"STORM DS DECRYPTOR.exe" --cli %*

echo.
echo ============================================================
echo Обработка завершена. Нажмите любую клавишу для выхода...
pause >nul
