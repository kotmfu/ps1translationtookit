#!/bin/sh
# Recompile a game into a native PC build: ./port.sh path/to/game.cue [linux-x64|win-x64]
# Output: dist/port-<rid>/ps1port (put the .cue/.bin next to it, or pass the .cue as an argument)
set -e
here=$(cd "$(dirname "$0")" && pwd)
p="$here/Ps1tl.Port"
rid=${2:-linux-x64}
rm -rf "$p/Generated"
dotnet build "$p" -c Release >/dev/null
dotnet run --project "$p" -c Release --no-build -- "$1" "--recomp=$p/Generated"
dotnet publish "$p" -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -o "$here/dist/port-$rid"
