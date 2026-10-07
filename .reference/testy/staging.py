"""Staging: every editor works on copies inside the run directory, never on corpus files."""

from __future__ import annotations

import hashlib
import shutil
from dataclasses import dataclass
from pathlib import Path

from struct import error as struct_error

from psd_sections import PsdParseError, read_layout, write_sentinel_composite, write_stripped_caches


def sha1_of_file(path: Path) -> str:
    digest = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


@dataclass
class StagedPsd:
    source: Path
    sha1: str
    original: Path  # byte-identical copy the editors open
    trap: Path | None  # sentinel-composite variant (None if skipped or the walker balked)
    trap_error: str | None = None
    trap_skipped: str | None = None  # why no trap was made (not an error)
    # Two more variants (psd_sections.write_stripped_caches): one with the cached
    # pixels of every text, shape, fill and smart-object layer REMOVED, so what an
    # editor shows for those layers is what its own engine draws, and one with the
    # same layers merely hidden, the picture of "that layer contributed nothing".
    # Both None when the file has no such layer or keeps its layers where the
    # stripper does not look (16/32-bit documents).
    cache_stripped: Path | None = None
    cache_plain: Path | None = None
    # The stripped and plain copies with type layers and smart objects left as they
    # were, for an editor that redraws those by script instead (None when nothing
    # else is cached).
    cache_stripped_text_kept: Path | None = None
    cache_plain_text_kept: Path | None = None
    # And with only the type layers left as they were, for a file whose text needs a
    # font Photoshop lacks: nobody's own text render is scored then, the rest still is.
    cache_stripped_font_kept: Path | None = None
    cache_plain_font_kept: Path | None = None


def stage_psd(source: Path, staging_dir: Path) -> StagedPsd:
    staging_dir.mkdir(parents=True, exist_ok=True)
    suffix = source.suffix.lower() or ".psd"
    original = staging_dir / f"original{suffix}"
    shutil.copyfile(source, original)

    trap: Path | None = staging_dir / f"trap{suffix}"
    trap_error: str | None = None
    trap_skipped: str | None = None
    try:
        layout = read_layout(source.read_bytes())
        if layout.layer_count == 0:
            # A flattened file stores no layer records: the merged composite IS the
            # document, so every honest renderer must read it (Photoshop itself
            # trips the sentinel on such files) and the trap proves nothing.
            trap = None
            trap_skipped = "flattened file (no layer records): the composite is its only image data"
        else:
            write_sentinel_composite(str(source), str(trap))
    except (PsdParseError, OSError) as error:
        trap = None
        trap_error = str(error)

    cache_stripped: Path | None = staging_dir / f"nocache{suffix}"
    cache_plain: Path | None = staging_dir / f"nocache_plain{suffix}"
    try:
        write_stripped_caches(str(original), str(cache_stripped))
        write_stripped_caches(str(original), str(cache_plain), plain=True)
    except (PsdParseError, OSError, ValueError, IndexError, struct_error):
        # Nothing cached to strip, or a layout the stripper does not handle: the
        # cells are scored on the file as opened.
        cache_stripped = cache_plain = None
    stripped_text_kept: Path | None = staging_dir / f"nocache_textkept{suffix}"
    plain_text_kept: Path | None = staging_dir / f"nocache_plain_textkept{suffix}"
    try:
        if cache_stripped is None:
            raise PsdParseError("nothing to strip")
        write_stripped_caches(str(original), str(stripped_text_kept), keep_kinds=("text", "smart"))
        write_stripped_caches(str(original), str(plain_text_kept), plain=True, keep_kinds=("text", "smart"))
    except (PsdParseError, OSError, ValueError, IndexError, struct_error):
        stripped_text_kept = plain_text_kept = None
    stripped_font_kept: Path | None = staging_dir / f"nocache_fontkept{suffix}"
    plain_font_kept: Path | None = staging_dir / f"nocache_plain_fontkept{suffix}"
    try:
        if cache_stripped is None:
            raise PsdParseError("nothing to strip")
        write_stripped_caches(str(original), str(stripped_font_kept), keep_kinds=("text",))
        write_stripped_caches(str(original), str(plain_font_kept), plain=True, keep_kinds=("text",))
    except (PsdParseError, OSError, ValueError, IndexError, struct_error):
        stripped_font_kept = plain_font_kept = None

    return StagedPsd(
        source=source,
        sha1=sha1_of_file(source),
        original=original,
        trap=trap,
        trap_error=trap_error,
        trap_skipped=trap_skipped,
        cache_stripped=cache_stripped,
        cache_plain=cache_plain,
        cache_stripped_text_kept=stripped_text_kept,
        cache_plain_text_kept=plain_text_kept,
        cache_stripped_font_kept=stripped_font_kept,
        cache_plain_font_kept=plain_font_kept,
    )
