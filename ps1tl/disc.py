"""Single-track MODE2/2352 bin/cue reading + ISO9660 listing."""
import os, re, struct

SECTOR = 2352


def bin_path(cue):
    text = open(cue, encoding='utf-8', errors='replace').read()
    m = re.search(r'FILE "(.+?)" BINARY', text)
    if not m or 'MODE2/2352' not in text:
        raise ValueError('only MODE2/2352 bin/cue is supported')
    if len(re.findall(r'TRACK', text)) > 1:
        print('warning: multi-track cue, only track 1 (data) is read')
    return os.path.join(os.path.dirname(cue), m.group(1))


class Disc:
    def __init__(self, cue):
        self.path = bin_path(cue)
        self.f = open(self.path, 'rb')

    def read(self, lba, size):
        out = bytearray()
        for i in range((size + 2047) // 2048):
            self.f.seek((lba + i) * SECTOR)
            out += self.f.read(SECTOR)[24:24 + 2048]
        return bytes(out[:size])

    def files(self):
        """{path: (lba, size)} for every file in the ISO9660 tree."""
        root = self.read(16, 2048)[156:190]
        stack = [('/', struct.unpack_from('<I', root, 2)[0], struct.unpack_from('<I', root, 10)[0])]
        out, seen = {}, set()
        self.records = {}   # path -> (directory sector lba, byte offset of the record in it)
        while stack:
            path, lba, size = stack.pop()
            if lba in seen: continue
            seen.add(lba)
            data, i = self.read(lba, size), 0
            while i < len(data):
                l = data[i]
                if l == 0: i = (i // 2048 + 1) * 2048; continue
                r = data[i:i + l]; rec = (lba + i // 2048, i % 2048); i += l
                name = r[33:33 + r[32]]
                if name in (b'\0', b'\1'): continue
                elba, esz = struct.unpack_from('<I', r, 2)[0], struct.unpack_from('<I', r, 10)[0]
                name = name.decode('ascii', 'replace').split(';')[0]
                if r[25] & 2: stack.append((path + name + '/', elba, esz))
                else: out[path + name] = (elba, esz); self.records[path + name] = rec
        return out

    def sectors(self):
        return os.path.getsize(self.path) // SECTOR

    def serial(self):
        """game serial from SYSTEM.CNF, e.g. 'SLPS_022.74'"""
        lba, size = self.files()['/SYSTEM.CNF']
        m = re.search(rb'cdrom:\\?([A-Z]{4}_\d{3}\.\d{2})', self.read(lba, size))
        return m.group(1).decode() if m else None
