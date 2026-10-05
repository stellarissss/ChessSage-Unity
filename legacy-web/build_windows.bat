@echo off
REM --- Switch to UTF-8 code page BEFORE reading any non-ASCII text ---
chcp 65001 >nul 2>nul

REM ==========================================================================
REM  ChessSage - Windows one-click build (Nuitka standalone onedir)
REM  Project: CHessGAme / 棋圣
REM
REM  Usage:
REM    1) Double-click this file, or run build_windows.bat in cmd
REM    2) First run creates .venv-build and installs dependencies
REM
REM  Options (append after the command):
REM    --python 3.12  Python version to use (default 3.12)
REM    --msvc         Use installed Visual Studio Build Tools
REM    --no-clean     Keep old dist output (default: clean it first)
REM    --no-cache     Disable Nuitka compile cache
REM    --tmp D:\nktmp Redirect compile temp dir to a roomy disk
REM    --no-sleep     Skip the sleep-suppression step
REM
REM  Notes:
REM    - Sleep is temporarily disabled during build, restored afterwards
REM    - This script NEVER shuts down your PC
REM    - Output: dist\棋圣\  (exe + dependencies + game assets)
REM ==========================================================================

setlocal EnableDelayedExpansion
pushd "%~dp0" 2>nul || cd /d "%~dp0"

echo ============================================================
echo   ChessSage - Windows build (Nuitka)
echo ============================================================
echo.

REM ----------------------------------------------------------------------
REM  0) Defaults and argument parsing
REM ----------------------------------------------------------------------
set "PYVER=3.12"
set "USE_MSVC=0"
REM Default to cleaning: a stale dist\棋圣 mixed with new files causes confusing
REM "missing module" failures. Use --no-clean to keep the old output.
set "DO_CLEAN=1"
set "USE_CCACHE=1"
set "TMPDIR="
set "DO_NOSLEEP=1"
set "SLEEP_TIMEOUT_CHANGED="

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--python"    ( set "PYVER=%~2" & shift & shift & goto parse_args )
if /i "%~1"=="--msvc"      ( set "USE_MSVC=1" & shift & goto parse_args )
if /i "%~1"=="--clean"     ( set "DO_CLEAN=1" & shift & goto parse_args )
if /i "%~1"=="--no-clean"  ( set "DO_CLEAN=0" & shift & goto parse_args )
if /i "%~1"=="--no-cache"  ( set "USE_CCACHE=0" & shift & goto parse_args )
if /i "%~1"=="--tmp"       ( set "TMPDIR=%~2" & shift & shift & goto parse_args )
if /i "%~1"=="--no-sleep"  ( set "DO_NOSLEEP=0" & shift & goto parse_args )
echo   [WARN] Unknown option ignored: %~1
shift
goto parse_args
:args_done

REM ----------------------------------------------------------------------
REM  0.0) pip mirror + common flags
REM ----------------------------------------------------------------------
REM All pip traffic goes through the Tsinghua mirror. Direct PyPI access
REM from CN often hangs at TCP level with no CPU/disk/network activity.
set "PIP_MIRROR=-i https://pypi.tuna.tsinghua.edu.cn/simple"
set "PIP_HOST=--trusted-host pypi.tuna.tsinghua.edu.cn"
set "PIP_COMMON=--disable-pip-version-check --timeout 30 --retries 5"
set "PIP_FALLBACK=-i https://mirrors.aliyun.com/pypi/simple/"
set "PIP_FALLBACK_HOST=--trusted-host mirrors.aliyun.com"

REM Environment-level mirror injection: Nuitka's own pip subprocess reads
REM PIP_INDEX_URL. Without this, its internal `pip install ziglang` goes to
REM raw PyPI and can stall silently for a long time (CPU/disk/net all 0).
set "PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple"
set "PIP_TRUSTED_HOST=pypi.tuna.tsinghua.edu.cn"
set "PIP_TIMEOUT=30"
set "PIP_RETRIES=5"

REM ----------------------------------------------------------------------
REM  0.1) Suppress system sleep during the build
REM ----------------------------------------------------------------------
echo   [INFO] Enabling build-time sleep suppression...
if "%DO_NOSLEEP%"=="1" (
    powercfg /change standby-timeout-ac 0 >nul 2>nul
    if !errorlevel! equ 0 (
        set "SLEEP_TIMEOUT_CHANGED=1"
        echo   [ OK ] Sleep disabled ^(AC standby timeout set to never^).
    ) else (
        echo   [WARN] Could not change power plan; try running as Administrator.
    )
    powercfg /requestsoverride PROCESS python.exe SYSTEM >nul 2>nul
) else (
    echo   [INFO] Skipped sleep suppression ^(--no-sleep^).
)
echo.

