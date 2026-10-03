@echo off
rem Builds dist\ps1tl.exe (Windows). Needs Python 3.10+ on PATH.
cd /d "%~dp0"
python -m pip install -r requirements.txt pyinstaller || goto :fail
python -m PyInstaller --noconfirm --onefile --windowed --name ps1tl --add-data "ps1tl/fonts:ps1tl/fonts" ps1tl_app.py || goto :fail
echo.
echo Built dist\ps1tl.exe
pause
exit /b 0
:fail
echo Build failed.
pause
exit /b 1
