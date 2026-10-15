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
high-level BIOS (no Sony BIOS needed).

Saves work like a native game's: there is no memory card to manage. Each save the game makes is its own file
in `%APPDATA%\ps1port\<serial>\saves\` (`~/.config/ps1port/<serial>/saves/` on Linux), written atomically
half a second after the game finishes writing. An old `<game>.mcd` next to the `.cue` is imported on first run.

```
port.ps1 "path\to\game.cue"            # Windows -> dist\port-win-x64\ps1port.exe
./port.sh path/to/game.cue linux-x64   # Linux   -> dist/port-linux-x64/ps1port
```

Run `ps1port game.cue` (or put the `.cue`/`.bin` next to it). Any game controller works (SDL3: Xbox,
DualShock/DualSense, Switch, generic pads; hot-plug; d-pad or left stick), mapped by position: south = Cross,
east = Circle, west = Square, north = Triangle, shoulders/triggers = L1/R1/L2/R2, stick clicks = L3/R3, guide =
fast forward. Keyboard: arrows, Z = Cross, X = Circle, A = Square, S = Triangle, Enter = Start, Backspace =
Select, Q/W = L1/R1, 1/2 = L2/R2, Tab = fast forward, F11 = fullscreen. Both are remappable in `controls.json`
next to the saves (controller names: SDL's, e.g. `dpup`, `leftshoulder`, `+leftx`; keys: Avalonia `Key` names). Patched discs work as long as the executable is unchanged (the recompiled code checks
the EXE's CRC and falls back to the interpreter otherwise). `--dither` turns the console's dithering back on.

How it works:
- `Recomp.cs` turns the game's MIPS code into C#: one method per function (entry point, `jal` targets, function
  pointers, plus `entries.txt` from earlier runs), gotos for branches, delay slots folded in, jump tables as
  switches. Output goes to `Ps1tl.Port/Generated/` (game code: never commit or distribute it; a build that
  contains it contains the game).
- Overlays (code the game loads from disc at run time) are recompiled too: runs save each disc load the game
  executes code from to `overlays/`, the next recompile turns them into C#, and each function switches in when
  RAM holds exactly the code it was made from (`Overlays.cs`).
- Sony's SDK libraries are replaced, not recompiled (`PsyQ.cs`, with a per-executable symbol table): libgpu
  (`DrawOTag`, `LoadImage`, `PutDrawEnv`, ...) draws straight into the renderer and `VSync` is the frame loop,
  so graphics never go through emulated GPU registers, DMA or interrupts. libcd and libcds (`LibCd.cs`) send
  commands straight to the drive model (disc image, read timing, XA audio into the SPU) and hand its answers to
  the game's CD callbacks as native interrupts; movie sectors go straight into the game's libcds ring buffer.
  No CD registers or CD DMA are touched. libpress (`LibPress.cs`) decodes FMV frames natively: DecDCTvlc in C#
  (byte-identical to Sony's, `--vlccheck` compares the two on every frame), DecDCTin/out straight to the MDEC
  decoder, so no MDEC registers or DMA 0/1. Still on the emulated hardware path for now: libspu, the pad driver
  and the memory card driver. `--emulate-sdk` runs Sony's own code for everything, `--emulate-cd` /
  `--emulate-press` just for that library.
- Anything not recompiled runs in the interpreter (`Cpu.cs`), which is also the reference (`--interp`).
- Debugging: `--headless --frames=N --shots=a,b --press=frame:button[:frames],... --wav --trace --cdtrace --gputrace`,
  `--dis=addr,count` (after a headless run: disassembles RAM, so overlays too), `--ram` (dump RAM), and with `--interp`:
  `--prof=frame` (per-pc and I/O read counts from that frame) and `--trap=addr` (log registers at a pc).
  A headless run writes `entries.txt` (entry points it had to interpret) and `portdata/overlays/` for the next
  recompile (`--overlays=dir` points the recompiler at them). `--map=file` lists every function with the
  hardware it touches; `--ioprof` (with `--interp`) records which code touches which device; `--sdktrace` logs
  every native SDK call; `--selftest` checks the saves and the libcds ring buffer.

Status: Yuuyami Doori Tankentai plays through the intro FMVs, title, school and street scenes into the
Torizuka forest prologue (walking, events, the notebook/discussion screen); save/load is untested because the
prologue could not be finished headless yet. Gunparade March boots to its title menu (interpreted; recompile it
the same way). Audio has SPU reverb; output is winmm on Windows and pw-cat / pacat / aplay elsewhere.
