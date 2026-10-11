"""Packs src/ into dist/bison-time-checker-<version>.zip (load-unpacked / Web Store upload)."""
import json, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
version = json.loads((SRC / "manifest.json").read_text(encoding="utf-8"))["version"]
out = ROOT / "dist" / f"bison-time-checker-{version}.zip"
out.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in sorted(SRC.rglob("*")):
        if f.is_file() and not f.name.startswith("."):
            z.write(f, f.relative_to(SRC).as_posix())
print(out)
