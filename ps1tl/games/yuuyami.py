"""Yuuyami Doori Tankentai (SLPS-02274, Spike 1999).

Dialogue lives in ACD files inside DATA/FILELINK.FLB. Each ACD carries its own font page:
  header +08: 13 u32 = file size, then section offsets s[0..11]. 'ACD ' (space) files are the same, but +08
              holds 0x20 (or 0) instead of the size; they carry extra timing tables in s[0]..s[3]
  s[4]..s[5]  messages: u16 pairs. (glyph, 0x0200) = glyph + more follows, (glyph, 0) = last glyph,
              (0xFFFF, ctrl) closes the message (ctrl meaning unknown, preserved).
  s[5]        font header: +10/+12 u16 glyph w/h (16x16)
  s[6]..s[7]  per-glyph metrics, 4 bytes each: (0, 0, x, width) -> glyph count
  s[7]        texture table; first u32 = palette+glyph block size
  s[10]       16-colour palette, glyphs follow at +0x20 (4bpp, 128 bytes each; 15 = background)
Glyph order differs per file, so lines are keyed by glyph *bitmaps*, not indices.
"""
import hashlib, struct
from .. import flb

SERIALS = ['SLPS_022.74']
NAME = 'Yuuyami Doori Tankentai'
EXTRACTOR = 3   # bump when extract() finds more text; open projects merge the new lines in


def parse_acd(d):
    """-> (glyph bitmaps, metrics, [(glyph indices, ctrl)]) or None if the ACD has no font"""
    if len(d) <= 0x50 or d[:4] not in (b'ACD\0', b'ACD ') or d[4:8] != b'1.20': return None
    s = struct.unpack_from('<13I', d, 8)[1:]
    if s[6] - s[5] < 0x14: return None
    n = (s[7] - s[6]) // 4  # metrics size; the u16 count in the font header undercounts some files
    g0 = s[10] + 0x20
    glyphs = [d[g0 + i * 128: g0 + (i + 1) * 128] for i in range(n)]
    metrics = [tuple(d[s[6] + i * 4 + 2: s[6] + i * 4 + 4]) for i in range(n)]
    raw = struct.unpack_from(f'<{(s[5] - s[4]) // 2}H', d, s[4])
    msgs, cur = [], []
    for a, b in zip(raw[0::2], raw[1::2]):
        if a == 0xFFFF:
            msgs.append((cur, b)); cur = []
        elif a < n:
            cur.append(a)
        else:
            raise ValueError(f'glyph {a} out of range {n}')
    return glyphs, metrics, msgs


def gid(bitmap):
    return hashlib.md5(bitmap).hexdigest()[:12]


def extract(disc):
    """-> {'glyphs': {gid: {bitmap, x, w}}, 'lines': [{key, glyphs, refs, ja, en}]} (unique lines)"""
    lba, size = disc.files()['/DATA/FILELINK.FLB']
    glyphs, lines, pics = {}, {}, {}
    for path, data in flb.walk(disc.read(lba, size)):
        if parse_eas(data): pics[path] = data
        p = parse_acd(data)
        if not p: continue
        bitmaps, metrics, msgs = p
        ids = [gid(b) for b in bitmaps]
        for b, i, (x, w) in zip(bitmaps, ids, metrics):
            glyphs.setdefault(i, {'bitmap': b.hex(), 'x': x, 'w': w})
        for m, (seq, ctrl) in enumerate(msgs):
            if not seq: continue
            g = [ids[k] for k in seq]
            key = line_key(g)
            line = lines.setdefault(key, {'key': key, 'glyphs': g, 'refs': [], 'ja': '', 'en': ''})
            line['refs'].append(f'{path}:{m}')
    return {'game': NAME, 'glyphs': glyphs, 'lines': list(lines.values()), 'images': image_catalog(pics)}


def line_key(ids):
    return hashlib.md5(''.join(ids).encode()).hexdigest()[:16]


MAX_GLYPHS = 465   # largest font page the game itself ships; growing past it is untested


