import tempfile
import unittest
from pathlib import Path, PurePosixPath
import sys


TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import candidate_fingerprint as candidate  # noqa: E402


class CandidateFingerprintTests(unittest.TestCase):
    def _candidate(self, root: Path) -> Path:
        package = root / "Hatifect"
        for index, relative_text in enumerate(candidate.REQUIRED_RUNTIME_PATHS):
            relative = PurePosixPath(relative_text)
            path = package.joinpath(*relative.parts)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"runtime-{index}:{relative.as_posix()}".encode("utf-8"))
        return package

    def test_inventory_is_exact_three_module_runtime(self) -> None:
        paths = set(candidate.REQUIRED_RUNTIME_PATHS)
        dlls = {path for path in paths if path.endswith(".dll")}
        manifests = {path for path in paths if path.endswith("manifest.json")}
        self.assertEqual(13, len(dlls))
        self.assertEqual(3, len(manifests))
        configs = {path for path in paths if path.endswith("config.json")}
        self.assertEqual(1, len(configs))
        self.assertEqual(19, len(paths))
        self.assertEqual(4, sum(path.startswith("Hatifect Flow/") for path in paths))

    def test_mutating_each_runtime_file_changes_candidate_digest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            package = self._candidate(Path(temporary))
            expected = candidate.compute_candidate_fingerprint(package)

            for relative_text in candidate.REQUIRED_RUNTIME_PATHS:
                with self.subTest(path=relative_text):
                    path = package.joinpath(*PurePosixPath(relative_text).parts)
                    original = path.read_bytes()
                    path.write_bytes(original + b"-changed")
                    self.assertNotEqual(
                        expected,
                        candidate.compute_candidate_fingerprint(package),
                    )
                    path.write_bytes(original)

    def test_removing_each_runtime_file_fails_closed(self) -> None:
        for relative_text in candidate.REQUIRED_RUNTIME_PATHS:
            with self.subTest(path=relative_text), tempfile.TemporaryDirectory() as temporary:
                package = self._candidate(Path(temporary))
                package.joinpath(*PurePosixPath(relative_text).parts).unlink()
                with self.assertRaisesRegex(ValueError, "missing required candidate runtime file"):
                    candidate.compute_candidate_fingerprint(package)

    def test_wrong_root_name_and_symlink_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            package = self._candidate(temporary_root)
            wrong = temporary_root / "candidate"
            wrong.mkdir()
            with self.assertRaisesRegex(ValueError, "must be named 'Hatifect'"):
                candidate.compute_candidate_fingerprint(wrong)

            target_relative = PurePosixPath(candidate.REQUIRED_RUNTIME_PATHS[0])
            target = package.joinpath(*target_relative.parts)
            original = target.with_suffix(".original")
            target.rename(original)
            target.symlink_to(original)
            with self.assertRaisesRegex(ValueError, "must not be a symlink"):
                candidate.compute_candidate_fingerprint(package)


if __name__ == "__main__":
    unittest.main()
