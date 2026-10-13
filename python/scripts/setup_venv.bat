@echo off
rem Creates python\.venv with the app, MIDI device support and the test tools.
rem Then: .venv\Scripts\jctool, or .venv\Scripts\activate
setlocal
cd /d "%~dp0.."
if not exist .venv\Scripts\python.exe (
    py -3 -m venv .venv 2>nul || python -m venv .venv || goto :error
)
.venv\Scripts\python -m pip install --upgrade pip || goto :error
.venv\Scripts\python -m pip install -e ".[midi,test]" || goto :error
echo.
echo Ready. Run the app with: .venv\Scripts\jctool   (or: .venv\Scripts\activate, then jctool)
exit /b 0
:error
echo Setup failed.
exit /b 1
