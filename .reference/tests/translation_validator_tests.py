"""Fault injection for the shared catalog validator and its CMake failure contract."""

import argparse
import importlib.util
import codecs
from pathlib import Path
import subprocess
import re
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/check-translations.py"
spec = importlib.util.spec_from_file_location("translation_validator", SCRIPT)
validator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator)
LANGUAGES = ("de", "es", "fr", "it", "ja", "ko", "pl", "pt_BR", "ru", "zh_CN", "zh_TW")


class TranslationValidatorTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / "build/translation-validator-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.template = self.directory / "patchy_en.ts"
        self.source = self.directory / "source.cpp"
        self.source.write_text('QT_TRANSLATE_NOOP("Fixture", "&Hello %1...")\n', encoding="utf-8")
        self.manifest = dict(lupdate=ARGS.lupdate, options="-locations;none;-no-obsolete;-source-language;en",
                             include=str(self.directory), template=str(self.template),
                             languages=list(LANGUAGES), sources=[str(self.source)])
        self.manifest_path = self.directory / "manifest.txt"
        self.manifest_path.write_text("\n".join(
            f"{key}={';'.join(value) if isinstance(value, list) else value}"
            for key, value in self.manifest.items() if key != "sources") + "\n" + str(self.source), encoding="utf-8")
        for language in ("en", *LANGUAGES):
            self.write_catalog(language)

    def write_catalog(self, language):
        root = ET.Element("TS", language=language, sourcelanguage="en")
        context = ET.SubElement(root, "context")
        ET.SubElement(context, "name").text = "Fixture"
        message = ET.SubElement(context, "message")
        ET.SubElement(message, "source").text = "&Hello %1..."
        ET.SubElement(message, "translation").text = "&Translated %1..." if language != "en" else ""
        path = self.directory / f"patchy_{language}.ts"
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
        return path

    def problems(self):
        template = validator.read_catalog(self.template, "en")
        return validator.check_catalogs(self.manifest, template)

    def test_every_language_rejects_missing_empty_unfinished_and_broken_placeholder(self):
        self.assertEqual([], self.problems())
        for language in LANGUAGES:
            for kind in ("missing", "empty", "unfinished", "placeholder", "duplicate", "duplicate_translation", "malformed", "stale", "numerus"):
                with self.subTest(language=language, kind=kind):
                    path = self.write_catalog(language)
                    tree = ET.parse(path)
                    context = tree.getroot().find("context")
                    message = context.find("message")
                    translation = message.find("translation")
                    if kind == "missing": context.remove(message)
                    if kind == "empty": translation.text = "  "
                    if kind == "unfinished": translation.set("type", "unfinished")
                    if kind == "placeholder": translation.text = "&Translated..."
                    if kind == "duplicate": context.append(ET.fromstring(ET.tostring(message)))
                    if kind == "duplicate_translation": message.append(ET.fromstring(ET.tostring(translation)))
                    if kind == "stale": message.find("source").text = "Old label"
                    if kind == "numerus": message.set("numerus", "yes")
                    tree.write(path, encoding="utf-8")
                    if kind == "malformed": path.write_text("<TS>", encoding="utf-8")
                    self.assertTrue(any(language in problem for problem in self.problems()))
                    self.write_catalog(language)

    def test_tokens_punctuation_and_plurals(self):
        for source, forms in [
            ("Open %10", ["Open %1"]), ("Width %L1", ["Width %1"]),
            ("%CTRL%+%ALT%", ["Ctrl+Alt"]), ("&Open", ["Open"]),
            ("Open...", ["Open"]), ("Width:", ["Width"]), ("%n files", ["Files", "Files"]),
        ]:
            self.assertTrue(validator.placeholder_problems(source, forms))
        self.assertEqual([], validator.placeholder_problems("%n files", ["One file", "%n files"]))
        self.assertEqual([], validator.placeholder_problems("RGB", ["RGB"]))
        self.assertEqual([], validator.placeholder_problems("A && B", ["A && B"]))
        path = self.write_catalog("de")
        tree = ET.parse(path)
        message = tree.getroot().find("context/message")
        message.set("numerus", "yes")
        translation = message.find("translation")
        translation.text = None
        ET.SubElement(translation, "numerusform").text = "&Translated %1..."
        tree.write(path, encoding="utf-8")
        self.assertTrue(any("plural forms" in problem for problem in self.problems()))

    def test_plural_form_counts_for_every_language(self):
        # Independent expectations include Korean's single form and both Slavic
        # three-form catalogs; a two-form assumption must never silently pass.
        counts = {"de": 2, "es": 2, "fr": 2, "it": 2, "ja": 1, "ko": 1,
                  "pl": 3, "pt_BR": 2, "ru": 3, "zh_CN": 1, "zh_TW": 1}
        template = validator.read_catalog(self.template, "en")
        next(iter(template.values())).set("numerus", "yes")
        for language, expected in counts.items():
            for actual in (1, 2, 3, 4):
                with self.subTest(language=language, forms=actual):
                    path = self.write_catalog(language)
                    tree = ET.parse(path)
                    message = tree.getroot().find("context/message")
                    message.set("numerus", "yes")
                    translation = message.find("translation")
                    translation.text = None
                    for index in range(actual):
                        ET.SubElement(translation, "numerusform").text = f"&Translated %1 form {index}..."
                    tree.write(path, encoding="utf-8")
                    manifest = dict(self.manifest, languages=[language])
                    problems = validator.check_catalogs(manifest, template)
                    self.assertEqual(actual != expected, bool(problems), problems)

    def test_template_freshness_and_unicode_paths(self):
        # Real lupdate, with all fixture inputs inside a Unicode directory.
        # Use the same authoritative names as the C++ Unicode-path suites.
        names = (ROOT / "tests/unicode_path_names.hpp").read_text(encoding="utf-8")
        directory_literal = re.search(r'kUnicodeDirName = u8"([^"]+)"', names).group(1)
        unicode_dir = self.directory / codecs.decode(directory_literal, "unicode_escape")
        unicode_dir.mkdir()
        source = unicode_dir / "\u30bd\u30fc\u30b9.cpp"
        source.write_text(self.source.read_text(encoding="utf-8"), encoding="utf-8")
        self.manifest["sources"] = [str(source)]
        template = validator.read_catalog(self.template, "en")
        self.assertEqual([], validator.check_template(self.manifest, template, unicode_dir))
        source.write_text(source.read_text(encoding="utf-8") + '\nQT_TRANSLATE_NOOP("Fixture", "New label")', encoding="utf-8")
        self.assertTrue(any("New label" in problem for problem in validator.check_template(self.manifest, template, unicode_dir)))

    def test_cli_failure_stops_cmake_build(self):
        self.write_catalog("zh_TW").unlink()
        command = [sys.executable, str(SCRIPT), "--manifest", str(self.manifest_path)]
        result = subprocess.run(command, capture_output=True, check=False)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"zh_TW", result.stderr)
        args = " ".join('"' + value.replace('\\', '/') + '"' for value in command)
        (self.directory / "CMakeLists.txt").write_text(
            'cmake_minimum_required(VERSION 3.26)\nproject(translation_gate NONE)\n'
            f'add_custom_target(validate COMMAND {args} VERBATIM)\n'
            'add_custom_target(consumer ALL COMMAND ${CMAKE_COMMAND} -E touch "${CMAKE_BINARY_DIR}/published" DEPENDS validate)\n', encoding="utf-8")
        build = self.directory / "cmake-build"
        subprocess.run([ARGS.cmake, "-G", "Ninja", "-S", str(self.directory), "-B", str(build)], check=True, capture_output=True)
        failed = subprocess.run([ARGS.cmake, "--build", str(build), "-j", "2"], capture_output=True, check=False)
        self.assertNotEqual(0, failed.returncode)
        self.assertFalse((build / "published").exists())
        self.write_catalog("zh_TW")
        # The exact same build must also reject a finished translation whose
        # argument was dropped and an English template lagging behind source.
        path = self.write_catalog("ja")
        tree = ET.parse(path)
        tree.getroot().find("context/message/translation").text = "&Translated..."
        tree.write(path, encoding="utf-8")
        failed = subprocess.run([ARGS.cmake, "--build", str(build), "-j", "2"], capture_output=True, check=False)
        self.assertNotEqual(0, failed.returncode)
        self.assertIn(b"placeholder", failed.stdout + failed.stderr)
        self.assertFalse((build / "published").exists())
        self.write_catalog("ja")
        source = self.source.read_text(encoding="utf-8")
        self.source.write_text(source + '\nQT_TRANSLATE_NOOP("Fixture", "Unextracted label")', encoding="utf-8")
        failed = subprocess.run([ARGS.cmake, "--build", str(build), "-j", "2"], capture_output=True, check=False)
        self.assertNotEqual(0, failed.returncode)
        self.assertIn(b"English template", failed.stdout + failed.stderr)
        self.assertFalse((build / "published").exists())
        self.source.write_text(source, encoding="utf-8")
        passed = subprocess.run([ARGS.cmake, "--build", str(build), "-j", "2"], capture_output=True, check=False)
        self.assertEqual(0, passed.returncode, passed.stdout + passed.stderr)
        self.assertTrue((build / "published").exists())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--lupdate", required=True)
    parser.add_argument("--cmake", required=True)
    ARGS, remaining = parser.parse_known_args()
    unittest.main(argv=[sys.argv[0], *remaining])
