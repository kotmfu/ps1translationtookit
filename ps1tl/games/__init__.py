"""Game plugins, looked up by disc serial. A plugin exposes SERIALS, NAME, extract(disc), glyph_pixels(entry)."""
from . import yuuyami

PLUGINS = [yuuyami]


def for_serial(serial):
    for p in PLUGINS:
        if serial in p.SERIALS: return p
    raise SystemExit(f'unknown game {serial}; supported: ' + ', '.join(f'{p.NAME} ({", ".join(p.SERIALS)})' for p in PLUGINS))
