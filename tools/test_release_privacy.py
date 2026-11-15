"""Synthetic regression tests for the vendorable release privacy scanner."""

import io
import hashlib
import struct
import unittest
import zipfile
import zlib

from verify_release_privacy import PrivacyScanner, portable_pdb_documents
from dotnet_static_payloads import BUNDLE_SIGNATURE, iter_dotnet_payloads


def portable_pdb(document):
    parts = document.replace("\\", "/").split("/")
    blobs = bytearray(b"\0")
    indices = []
    for part in parts:
        encoded = part.encode("utf-8")
        indices.append(len(blobs))
        blobs.extend(bytes([len(encoded)]) + encoded)
    name_index = len(blobs)
    name = b"/" + bytes(indices)
    blobs.extend(bytes([len(name)]) + name)
    tables = struct.pack("<IBBBBQQI", 0, 2, 0, 0, 1, 1 << 48, 0, 1)
    tables += struct.pack("<HHHH", name_index, 0, 0, 0)
    version = b"PDB v1.0\0\0\0\0"
    header = b"BSJB" + struct.pack("<HHII", 1, 1, 0, len(version)) + version + struct.pack("<HH", 0, 2)
    stream_headers_size = 12 + 16
    start = len(header) + stream_headers_size
    header += struct.pack("<II", start, len(tables)) + b"#~\0\0"
    header += struct.pack("<II", start + len(tables), len(blobs)) + b"#Blob\0\0\0"
    return header + tables + blobs


def pe_with_debug(record, kind=2):
    data = bytearray(0x21C)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x86, 1)
    struct.pack_into("<H", data, 0x94, 240)
    struct.pack_into("<H", data, 0x98, 0x20B)
    struct.pack_into("<II", data, 0x98 + 112 + 6 * 8, 0x1000, 28)
    struct.pack_into("<IIII", data, 0x98 + 240 + 8, 28 + len(record), 0x1000, 28 + len(record), 0x200)
    struct.pack_into("<IIII", data, 0x200 + 12, kind, len(record), 0x101C, 0x21C)
    return bytes(data) + record


def rsds(path):
    return pe_with_debug(b"RSDS" + b"\0" * 20 + path.encode("utf-8") + b"\0")


def compressed_bundle(payload, member="Product.dll"):
    host = rsds("/_/native/apphost.pdb")
    compressor = zlib.compressobj(wbits=-15)
    compressed = compressor.compress(payload) + compressor.flush()
    offset = len(host) + 8 + len(BUNDLE_SIGNATURE)
    header_offset = offset + len(compressed)
    encoded_name = member.encode("utf-8")
    header = struct.pack("<IIi", 6, 0, 1) + b"\x04test" + b"\0" * 40
    header += struct.pack("<qqqB", offset, len(payload), len(compressed), 1)
    header += bytes([len(encoded_name)]) + encoded_name
    return host + struct.pack("<q", header_offset) + BUNDLE_SIGNATURE + compressed + header


