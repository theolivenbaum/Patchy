"""Fetch the psd-tools PSD test collection as a local Testy corpus.

psd-tools (https://github.com/psd-tools/psd-tools, MIT) keeps several hundred small
single-feature PSD/PSB files under tests/psd_files. This script checks that directory
out at a pinned commit into local-test-fixtures/psd-tools (gitignored; the files are
never committed here) and writes testy/corpus/psd-tools.txt, a corpus list for

    python testy\\testy.py --corpus corpus\\psd-tools.txt

(a relative --corpus resolves against the testy directory).

Running it again is cheap: an existing checkout at the pinned commit is left alone and
only the list is rewritten. Bump PINNED_COMMIT deliberately; results from different
commits are not comparable file for file.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

import config

REPOSITORY = "https://github.com/psd-tools/psd-tools.git"
PINNED_COMMIT = "605ee1284952c18c649ba60ebe73df4dabca9fbb"  # main, October 5, 2026
SUBDIRECTORY = "tests/psd_files"

CHECKOUT = config.REPO_ROOT / "local-test-fixtures" / "psd-tools"
CORPUS_LIST = config.TESTY_ROOT / "corpus" / "psd-tools.txt"


def git(*args: str, cwd: Path | None = None) -> str:
    completed = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if completed.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed:\n{completed.stderr.strip()}")
    return completed.stdout.strip()


def checkout_is_current() -> bool:
    if not (CHECKOUT / ".git").exists():
        return False
    completed = subprocess.run(["git", "rev-parse", "HEAD"], cwd=CHECKOUT,
                               capture_output=True, text=True)
    return completed.returncode == 0 and completed.stdout.strip() == PINNED_COMMIT


def fetch() -> None:
    if checkout_is_current():
        print(f"checkout already at {PINNED_COMMIT[:12]}: {CHECKOUT}")
        return
    if CHECKOUT.exists() and not (CHECKOUT / ".git").exists() and any(CHECKOUT.iterdir()):
        raise SystemExit(f"{CHECKOUT} exists and is not a checkout this script made; "
                         "move it aside first")
    CHECKOUT.mkdir(parents=True, exist_ok=True)
    if not (CHECKOUT / ".git").exists():
        git("init", "--quiet", cwd=CHECKOUT)
        git("remote", "add", "origin", REPOSITORY, cwd=CHECKOUT)
        git("sparse-checkout", "set", "--no-cone", f"/{SUBDIRECTORY}/", cwd=CHECKOUT)
    print(f"fetching {REPOSITORY} at {PINNED_COMMIT[:12]} ...")
    git("fetch", "--quiet", "--depth", "1", "--filter=blob:none", "origin", PINNED_COMMIT,
        cwd=CHECKOUT)
    git("checkout", "--quiet", "--detach", "FETCH_HEAD", cwd=CHECKOUT)


def write_corpus_list() -> int:
    root = CHECKOUT / SUBDIRECTORY
    files = sorted((p for p in root.rglob("*") if p.suffix.lower() in (".psd", ".psb")),
                   key=lambda p: p.relative_to(root).as_posix().lower())
    lines = [
        "# psd-tools test collection (https://github.com/psd-tools/psd-tools, MIT).",
        f"# Commit {PINNED_COMMIT}; written by testy/fetch_psd_tools_corpus.py.",
        "# Paths are relative to the repository root.",
    ]
    lines += [p.relative_to(config.REPO_ROOT).as_posix() for p in files]
    CORPUS_LIST.parent.mkdir(parents=True, exist_ok=True)
    CORPUS_LIST.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return len(files)


def corpus_credit(files) -> dict | None:
    """The attribution a run records when every file comes from this checkout, for the
    report and the public export: name, URL, license and the pinned commit. None for
    any other corpus."""
    paths = [Path(f) for f in files]
    if not paths:
        return None
    root = (CHECKOUT / SUBDIRECTORY).resolve()
    for path in paths:
        try:
            path.resolve().relative_to(root)
        except ValueError:
            return None
    return {
        "name": "psd-tools test collection",
        "url": "https://github.com/psd-tools/psd-tools/tree/main/tests/psd_files",
        "project": "psd-tools",
        "projectUrl": "https://github.com/psd-tools/psd-tools",
        "license": "MIT",
        "commit": PINNED_COMMIT,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--list-only", action="store_true",
                        help="rewrite the corpus list from the existing checkout, no network")
    args = parser.parse_args()
    if not args.list_only:
        fetch()
    count = write_corpus_list()
    print(f"{count} PSD/PSB files listed in {CORPUS_LIST}")
    return 0 if count else 1


if __name__ == "__main__":
    sys.exit(main())
