"""FLB 2.00 archive (Spike engine; Yuuyami Doori Tankentai).

header: +00 'FLB\\x90' +04 '2.00' +0C dir-table off +10 data off +14 entry count
        +18 subdir count +1C data size; entries at +28: (offset | subdir index, tag).
tag >> 24: 0/1 = file at data+offset, >=2 = subdir (16-byte (off, count, 0, size) record in dir table).
Children sit back to back in the data area; a file's span runs to the next item (keeps original padding).
"""
import struct

u32 = lambda b, o: struct.unpack_from('<I', b, o)[0]


def short(path):
    """'/008/137/018' -> '8/137/18' (the label a diagnostic build shows)"""
    return '/'.join(str(int(x)) for x in path.strip('/').split('/'))


def long(label):
    """'8/137/18' -> '/008/137/018'"""
    return '/' + '/'.join(f'{int(x):03d}' for x in label.split('/'))


def _items(b):
    """header fields + [(entry index, is_dir, data offset)] sorted by data offset, with spans"""
    toff, doff, n1, n2, total = (u32(b, o) for o in (12, 16, 20, 24, 28))
    items = []
    for i in range(n1):
        off, tag = struct.unpack_from('<2I', b, 0x28 + i * 8)
        is_dir = tag >> 24 >= 2
        items.append((i, is_dir, struct.unpack_from('<I', b, toff + off * 16)[0] if is_dir else off))
    order = sorted(items, key=lambda t: t[2])
    starts = [t[2] for t in order] + [total]
    spans = {t[0]: (t[2], starts[k + 1]) for k, t in enumerate(order)}
    return toff, doff, total, items, order, spans


def _child(b, doff, start, end):
    c = b[doff + start: doff + end]
    return c[:u32(c, 16) + u32(c, 28)]


def walk(b, path=''):
    """yield (path, data) for every leaf file"""
    assert b[:4] == b'FLB\x90', path
    toff, doff, total, items, order, spans = _items(b)
    for i, is_dir, _ in items:
        s, e = spans[i]
        if is_dir: yield from walk(_child(b, doff, s, e), f'{path}/{i:03d}')
        else: yield f'{path}/{i:03d}', b[doff + s: doff + e]


ALIGN = 2048  # size changes are rounded up to whole sectors so everything after a change moves by whole
              # sectors; a delta patch (BPS) can then express the shifted data as cheap copies


def _span(old_span, new_len):
    return old_span if new_len <= old_span else old_span + -(-(new_len - old_span) // ALIGN) * ALIGN


def rebuild(b, repl, path=''):
    """new archive bytes with repl = {path: new file data}; unchanged subtrees are copied verbatim.
    Every item's span changes by a multiple of ALIGN (files pad with zeros)."""
    if not any(p.startswith(path + '/') for p in repl): return b
    toff, doff, total, items, order, spans = _items(b)
    hdr = bytearray(b[:doff])
    out, pos = bytearray(), 0
    for i, is_dir, _ in order:
        s, e = spans[i]
        p = f'{path}/{i:03d}'
        if is_dir:
            old = _child(b, doff, s, e)
            new = rebuild(old, repl, p)
            blob = new + b'\0' * ((e - s) - len(old))   # keep the original slack; new - old is k * ALIGN
            rec = toff + u32(hdr, 0x28 + i * 8) * 16
            struct.pack_into('<I', hdr, rec, pos)
            struct.pack_into('<I', hdr, rec + 12, u32(new, 28))
        else:
            blob = repl.get(p)
            blob = b[doff + s: doff + e] if blob is None else blob.ljust(_span(e - s, len(blob)), b'\0')
            struct.pack_into('<I', hdr, 0x28 + i * 8, pos)
        out += blob; pos += len(blob)
    struct.pack_into('<I', hdr, 28, pos)
    return bytes(hdr + out)