REM ----------------------------------------------------------------------
REM  1) Locate Python
REM ----------------------------------------------------------------------
echo [1/7] Locating Python %PYVER% ...
set "PYCMD="
set "PYACTUAL="

where py >nul 2>nul
if !errorlevel! equ 0 (
    py -%PYVER% -c "import sys" >nul 2>nul
    if !errorlevel! equ 0 (
        set "PYCMD=py -%PYVER%"
        goto python_found
    )
)

where python >nul 2>nul
if !errorlevel! equ 0 (
    for /f "tokens=2" %%v in ('python -V 2^>^&1') do set "PYACTUAL=%%v"
    set "PYCMD=python"
    goto python_found
)

echo   [FAIL] Python not found. Please install Python 3.11 / 3.12:
echo       https://www.python.org/downloads/
echo       During install, check "Add python.exe to PATH".
echo.
pause
exit /b 1

:python_found
if "!PYCMD!"=="py -%PYVER%" (
    echo   [ OK ] Using py -%PYVER%
) else (
    echo   [ OK ] Using python !PYACTUAL!
)

REM ----------------------------------------------------------------------
REM  2) Virtual environment
REM ----------------------------------------------------------------------
echo.
echo [2/7] Preparing build virtual environment (.venv-build) ...
if not exist ".venv-build\Scripts\python.exe" (
    echo   [INFO] Creating .venv-build ^(first run only^)...
    %PYCMD% -m venv .venv-build
    if !errorlevel! neq 0 (
        echo   [FAIL] Failed to create virtual environment.
        echo       Check that Python is installed correctly.
        pause
        exit /b 1
    )
    echo   [ OK ] Created .venv-build
) else (
    echo   [ OK ] .venv-build already exists, reusing it.
)
set "VPY=.venv-build\Scripts\python.exe"

REM ----------------------------------------------------------------------
REM  3) Install dependencies
REM ----------------------------------------------------------------------
echo.
echo [3/7] Installing / verifying dependencies (first run is slow) ...
echo       Using requirements-build.txt (lean set; no rembg/onnxruntime).
echo       All pip traffic goes through the Tsinghua mirror for speed.
echo.

echo       [3.1/3.3] Upgrading pip / setuptools / wheel ...
"%VPY%" -m pip install --upgrade pip setuptools wheel !PIP_MIRROR! !PIP_HOST! !PIP_COMMON!
if !errorlevel! neq 0 (
    echo   [FAIL] pip upgrade failed.
    pause
    exit /b 1
)

echo.
echo       [3.2/3.3] Installing runtime dependencies ...
set "BUILD_REQ=requirements-build.txt"
if not exist "%BUILD_REQ%" set "BUILD_REQ=requirements.txt"
"%VPY%" -m pip install -r "%BUILD_REQ%" !PIP_MIRROR! !PIP_HOST! !PIP_COMMON!
if !errorlevel! neq 0 (
    echo   [WARN] Mirror install failed; retrying with Aliyun mirror...
    "%VPY%" -m pip install -r "%BUILD_REQ%" !PIP_FALLBACK! !PIP_FALLBACK_HOST! !PIP_COMMON!
    if !errorlevel! neq 0 (
        echo   [FAIL] Dependency installation failed.
        pause
        exit /b 1
    )
)

echo.
echo       [3.3/3.3] Installing build toolchain (Nuitka) ...
REM ccache is a C program, not a PyPI package (Windows uses Zig/MSVC anyway).
REM Only the deps Nuitka actually needs:
set "NKPKGS=nuitka ordered-set zstandard"
"%VPY%" -m pip install %NKPKGS% !PIP_MIRROR! !PIP_HOST! !PIP_COMMON!
if !errorlevel! neq 0 (
    echo   [WARN] Retrying with Aliyun mirror...
    "%VPY%" -m pip install %NKPKGS% !PIP_FALLBACK! !PIP_FALLBACK_HOST! !PIP_COMMON!
    if !errorlevel! neq 0 (
        echo   [FAIL] Build toolchain installation failed.
        pause
        exit /b 1
    )
)

REM Verify Nuitka is callable early, so failures surface now not mid-compile.
REM NOTE: `nuitka --version` does NOT download Zig; only an actual build does.
echo.
echo   [INFO] Verifying Nuitka...
"%VPY%" -m nuitka --version
if !errorlevel! neq 0 (
    echo   [FAIL] Nuitka is not runnable. Check the install above.
    pause
    exit /b 1
)
echo.
echo   [ OK ] Dependencies ready.

REM ----------------------------------------------------------------------
REM  4) C compiler selection
REM ----------------------------------------------------------------------
echo.
echo [4/7] Checking C compiler ...
if "%USE_MSVC%"=="1" (
    echo   [INFO] MSVC mode; requires Visual Studio Build Tools with C++ desktop workload.
    set "NK_COMPILER="
) else (
    echo   [INFO] Zig mode ^(auto-downloaded by Nuitka; no Visual Studio needed^).
    set "NK_COMPILER=--zig"
)

