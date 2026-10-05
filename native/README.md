# ps1tl native app

The PS1 translation editor as a native desktop app (C# / .NET 10 + Avalonia), for Windows and Linux.
A project is a `<rom>.script.json` file next to the rom; translations can also be shared as CSV files.

## Run

- Windows: `dist\win-x64\ps1tl.exe` (optionally drag a `.cue` onto it)
- Linux: `dist/linux-x64/ps1tl` (needs an X11 or XWayland desktop with fontconfig, as any Avalonia app does)

Claude features use your Claude Code login (`claude` on PATH, no API account needed) or an API key entered
in the app. Requests run 4 at a time; the "Claude output (live)" panel shows each one as it is written.

## Build

Needs the .NET 10 SDK. `publish.bat` (Windows) or `./publish.sh` (Linux/macOS) builds both single-file apps
into `dist/`.

## Layout

- `Ps1tl.Core`: disc / ISO9660, FLB archives, sector EDC/ECC, patched-disc writer, BPS patches, fonts, the
  game plugins (`Games.cs` picks one by serial): Yuuyami Doori Tankentai (dialogue scene files + menu pictures)
  and Gunparade March (EVDATA.BIN event scripts, Shift-JIS text, half-width English font), picture drawing,
  CSV translation files, Claude client (CLI or API, streaming), jobs, and the project model.
- `Ps1tl.App`: the desktop editor.
- `Ps1tl.Cli` (`ps1tl-cli`): `extract`, `insert`, `patch`, `apply`, `llm-test` for scripting and checks, plus
  reverse-engineering helpers (`files`, `dump`, `sjis-scan`, `evd-show`, `gpm-roundtrip`, `gpm-font`, ...).
