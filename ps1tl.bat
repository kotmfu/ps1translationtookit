@echo off
rem ps1tl launcher: installs missing packages, starts the GUI, opens the browser.
rem Optional: drag a .cue file onto this .bat to open it straight away.
setlocal
cd /d "%~dp0"

where python >nul 2>nul
if errorlevel 1 (
  echo Python was not found. Install Python 3 from https://www.python.org/downloads/ and tick "Add to PATH".
  pause
  exit /b 1
)

python -c "import numpy, PIL, anthropic" >nul 2>nul
if errorlevel 1 (
  echo Installing required packages...
  python -m pip install -r requirements.txt
  if errorlevel 1 (
    echo Package install failed.
    pause
    exit /b 1
  )
)

where claude >nul 2>nul
if errorlevel 1 echo Note: Claude Code not found - AI reading/translation needs Claude Code or an API key.

if "%~1"=="" (
  python -m ps1tl gui
) else (
  python -m ps1tl gui "%~1"
)
if errorlevel 1 pause
