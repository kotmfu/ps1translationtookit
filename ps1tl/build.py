"""Write a patched bin/cue (+ BPS patch) with replaced ISO files."""
import hashlib, os, shutil, struct, zlib
import numpy as np
from . import sector
from .disc import Disc, SECTOR

SYNC = bytes([0] + [0xFF] * 10 + [0])


def _write_file_sectors(f, lba, data, subheaders):
    """write data as Mode 2 Form 1 sectors starting at lba; subheaders: list of 8-byte subheaders to reuse"""
    n = (len(data) + 2047) // 2048
    data = data + b'\0' * (n * 2048 - len(data))
    CHUNK = 8192
    for c in range(0, n, CHUNK):
        k = min(CHUNK, n - c)
        s = np.zeros((k, SECTOR), np.uint8)
        for j in range(k):
            i = c + j
            sub = subheaders[min(i, len(subheaders) - 1)] if i < len(subheaders) - 1 else (b'\0\0\x89\0' * 2 if i == n - 1 else b'\0\0\x08\0' * 2)
            s[j, :16] = np.frombuffer(SYNC + sector.msf(lba + i) + b'\x02', np.uint8)
            s[j, 16:24] = np.frombuffer(sub, np.uint8)
            s[j, 24:24 + 2048] = np.frombuffer(data[i * 2048:(i + 1) * 2048], np.uint8)
        sector.fix_form1(s)
        f.seek((lba + c) * SECTOR); f.write(s.tobytes())


def _read_raw(f, lba, n):
    f.seek(lba * SECTOR); return f.read(n * SECTOR)


def _set_record(f, disc, path, lba, size):
    """patch an ISO9660 directory record (both-endian LBA + size) and refresh that sector's ECC"""
    rlba, off = disc.records[path]
    raw = bytearray(_read_raw(f, rlba, 1))
    p = 24 + off
    raw[p + 2:p + 10] = struct.pack('<I', lba) + struct.pack('>I', lba)
    raw[p + 10:p + 18] = struct.pack('<I', size) + struct.pack('>I', size)
    s = np.frombuffer(bytes(raw), np.uint8).reshape(1, SECTOR).copy()
    sector.fix_form1(s)
    f.seek(rlba * SECTOR); f.write(s.tobytes())


def _move_to_end(f, disc, path, end_lba):
    """relocate a file's raw sectors (any form) to end_lba; only header addresses change"""
    lba, size = disc.files()[path]
    n = (size + 2047) // 2048
    for c in range(0, n, 4096):
        k = min(4096, n - c)
        raw = bytearray(_read_raw(f, lba + c, k))
        for j in range(k):
            raw[j * SECTOR + 12: j * SECTOR + 15] = sector.msf(end_lba + c + j)
        f.seek((end_lba + c) * SECTOR); f.write(raw)
    _set_record(f, disc, path, end_lba, size)
    return end_lba + n


def _set_volume_size(f, n_sectors):
    raw = bytearray(_read_raw(f, 16, 1))
    raw[24 + 80:24 + 88] = struct.pack('<I', n_sectors) + struct.pack('>I', n_sectors)
    s = np.frombuffer(bytes(raw), np.uint8).reshape(1, SECTOR).copy()
    sector.fix_form1(s)
    f.seek(16 * SECTOR); f.write(s.tobytes())


def write_patched(cue, repl, out_dir, name):
    """copy the disc, replace ISO files {path: data} -> (new cue path, new bin path)"""
    disc = Disc(cue)
    os.makedirs(out_dir, exist_ok=True)
    out_bin = os.path.join(out_dir, name + '.bin')
    shutil.copyfile(disc.path, out_bin)
    with open(out_bin, 'r+b') as f:
        for path, data in repl.items():
            _replace(f, disc, path, data)
    out_cue = os.path.join(out_dir, name + '.cue')
    open(out_cue, 'w').write(f'FILE "{name}.bin" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n')
    return out_cue, out_bin


def _replace(f, disc, path, data):
    files = disc.files()
    lba, size = files[path]
    old_n, new_n = (size + 2047) // 2048, (len(data) + 2047) // 2048
    subs = [_read_raw(f, lba + i, 1)[16:24] for i in range(old_n)]
    if new_n > old_n:
        # make room: move files that start inside the new extent to the end of the image
        end = os.fstat(f.fileno()).st_size // SECTOR
        for l, p in sorted((l, p) for p, (l, s) in files.items() if lba < l < lba + new_n):
            end = _move_to_end(f, disc, p, end)
        _set_volume_size(f, end)
    _write_file_sectors(f, lba, data, subs)
    _set_record(f, disc, path, lba, len(data))


def _varint(n):
    out = bytearray()
    while True:
        x = n & 0x7F; n >>= 7
        if n == 0: out.append(0x80 | x); return bytes(out)
        out.append(x); n -= 1


