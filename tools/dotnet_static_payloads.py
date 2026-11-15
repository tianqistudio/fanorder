"""Bounded, non-executing readers for .NET bundles and embedded portable PDBs.

Format sources: dotnet/runtime Microsoft.NET.HostModel/Bundle/{Manifest,
FileEntry,Bundler}.cs and System.Reflection.Metadata PE debug records.
This module never writes extracted files or loads an assembly.
"""
from __future__ import annotations

import pathlib
import struct
import zlib

BUNDLE_SIGNATURE = bytes.fromhex(
    "8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae"
)


def pe_debug_records(data: bytes):
    """Yield (type, raw offset, record bytes) for the outer PE only."""
    if not data.startswith(b"MZ") or len(data) < 64:
        return
    pe = struct.unpack_from("<I", data, 60)[0]
    if pe + 24 > len(data) or data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("invalid PE header")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    if opt + optional_size > len(data) or optional_size < 96:
        raise ValueError("invalid PE optional header")
    magic = struct.unpack_from("<H", data, opt)[0]
    directories = opt + (112 if magic == 0x20B else 96 if magic == 0x10B else 0)
    if directories == opt or directories + 56 > opt + optional_size:
        return
    rva, size = struct.unpack_from("<II", data, directories + 6 * 8)
    if not rva or not size:
        return
    if size > 1024 * 1024 or size % 28:
        raise ValueError("invalid PE debug directory size")
    sections = opt + optional_size
    if count > 96 or sections + count * 40 > len(data):
        raise ValueError("invalid PE sections")
    headers = struct.unpack_from("<I", data, opt + 60)[0]
    offset = rva if rva < headers else None
    for index in range(count):
        virtual_size, address, raw_size, raw = struct.unpack_from("<IIII", data, sections + index * 40 + 8)
        if address <= rva < address + max(virtual_size, raw_size):
            if rva - address + size > raw_size:
                raise ValueError("PE debug directory outside raw section")
            offset = raw + rva - address
            break
    if offset is None or offset + size > len(data):
        raise ValueError("PE debug directory outside file")
    for position in range(offset, offset + size, 28):
        _, _, _, _, kind, length, _, pointer = struct.unpack_from("<IIHHIIII", data, position)
        if pointer + length > len(data):
            raise ValueError("PE debug data outside file")
        yield kind, pointer, data[pointer:pointer + length]


def _inflate(data: bytes, expected: int, maximum: int) -> bytes:
    if expected < 0 or expected > maximum:
        raise ValueError("expanded member exceeds limit")
    try:
        inflater = zlib.decompressobj(-15)
        result = inflater.decompress(data, expected + 1)
    except zlib.error as error:
        raise ValueError("invalid compressed member") from error
    if len(result) != expected or not inflater.eof or inflater.unconsumed_tail:
        raise ValueError("expanded member size mismatch")
    return result


def iter_dotnet_payloads(data: bytes, logical_name: str, max_member=128 * 1024 * 1024, max_total=512 * 1024 * 1024):
    """Yield immediate bundle members and outer-PE embedded portable PDBs."""
    total = 0
    for index, (kind, pointer, record) in enumerate(pe_debug_records(data)):
        if kind != 17:
            continue
        if len(record) < 8 or record[:4] != b"MPDB":
            raise ValueError("invalid embedded portable PDB header")
        expected = struct.unpack_from("<I", record, 4)[0]
        total += expected
        if total > max_total:
            raise ValueError("expanded total exceeds limit")
        pdb = _inflate(record[8:], expected, max_member)
        if not pdb.startswith(b"BSJB"):
            raise ValueError("invalid portable PDB metadata")
        filename = pathlib.PurePosixPath(logical_name.replace("\\", "/")).name
        yield logical_name + "!" + filename + ".embedded-%d.pdb" % index, pdb

    marker = data.find(BUNDLE_SIGNATURE)
    if marker < 8:
        return
    cursor = struct.unpack_from("<q", data, marker - 8)[0]
    if cursor == 0:
        return
    if cursor < marker + len(BUNDLE_SIGNATURE) or cursor + 12 > len(data):
        raise ValueError("invalid bundle header offset")

    def read(fmt):
        nonlocal cursor
        length = struct.calcsize(fmt)
        if cursor + length > len(data):
            raise ValueError("truncated bundle metadata")
        result = struct.unpack_from(fmt, data, cursor)
        cursor += length
        return result

    def read_string():
        nonlocal cursor
        length = 0
        for shift in range(0, 35, 7):
            value = read("<B")[0]
            length |= (value & 127) << shift
            if not value & 128:
                break
        else:
            raise ValueError("invalid bundle string length")
        if length > 4096 or cursor + length > len(data):
            raise ValueError("bundle string exceeds bounds")
        value = data[cursor:cursor + length].decode("utf-8", "strict")
        cursor += length
        return value

    major, minor, count = read("<IIi")
    if major not in (1, 2, 6) or minor != 0 or not 0 <= count <= 10000:
        raise ValueError("unsupported bundle version or member count")
    read_string()
    if major >= 2:
        read("<qqqqQ")
    members = []
    for _ in range(count):
        offset, size = read("<qq")
        compressed = read("<q")[0] if major >= 6 else 0
        kind = read("<B")[0]
        name = read_string().replace("\\", "/")
        path = pathlib.PurePosixPath(name)
        if not name or path.is_absolute() or ".." in path.parts or ":" in name or "\0" in name:
            raise ValueError("unsafe bundle member name")
        stored = compressed or size
        if min(offset, size, compressed) < 0 or offset + stored > len(data) or size > max_member:
            raise ValueError("bundle member outside limits")
        total += size
        if total > max_total:
            raise ValueError("expanded total exceeds limit")
        members.append((name, offset, size, compressed))
    for name, offset, size, compressed in members:
        payload = data[offset:offset + (compressed or size)]
        if compressed:
            payload = _inflate(payload, size, max_member)
        yield logical_name + "!" + name, payload
