#!/bin/sh
# Builds the native app as single files: dist/linux-x64/ps1tl and dist/win-x64/ps1tl.exe (no Python or .NET install needed).
# Needs the .NET 10 SDK.
set -e
cd "$(dirname "$0")"
for r in linux-x64 win-x64; do
  dotnet publish Ps1tl.App -c Release -r "$r" --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "dist/$r"
  rm -f "dist/$r"/*.pdb
done
echo "Built dist/linux-x64/ps1tl and dist/win-x64/ps1tl.exe"
