import hashlib
import importlib.util
import pathlib
import tempfile
import unittest
import zipfile

module_path = pathlib.Path(__file__).parents[1] / "build/compute-linux/browser/extract.py"
specification = importlib.util.spec_from_file_location("browser_extract", module_path)
extractor = importlib.util.module_from_spec(specification)
specification.loader.exec_module(extractor)


class BrowserArchiveTests(unittest.TestCase):
    def archive(self, entries):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        archive = pathlib.Path(directory.name) / "build.zip"
        with zipfile.ZipFile(archive, "w") as bundle:
            for name, contents in entries:
                bundle.writestr(name, contents)
        for name, _ in entries:
            if "\\" in name:
                archive.write_bytes(archive.read_bytes().replace(name.replace("\\", "/").encode(), name.encode()))
        destination = pathlib.Path(directory.name) / "output"
        destination.mkdir()
        return archive, destination, hashlib.sha256(archive.read_bytes()).hexdigest()

    def test_extracts_the_exact_pinned_build(self):
        archive, destination, digest = self.archive([("index.html", "<canvas>"), ("assets/game.js", "true")])
        extractor.extract(archive, destination, digest)
        self.assertEqual("true", destination.joinpath("assets/game.js").read_text())

    def test_rejects_hash_substitution(self):
        archive, destination, _ = self.archive([("index.html", "fixture")])
        with self.assertRaisesRegex(ValueError, "digest"):
            extractor.extract(archive, destination, "0" * 64)

    def test_rejects_symbolic_links(self):
        archive, destination, _ = self.archive([("index.html", "fixture")])
        with zipfile.ZipFile(archive, "a") as bundle:
            entry = zipfile.ZipInfo("linked")
            entry.external_attr = 0o120777 << 16
            bundle.writestr(entry, "/outside")
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "unsafe"):
            extractor.extract(archive, destination, digest)

    def test_rejects_traversal_links_duplicates_and_missing_index(self):
        for name in ("../escape", "/escape", "dir/../../escape", "dir\\escape", "C:/escape", "./index.html"):
            with self.subTest(name=name):
                archive, destination, digest = self.archive([("index.html", "fixture"), (name, "bad")])
                with self.assertRaises(ValueError):
                    extractor.extract(archive, destination, digest)
        archive, destination, digest = self.archive([("INDEX.HTML", "one"), ("index.html", "two")])
        with self.assertRaises(ValueError):
            extractor.extract(archive, destination, digest)
        archive, destination, digest = self.archive([("game.js", "true")])
        with self.assertRaisesRegex(ValueError, "index"):
            extractor.extract(archive, destination, digest)


if __name__ == "__main__":
    unittest.main()
