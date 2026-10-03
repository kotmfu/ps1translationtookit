"""PS1 translation toolkit.

  python -m ps1tl app       [game.cue]                      # desktop app (Qt)
  python -m ps1tl gui       [game.cue]                      # same editor in the browser
  python -m ps1tl info      game.cue
  python -m ps1tl extract   game.cue  script.json           # dialogue -> json (unique lines)
  python -m ps1tl preview   game.cue  script.json out.png [start] [count]
  python -m ps1tl translate game.cue  script.json [--limit N] [--glossary glossary.txt] [--model opus|sonnet|haiku]
  python -m ps1tl ocr       game.cue  script.json           # label glyphs -> Japanese text for every line
  python -m ps1tl save-tl   game.cue  script.json translation.csv   # shareable translation file
  python -m ps1tl load-tl   game.cue  script.json translation.csv
  python -m ps1tl patch     game.cue  script.json outdir    # patched bin/cue + .bps
"""
import json, os, sys
from .disc import Disc
from . import games


def main():
    a = sys.argv[1:]
    if a and a[0] == 'app':
        from .app import main as app_main
        return app_main([sys.argv[0]] + a[1:])
    if a and a[0] == 'gui':
        from .gui import serve
        return serve(a[1] if len(a) > 1 else None)
    if len(a) < 2: return print(__doc__)
    cmd, disc = a[0], Disc(a[1])
    serial = disc.serial()
    plugin = games.for_serial(serial)
    load = lambda: json.load(open(a[2], encoding='utf-8'))
    dump = lambda s: json.dump(s, open(a[2], 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    if cmd == 'info':
        print(serial, plugin.NAME)
        for p, (lba, size) in disc.files().items(): print(f'{lba:8d} {size:10d} {p}')
    elif cmd == 'extract':
        script = plugin.extract(disc); dump(script)
        print(f'{len(script["lines"])} unique lines, {len(script["glyphs"])} unique glyphs -> {a[2]}')
    elif cmd == 'preview':
        from .translate import render
        script = load()
        start, count = (int(a[4]) if len(a) > 4 else 0), (int(a[5]) if len(a) > 5 else 30)
        open(a[3], 'wb').write(render(script['lines'][start:start + count], script['glyphs'], plugin.glyph_pixels))
    elif cmd == 'translate':
        from . import translate
        opt = dict(zip(a[3::2], a[4::2]))
        g = opt.get('--glossary')
        script = load()
        translate.translate_lines(script, plugin, int(opt['--limit']) if '--limit' in opt else None,
                                  open(g, encoding='utf-8').read() if g and os.path.exists(g) else '', save=lambda: dump(script),
                                  model=opt.get('--model', 'opus'))
    elif cmd == 'ocr':
        from . import ocr
        script = load(); ocr.label_glyphs(script, plugin, save=lambda: dump(script))
    elif cmd == 'save-tl':
        from . import tfile
        tfile.save(load(), a[3]); print('saved', a[3])
    elif cmd == 'load-tl':
        from . import tfile
        script = load(); updated, unknown = tfile.load(script, a[3]); dump(script)
        print(f'{updated} lines updated, {unknown} unknown keys')
    elif cmd == 'patch':
        from .build import make_patch
        r = make_patch(a[1], load(), plugin, a[3])
        print({k: (len(v) if isinstance(v, dict) else v) for k, v in r.items()})
    else:
        print(__doc__)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
