"""Text baked into pictures: menus, the notebook, title and map labels.

script['images'] = {key: {w, h, refs, checked}} lists every picture (from the plugin). Claude looks at them in
sheets; pictures with Japanese text become ordinary lines ({'key': 'img:<key>', 'image': key, 'glyphs': []}),
so they are edited, shared and translated like dialogue. On build the English is drawn into the same box with
the picture's own palette (text colour and outline are taken from the Japanese), so nothing else moves.
"""
import io
import numpy as np
from PIL import Image, ImageDraw
from . import font as fontmod

PREFIX = 'img:'
MAX_H = 64          # taller pictures are scenery/photos; text painted over a photo is not handled
PER_SHEET = 30
FONTS = {}


def fonts():
    if not FONTS:
        FONTS['big'], FONTS['small'] = fontmod.load('en_pixel'), fontmod.load('en_small')
    return FONTS


def is_image(line):
    return 'image' in line


def merge_catalog(script, catalog):
    """add pictures/refs from a (re-)extraction; keeps what Claude already checked"""
    imgs = script.setdefault('images', {})
    for k, v in catalog.items():
        if k in imgs: imgs[k]['refs'] = sorted(set(imgs[k]['refs']) | set(v['refs']))
        else: imgs[k] = dict(v, checked=False)


# --- pictures -> PNG ---------------------------------------------------------
def rgba(idx, pal):
    c = pal[idx]
    rgb = np.stack([(c & 31) << 3, (c >> 5 & 31) << 3, (c >> 10 & 31) << 3], -1).astype(np.uint8)
    return np.dstack([rgb, np.where(c == 0, 0, 255).astype(np.uint8)])


def png(idx, pal, scale=2, bg=(24, 24, 32)):
    im = Image.fromarray(rgba(idx, pal))
    out = Image.new('RGBA', im.size, bg + (255,)); out.alpha_composite(im)
    out = out.resize((im.width * scale, im.height * scale), Image.NEAREST)
    buf = io.BytesIO(); out.save(buf, 'PNG')
    return buf.getvalue()


def sheet(pics):
    """numbered rows, each picture shown on black and on white (dark or light text both readable)"""
    s = 2
    rows = [(Image.fromarray(rgba(*p)), ) for p in pics]
    h = sum(r[0].height * s + 6 for r in rows)
    w = 44 + max(r[0].width * s * 2 + 12 for r in rows)
    out = Image.new('RGBA', (w, h), (90, 90, 90, 255))
    dr = ImageDraw.Draw(out)
    y = 0
    for n, (im,) in enumerate(rows):
        big = im.resize((im.width * s, im.height * s), Image.NEAREST)
        for x, bg in ((44, (0, 0, 0)), (44 + big.width + 6, (255, 255, 255))):
            out.paste(Image.new('RGBA', big.size, bg + (255,)), (x, y)); out.alpha_composite(big, (x, y))
        dr.text((4, y + 2), str(n + 1), fill=(255, 255, 0, 255))
        y += big.height + 6
    buf = io.BytesIO(); out.convert('RGB').save(buf, 'PNG')
    return buf.getvalue()


READ_SCHEMA = {
    'type': 'object',
    'properties': {'images': {'type': 'array', 'items': {
        'type': 'object', 'properties': {'n': {'type': 'integer'}, 'ja': {'type': 'string'}},
        'required': ['n', 'ja'], 'additionalProperties': False}}},
    'required': ['images'], 'additionalProperties': False,
}
READ_PROMPT = ("Each numbered row shows one picture from a Japanese PS1 game, twice (on black and on white). "
               "For every number 1-{n}: if the picture contains Japanese text (menu labels, buttons, place names, "
               "messages, single kanji such as 月 or 日), give that text exactly as ja, with a line break between "
               "lines of text. Otherwise (icons, people, photos, maps, only digits or Latin letters) give ja = \"\".")


