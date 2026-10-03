"""Japanese OCR by glyph: label each unique glyph bitmap once, then every line's text follows.

script['chars'] = {glyph id: character}. Filled from glyph sheets (Claude vision) and refined from
line transcriptions (translate step), where a line's ja text lines up 1:1 with its glyphs.
"""
import collections, io
from PIL import Image, ImageDraw

PER_SHEET, COLS, S = 100, 10, 3

SCHEMA = {
    'type': 'object',
    'properties': {'glyphs': {'type': 'array', 'items': {
        'type': 'object', 'properties': {'n': {'type': 'integer'}, 'ch': {'type': 'string'}},
        'required': ['n', 'ch'], 'additionalProperties': False}}},
    'required': ['glyphs'], 'additionalProperties': False,
}

PROMPT = ("Each numbered cell shows one character from a Japanese game font (hiragana, katakana, kanji, "
          "punctuation such as 、。・！？ー～「」, digits or letters; small kana like っゃゅょ are drawn smaller). "
          "For every number 1-{n}, give the single character it shows. Use full-width forms for punctuation.")


def sheet(gids, glyphs, glyph_pixels):
    cell = 16 * S + 14
    rows = (len(gids) + COLS - 1) // COLS
    im = Image.new('L', (COLS * cell, rows * cell), 0)
    dr = ImageDraw.Draw(im)
    for k, g in enumerate(gids):
        x0, y0 = (k % COLS) * cell, (k // COLS) * cell
        dr.text((x0 + 2, y0 + 1), str(k + 1), fill=140)
        for y, row in enumerate(glyph_pixels(glyphs[g])):
            for x, v in enumerate(row):
                if v != 15: dr.rectangle([x0 + 12 + x * S, y0 + 12 + y * S, x0 + 11 + (x + 1) * S, y0 + 11 + (y + 1) * S], fill=255 if v >= 6 else 90)
    buf = io.BytesIO(); im.save(buf, 'PNG')
    return buf.getvalue()


def apply(script, overwrite=False):
    """fill each line's ja from the glyph table; -> number of lines filled"""
    chars, n = script.get('chars', {}), 0
    for l in script['lines']:
        if l['glyphs'] and (overwrite or not l.get('ja')) and all(g in chars for g in l['glyphs']):
            l['ja'] = ''.join(chars[g] for g in l['glyphs']); n += 1
    return n


def learn(script):
    """vote glyph -> char from lines whose ja has exactly one char per glyph"""
    votes = collections.defaultdict(collections.Counter)
    for l in script['lines']:
        ja = l.get('ja', '')
        if ja and len(ja) == len(l['glyphs']):
            for g, c in zip(l['glyphs'], ja): votes[g][c] += 1
    chars = script.setdefault('chars', {})
    for g, v in votes.items(): chars[g] = v.most_common(1)[0][0]
    return len(votes)


def label_glyphs(script, plugin, log=print, save=None, stop=lambda: False, model='opus'):
    """ask Claude for every unlabelled glyph, sheet by sheet; then fill line text"""
    from . import llm
    chars = script.setdefault('chars', {})
    todo = [g for g in script['glyphs'] if g not in chars]
    sheets = [todo[i:i + PER_SHEET] for i in range(0, len(todo), PER_SHEET)]
    log(f'labelling {len(todo)} glyphs with {model} via {llm.backend() or "nothing"}: '
        f'{len(sheets)} requests, {llm.WORKERS} at a time')
    task = lambda gids: lambda: llm.ask(sheet(gids, script['glyphs'], plugin.glyph_pixels), PROMPT.format(n=len(gids)),
                                        SCHEMA, model=model, effort='medium')
    for k, out in llm.parallel([task(g) for g in sheets], stop):
        gids = sheets[k]
        if isinstance(out, llm.LLMError):
            log(f'request {k + 1} failed: {out}'); continue
        for item in out['glyphs']:
            if 1 <= item['n'] <= len(gids) and len(item['ch']) == 1: chars[gids[item['n'] - 1]] = item['ch']
        filled = apply(script)
        if save: save()
        log(f'{len(chars)}/{len(script["glyphs"])} glyphs labelled, {filled} more lines got Japanese text')
