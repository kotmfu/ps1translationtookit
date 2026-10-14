# Recompile a game into a native PC build: ./port.ps1 "path\to\game.cue"
# Output: dist\port-win-x64\ps1port.exe (put the .cue/.bin next to it, or pass the .cue as an argument)
param([Parameter(Mandatory)] [string] $Cue)
$ErrorActionPreference = "Stop"
$p = "$PSScriptRoot\Ps1tl.Port"
Remove-Item -Recurse -Force "$p\Generated" -ErrorAction SilentlyContinue
dotnet build "$p" -c Release | Out-Null
& "$p\bin\Release\net10.0\ps1port.exe" $Cue "--recomp=$p\Generated"
dotnet publish "$p" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "$PSScriptRoot\dist\port-win-x64"
