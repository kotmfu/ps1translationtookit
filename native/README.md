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

CI (`.github/workflows/build.yml`) builds both on every push to `main`, tags the commit with the next patch
version (`v0.1.0`, `v0.1.1`, ...) and publishes a GitHub release with the Windows `.zip` and Linux `.tar.gz`
attached. Push a tag like `v0.2.0` by hand to start a new version line. PRs only build (files are in the run's
artifacts).

## Layout

- `Ps1tl.Core`: disc / ISO9660, FLB archives, sector EDC/ECC, patched-disc writer, BPS patches, fonts, the
  game plugins (`Games.cs` picks one by serial): Yuuyami Doori Tankentai (dialogue scene files + menu pictures)
  and Gunparade March (EVDATA.BIN event scripts, Shift-JIS text, half-width English font), picture drawing,
  CSV translation files, Claude client (CLI or API, streaming), jobs, and the project model.
- `Ps1tl.App`: the desktop editor.
- `Ps1tl.Port` (`ps1port`): experimental native PC port, see below.
- `Ps1tl.Cli` (`ps1tl-cli`): `extract`, `insert`, `patch`, `apply`, `llm-test` for scripting and checks, plus
  reverse-engineering helpers (`files`, `dump`, `sjis-scan`, `evd-show`, `gpm-roundtrip`, `gpm-font`, ...).

## PC port (experimental)

`Ps1tl.Port` turns a game's executable into C# (static recompilation) and runs it natively against a
software PS1: GPU rasteriser, CD-ROM controller, SPU + XA audio, MDEC (FMVs), GTE, timers/DMA/IRQs and a
high-level BIOS (no Sony BIOS needed). Memory card saves go to `<game>.mcd` next to the `.cue`
(standard 128 KB image, works with DuckStation etc.).

```
port.ps1 "path\to\game.cue"            # Windows -> dist\port-win-x64\ps1port.exe
./port.sh path/to/game.cue linux-x64   # Linux   -> dist/port-linux-x64/ps1port
```

Run `ps1port game.cue` (or put the `.cue`/`.bin` next to it). Keys: arrows, Z = Cross, X = Circle, A = Square,
S = Triangle, Enter = Start, Backspace = Select, Q/W = L1/R1, 1/2 = L2/R2, Tab = fast forward, F11 = fullscreen;
an XInput pad works too. Patched discs work as long as the executable is unchanged (the recompiled code checks
the EXE's CRC and falls back to the interpreter otherwise). `--dither` turns the console's dithering back on.

How it works:
- `Recomp.cs` finds functions (entry point, `jal` targets, function pointers in data and code, plus
  `entries.txt` from earlier runs) and emits one C# method per function with gotos for branches, delay slots
  folded in, jump tables as switches. Output goes to `Ps1tl.Port/Generated/` (game code: never commit or
  distribute it; a build that contains it contains the game).
- Anything not recompiled (code loaded into RAM at run time, odd entry points) runs in the interpreter
  (`Cpu.cs`), which is also the reference: `--interp` runs the whole game interpreted.
- Debugging: `--headless --frames=N --shots=a,b --press=frame:button,... --wav --trace --cdtrace --gputrace`,
  `--dis=addr,count`. A headless run writes `entries.txt` (entry points it had to interpret) for the next recompile.

Status: Yuuyami Doori Tankentai plays through the intro FMVs, title, memory card check and into the story
with ~99.9% of executed instructions recompiled (~16x real time headless). Gunparade March boots to its
title menu (interpreted; recompile it the same way). Audio output is Windows-only (winmm) for now; no reverb.
