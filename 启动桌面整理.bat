@echo off
chcp 65001 >nul
cd /d "%~dp0"
where py >nul 2>nul
if not errorlevel 1 (
    py -3 main.py
) else (
    where python >nul 2>nul
    if errorlevel 1 (
        echo 请先安装 Python 3.11 或更高版本：https://www.python.org/downloads/windows/
        echo 安装时勾选 Add python.exe to PATH，然后重新双击本文件。
        pause
        exit /b 1
    )
    python main.py
)
if errorlevel 1 (
    echo 启动失败，请截图这段提示以便排查。
    pause
    exit /b 1
)