def insert_acd(d, lookup, cell, pack=list):
    """rewrite one ACD with English. cell(token) -> (bitmap, x, w); pack(text) -> tokens, one glyph cell each
    (several narrow letters can share a cell, see font.pack).
    Glyph slots used by untranslated lines keep their Japanese bitmap; free slots get English cells, and the
    font page grows by extra slots when needed (new cells go at the end of the glyph block; every texture
    offset in sections 8/9, relative to s[10] and all past the glyphs, moves by the same amount).
    -> (new bytes | None if unchanged, problem string | None)"""
    p = parse_acd(d)
    if not p: return None, None
    bitmaps, metrics, msgs = p
    n = len(bitmaps)
    ids = [gid(b) for b in bitmaps]
    texts = [lookup(m, line_key([ids[k] for k in seq])) if seq else None for m, (seq, _) in enumerate(msgs)]
    if not any(texts): return None, None
    keep = {k for (seq, _), t in zip(msgs, texts) if not t for k in seq}
    free = [i for i in range(n) if i not in keep]
    fits = lambda toks: n + max(0, len(set(x for t in toks if t for x in t)) - len(free)) <= MAX_GLYPHS
    # fewest sprites first; fall back to one letter per cell, then ALL CAPS (fewer distinct letters)
    for toks in ([pack(t) if t else None for t in texts], [list(t) if t else None for t in texts],
                 [list(t.upper()) if t else None for t in texts]):
        if fits(toks): break
    else:
        return None, f'font page would exceed {MAX_GLYPHS} glyphs'
    chars = sorted(set(x for t in toks if t for x in t))
    grow = max(0, len(chars) - len(free))
    slot = dict(zip(chars, free + list(range(n, n + grow))))

    s = list(struct.unpack_from('<13I', d, 8))
    sec = s[1:]
    gend = 0x20 + n * 128                                    # glyph block end, relative to s[10]
    stream = bytearray()
    for (seq, ctrl), t in zip(msgs, toks):
        idx = [slot[c] for c in t] if t else seq
        for j, k in enumerate(idx):
            stream += struct.pack('<2H', k, 0x200 if j < len(idx) - 1 else 0)
        stream += struct.pack('<2H', 0xFFFF, ctrl)
    met = bytearray(d[sec[6]:sec[7]]) + bytes(4 * grow)
    tables = bytearray(d[sec[7]:sec[10]])
    for o in range(sec[8] - sec[7], sec[9] - sec[7], 12):    # texture table: u32 offset, u16 w, u16 h, u32
        struct.pack_into('<I', tables, o, struct.unpack_from('<I', tables, o)[0] + 128 * grow)
    for o in range(sec[9] - sec[7], sec[10] - sec[7], 8):    # tail table: u32 offset, u32
        struct.pack_into('<I', tables, o, struct.unpack_from('<I', tables, o)[0] + 128 * grow)
    glyphs = bytearray(d[sec[10]:sec[10] + gend]) + bytes(128 * grow)
    for c, i in slot.items():
        bm, x, w = cell(c)
        glyphs[0x20 + i * 128: 0x20 + (i + 1) * 128] = bm
        met[i * 4: i * 4 + 4] = bytes([0, 0, x, w])
    fhdr = bytearray(d[sec[5]:sec[6]])
    for o in (8, 14):                                         # glyph count fields, when they hold the real count
        if struct.unpack_from('<H', fhdr, o)[0] == n: struct.pack_into('<H', fhdr, o, n + grow)
    out = d[:sec[4]] + stream + fhdr + met + tables + glyphs + d[sec[10] + gend:]
    new = [sec[4], sec[4] + len(stream)]
    new.append(new[1] + len(fhdr)); new.append(new[2] + len(met))
    for k in (8, 9, 10): new.append(new[3] + sec[k] - sec[7])
    if d[3] == 0: s[0] = len(out)   # 'ACD ' files keep their +08 value
    s[5:12] = new
    out = bytearray(out)
    struct.pack_into('<13I', out, 8, *s)
    return bytes(out), None


def insert(disc, script, font_glyphs, labels=False):
    """-> ({iso path: new file bytes}, report dict). Only lines with English are replaced.
    labels=True is a diagnostic build: every message shows its own 'file:message' id instead of text."""
    import functools
    from ..font import to_cell, join, pack
    cell = functools.lru_cache(None)(lambda tok: to_cell(join(tok, font_glyphs)))
    packer = lambda t: pack(t, font_glyphs)
    from ..tfile import shared_en, effective_en
    shared = shared_en(script)
    en = {}
    for l in script['lines']:
        t = effective_en(l, shared).strip()
        if t and 'image' in l: en[l['key']] = t   # pictures: images.draw handles line breaks / missing letters
        elif t: en[l['key']] = ''.join(c if c in font_glyphs else '?' for c in t.replace('\n', ' '))
    from .. import images
    lba, size = disc.files()['/DATA/FILELINK.FLB']
    archive = disc.read(lba, size)
    repl, problems, pics = {}, {}, {}
    for path, data in flb.walk(archive):
        if parse_eas(data): pics[path] = data
        tag = flb.short(path)
        lookup = (lambda m, k, tag=tag: f'{tag}:{m}') if labels else (lambda m, k: en.get(k))
        new, err = insert_acd(data, lookup, cell, packer)
        if err: problems[path] = err
        if new: repl[path] = new
    import sys
    new_pics, tight = images.apply(pics, script, sys.modules[__name__], lambda l: en.get(l['key']), labels)
    repl.update(new_pics)
    for k in tight: problems[k] = 'English clipped: does not fit the picture'
    report = {'files_changed': len(repl), 'files_skipped': problems, 'lines_with_english': len(en)}
    return ({'/DATA/FILELINK.FLB': flb.rebuild(archive, repl)} if repl else {}), report


