"""Translation files: shareable CSV (key, ja, en, notes) that people edit and load back.

Lines are matched by key (hash of the line's glyph bitmaps), so a translation file made from one
copy of the game applies to any copy of the same game.
"""
import csv

COLUMNS = ['key', 'ja', 'en', 'notes']
_SAME = str.maketrans({'一': 'ー', '－': 'ー', '?': '？', '!': '！', ' ': None, '　': None})


def norm_ja(ja):
    """matching key for Japanese text: smooths over common OCR mix-ups (一/ー, half/full-width ?!)"""
    return (ja or '').translate(_SAME)


def shared_en(script):
    """{normalised ja: en} from translated lines. The same sentence is often stored with slightly different
    glyph bitmaps (so a different key); one translation covers every copy."""
    out = {}
    for l in script['lines']:
        if l.get('en') and l.get('ja'): out.setdefault(norm_ja(l['ja']), l['en'])
    return out


def effective_en(line, shared):
    """the line's own English, else a translation of identical Japanese elsewhere ('' if none)"""
    return line.get('en') or (shared.get(norm_ja(line['ja'])) if line.get('ja') else '') or ''


def save(script, path):
    with open(path, 'w', newline='', encoding='utf-8-sig') as f:   # BOM so Excel shows Japanese
        w = csv.writer(f)
        w.writerow(COLUMNS)
        for l in script['lines']:
            w.writerow([l['key'], l.get('ja', ''), l.get('en', ''), l.get('notes', '')])


def load(script, path, overwrite=True):
    """merge a translation file into script; -> (updated, unknown keys)"""
    by_key = {l['key']: l for l in script['lines']}
    updated = unknown = 0
    with open(path, newline='', encoding='utf-8-sig') as f:
        for row in csv.DictReader(f):
            l = by_key.get(row.get('key', ''))
            if not l: unknown += 1; continue
            for col in ('ja', 'en', 'notes'):
                v = (row.get(col) or '').strip()
                if v and (overwrite or not l.get(col)): l[col] = v
            updated += 1
    return updated, unknown