def read_images(script, plugin, files, log=print, save=None, stop=lambda: False, model='opus'):
    """ask Claude which pictures hold Japanese text; those become lines"""
    from . import llm
    imgs = script.setdefault('images', {})
    todo = [k for k, v in imgs.items() if not v.get('checked') and v['h'] <= MAX_H]
    have = {l['key'] for l in script['lines']}
    groups = [todo[i:i + PER_SHEET] for i in range(0, len(todo), PER_SHEET)]
    log(f'reading {len(todo)} pictures with {model} via {llm.backend() or "nothing"}: '
        f'{len(groups)} requests, {llm.WORKERS} at a time')
    task = lambda keys: lambda: llm.ask(sheet([pixels(files, imgs[k], plugin) for k in keys]), READ_PROMPT.format(n=len(keys)),
                                        READ_SCHEMA, model=model, effort='medium')
    checked = 0
    for g, out in llm.parallel([task(k) for k in groups], stop):
        keys = groups[g]
        if isinstance(out, llm.LLMError):
            log(f'request {g + 1} failed: {out}'); continue
        found = {item['n']: item['ja'].strip() for item in out['images'] if 1 <= item['n'] <= len(keys)}
        for n, k in enumerate(keys, 1):
            imgs[k]['checked'] = True
            ja = found.get(n, '')
            if ja and PREFIX + k not in have:
                script['lines'].append({'key': PREFIX + k, 'image': k, 'glyphs': [], 'refs': imgs[k]['refs'], 'ja': ja, 'en': ''})
                have.add(PREFIX + k)
        if save: save()
        checked += len(keys)
        log(f'{checked}/{len(todo)} pictures checked, {sum(1 for l in script["lines"] if is_image(l))} with text')
    if stop(): log('stopped')


def pixels(files, entry, plugin):
    path, i = entry['refs'][0].rsplit(':', 1)
    return plugin.image_data(files[path], int(i))


TL_SCHEMA = {
    'type': 'object',
    'properties': {'lines': {'type': 'array', 'items': {
        'type': 'object', 'properties': {'n': {'type': 'integer'}, 'en': {'type': 'string'}},
        'required': ['n', 'en'], 'additionalProperties': False}}},
    'required': ['lines'], 'additionalProperties': False,
}
TL_SYSTEM = """You translate menu and interface text of the Japanese PS1 game "{game}" into English for a fan patch.
Each item is text drawn inside a small picture of fixed size; the English is redrawn in the same box with a
pixel font, so it MUST be short: stay within the given letter budget (abbreviate if needed, e.g. "Rumor", "Opt.").
Single kanji used in dates (月 日 and weekdays) become short forms such as "/" or "Mon". Keep names in Hepburn.
Use only ASCII letters, digits and . , ! ? ' " - : ; ( ) ~ & / + %.
{glossary}"""


