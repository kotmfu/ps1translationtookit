---
name: new-game
description: Add support for a new PS1 game to ps1tl from its disc image - identify it, reverse-engineer where and how its Japanese text is stored, write the game plugin (extract + insert), wire it into the app, and verify a patched disc. Use when the user wants to translate a game the app doesn't support yet, or hands over a new .cue/.bin/.iso.
---

# New game for ps1tl

Goal: the user opens the game's `.cue` in the app and gets a `<rom>.script.json` they can translate and patch,
exactly as with Yuuyami. Everything game-specific lives in one file, `native/Ps1tl.Core/<Game>.cs`, modelled on
`native/Ps1tl.Core/Yuuyami.cs`. Read `Yuuyami.cs`, `Script.cs`, `Project.cs` and `Build.cs` before starting:
they are the contract.

Ground rules:
- **Never write to the user's original bin.** Work on copies in the scratchpad (a `.cue` there may point at the
  original bin; reading is fine). Patched output goes to `patched/` or `diagnostic/` next to the copy.
- Reverse-engineering helpers go in `native/Ps1tl.Cli/Program.cs` as new `case` commands (as `acd-stats`,
  `acd-show`, `line-png` do), not throwaway scripts. They're how the next session re-checks findings.
- Look at pictures yourself: render candidates to PNG (`Raster`, `Images.Png`, `Jobs.RenderGlyph`) and open them
  with Read. Seeing a font page or a garbled-vs-clean text dump settles questions faster than reasoning about hex.
- Write findings into the plugin's top doc comment as you go (format, offsets, control codes), in the style of
  the Yuuyami header. That comment is the format documentation.
- Build/run: `dotnet run --project native/Ps1tl.Cli -- <command> ...` (needs the .NET 10 SDK).

## 1. Disc and identity

1. `Disc` only reads **single-track MODE2/2352 bin/cue** (`Disc.BinPath`). If the user has a `.iso` (2048-byte
   sectors), a `.chd`, or a multi-track cue (CD audio tracks), stop and tell them: they need a 2352 bin/cue dump
   (`chdman extractcd` converts a CHD). Multi-track is a real limitation: `Build.WritePatched` writes a
   single-track cue and would drop audio tracks; extending `Disc`/`Build` for it is its own task, so ask first.
2. Serial: `disc.Serial()` (from SYSTEM.CNF, e.g. `SLPS_012.34`). Check it isn't already in a plugin's `Serials`.
   Look the game up (developer, engine, year, existing fan translations or format notes on romhacking.net,
   datacrystal) with WebSearch. Another game on the same engine may already be documented, and Spike games may
   reuse Yuuyami's FLB/ACD/EAS formats (`Flb.Walk` throws on a non-FLB, so it's a cheap test).
3. Add a `files` CLI command if missing: `disc.Files()` sorted by LBA, with sizes. Note the executable (named
   after the serial) and the big container files.

## 2. Find the text

Work out which of these the game uses. Many use more than one (dialogue one way, menus another).

**A. Encoded text (Shift-JIS or a custom table).** Most common. Scan every file (and the EXE) for runs of valid
Shift-JIS (lead bytes 0x81-0x9F / 0xE0-0xEF, trail 0x40-0xFC except 0x7F) and dump the longest runs decoded.
Real dialogue reads as Japanese sentences. If nothing turns up but the game clearly has lots of text:
- the text may be **compressed**: high-entropy files, or a header with a decompressed size. Find the
  decompressor (LZSS variants are typical; search the EXE for the routine or look up the engine);
- or use a **custom table**: take a known on-screen string (ask the user for a screenshot of a dialogue box)
  and search for its byte pattern relative to itself (equal characters = equal codes) to recover the table.

