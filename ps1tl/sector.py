"""CD-ROM Mode 2 Form 1 sector EDC/ECC (vectorised over many sectors with numpy)."""
import numpy as np

_f = np.zeros(256, np.uint8); _b = np.zeros(256, np.uint8); _edc = np.zeros(256, np.uint32)
for i in range(256):
    j = ((i << 1) ^ (0x11D if i & 0x80 else 0)) & 0xFF
    _f[i] = j; _b[i ^ j] = i
    e = i
    for _ in range(8): e = (e >> 1) ^ (0xD8018001 if e & 1 else 0)
    _edc[i] = e


def edc(block):
    """block: (n, len) uint8 -> (n,) uint32 CRC (poly 0xD8018001, init 0)"""
    crc = np.zeros(block.shape[0], np.uint32)
    for k in range(block.shape[1]):
        crc = (crc >> 8) ^ _edc[(crc ^ block[:, k]) & 0xFF]
    return crc


def _ecc_block(src, major_count, minor_count, major_mult, minor_inc):
    size = major_count * minor_count
    out = np.zeros((src.shape[0], major_count * 2), np.uint8)
    for major in range(major_count):
        index = (major >> 1) * major_mult + (major & 1)
        a = np.zeros(src.shape[0], np.uint8); b = np.zeros(src.shape[0], np.uint8)
        for _ in range(minor_count):
            t = src[:, index]
            index += minor_inc
            if index >= size: index -= size
            a ^= t; b ^= t; a = _f[a]
        a = _b[_f[a] ^ b]
        out[:, major] = a; out[:, major + major_count] = a ^ b
    return out


def fix_form1(sectors):
    """sectors: (n, 2352) uint8, Mode 2 Form 1 with sync/header/subheader/data set. Fills EDC + ECC in place."""
    s = sectors
    e = edc(s[:, 16:16 + 8 + 2048])
    s[:, 0x818:0x81C] = e.view(np.uint8).reshape(-1, 4) if e.dtype.byteorder != '>' else e.byteswap().view(np.uint8).reshape(-1, 4)
    hdr = s[:, 12:16].copy()
    s[:, 12:16] = 0                       # Mode 2: ECC computed with the header zeroed
    s[:, 0x81C:0x8C8] = _ecc_block(s[:, 12:], 86, 24, 2, 86)
    s[:, 0x8C8:0x930] = _ecc_block(s[:, 12:], 52, 43, 86, 88)
    s[:, 12:16] = hdr
    return s


def msf(lba):
    """header address bytes (BCD minute/second/frame, +150 pregap)"""
    lba += 150
    bcd = lambda v: (v // 10) << 4 | v % 10
    return bytes([bcd(lba // 4500), bcd(lba // 75 % 60), bcd(lba % 75)])
