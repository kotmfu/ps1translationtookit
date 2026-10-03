@echo off
rem Builds the native app as single files: dist\win-x64\ps1tl.exe and dist\linux-x64\ps1tl (no Python or .NET install needed).
rem Needs the .NET 10 SDK.
cd /d "%~dp0"
for %%r in (win-x64 linux-x64) do (
  dotnet publish Ps1tl.App -c Release -r %%r --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist\%%r || goto :fail
  del /q dist\%%r\*.pdb 2>nul
)
echo.
echo Built dist\win-x64\ps1tl.exe and dist\linux-x64\ps1tl
exit /b 0
:fail
echo Build failed.
exit /b 1
