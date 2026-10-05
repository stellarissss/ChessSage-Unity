@echo off
chcp 65001 >nul
title 棋圣 · 一键打包
echo ================================================
echo  棋圣 ChessSage · PyInstaller 一键打包
echo  ================================================
echo  需在 Windows 上运行，产出 棋圣.exe（onedir）
echo  请确保已装 Python 3.10+，并安装依赖：
echo    pip install pyinstaller fastapi "uvicorn[standard]" httpx pydantic jsonschema jsonpatch
echo ================================================
cd /d "%~dp0"

REM html转码脚本所在即 pyinstaller 目录；尝试定位项目根
set "ROOT=%~dp0.."
if exist "%ROOT%\main.py" (
    cd /d "%ROOT%"
) else (
    cd /d "%~dp0"
)

if not exist "main.py" (
    echo [错误] 未找到项目根（main.py）。
    pause
    exit /b 1
)

python pyinstaller\build_game.py
if errorlevel 1 (
    echo.
    echo 打包失败，请检查上方日志。
    pause
    exit /b 1
)
echo.
echo 打包完成！产物位于 dist\chesssage\ 目录。
pause