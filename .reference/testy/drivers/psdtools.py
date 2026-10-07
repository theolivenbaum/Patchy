"""psd-tools driver: a Python PSD library measured beside the editors (opt-in).

psd-tools (https://github.com/psd-tools/psd-tools) is not an editor, but it has its
own layer compositor, which makes it a useful yardstick for the render column.
`composite(force=True)` makes it composite the layers itself; without it the
library hands back the file's baked preview whenever one exists, which is exactly
the shortcut Testy's trap leg exists to catch. A file with no layer records has
only that preview, so it is read as-is there.

The output extension picks the leg, as with the CLI editors: an image extension
renders, .psd/.psb loads the document and saves it again (`PSDImage.save`), which
Photoshop then reopens for the "data kept" comparison like any editor's resave.
Each call runs in a child interpreter so a hang or crash costs one cell, not the run.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

TIMEOUT_SECONDS = 180
# A line the child prints on stdout to explain how a leg was produced; export()
# returns it as "note" for the cell's driver notes.
NOTE_MARKER = "TESTY-NOTE: "


def version() -> str | None:
    """The installed psd-tools version, or None when the package is missing."""
    try:
        from importlib import metadata

        return metadata.version("psd-tools")
    except Exception:
        return None


def missing_composite_modules() -> list[str]:
    """Modules psd-tools' compositor imports that are not installed."""
    from importlib import util

    return [name for name in ("scipy", "skimage", "aggdraw") if util.find_spec(name) is None]


def export(input_path: Path, output_path: Path) -> dict:
    try:
        completed = subprocess.run(
            [sys.executable, str(Path(__file__).resolve()), str(input_path), str(output_path)],
            capture_output=True,
            text=True,
            timeout=TIMEOUT_SECONDS,
        )
        exit_code = completed.returncode
        lines = [line for line in (completed.stderr or "").splitlines() if line.strip()]
        stderr = lines[-1][-2000:] if lines else ""
        note = next((line[len(NOTE_MARKER):].strip() for line in (completed.stdout or "").splitlines()
                     if line.startswith(NOTE_MARKER)), "")
    except subprocess.TimeoutExpired:
        exit_code, stderr, note = -1, f"timeout after {TIMEOUT_SECONDS}s", ""
    except OSError as error:
        exit_code, stderr, note = -1, str(error), ""
    ok = exit_code == 0 and output_path.exists() and output_path.stat().st_size > 0
    return {
        "exitCode": exit_code,
        "stderr": stderr,
        "note": note,
        "ok": ok,
        # Exit 1 is a Python exception from psd-tools: its verdict on the file. A
        # timeout or a crashed interpreter counts against the driver instead.
        "fileRejected": not ok and exit_code == 1,
    }


def _run(input_path: str, output_path: str) -> None:
    from psd_tools import PSDImage

    psd = PSDImage.open(input_path)
    if Path(output_path).suffix.lower() in (".psd", ".psb"):
        psd.save(output_path)
        return
    if not len(psd):
        image = psd.composite()
    else:
        try:
            image = psd.composite(force=True)
        except Exception as error:
            # force=True also re-rasterizes vector shapes, and that path cannot build a
            # CMYK-plus-alpha image (psd-tools 1.17: "Cannot handle this data type:
            # (1, 1, 5)"). ignore_preview still composites the layers itself, which is
            # all the trap leg requires; stored shape pixels are used instead.
            print(f"{NOTE_MARKER}composited with ignore_preview after force=True failed "
                  f"({type(error).__name__}: {str(error)[:120]})")
            image = psd.composite(ignore_preview=True)
    if image.mode not in ("1", "L", "LA", "RGB", "RGBA"):
        # PNG cannot hold CMYK; psd-tools normally converts through the ICC profile
        # itself, and this covers the documents where it hands back the raw mode.
        image = image.convert("RGBA" if "A" in image.getbands() else "RGB")
    image.save(output_path)


if __name__ == "__main__":
    _run(sys.argv[1], sys.argv[2])
