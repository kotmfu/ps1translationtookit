"""End-to-end check: fake English for N lines -> patched disc -> read back and verify.

  python -m ps1tl.selftest game.cue script.json outdir [N]
"""
import json, sys, struct
import numpy as np
from .disc import Disc, SECTOR
from . import flb, sector, font as fontmod, build, games


def main(cue, script_path, out_dir, n=300):
    disc = Disc(cue)
    plugin = games.for_serial(disc.serial())
    script = json.load(open(script_path, encoding='utf-8'))
    for i, l in enumerate(script['lines']):
        l['en'] = f'Line {i}: the quick brown fox?' if i < n else ''
    rep = build.make_patch(cue, script, plugin, out_dir, name='selftest')
    print({k: (len(v) if isinstance(v, dict) else v) for k, v in rep.items()})

    g = fontmod.load()
    bms = lambda toks: [fontmod.to_cell(fontmod.join(t, g))[0] for t in toks]
    from .tfile import shared_en, effective_en
    shared = shared_en(script)   # identical Japanese elsewhere inherits the translation
    want = {l['key']: effective_en(l, shared) for l in script['lines'] if effective_en(l, shared)}
    new, old = Disc(rep['cue']), disc
    nl, ns = new.files()['/DATA/FILELINK.FLB'], old.files()['/DATA/FILELINK.FLB']
    newf = dict(flb.walk(new.read(*nl)))
    oldf = dict(flb.walk(old.read(*ns)))
    assert newf.keys() == oldf.keys()
    changed = [p for p in newf if newf[p] != oldf[p]]
    seen = 0
    for p in changed:
        if not plugin.parse_acd(oldf[p]): continue   # pictures: checked against images.draw in build tests
        _, _, msgs_old = plugin.parse_acd(oldf[p])
        bm_old = plugin.parse_acd(oldf[p])[0]
        bm, _, msgs = plugin.parse_acd(newf[p])
        assert len(msgs) == len(msgs_old), p
        # textures must still point at the same image data after the glyph block grew
        so, sn = struct.unpack_from('<13I', oldf[p], 8)[1:], struct.unpack_from('<13I', newf[p], 8)[1:]
        to = [struct.unpack_from('<I', oldf[p], o)[0] for o in range(so[8], so[9], 12)] + [struct.unpack_from('<I', oldf[p], o)[0] for o in range(so[9], so[10], 8)]
        tn = [struct.unpack_from('<I', newf[p], o)[0] for o in range(sn[8], sn[9], 12)] + [struct.unpack_from('<I', newf[p], o)[0] for o in range(sn[9], sn[10], 8)]
        for a, b in zip(to, tn):
            old = oldf[p][so[10] + a: so[10] + a + 64]   # new file may carry sector padding at the end; compare what the old has
            assert old == newf[p][sn[10] + b: sn[10] + b + len(old)], f'{p}: texture moved wrong'
        for (seq, ctrl), (oseq, octrl) in zip(msgs, msgs_old):
            assert ctrl == octrl
            key = plugin.line_key([plugin.gid(bm_old[k]) for k in oseq])
            if key in want:
                w = want[key]
                assert [bm[k] for k in seq] in (bms(fontmod.pack(w, g)), bms(w), bms(w.upper())), p  # packed / fallbacks
                seen += 1
            else:
                assert [bm[k] for k in seq] == [bm_old[k] for k in oseq], p  # untranslated lines intact
    # every FLB sector must have valid EDC/ECC
    with open(new.path, 'rb') as f:
        lba, size = nl
        nsec = (size + 2047) // 2048
        for c in range(0, nsec, 20000):
            k = min(20000, nsec - c)
            f.seek((lba + c) * SECTOR)
            s = np.frombuffer(f.read(k * SECTOR), np.uint8).reshape(-1, SECTOR)
            assert np.array_equal(sector.fix_form1(s.copy()), s), f'bad ECC near sector {lba + c}'
    # the BPS patch must reproduce the patched image from the original
    import os, zlib
    rebuilt = os.path.join(out_dir, 'from_bps.bin')
    build.apply_bps(disc.path, rep['patch'], rebuilt)
    assert build._crc(open(rebuilt, 'rb')) == build._crc(open(rep['bin'], 'rb')), 'BPS output differs'
    os.remove(rebuilt)
    print(f'OK: {len(changed)} ACDs changed, {seen} translated lines verified, other files identical, ECC valid, '
          f'BPS reproduces the disc ({os.path.getsize(rep["patch"]) / 1e6:.1f} MB)')
    print('files:', {p: v for p, v in new.files().items()})


if __name__ == '__main__':
    a = sys.argv[1:]
    main(a[0], a[1], a[2], int(a[3]) if len(a) > 3 else 300)
