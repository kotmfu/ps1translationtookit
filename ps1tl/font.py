"""Built-in pixel fonts -> game glyph cells.

A font file (fonts/*.txt) holds '#'-art glyphs. to_cell() turns one into a 16x16 4bpp cell in the
Spike style: light core, dark 1px outline, 15 = transparent background.
"""
import os

FONT_DIR = os.path.join(os.path.dirname(__file__), 'fonts')
CORE, EDGE, BG = 13, 3, 15
TOP = 2          # cell row where font row 0 lands (rows 2..13, outline 1..14)
SPACE_W = 3


def load(name='en_pixel'):
    glyphs, cur = {}, None
    for line in open(os.path.join(FONT_DIR, name + '.txt'), encoding='utf-8'):
        line = line.rstrip('\n')
        if line.startswith('== '):
            cur = line[3:4] if len(line) > 3 else ' '; glyphs[cur] = []
        elif cur is not None and line:  # text before the first '==' is the header comment
            glyphs[cur].append(line)
    glyphs[' '] = ['.' * SPACE_W]
    return glyphs


def to_cell(rows):
    """'#'-art -> (128 bytes 4bpp, x, w). Ink starts at column 1 so the outline fits; w covers outline."""
    px = [[BG] * 16 for _ in range(16)]
    ink = {(TOP + y, 1 + x) for y, r in enumerate(rows) for x, c in enumerate(r) if c == '#'}
    for y, x in ink:
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if 0 <= y + dy < 16 and 0 <= x + dx < 16 and px[y + dy][x + dx] == BG: px[y + dy][x + dx] = EDGE
    for y, x in ink: px[y][x] = CORE
    flat = [v for r in px for v in r]
    data = bytes(flat[i] | flat[i + 1] << 4 for i in range(0, 256, 2))
    return data, 0, max(len(r) for r in rows) + 2


CELL_INK = 14    # widest ink a 16px cell holds with its outline


def join(text, glyphs):
    """'#'-art of several characters side by side, spaced exactly as separate glyphs would be"""
    parts = [glyphs[c] for c in text]
    h = max(len(r) for r in parts)
    widths = [max(len(r) for r in g) for g in parts]
    return ['..'.join((g[y] if y < len(g) else '').ljust(w, '.') for g, w in zip(parts, widths)) for y in range(h)]


def pack(text, glyphs):
    """split text into runs that each fit one 16px cell. The engine draws one sprite per cell and only has a
    small pool of them for the dialogue box (~30, sized for Japanese), so narrow Latin letters share cells."""
    out = []
    for c in text:
        if out and len(join(out[-1] + c, glyphs)[0]) <= CELL_INK: out[-1] += c
        else: out.append(c)
    return out


def missing(text, glyphs):
    return sorted(set(c for c in text if c not in glyphs))