def glyph_pixels(entry):
    """16x16 list of ink alpha 0..1 (outline dark, fill light; background transparent)"""
    b = bytes.fromhex(entry['bitmap'])
    px = [v for x in b for v in (x & 15, x >> 4)]
    return [px[y * 16:(y + 1) * 16] for y in range(16)]


# --- EAS image containers (menus, notebook, title screens: text is baked into these pictures) -------------
#   header +08: u32 section offsets t[0..]; t[6] = pixel data start
#   t[0]..t[1]  image records, 20 bytes: u32 pixel offset (relative to t[6]), u16 palette index, u16 palette
#               count, u16 hotspot x, y, u16 w, h, u16 bits per pixel (4/8), u16 0
#   t[1]..t[2]  palette records, 8 bytes: u32 offset (relative to t[6]), u32 bpp; 16 or 256 PS1 15-bit colours

def parse_eas(d):
    """-> [{'w', 'h', 'bpp', 'off' (absolute), 'pal' (absolute palette offset)}] or None"""
    if d[:4] != b'EAS\x90': return None
    t = struct.unpack_from('<7I', d, 8)
    pals = [struct.unpack_from('<I', d, o)[0] + t[6] for o in range(t[1], t[2], 8)]
    out = []
    for o in range(t[0], t[1], 20):
        off, pi, pn, hx, hy, w, h, bpp, _ = struct.unpack_from('<I8H', d, o)
        out.append({'w': w, 'h': h, 'bpp': bpp, 'off': off + t[6], 'pal': pals[pi] if pi < len(pals) else None})
    return out


def eas_rgba(d, im):
    """one EAS image -> numpy (h, w, 4) uint8. Colour 0x0000 is transparent, as on the PS1."""
    import numpy as np
    w, h, bpp = im['w'], im['h'], im['bpp']
    stride = (w * bpp + 15) // 16 * 2                      # rows are padded to 16 bits (VRAM words)
    raw = np.frombuffer(d, np.uint8, stride * h, im['off']).reshape(h, stride)
    idx = np.stack([raw & 15, raw >> 4], 2).reshape(h, -1) if bpp == 4 else raw
    pal = np.frombuffer(d, '<u2', 16 if bpp == 4 else 256, im['pal'])
    c = pal[idx[:, :w]]
    rgb = np.stack([(c & 31) << 3, (c >> 5 & 31) << 3, (c >> 10 & 31) << 3], -1).astype(np.uint8)
    return np.dstack([rgb, np.where(c == 0, 0, 255).astype(np.uint8)])


def image_files(disc):
    """{flb path: bytes} of every file holding images"""
    lba, size = disc.files()['/DATA/FILELINK.FLB']
    return {p: d for p, d in flb.walk(disc.read(lba, size)) if parse_eas(d)}


def _stride(im):
    return (im['w'] * im['bpp'] + 15) // 16 * 2


def image_catalog(files):
    """-> {key: {'w', 'h', 'refs': ['path:index', ...]}}; identical pictures (pixels + palette) share a key"""
    out = {}
    for p, d in files.items():
        for i, im in enumerate(parse_eas(d)):
            if not im['w'] * im['h'] or im['pal'] is None: continue
            pal = d[im['pal']: im['pal'] + (32 if im['bpp'] == 4 else 512)]
            key = hashlib.md5(d[im['off']: im['off'] + _stride(im) * im['h']] + pal).hexdigest()[:16]
            out.setdefault(key, {'w': im['w'], 'h': im['h'], 'refs': []})['refs'].append(f'{p}:{i}')
    return out


def image_data(d, i):
    """-> (palette indices (h, w) uint8, palette uint16 array) of image i in file d"""
    import numpy as np
    im = parse_eas(d)[i]
    h, w = im['h'], im['w']
    raw = np.frombuffer(d, np.uint8, _stride(im) * h, im['off']).reshape(h, -1)
    idx = np.stack([raw & 15, raw >> 4], 2).reshape(h, -1) if im['bpp'] == 4 else raw
    return idx[:, :w].copy(), np.frombuffer(d, '<u2', 16 if im['bpp'] == 4 else 256, im['pal']).copy()


def put_image(d, i, idx):
    """write palette indices (same size as the original) back into image i -> new file bytes"""
    import numpy as np
    im = parse_eas(d)[i]
    h, w = im['h'], im['w']
    st = _stride(im)
    raw = np.frombuffer(d, np.uint8, st * h, im['off']).reshape(h, st)
    full = np.stack([raw & 15, raw >> 4], 2).reshape(h, -1) if im['bpp'] == 4 else raw.copy()
    full[:, :w] = idx                                         # row padding keeps its original bytes
    raw = (full[:, 0::2] | full[:, 1::2] << 4) if im['bpp'] == 4 else full
    out = bytearray(d)
    out[im['off']: im['off'] + st * h] = raw.astype(np.uint8).tobytes()
    return bytes(out)
