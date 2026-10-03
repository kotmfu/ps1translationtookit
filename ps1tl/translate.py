"""Read + translate extracted lines with Claude vision.

Lines are rendered from the game's own glyph bitmaps (no Japanese character table needed):
each batch becomes one numbered image, Claude returns the Japanese transcription and English.
"""
import io
from PIL import Image, ImageDraw

BATCH = 20   # lines per request; WORKERS requests run at once (llm.py)
SCALE = 2

SYSTEM = """You are translating the Japanese PS1 horror/adventure game "{game}" into natural English for a fan patch.
You get an image of numbered dialogue lines rendered in the game's font. For each line number:
- ja: transcribe the Japanese exactly ("・・" is the game's ellipsis; keep it as ・・).
- en: translate it. Keep it short: each line is one row of a narrow text box; aim for at most ~1.6x the Japanese
  character count in English letters. Consecutive lines are often one sentence split over two rows: translate
  them so each row reads naturally on its own. Render a trailing ・・ as "..". Keep names in Hepburn romanization.
Use only ASCII letters, digits and . , ! ? ' " - : ; ( ) ~ & / + %.
Lines appear roughly in story order; earlier context is given when available.
{glossary}"""

SCHEMA = {
    'type': 'object',
    'properties': {'lines': {'type': 'array', 'items': {
        'type': 'object',
        'properties': {'n': {'type': 'integer'}, 'ja': {'type': 'string'}, 'en': {'type': 'string'}},
        'required': ['n', 'ja', 'en'], 'additionalProperties': False}}},
    'required': ['lines'], 'additionalProperties': False,
}


def _draw(cells, rowh, rows, numbered):
    """cells: per row list of (pixel rows 16x16 of palette values, x, w); 15 = transparent"""
    pad = 44 if numbered else 4
    width = pad + max([sum(w + 0 for _, _, w in r) for r in rows] + [16]) + 8
    im = Image.new('L', (width, rowh * len(rows) + 4), 0)
    dr = ImageDraw.Draw(im)
    for r, row in enumerate(rows):
        y = r * rowh + 2
        if numbered: dr.text((2, y + 4), f'{r + 1:>3}', fill=160)
        x = pad
        for px, gx0, w in row:
            for gy, line in enumerate(px):
                for gx, v in enumerate(line):
                    if v != 15 and 0 <= x + gx - gx0 < width: im.putpixel((x + gx - gx0, y + gy), 255 if v >= 6 else 90)
            x += w
    im = im.resize((im.width * SCALE, im.height * SCALE), Image.NEAREST)
    buf = io.BytesIO(); im.save(buf, 'PNG')
    return buf.getvalue()


def render(lines, glyphs, glyph_pixels, numbered=True):
    """Japanese lines in the game's own glyphs, white-on-black like the game"""
    rows = [[(glyph_pixels(glyphs[g]), glyphs[g]['x'], glyphs[g]['w'] + 2) for g in l['glyphs']] for l in lines]
    return _draw(None, 20, rows, numbered)


def render_en(texts, font_glyphs):
    """English preview in the built-in font, as it will look in game"""
    from .font import to_cell
    rows = []
    for t in texts:
        row = []
        for c in t:
            if c not in font_glyphs: c = '?'
            bm, x, w = to_cell(font_glyphs[c])
            px = [v for b in bm for v in (b & 15, b >> 4)]
            row.append(([px[i * 16:(i + 1) * 16] for i in range(16)], x, w))
        rows.append(row)
    return _draw(None, 20, rows, False)


def line_width(line, glyphs):
    return sum(glyphs[g]['w'] + 2 for g in line['glyphs'])


def text_width(text, font_glyphs):
    return sum(max(len(r) for r in font_glyphs.get(c, font_glyphs['?'])) + 2 for c in text)


def translate_lines(script, plugin, limit=None, glossary='', log=print, save=None, stop=lambda: False, model='opus'):
    """translate untranslated lines in place, batch by batch; save() is called after each batch"""
    from . import llm, ocr
    glyphs = script['glyphs']
    from .tfile import shared_en, effective_en
    shared = shared_en(script)
    todo = [l for l in script['lines'] if l['glyphs'] and not effective_en(l, shared)][:limit]   # pictures: images.py
    system = SYSTEM.format(game=script['game'], glossary=f'Glossary (use these):\n{glossary}' if glossary else '')
    done = [l for l in script['lines'] if l.get('en')]
    batches = [todo[i:i + BATCH] for i in range(0, len(todo), BATCH)]
    log(f'translating {len(todo)} lines with {model} via {llm.backend() or "nothing"}: '
        f'{len(batches)} requests, {llm.WORKERS} at a time (each takes about a minute)')

    def task(batch):
        context = '\n'.join(f'{l.get("ja", "")} => {l["en"]}' for l in done[-15:])
        prompt = (f'Previous lines for context:\n{context}\n\n' if context else '') + f'Transcribe and translate lines 1-{len(batch)}.'
        return lambda: llm.ask(render(batch, glyphs, plugin.glyph_pixels), prompt, SCHEMA, system, model)

    finished = 0
    for k, out in llm.parallel([task(b) for b in batches], stop):
        batch = batches[k]
        if isinstance(out, llm.LLMError):
            log(f'request {k + 1} failed: {out}'); continue
        for item in out['lines']:
            if 1 <= item['n'] <= len(batch):
                batch[item['n'] - 1].update(ja=item['ja'], en=item['en'])
        done += [l for l in batch if l.get('en')]
        ocr.learn(script); ocr.apply(script)   # transcriptions teach the glyph table, which fills other lines
        if save: save()
        finished += 1
        log(f'request {finished}/{len(batches)} done · {sum(1 for l in script["lines"] if l.get("en"))} lines translated in total')
    if stop(): log('stopped')