REM ----------------------------------------------------------------------
REM  5) Clean old output
REM ----------------------------------------------------------------------
echo.
echo [5/7] Cleaning old output ...
if "%DO_CLEAN%"=="1" (
    if exist "build\nuitka" (
        rmdir /s /q "build\nuitka" 2>nul
        echo   [ OK ] Removed build\nuitka
    )
    if exist "dist\棋圣" (
        rmdir /s /q "dist\棋圣" 2>nul
        echo   [ OK ] Removed dist\棋圣
    )
) else (
    echo   [INFO] --clean not given; keeping incremental cache ^(faster rebuild^).
)

REM ----------------------------------------------------------------------
REM  6) Run the Nuitka build
REM ----------------------------------------------------------------------
echo.
echo [6/7] Starting Nuitka build (expect 10-25 minutes) ...
echo       Lots of C compiler output is normal. Do not close this window.
echo       A separate heartbeat window will show elapsed time.
echo.

REM Argument contract with nuitka_build.py:
REM   Nuitka has no --enable-cache option (caching is on by default);
REM   only --disable-cache exists. So pass --no-cache only when disabling.
set "NK_ARGS=--windows"
if defined NK_COMPILER set "NK_ARGS=%NK_ARGS% %NK_COMPILER%"
if "%USE_CCACHE%"=="0" set "NK_ARGS=%NK_ARGS% --no-cache"
if defined TMPDIR set NK_ARGS=%NK_ARGS% --tmp "%TMPDIR%"
if "%DO_CLEAN%"=="1" set "NK_ARGS=%NK_ARGS% --clean"
set "NK_ARGS=%NK_ARGS% %PIP_HOST%"

REM --- Heartbeat: a visible clock so a silent compile never looks hung ---
REM The script is emitted by Python (nuitka_build.py --emit-heartbeat) instead
REM of being echoed from batch: generating it inline means fighting delayed
REM expansion, redirection AND parenthesis escaping at once, which is exactly
REM where parsing bugs hide.
set "HB_SCRIPT=%TEMP%\chesssage_heartbeat.bat"
"%VPY%" nuitka_build.py --emit-heartbeat "%HB_SCRIPT%"
if exist "%HB_SCRIPT%" (
    start "ChessSage Heartbeat" cmd /c "%HB_SCRIPT%"
    echo   [INFO] Heartbeat window opened ^(shows elapsed time^).
) else (
    echo   [WARN] Could not create heartbeat script; build continues without it.
)

"%VPY%" nuitka_build.py %NK_ARGS%
set "BUILD_RC=!errorlevel!"

REM --- Stop the heartbeat window ---
taskkill /f /fi "WINDOWTITLE eq ChessSage Heartbeat*" >nul 2>nul
if exist "%HB_SCRIPT%" del /q "%HB_SCRIPT%" >nul 2>nul

if !BUILD_RC! neq 0 (
    echo.
    echo   [FAIL] Build failed ^(exit code !BUILD_RC!^). Common causes:
    echo       1. Network blocked downloading Zig; add --msvc to use Visual Studio.
    echo       2. Not enough disk space; add --tmp D:\nktmp to use another disk.
    echo       3. Missing pywebview ^(desktop window mode only^); run:
    echo          .venv-build\Scripts\python.exe -m pip install pywebview
    echo       4. Long silence on first build is normal; the second is much faster.
    echo.
    goto finish
)

REM ----------------------------------------------------------------------
REM  7) Report
REM ----------------------------------------------------------------------
echo.
echo [7/7] Build finished.
if exist "dist\棋圣" (
    echo    Output   : %CD%\dist\棋圣
    echo    Launcher : dist\棋圣\棋圣.exe
    echo.
    echo    Distribute the whole "dist\棋圣" folder.
    echo    Players double-click 棋圣.exe ^(or 启动游戏.bat^) to run.
    echo.
    goto finish
) else (
    echo   [WARN] dist\棋圣 not found. Check the Nuitka output above.
)

REM ----------------------------------------------------------------------
REM  Cleanup: restore power plan, keep the window open
REM ----------------------------------------------------------------------
:finish
echo.
echo ------------------------------------------------------------
powercfg /requestsoverride PROCESS python.exe >nul 2>nul
if defined SLEEP_TIMEOUT_CHANGED (
    powercfg /change standby-timeout-ac 30 >nul 2>nul
    echo   [INFO] Power plan restored ^(sleep timeout 30 minutes^).
) else (
    echo   [INFO] Sleep suppression lifted ^(power plan unchanged^).
)
echo.
echo Done. This window will stay open so you can read the result.
echo Press any key to close...
pause >nul
popd 2>nul
endlocal
