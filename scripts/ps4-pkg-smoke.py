"""Build and merge synthetic PS4 packages, then verify the extracted overlay on Linux."""
import hashlib
from pathlib import Path
import struct
import subprocess
import sys
import tempfile


def sfo(values):
    keys, data, entries = bytearray(), bytearray(), []
    for key, value in values.items():
        while len(data) % 4:
            data.append(0)
        raw = (value + "\0").encode() if isinstance(value, str) else struct.pack("<I", value)
        entries.append(struct.pack("<HHIII", len(keys), 0x204 if isinstance(value, str) else 0x404,
                                   len(raw), len(raw), len(data)))
        keys.extend(key.encode() + b"\0")
        data.extend(raw)
    key_start = 20 + len(entries) * 16
    pad = (-(key_start + len(keys))) % 4
    return (struct.pack("<4sIIII", b"\0PSF", 0x101, key_start, key_start + len(keys) + pad, len(entries))
            + b"".join(entries) + keys + bytes(pad) + data)


def read_sfo(path):
    raw = path.read_bytes()
    _, _, keys, data, count = struct.unpack_from("<4sIIII", raw)
    result = {}
    for i in range(count):
        key, fmt, length, _, offset = struct.unpack_from("<HHIII", raw, 20 + i * 16)
        name = raw[keys + key:].split(b"\0", 1)[0].decode()
        value = raw[data + offset:data + offset + length]
        result[name] = value.rstrip(b"\0").decode() if fmt == 0x204 else struct.unpack("<I", value)[0]
    return result


def main(tool):
    def run(*args):
        p = subprocess.run([tool, *map(str, args)], text=True, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, timeout=180)
        if p.returncode:
            raise RuntimeError(f"{args[0]} failed ({p.returncode}):\n{p.stdout[-12000:]}")

    with tempfile.TemporaryDirectory(prefix="ps4-merge-smoke-") as tmp:
        root = Path(tmp)
        packages = []
        for kind, version, category in [("base", "01.00", "gd"), ("patch", "01.01", "gp")]:
            folder = root / kind
            (folder / "sce_sys").mkdir(parents=True)
            (folder / "sce_sys" / "param.sfo").write_bytes(sfo({
                "TITLE": "Synthetic Merge Test", "TITLE_ID": "CUSA00001",
                "CONTENT_ID": "UP0000-CUSA00001_00-RETROARRTEST0000",
                "CATEGORY": category, "APP_VER": version, "VERSION": version,
                "SYSTEM_VER": 0x05050000, "APP_TYPE": 1, "ATTRIBUTE": 0,
            }))
            (folder / "eboot.bin").write_bytes((kind + " executable\n").encode() * 100)
            (folder / "shared.dat").write_bytes((kind + " shared\n").encode() * 10000)
            (folder / (kind + "-only.dat")).write_text(kind)
            gp4, pkg = root / (kind + ".gp4"), root / (kind + ".pkg")
            run("gp4gen", folder, "--out", gp4, "--passcode", "0" * 32, *(["--patch"] if kind == "patch" else []))
            run("build", gp4, folder, "--out", pkg, "--validate")
            packages.append(pkg)
        before = [hashlib.sha256(p.read_bytes()).digest() for p in packages]
        merged = root / "merged.pkg"
        run("merge", *packages, "--out", merged, "--work-dir", root / "work", "--validate")
        run("validate", merged)
        extracted = root / "extracted"
        run("extract", merged, extracted)
        assert (extracted / "Image0" / "shared.dat").read_bytes() == (root / "patch" / "shared.dat").read_bytes()
        assert (extracted / "Image0" / "eboot.bin").read_bytes() == (root / "patch" / "eboot.bin").read_bytes()
        assert (extracted / "Image0" / "base-only.dat").read_text() == "base"
        assert (extracted / "Image0" / "patch-only.dat").read_text() == "patch"
        info = read_sfo(extracted / "Sc0" / "param.sfo")
        assert info["CATEGORY"] == "gd" and info["APP_VER"] == "01.01", info
        assert before == [hashlib.sha256(p.read_bytes()).digest() for p in packages]
        print("PS4 merge smoke passed: base + patch -> validated game PKG; overlay and version verified; inputs unchanged.")


if __name__ == "__main__":
    main(str(Path(sys.argv[1]).resolve()))
