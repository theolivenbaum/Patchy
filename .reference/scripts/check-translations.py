#!/usr/bin/env python3
"""Read-only translation build gate. Requires only the Python standard library."""

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def read_manifest(path):
    values = {}
    sources = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line:
            continue
        key, sep, value = line.partition("=")
        if sep and key in {"lupdate", "options", "include", "template", "languages"}:
            values[key] = value
        else:
            sources.append(line)
    for key in ("lupdate", "include", "template", "languages"):
        if not values.get(key):
            raise ValueError(f"{path}: missing {key}")
    if not sources:
        raise ValueError(f"{path}: no extraction sources")
    values["sources"] = sources
    values["languages"] = values["languages"].split(";")
    if "en" in values["languages"] or len(set(values["languages"])) != len(values["languages"]):
        raise ValueError(f"{path}: invalid translated language list")
    return values


def read_catalog(path, language):
    root = ET.parse(path).getroot()
    if root.tag != "TS" or (language is not None and root.get("language") != language) or root.get("sourcelanguage") != "en":
        raise ValueError(f"{path}: expected TS language={language}, sourcelanguage=en")
    messages = {}
    for context in root.findall("context"):
        name = context.findtext("name")
        if not name or len(context.findall("name")) != 1:
            raise ValueError(f"{path}: missing context name")
        for message in context.findall("message"):
            source = message.findtext("source")
            if not source or len(message.findall("source")) != 1:
                raise ValueError(f"{path}: [{name}] missing source")
            key = (name, source, message.findtext("comment", ""))
            if key in messages:
                raise ValueError(f"{path}: duplicate message {key}")
            if message.get("numerus", "no") not in ("yes", "no"):
                raise ValueError(f"{path}: invalid numerus attribute {key}")
            messages[key] = message
    if not messages:
        raise ValueError(f"{path}: empty catalog")
    return messages


def describe(key):
    context, source, comment = key
    return f"[{context}] {source!r}" + (f" ({comment})" if comment else "")


def compare_keys(expected, actual, label):
    problems = []
    for key in expected.keys() - actual.keys():
        problems.append(f"{label}: {describe(key)}: missing (run update-translations.ps1)")
    for key in actual.keys() - expected.keys():
        problems.append(f"{label}: {describe(key)}: stale (run update-translations.ps1)")
    for key in expected.keys() & actual.keys():
        if expected[key].get("numerus", "no") != actual[key].get("numerus", "no"):
            problems.append(f"{label}: {describe(key)}: plural flag differs")
    return problems


def placeholder_problems(source, forms):
    problems = []
    # Consume the entire Qt argument, including %L1 and two-digit arguments.
    pattern = r"%L?[1-9][0-9]?"
    arguments = set(re.findall(pattern, source))
    accelerator = bool(re.search(r"&[^&\s]", source.replace("&&", "")))
    for text in forms:
        if not arguments.issubset(set(re.findall(pattern, text))):
            problems.append("missing argument placeholder")
        for token in ("%CTRL%", "%ALT%"):
            if text.count(token) != source.count(token):
                problems.append(f"{token} token count differs")
        if accelerator and not re.search(r"&[^&\s]", text.replace("&&", "")):
            problems.append("accelerator (&) dropped")
        if source.endswith("...") and not text.endswith(("...", "\u2026")):
            problems.append("trailing ellipsis dropped")
        if source.endswith(":") and not text.endswith((":", "\uff1a")):
            problems.append("trailing colon dropped")
    if "%n" in source and not any("%n" in text for text in forms):
        problems.append("%n missing from every plural form")
    return sorted(set(problems))


def check_catalogs(manifest, template):
    problems = []
    directory = Path(manifest["template"]).parent
    for language in manifest["languages"]:
        path = directory / f"patchy_{language}.ts"
        try:
            messages = read_catalog(path, language)
        except (OSError, ValueError, ET.ParseError) as error:
            problems.append(f"{language}: {error}")
            continue
        problems.extend(compare_keys(template, messages, language))
        for key, message in messages.items():
            prefix = f"{language}: {describe(key)}: "
            translation = message.find("translation")
            if translation is None:
                problems.append(prefix + "no translation")
                continue
            if len(message.findall("translation")) != 1:
                problems.append(prefix + "duplicate translation elements")
            if translation.get("type"):
                problems.append(prefix + f"translation type is {translation.get('type')!r}")
            plural = message.get("numerus") == "yes"
            if plural:
                forms = [form.text or "" for form in translation.findall("numerusform")]
                expected = (1 if language in ("ja", "ko") or language.startswith("zh")
                            else 3 if language in ("pl", "ru") else 2)
                if len(forms) != expected:
                    problems.append(prefix + f"{len(forms)} plural forms, expected {expected}")
                if ((translation.text or "").strip() or
                        any(child.tag != "numerusform" or len(child) or (child.tail or "").strip()
                            for child in translation)):
                    problems.append(prefix + "malformed plural translation")
            else:
                forms = [translation.text or ""]
                if len(translation):
                    problems.append(prefix + "unexpected translation child elements")
            if not forms or any(not text.strip() for text in forms):
                problems.append(prefix + "empty translation")
            problems.extend(prefix + problem for problem in placeholder_problems(key[1], forms))
    return problems


def check_template(manifest, template, scratch):
    # lupdate owns catalog structure, but this invocation only writes to scratch.
    with tempfile.TemporaryDirectory(prefix="lupdate-check-", dir=scratch) as temporary:
        directory = Path(temporary)
        output = directory / "patchy_en.ts"
        # Qt 6.8's @response-file reader on Windows mangles non-ASCII paths.
        # Its JSON project reader preserves Unicode and avoids command-line limits.
        project = directory / "project.json"
        project.write_text(json.dumps({
            "projectFile": str(Path(manifest["include"]).parent / "CMakeLists.txt"),
            "includePaths": [manifest["include"]],
            "sources": manifest["sources"],
            "translations": [str(output)],
        }), encoding="utf-8")
        command = [manifest["lupdate"], *filter(None, manifest.get("options", "").split(";")),
                   "-project", str(project)]
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                encoding="utf-8", errors="replace", timeout=300, check=False)
        if result.returncode:
            raise ValueError(f"lupdate failed ({result.returncode}):\n{result.stdout}")
        # A newly generated template need not have a target-language attribute.
        fresh = read_catalog(output, None)
        return compare_keys(fresh, template, "English template")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--check", choices=("all", "template", "catalogs"), default="all")
    args = parser.parse_args(argv)
    try:
        manifest = read_manifest(args.manifest)
        template = read_catalog(Path(manifest["template"]), "en")
        problems = []
        if args.check != "catalogs":
            problems.extend(check_template(manifest, template, args.manifest.parent))
        if args.check != "template":
            problems.extend(check_catalogs(manifest, template))
        if problems:
            for problem in sorted(problems):
                print(problem, file=sys.stderr)
            print(f"Translation validation failed: {len(problems)} problem(s)", file=sys.stderr)
            return 1
        print(f"Translations verified ({args.check}): {len(template)} messages, "
              f"{len(manifest['languages'])} languages")
        return 0
    except (OSError, ValueError, ET.ParseError, subprocess.TimeoutExpired) as error:
        print(f"Translation validation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    sys.exit(main())
