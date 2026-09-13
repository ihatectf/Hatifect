"""Repository policy for keeping AI tooling outside product development paths."""
from __future__ import annotations

from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
ALLOWED_REFERENCES = {
    Path(".gitignore"),
    Path("docs/AI_TESTING.md"),
    Path("tools/tests/test_ai_scope.py"),
}
LOCAL_ONLY_PATHS = {
    Path("tools/agent_setup.py"),
    Path("tools/hatifect-agent-check"),
    Path("tools/tests/test_agent_setup.py"),
}
LOCAL_ONLY_NAMES = {
    "AGENTS.md",
    "CLAUDE.md",
    "GEMINI.md",
    "copilot-instructions.md",
}
LOCAL_ONLY_DIRECTORIES = {".agents", ".ai", ".claude", ".codex", ".cursor", ".prompts"}
AI_MARKERS = ("codex", "chatgpt", "openai", "claude", "copilot", "gemini", "subagent")


def repository_files() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return sorted(
        Path(item.decode("utf-8"))
        for item in result.stdout.split(b"\0")
        if item and (ROOT / item.decode("utf-8")).is_file()
    )


class AiScopeTests(unittest.TestCase):
    def test_tracked_ai_references_are_confined_to_test_policy(self) -> None:
        forbidden_paths: list[str] = []
        forbidden_references: list[str] = []

        for relative in repository_files():
            if (
                relative in LOCAL_ONLY_PATHS
                or relative.name in LOCAL_ONLY_NAMES
                or LOCAL_ONLY_DIRECTORIES.intersection(relative.parts)
            ):
                forbidden_paths.append(relative.as_posix())

            if relative in ALLOWED_REFERENCES:
                continue
            try:
                text = (ROOT / relative).read_text(encoding="utf-8").lower()
            except UnicodeDecodeError:
                continue
            markers = sorted(marker for marker in AI_MARKERS if marker in text)
            if markers:
                forbidden_references.append(f"{relative.as_posix()}: {', '.join(markers)}")

        self.assertEqual([], forbidden_paths, "local assistant files must remain untracked")
        self.assertEqual(
            [],
            forbidden_references,
            "AI references are allowed only in the documented testing policy",
        )


if __name__ == "__main__":
    unittest.main()