def budget(entry):
    """rough letter budget of a picture: small font ~4px per letter, wrapped over the lines that fit"""
    return max(1, (entry['w'] - 2) // 4) * max(1, entry['h'] // 9)


def translate_images(script, plugin, files, limit=None, glossary='', log=print, save=None, stop=lambda: False, model='opus'):
    from . import llm
    from .tfile import shared_en, effective_en
    shared = shared_en(script)
    imgs = script['images']
    todo = [l for l in script['lines'] if is_image(l) and not effective_en(l, shared)][:limit]
    system = TL_SYSTEM.format(game=script['game'], glossary=f'Glossary (use these):\n{glossary}' if glossary else '')
    batches = [todo[i:i + PER_SHEET] for i in range(0, len(todo), PER_SHEET)]
    log(f'translating {len(todo)} picture texts with {model}: {len(batches)} requests, {llm.WORKERS} at a time')

    def task(batch):
        listing = '\n'.join(f'{n}. {l["ja"]!r} - box {imgs[l["image"]]["w"]}x{imgs[l["image"]]["h"]} px, '
                            f'at most {budget(imgs[l["image"]])} letters' for n, l in enumerate(batch, 1))
        return lambda: llm.ask(sheet([pixels(files, imgs[l['image']], plugin) for l in batch]),
                               f'The numbered pictures and their Japanese text:\n{listing}\n\nTranslate items 1-{len(batch)}.',
                               TL_SCHEMA, system, model)

    finished = 0
    for k, out in llm.parallel([task(b) for b in batches], stop):
        batch = batches[k]
        if isinstance(out, llm.LLMError):
            log(f'request {k + 1} failed: {out}'); continue
        for item in out['lines']:
            if 1 <= item['n'] <= len(batch): batch[item['n'] - 1]['en'] = item['en'].strip()
        if save: save()
        finished += len(batch)
        log(f'{finished}/{len(todo)} picture texts translated')
    if stop(): log('stopped')


# --- drawing English into a picture ------------------------------------------
def _lum(c):
    c = int(c)
    return (c & 31) * 3 + (c >> 5 & 31) * 6 + (c >> 10 & 31)


def style(idx, pal):
    """-> (background index, text index, outline index or None, text bbox (x0, y0, x1, y1))"""
    h, w = idx.shape
    clear = [i for i in np.unique(idx) if pal[i] == 0]
    if clear: bg = clear[0]
    else:
        border = np.concatenate([idx[0], idx[-1], idx[:, 0], idx[:, -1]])
        bg = np.bincount(border).argmax()
    ink = idx != bg
    if not ink.any(): return bg, bg, None, (0, 0, w, h)
    ys, xs = np.nonzero(ink)
    box = (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)
    pad = np.pad(~ink, 1, constant_values=True)
    edge = ink & (pad[:-2, 1:-1] | pad[2:, 1:-1] | pad[1:-1, :-2] | pad[1:-1, 2:])
    # split the text colours into a dark and a light group (Otsu on brightness); the group that hugs the
    # background is the outline, the other the letters. Similar edge share -> plain text, no outline.
    cols = sorted(np.unique(idx[ink]), key=lambda c: _lum(pal[c]))
    n = np.array([(idx == c).sum() for c in cols], float)
    e = np.array([((idx == c) & edge).sum() for c in cols], float)
    lum = np.array([_lum(pal[c]) for c in cols], float)
    fill, outline = cols[int(n.argmax())], None
    if len(cols) > 1:
        def spread(k):
            a, b = slice(0, k), slice(k, None)
            return sum(n[g].sum() * np.average((lum[g] - np.average(lum[g], weights=n[g])) ** 2, weights=n[g]) for g in (a, b))
        k = min(range(1, len(cols)), key=spread)
        groups = [list(range(k)), list(range(k, len(cols)))]
        share = [e[g].sum() / n[g].sum() for g in groups]
        if abs(share[0] - share[1]) >= 0.15:
            og, fg = (groups[0], groups[1]) if share[0] > share[1] else (groups[1], groups[0])
            outline = cols[max(og, key=lambda j: n[j])]
            # letters: the colour furthest in brightness from the outline that still covers 10% of its group
            big = [j for j in fg if n[j] >= 0.1 * n[fg].sum()]
            fill = cols[max(big, key=lambda j: abs(lum[j] - _lum(pal[outline])))]
    return bg, fill, outline, box


def _layout(text, f, gap, width):
    """word wrap (and explicit line breaks) -> (lines, advance fn), or None if a word is wider than width"""
    adv = lambda s: sum(max(len(r) for r in f.get(c, f['?'])) + gap for c in s) - gap if s else 0
    lines = []
    for para in text.split('\n'):
        cur = ''
        for word in para.split(' '):
            if adv(word) > width: return None
            t = (cur + ' ' + word) if cur else word
            if adv(t) <= width: cur = t
            else: lines.append(cur); cur = word
        lines.append(cur)
    return lines, adv


def draw(idx, pal, text):
    """-> (new indices, fits). Same size as the original; English replaces everything but the background.
    Tall narrow pictures (vertical Japanese) get the English rotated to read top to bottom."""
    h, w = idx.shape
    if h >= 2 * w and h >= 16:
        new, fits = _draw(np.rot90(idx, 1), pal, text)
        return np.ascontiguousarray(np.rot90(new, -1)), fits
    return _draw(idx, pal, text)


def _draw(idx, pal, text):
    h, w = idx.shape
    bg, fill, outline, (x0, y0, x1, y1) = style(idx, pal)
    o = 1 if outline is not None else 0
    F = fonts()
    text = ''.join(c if c in F['big'] or c == '\n' else '?' for c in text)
    # roomiest first: big font, small font, then letters closer together (outlines may touch)
    tries = [('big', 9, 3, 1 + o), ('small', 5, 2, 1 + o)] + ([('big', 9, 3, 1), ('small', 5, 2, 1)] if o else [])
    choice = None
    for name, cap, desc, gap in tries:
        f = F[name]
        lay = _layout(text, f, gap, w - 2 * o)
        if not lay: continue
        lines, adv = lay
        lh = cap + desc + 2 * o
        if lh * len(lines) - desc <= h:
            choice = (f, lines, adv, cap, lh, gap); break
    fits = choice is not None
    if not fits:   # clip: small font, tight, one line
        f = F['small']; gap = 1
        choice = (f, [text.replace('\n', ' ')], _layout('', f, gap, 0)[1], 5, 7 + 2 * o, gap)
    f, lines, adv, cap, lh, gap = choice
    block = lh * len(lines) - (lh - cap - 2 * o)
    top = int(np.clip((y0 + y1) / 2 - block / 2, 0, max(0, h - block)))
    ink = np.zeros((h, w), bool)
    for li, line in enumerate(lines):
        lw = adv(line)
        x = o + x0 if x0 <= 2 else int(np.clip((x0 + x1) / 2 - lw / 2, o, max(o, w - lw - o)))
        y = top + o + li * lh
        for c in line:
            rows = f[c]
            for ry, r in enumerate(rows):
                for rx, v in enumerate(r):
                    if v == '#' and 0 <= y + ry < h and 0 <= x + rx < w: ink[y + ry, x + rx] = True
            x += max(len(r) for r in rows) + gap
    new = np.full((h, w), bg, np.uint8)
    if outline is not None:
        p = np.pad(ink, 1)
        ring = np.zeros_like(ink)
        for dy in (0, 1, 2):
            for dx in (0, 1, 2): ring |= p[dy:dy + h, dx:dx + w]
        new[ring] = outline
    new[ink] = fill
    return new, fits


def apply(files, script, plugin, en_for, labels=False):
    """draw English into every picture line that has it -> ({path: new bytes}, [keys that did not fit])"""
    out, tight = {}, []
    for l in script['lines']:
        if not is_image(l): continue
        text = l['key'][len(PREFIX):len(PREFIX) + 6] if labels else en_for(l)
        if not text: continue
        for ref in script['images'][l['image']]['refs']:
            path, i = ref.rsplit(':', 1)
            d = out.get(path, files[path])
            idx, pal = plugin.image_data(d, int(i))
            new, fits = draw(idx, pal, text)
            if not fits and l['key'] not in tight: tight.append(l['key'])
            out[path] = plugin.put_image(d, int(i), new)
    return out, tight


if __name__ == '__main__':   # self-check: drawing keeps size, uses only the picture's own colours, wraps
    pal = np.zeros(16, '<u2'); pal[1], pal[2] = 0x7fff, 0x0421
    idx = np.zeros((20, 60), np.uint8); idx[5:15, 3:50] = 2; idx[7:13, 5:48] = 1
    new, fits = draw(idx, pal, 'Start')
    assert new.shape == idx.shape and fits and set(np.unique(new)) <= {0, 1, 2}, np.unique(new)
    new, fits = draw(idx, pal, 'A much longer label')
    assert new.shape == idx.shape
    new, fits = draw(idx, pal, 'Two\nlines')
    assert fits and new.shape == idx.shape
    tall = np.zeros((60, 12), np.uint8); tall[3:57, 2:10] = 1
    new, fits = draw(tall, pal, 'Main St')
    assert fits and new.shape == tall.shape
    print('ok', fits)