def write_bps(orig, patched, out):
    """BPS delta patch, sector-aware: unchanged sectors -> SourceRead, sectors whose content moved
    (same bytes apart from the 16-byte sync/address header) -> header literal + SourceCopy, rest literal."""
    CH = 4096
    index = {}
    with open(orig, 'rb') as a:
        lba = 0
        while True:
            raw = a.read(CH * SECTOR)
            if not raw: break
            for j in range(len(raw) // SECTOR):
                index.setdefault(hashlib.blake2b(raw[j * SECTOR + 16:(j + 1) * SECTOR], digest_size=12).digest(), lba + j)
            lba += len(raw) // SECTOR
    src_size, dst_size = os.path.getsize(orig), os.path.getsize(patched)
    body = bytearray()
    state = {'kind': None, 'n': 0, 'lit': bytearray(), 'rel': 0}

    def flush():
        if state['kind'] == 'read': body.extend(_varint(((state['n'] - 1) << 2) | 0))
        elif state['kind'] == 'lit': body.extend(_varint(((len(state['lit']) - 1) << 2) | 1) + state['lit']); state['lit'] = bytearray()
        state['kind'], state['n'] = None, 0

    def source_read(n):
        if state['kind'] != 'read': flush(); state['kind'] = 'read'
        state['n'] += n

    def literal(b):
        if state['kind'] != 'lit': flush(); state['kind'] = 'lit'
        state['lit'] += b

    def source_copy(off, n):
        flush()
        d = off - state['rel']
        body.extend(_varint(((n - 1) << 2) | 2) + _varint((abs(d) << 1) | (d < 0)))
        state['rel'] = off + n

    with open(orig, 'rb') as a, open(patched, 'rb') as b:
        lba = 0
        while True:
            y = b.read(CH * SECTOR)
            if not y: break
            x = a.read(len(y))
            k = len(y) // SECTOR
            same = np.zeros(k, bool)
            if len(x) == len(y):
                same = (np.frombuffer(x, np.uint8).reshape(k, SECTOR) == np.frombuffer(y, np.uint8).reshape(k, SECTOR)).all(axis=1)
            for j in range(k):
                sec = y[j * SECTOR:(j + 1) * SECTOR]
                if same[j] or ((j + 1) * SECTOR <= len(x) and x[j * SECTOR:(j + 1) * SECTOR] == sec):
                    source_read(SECTOR); continue
                src = index.get(hashlib.blake2b(sec[16:], digest_size=12).digest())
                if src is None: literal(sec); continue
                literal(sec[:16]); source_copy(src * SECTOR + 16, SECTOR - 16)
            if len(y) % SECTOR: literal(y[k * SECTOR:])
            lba += k
    flush()
    with open(orig, 'rb') as a: sc = _crc(a)
    with open(patched, 'rb') as b: tc = _crc(b)
    patch = b'BPS1' + _varint(src_size) + _varint(dst_size) + _varint(0) + bytes(body) + struct.pack('<II', sc, tc)
    open(out, 'wb').write(patch + struct.pack('<I', zlib.crc32(patch)))


def _crc(f):
    c = 0
    while True:
        b = f.read(1 << 24)
        if not b: return c
        c = zlib.crc32(b, c)


def apply_bps(orig, patch, out):
    """reference BPS applier (used to verify patches)"""
    p = open(patch, 'rb').read()
    assert p[:4] == b'BPS1' and zlib.crc32(p[:-4]) == struct.unpack('<I', p[-4:])[0], 'bad patch'
    pos = 4
    def vi():
        nonlocal pos
        d, s = 0, 1
        while True:
            x = p[pos]; pos += 1
            d += (x & 0x7F) * s
            if x & 0x80: return d
            s <<= 7; d += s
    src = open(orig, 'rb').read()
    ssz, tsz, msz = vi(), vi(), vi(); pos += msz
    t = bytearray(); srel = trel = 0
    while pos < len(p) - 12:
        x = vi(); cmd, n = x & 3, (x >> 2) + 1
        if cmd == 0: t += src[len(t):len(t) + n]
        elif cmd == 1: t += p[pos:pos + n]; pos += n
        else:
            d = vi(); d = -(d >> 1) if d & 1 else d >> 1
            if cmd == 2: srel += d; t += src[srel:srel + n]; srel += n
            else:
                trel += d
                for _ in range(n): t.append(t[trel]); trel += 1
    assert len(t) == tsz and zlib.crc32(t) == struct.unpack('<I', p[-8:-4])[0], 'target mismatch'
    open(out, 'wb').write(t)


def make_patch(cue, script, plugin, out_dir, name=None, font='en_pixel', log=print, labels=False):
    """script -> patched bin/cue + .bps in out_dir. -> report dict"""
    from . import font as fontmod
    disc = Disc(cue)
    name = name or os.path.splitext(os.path.basename(disc.path))[0] + ' (English)'
    log('inserting text...')
    repl, report = plugin.insert(disc, script, fontmod.load(font), labels=labels)
    if not repl: return {**report, 'error': 'no translated lines to insert'}
    log(f"{report['files_changed']} files changed, {len(report['files_skipped'])} skipped; writing disc...")
    out_cue, out_bin = write_patched(cue, repl, out_dir, name)
    log('writing BPS patch...')
    bps = os.path.join(out_dir, name + '.bps')
    write_bps(disc.path, out_bin, bps)
    log(f'done (patch {os.path.getsize(bps) / 1e6:.1f} MB)')
    return {**report, 'cue': out_cue, 'bin': out_bin, 'patch': bps}
