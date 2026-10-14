@echo off
rem Creates .venv with the app, MIDI device support and the test tools.
rem Then: .venv\Scripts\jctool, or .venv\Scripts\activate
setlocal
cd /d "%~dp0.."
rem Python: %PYTHON% if set, else the "python" on PATH, else the newest from the py launcher
if not exist .venv\Scripts\python.exe (
    if defined PYTHON (
        "%PYTHON%" -m venv .venv || goto :error
    ) else (
        python -m venv .venv 2>nul || py -3 -m venv .venv || goto :error
    )
)
rem 32-bit Python: Kivy 2.3.0 has the last 32-bit wheels, and python-rtmidi has none (no MIDI devices)
set "EXTRAS=midi,test"
.venv\Scripts\python -c "import sys; sys.exit(sys.maxsize > 2**32)" && (
    set "EXTRAS=test"
    set "PIP_CONSTRAINT=%CD%\packaging\constraints-win32.txt"
)
.venv\Scripts\python -m pip install --upgrade pip || goto :error
.venv\Scripts\python -m pip install -e ".[%EXTRAS%]" || goto :error
echo.
echo Ready. Run the app with: .venv\Scripts\jctool   (or: .venv\Scripts\activate, then jctool)
exit /b 0
:error
echo Setup failed.
exit /b 1