**B. Glyph indices with the font shipped beside the text** (Yuuyami's ACD style). Messages are lists of small
integers into a font page stored in the same file. Signs: 4bpp/8bpp blocks that render as glyph grids, and
u16 streams whose values never exceed the glyph count. The app already handles this fully: glyph bitmaps go in
`Script.Glyphs`, Claude reads them (OCR + glyph table), lines are keyed by bitmap hashes.

**C. Text baked into pictures** (menus, title, maps). Look for TIM images (magic `10 00 00 00`, then flags
8/9 = 4bpp/8bpp CLUT) or the engine's own image container. The `Images` pipeline (catalog, Claude finds text,
English drawn back in the picture's palette) handles these once the plugin can list, decode and re-encode them.

For every text source also find:
- **Control codes**: line break, page break / wait-for-button, speaker name, colour, variables (player name,
  numbers). Map them before writing Extract; they must round-trip untouched. Keep them out of `Ja` or as
  visible tags the translator preserves.
- **How messages are referenced**: pointer table (offsets per message), terminator-separated blob, or script
  bytecode pointing into text. This decides whether English may be longer than the Japanese.
- **The container**: if text sits inside an archive (like FLB), write `Walk` + `Rebuild` for it as its own small
  static class (see `Flb.cs`), byte-identical when nothing changed.

If the bytes don't give it up, ask the user for emulator help: DuckStation's memory viewer or VRAM viewer
while a dialogue box is open shows the decoded font and text buffer, and a RAM search for a known string finds
the format the engine really uses.

## 3. Write the plugin

`native/Ps1tl.Core/<Game>.cs`, same public surface as Yuuyami:

- `Serials`, `Name`, `ExtractorVersion` (bump when extraction finds more text; open projects merge new lines).
- `Script Extract(Disc)`: one `Line` per **unique** message. `Key` = stable hash of its content (same message in
  two files = one line, all places in `Refs`). `Refs` = `"<file path>:<message index>"`; the diagnostic build
  shows these labels in-game and the editor's search box jumps to them.
- `(Dictionary<string, byte[]> Repl, InsertReport) Insert(Disc, Script, Font, bool labels)`: `{iso path: new file
  bytes}` for changed files only. With `labels`, every message shows its own ref instead of English (this is how
  the user finds a line in game). Lines without English stay Japanese.
- `ImageFiles(Disc)` if the game has picture text (type C), else return an empty dictionary.

Encoded-text games (type A) need small changes in shared code, because the app assumed glyph lines so far:
- set `Ja` from the decoded text and `JaSource = "rom"`, and make `GlyphTable.Trusted` accept `"rom"` so lines go
  to Claude as text;
- `Jobs.TranslateLines` / `VerifyCharacters` select lines with `l.Glyphs.Count > 0`: use `!l.IsImage` for
  translation, and skip glyph-less lines in the character check;
- `Jobs.LineWidth` (and so `Project.Budget` and "too long" flags) sums glyph widths; for glyph-less lines use the
  game's character width × `Ja.Length`;
- `RenderJapanese` / "By picture" need glyphs; text lines are translated by text only.

Picture text (type C) in a format other than Yuuyami's EAS: `Images.cs` calls `Yuuyami.ImageData` and
`Yuuyami.PutImage`; route those through the active game.

## 4. Wire it in

Today `Project.Open`, `Project.Pics`, `Project.StartBuild`, `Project.BuildResult` and the CLI's
`extract`/`insert`/`patch` call `Yuuyami` directly. With a second game, add the smallest dispatch that works:
an `IGame` interface with exactly the members above, `Yuuyami` and the new game implementing it, and a
`Games.BySerial(serial)` lookup used by `Project.Open` (keep the "unknown game; supported: ..." error listing all
games). `Project` keeps the game it opened. Don't build more registry than that.

## 5. English in the game's font

Pick the cheapest route that reads well, and tell the user which one and why:
1. **Glyph-page games (B)**: done already. `Font` (`fonts/en_pixel.txt`, `en_small.txt`) draws English cells
   into the font page, as `Yuuyami.InsertAcd` does. Check cell size/bpp match and reuse.
2. **Shift-JIS games (A), first version**: write English as full-width Shift-JIS letters (`Ａ` = 0x8260 ...).
   Needs no code patch since the game's font has them, but only fits about half the text per row.
3. **Half-width or variable-width English**: needs the EXE's text renderer patched (MIPS assembly) or the font
   replaced with narrow glyphs plus a two-letters-per-code table. Real work: describe the plan and ask before
   starting.

Length: if messages are pointer-table addressed, rewrite the table and let files grow. `Build.WritePatched`
handles a grown ISO file by moving files that collide with it to the end of the image. **Check first whether the
game loads files by LBA from a table in its EXE** (common on PS1: grep the EXE for the LBAs of a few files as
u32/BCD). If it does, moved files break the game: keep every file within its original sector count, or patch
that table too.

## 6. Verify (all of these, in order)

1. **Round trip**: `Insert` on a fresh script (no English) returns no changes; a rebuilt container with nothing
   replaced is byte-identical to the original. Add this as a CLI check (`<game>-roundtrip`) and keep it.
2. `extract` on the copy: line count, a few `Ja` lines dumped and read by you; they must be real dialogue,
   not garbage (for glyph games, look at `line-png` renders instead).
3. Translate ~10 lines in a scratch copy of the project (`job-test`-style, or by hand), `patch` with labels and
   without, and check `Build.ApplyBps` reproduces the patched bin.
4. Ask the user to boot the patched and diagnostic discs in DuckStation and send screenshots: English renders,
   control codes still work (breaks, names, waits), nothing crashes when entering a scene with edited text.

## 7. Hand-off

Tell the user what was found (text kinds, counts, font route, length limits, anything left in Japanese), then
the normal app flow: open the `.cue` -> (glyph games) read Japanese / check characters -> translate -> build.
Update the README "Layout" line for Ps1tl.Core with the new game, and save non-obvious format facts to memory.