class ReleasePrivacyTests(unittest.TestCase):
    def test_codeview_real_path_rejected_without_echoing_private_value(self):
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(rsds("C:/Users/private-builder/project/Product.pdb"), "Product.dll")
        self.assertEqual(scanner.findings[0]["rule"], "absolute-codeview-pdb")
        self.assertNotIn("private-builder", str(scanner.report()))

    def test_virtual_codeview_path_allowed(self):
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(rsds("/_/src/Product.pdb"), "Product.dll")
        self.assertTrue(scanner.report()["passed"])

    def test_utf16_machine_path_rejected(self):
        scanner = PrivacyScanner([], ["D:/private-work"])
        scanner.scan_bytes("D:\\private-work\\Program.cs".encode("utf-16-le"), "Product.dll")
        self.assertEqual(scanner.findings[0]["rule"], "forbidden-machine-prefix")

    def test_portable_document_components_are_decoded(self):
        path = "C:/Users/private-builder/Program.cs"
        pdb = portable_pdb(path)
        self.assertEqual(portable_pdb_documents(pdb), [path])
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(pdb, "Product.pdb")
        self.assertEqual(scanner.findings[0]["rule"], "absolute-pdb-document")

    def test_embedded_portable_pdb_is_checked(self):
        pdb = portable_pdb("C:/Users/private-builder/Program.cs")
        compressor = zlib.compressobj(wbits=-15)
        dll = pe_with_debug(b"MPDB" + struct.pack("<I", len(pdb)) + compressor.compress(pdb) + compressor.flush(), 17)
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(dll, "Product.dll")
        self.assertEqual(scanner.findings[0]["rule"], "absolute-pdb-document")

    def test_nested_package_is_checked(self):
        inner = io.BytesIO()
        with zipfile.ZipFile(inner, "w") as archive:
            archive.writestr("lib/net10.0/Product.dll", rsds("C:/private-build/Product.pdb"))
        outer = io.BytesIO()
        with zipfile.ZipFile(outer, "w") as archive:
            archive.writestr("Product.nupkg", inner.getvalue())
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(outer.getvalue(), "delivery.zip")
        self.assertEqual(scanner.findings[0]["location"], "delivery.zip!Product.nupkg!lib/net10.0/Product.dll")

    def test_clean_portable_pdb_passes(self):
        scanner = PrivacyScanner(["Product"], ["C:/private-build"])
        scanner.scan_bytes(portable_pdb("/_/src/Program.cs"), "Product.pdb")
        self.assertTrue(scanner.report()["passed"])
        self.assertEqual(scanner.documents_scanned, 1)

    def test_numbered_deterministic_source_root_passes(self):
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(portable_pdb("/_1/test-sdk/Program.cs"), "Product.pdb")
        self.assertTrue(scanner.report()["passed"])

    def test_broken_portable_pdb_fails_closed(self):
        scanner = PrivacyScanner(["Product"], [])
        scanner.scan_bytes(b"BSJBbroken", "Product.pdb")
        self.assertFalse(scanner.report()["passed"])
        self.assertTrue(scanner.errors)

    def test_magic_bytes_outside_debug_directory_are_not_records(self):
        scanner = PrivacyScanner(["Product"], [])
        dll = rsds("/_/Product.pdb") + b"RSDS" + b"\0" * 20 + b"C:/third-party/Other.pdb\0MPDBxxxx"
        scanner.scan_bytes(dll, "Product.dll")
        self.assertTrue(scanner.report()["passed"])

    def test_compressed_bundle_member_cannot_hide_private_path(self):
        scanner = PrivacyScanner(["Product"], ["C:/private-build"])
        scanner.scan_bytes(compressed_bundle(rsds("C:/private-build/Product.pdb")), "Product.exe")
        self.assertTrue(any(f["rule"] == "absolute-codeview-pdb" and "!Product.dll" in f["location"] for f in scanner.findings))

    def test_compressed_bundle_member_limit_is_enforced(self):
        data = compressed_bundle(rsds("/_/Product.pdb"))
        with self.assertRaises(ValueError):
            list(iter_dotnet_payloads(data, "Product.exe", max_member=10))

    def test_upstream_allowance_never_overrides_forbidden_machine_root(self):
        path = "C:/private-build/Product.pdb"
        fingerprint = hashlib.sha256(path.encode()).hexdigest()
        scanner = PrivacyScanner(["Product"], ["C:/private-build"], [fingerprint])
        scanner.scan_bytes(rsds(path), "Product.dll")
        self.assertTrue(any(f["rule"] == "forbidden-machine-prefix" for f in scanner.findings))
        self.assertFalse(any(f["rule"] == "absolute-codeview-pdb" for f in scanner.findings))

    def test_unrelated_upstream_bundle_member_does_not_inherit_ownership(self):
        scanner = PrivacyScanner(["Product"], ["C:/private-build"])
        scanner.scan_bytes(compressed_bundle(rsds("D:/upstream-build/ThirdParty.pdb"), "ThirdParty.dll"), "Product.exe")
        self.assertTrue(scanner.report()["passed"])


if __name__ == "__main__":
    unittest.main()
