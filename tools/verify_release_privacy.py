"""Reject private build paths in distributable files without executing them.

Canonical source: tianqistudio/fanorder, tools/verify_release_privacy.py.
Other standalone repositories may vendor this file unchanged with its SHA-256.
Reports include locations and hashes, never the matched private strings.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import re
import struct
import sys
import zipfile

from dotnet_static_payloads import iter_dotnet_payloads, pe_debug_records

SCANNER_VERSION = "1.0.0"
MAX_FILE_BYTES = 512 * 1024 * 1024
MAX_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAX_DEPTH = 6
ARCHIVE_SUFFIXES = {".zip", ".nupkg", ".snupkg"}
BINARY_SUFFIXES = {".dll", ".exe", ".pdb"}


def compressed_uint(data: bytes, offset: int) -> tuple[int, int]:
    first = data[offset]
    if first < 0x80:
        return first, offset + 1
    if first < 0xC0:
        return ((first & 0x3F) << 8) | data[offset + 1], offset + 2
    if first < 0xE0:
        return ((first & 0x1F) << 24) | int.from_bytes(data[offset + 1:offset + 4], "big"), offset + 4
    raise ValueError("invalid compressed integer")


def portable_pdb_documents(data: bytes) -> list[str]:
    """Decode Document.Name, whose path components are separate #Blob entries."""
    if data[:4] != b"BSJB":
        raise ValueError("not portable PDB metadata")
    version_size = struct.unpack_from("<I", data, 12)[0]
    cursor = (16 + version_size + 3) & ~3
    stream_count = struct.unpack_from("<H", data, cursor + 2)[0]
    cursor += 4
    streams: dict[str, bytes] = {}
    for _ in range(stream_count):
        offset, size = struct.unpack_from("<II", data, cursor)
        end = data.index(b"\0", cursor + 8)
        name = data[cursor + 8:end].decode("ascii")
        streams[name] = data[offset:offset + size]
        cursor = (end + 4) & ~3
    tables = streams.get("#~", streams.get("#-", b""))
    blobs = streams["#Blob"]
    heap_sizes = tables[6]
    valid = struct.unpack_from("<Q", tables, 8)[0]
    cursor = 24
    rows = {}
    for table in range(64):
        if valid & (1 << table):
            rows[table] = struct.unpack_from("<I", tables, cursor)[0]
            cursor += 4
    # A portable PDB stores the debug tables (48..55); type-system row counts
    # referenced by those tables live in #Pdb, not before Document in #~.
    if any(count for table, count in rows.items() if table < 48):
        raise ValueError("unexpected type-system table in portable PDB")
    blob_width = 4 if heap_sizes & 4 else 2
    guid_width = 4 if heap_sizes & 2 else 2
    row_width = 2 * blob_width + 2 * guid_width

    def blob(index: int) -> bytes:
        size, start = compressed_uint(blobs, index)
        end = start + size
        if end > len(blobs):
            raise ValueError("truncated PDB blob")
        return blobs[start:end]

    documents = []
    for index in range(rows.get(48, 0)):
        start = cursor + index * row_width
        name_index = int.from_bytes(tables[start:start + blob_width], "little")
        name = blob(name_index)
        if not name:
            continue
        separator = chr(name[0])
        part_cursor = 1
        parts = []
        while part_cursor < len(name):
            part_index, part_cursor = compressed_uint(name, part_cursor)
            parts.append(blob(part_index).decode("utf-8", "strict"))
        documents.append(separator.join(parts))
    return documents


def is_private_absolute(path: str) -> bool:
    normalized = path.replace("\\", "/")
    # Roslyn uses /_/ for the repository and /_1/, /_2/, ... for
    # additional deterministic SourceRoot entries such as NuGet sources.
    if re.match(r"^/_[0-9]*/", normalized):
        return False
    return bool(re.match(r"^(?:[A-Za-z]:/|//|/)", normalized))


