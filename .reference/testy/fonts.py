"""Find this machine's font file for a PostScript font name, as a standalone file.

Photopea runs in a browser and only has its own web fonts, so a PSD whose text uses a
font installed here (and used by Photoshop for the reference) was laid out in a
substitute there. The Photopea driver hands it the same font files Photoshop has;
this module finds them. A face inside a .ttc collection is written out as its own
.ttf, because a browser-side loader takes one face per file.

The copies live under testy/cache/photopea-fonts/ (gitignored, served by the Testy
server like everything else under testy/); nothing is uploaded anywhere: Photopea
reads the bytes inside the local browser.
"""

from __future__ import annotations

import os
import re
import shutil
from pathlib import Path

import config

_FONT_SUFFIXES = (".ttf", ".otf", ".ttc", ".otc")
_index: dict[str, tuple[Path, int]] | None = None


def _font_dirs() -> list[Path]:
    dirs = [Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"]
    local = os.environ.get("LOCALAPPDATA")
    if local:
        dirs.append(Path(local) / "Microsoft" / "Windows" / "Fonts")
    # Fonts Adobe installs for its own apps (Photoshop sees them, Windows does not).
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    dirs.append(program_files / "Common Files" / "Adobe" / "Fonts")
    dirs.extend(sorted(program_files.glob("Adobe/Adobe Photoshop */Resources/Fonts")))
    dirs.extend(sorted(program_files.glob("Adobe/Adobe Photoshop */Required/Fonts")))
    return [d for d in dirs if d.is_dir()]


def _postscript_name(font) -> str | None:
    record = font["name"].getDebugName(6) if "name" in font else None
    return record.strip() if record else None


def _build_index() -> dict[str, tuple[Path, int]]:
    """PostScript name -> (file, face number in the file)."""
    from fontTools.ttLib import TTCollection, TTFont

    index: dict[str, tuple[Path, int]] = {}
    for directory in _font_dirs():
        for path in sorted(directory.iterdir()):
            if path.suffix.lower() not in _FONT_SUFFIXES:
                continue
            try:
                if path.suffix.lower() in (".ttc", ".otc"):
                    collection = TTCollection(str(path), lazy=True)
                    for number, face in enumerate(collection.fonts):
                        name = _postscript_name(face)
                        if name:
                            index.setdefault(name, (path, number))
                    collection.close()
                else:
                    face = TTFont(str(path), lazy=True)
                    name = _postscript_name(face)
                    face.close()
                    if name:
                        index.setdefault(name, (path, 0))
            except Exception:
                continue  # an unreadable font file is simply not offered
    return index


def font_file(postscript_name: str) -> Path | None:
    """A single-face font file for `postscript_name` under testy/cache, or None when
    no installed font has that PostScript name."""
    global _index
    if not postscript_name:
        return None
    if _index is None:
        _index = _build_index()
    found = _index.get(postscript_name)
    if found is None:
        return None
    source, number = found
    served_dir = config.CACHE_DIR / "photopea-fonts"
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", postscript_name)
    collection = source.suffix.lower() in (".ttc", ".otc")
    target = served_dir / (safe + (".ttf" if collection else source.suffix.lower()))
    if target.exists() and target.stat().st_size > 0:
        return target
    served_dir.mkdir(parents=True, exist_ok=True)
    try:
        if collection:
            from fontTools.ttLib import TTFont

            face = TTFont(str(source), fontNumber=number)
            face.save(str(target))
            face.close()
        else:
            shutil.copyfile(source, target)
    except Exception:
        return None
    return target


def font_files(postscript_names: list[str]) -> list[Path]:
    """The distinct font files for these names, in order; names with no file are skipped."""
    files: list[Path] = []
    for name in dict.fromkeys(postscript_names):
        path = font_file(name)
        if path is not None and path not in files:
            files.append(path)
    return files