class PrivacyScanner:
    def __init__(self, owned_prefixes: list[str], forbidden_prefixes: list[str], allowed_codeview_fingerprints: list[str] | None = None):
        self.allowed_codeview_fingerprints = set(allowed_codeview_fingerprints or [])
        self.owned_prefixes = [prefix.casefold() for prefix in owned_prefixes]
        self.forbidden_prefixes = []
        for prefix in forbidden_prefixes:
            normalized = prefix.rstrip("/\\")
            if not normalized:
                raise ValueError("empty forbidden prefix")
            self.forbidden_prefixes.extend({normalized.casefold(), normalized.replace("\\", "/").casefold(), normalized.replace("/", "\\").casefold(), normalized.replace("/", "\\").replace("\\", "\\\\").casefold()})
        self.findings: list[dict] = []
        self.errors: list[dict] = []
        self.files_scanned = 0
        self.bytes_scanned = 0
        self.documents_scanned = 0
        self.expanded_bytes = 0
        self._seen: set[tuple[str, str, str]] = set()

    def add(self, location: str, rule: str, value: str | bytes, **extra):
        payload = value.encode("utf-8") if isinstance(value, str) else value
        digest = hashlib.sha256(payload).hexdigest()[:16]
        key = location, rule, digest
        if key not in self._seen:
            self._seen.add(key)
            self.findings.append({"location": location, "rule": rule, "fingerprint": digest, **extra})

    def check_pdb(self, data: bytes, location: str, owned: bool = True):
        try:
            documents = portable_pdb_documents(data)
        except (ValueError, KeyError, IndexError, struct.error, UnicodeError):
            self.errors.append({"location": location, "reason": "portable-pdb-parse-failed"})
            return
        self.documents_scanned += len(documents)
        for index, document in enumerate(documents):
            if owned and is_private_absolute(document):
                self.add(location, "absolute-pdb-document", document, document=index)
            for prefix in self.forbidden_prefixes:
                if prefix in document.casefold():
                    self.add(location, "forbidden-pdb-document-prefix", prefix, document=index)

    def scan_bytes(self, data: bytes, location: str, depth: int = 0, owned: bool | None = None):
        self.files_scanned += 1
        self.bytes_scanned += len(data)
        filename = location.rsplit("!", 1)[-1].replace("\\", "/").rsplit("/", 1)[-1]
        suffix = pathlib.PurePosixPath(filename).suffix.lower()
        if owned is None:
            owned = not self.owned_prefixes or any(filename.casefold().startswith(prefix) for prefix in self.owned_prefixes)
        if self.forbidden_prefixes:
            for encoding in ("utf-8", "utf-16-le", "utf-16-be"):
                text = data.decode(encoding, "replace").casefold()
                for prefix in self.forbidden_prefixes:
                    if prefix in text:
                        self.add(location, "forbidden-machine-prefix", prefix, encoding=encoding)
        if suffix == ".pdb":
            if data.startswith(b"BSJB"):
                self.check_pdb(data, location, owned)
            elif owned:
                self.errors.append({"location": location, "reason": "unsupported-nonportable-pdb"})
        if owned and suffix in {".dll", ".exe"}:
            try:
                records = list(pe_debug_records(data))
            except (ValueError, IndexError, struct.error):
                self.errors.append({"location": location, "reason": "pe-debug-directory-parse-failed"})
                records = []
            for kind, offset, record in records:
                if kind == 2 and record.startswith(b"RSDS"):
                    try:
                        path = record[24:].split(b"\0", 1)[0].decode("utf-8", "strict")
                    except UnicodeError:
                        self.errors.append({"location": location, "reason": "invalid-codeview-path"})
                        continue
                    if (path.lower().endswith(".pdb") and is_private_absolute(path)
                            and hashlib.sha256(path.encode("utf-8")).hexdigest() not in self.allowed_codeview_fingerprints):
                        self.add(location, "absolute-codeview-pdb", path, offset=offset + 24)
        if suffix in {".dll", ".exe"} and data.startswith(b"MZ"):
            if depth >= MAX_DEPTH:
                self.errors.append({"location": location, "reason": "binary-depth-limit"})
                return
            try:
                for child, payload in iter_dotnet_payloads(data, location, max_member=MAX_FILE_BYTES, max_total=MAX_EXPANDED_BYTES - self.expanded_bytes):
                    self.expanded_bytes += len(payload)
                    self.scan_bytes(payload, child, depth + 1)
            except (ValueError, IndexError, struct.error, UnicodeError):
                self.errors.append({"location": location, "reason": "dotnet-payload-parse-failed"})
        if suffix in ARCHIVE_SUFFIXES:
            if depth >= MAX_DEPTH:
                self.errors.append({"location": location, "reason": "archive-depth-limit"})
                return
            try:
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    for member in archive.infolist():
                        if member.is_dir():
                            continue
                        self.expanded_bytes += member.file_size
                        child = location + "!" + member.filename
                        if member.file_size > MAX_FILE_BYTES or self.expanded_bytes > MAX_EXPANDED_BYTES:
                            self.errors.append({"location": child, "reason": "archive-size-limit"})
                            continue
                        self.scan_bytes(archive.read(member), child, depth + 1)
            except (zipfile.BadZipFile, RuntimeError, OSError, ValueError):
                self.errors.append({"location": location, "reason": "archive-read-failed"})

    def scan_path(self, path: pathlib.Path):
        if path.is_symlink():
            self.errors.append({"location": path.name, "reason": "symbolic-link-refused"})
            return
        files = sorted(path.rglob("*")) if path.is_dir() else [path]
        for file in files:
            if file.is_symlink():
                self.errors.append({"location": file.name, "reason": "symbolic-link-refused"})
            elif file.is_file():
                location = path.name + "/" + file.relative_to(path).as_posix() if path.is_dir() else file.name
                if file.stat().st_size > MAX_FILE_BYTES:
                    self.errors.append({"location": location, "reason": "file-size-limit"})
                    continue
                self.scan_bytes(file.read_bytes(), location)
            elif not file.exists():
                self.errors.append({"location": file.name, "reason": "missing-input"})

    def report(self) -> dict:
        return {"scanner_version": SCANNER_VERSION, "passed": not self.findings and not self.errors, "files_scanned": self.files_scanned, "bytes_scanned": self.bytes_scanned, "pdb_documents_scanned": self.documents_scanned, "findings": self.findings, "errors": self.errors, "scope": "Static build-path inspection; installers must also be unpacked and their payload scanned."}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", type=pathlib.Path, nargs="+")
    parser.add_argument("--owned-prefix", action="append", default=[])
    parser.add_argument("--forbid-prefix", action="append", default=[])
    parser.add_argument("--allow-codeview-fingerprint", action="append", default=[], help="Exact SHA-256 of an independently verified upstream native apphost PDB path; forbidden machine prefixes still apply.")
    parser.add_argument("--report", type=pathlib.Path)
    args = parser.parse_args()
    scanner = PrivacyScanner(args.owned_prefix, args.forbid_prefix, args.allow_codeview_fingerprint)
    for path in args.paths:
        scanner.scan_path(path)
    report = scanner.report()
    rendered = json.dumps(report, ensure_ascii=True, indent=2) + "\n"
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
