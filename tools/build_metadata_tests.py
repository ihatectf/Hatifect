"""Execute the metadata hook with SDK targets and a controlled Git process boundary.

These fixtures never access a repository or invoke the real Git executable. The
SDK discovery result models the retained loose/packed-ref reproduction; the real
packed-worktree/package regression remains an additional integration gate.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from validation import resolve_dotnet  # noqa: E402

REVISION = "0123456789abcdef0123456789abcdef01234567"
URL = "https://github.com/example/fixture"


class BuildMetadataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.dotnet = resolve_dotnet()

    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix="hatifect-build-metadata.")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name).resolve()
        self.worktree = self.directory / "worktree with spaces"
        self.project_directory = self.worktree / "nested project"
        self.project_directory.mkdir(parents=True)
        self.intermediate = self.directory / "intermediate"
        (self.intermediate / "net8.0").mkdir(parents=True)
        self.package_cache = self.directory / "package cache"
        self.package_cache.mkdir()
        self.git_directory = self.directory / "sdk-discovered-git"
        self.git_directory.mkdir()
        self.calls = self.directory / "calls.jsonl"
        executables = self.directory / "executables"
        executables.mkdir()
        stub = executables / "git"
        stub.write_text(
            f"#!{sys.executable}\n"
            "import json, os, pathlib, sys\n"
            "with open(os.environ['HATIFECT_TEST_GIT_CALLS'], 'a') as log:\n"
            "    log.write(json.dumps({'args': sys.argv[1:], 'cwd': os.getcwd()}) + '\\n')\n"
            "failure = os.environ.get('HATIFECT_TEST_GIT_FAIL')\n"
            "if (failure == 'head' and sys.argv[1:] == ['rev-parse', '--verify', 'HEAD']) or (failure == 'root' and sys.argv[1:] == ['rev-parse', '--show-toplevel']):\n"
            "    print('fixture: repository query failed', file=sys.stderr)\n"
            "    sys.exit(23)\n"
            "if sys.argv[1:] == ['rev-parse', '--absolute-git-dir']:\n"
            "    print(os.environ.get('GIT_DIR') or os.environ['HATIFECT_TEST_GIT_DIRECTORY'])\n"
            "elif sys.argv[1:] == ['rev-parse', '--verify', 'HEAD']:\n"
            "    foreign = bool(os.environ.get('GIT_COMMON_DIR')) or os.environ.get('GIT_DIR') not in (None, '', os.environ['HATIFECT_TEST_GIT_DIRECTORY'])\n"
            "    print('f' * 40 if foreign else os.environ['HATIFECT_TEST_GIT_REVISION'])\n"
            "elif sys.argv[1:] == ['rev-parse', '--show-toplevel']:\n"
            "    print(os.environ.get('GIT_WORK_TREE') or os.environ['HATIFECT_TEST_GIT_ROOT'])\n"
            "else:\n"
            "    sys.exit(24)\n",
            encoding="utf-8",
        )
        stub.chmod(0o755)
        self.environment = os.environ | {
            "PATH": str(executables) + os.pathsep + os.environ.get("PATH", ""),
            "HATIFECT_TEST_GIT_CALLS": str(self.calls),
            "HATIFECT_TEST_GIT_REVISION": REVISION,
            "HATIFECT_TEST_GIT_ROOT": str(self.worktree),
            "HATIFECT_TEST_GIT_DIRECTORY": str(self.git_directory),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        }
        self.environment.pop("GIT_DIR", None)
        self.environment.pop("GIT_WORK_TREE", None)
        self.environment.pop("GIT_COMMON_DIR", None)
        shutil.copy2(ROOT / "global.json", self.worktree / "global.json")
        hook = ROOT / "Directory.Build.targets"
        if hook.exists():
            shutil.copy2(hook, self.worktree / hook.name)
        self.project = self.project_directory / "Fixture.csproj"
        self.project.write_text(f"""<Project>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Version>1.2.3</Version>
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
    <EnableDefaultItems>false</EnableDefaultItems>
    <IntermediateOutputPath>{escape(str(self.intermediate))}/</IntermediateOutputPath>
    <PathMap>{escape(str(self.worktree))}/=/_/Hatifect/</PathMap>
  </PropertyGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
  <ItemGroup Condition="'$(FixtureAdditionalRoot)' == 'true'">
    <SourceRoot Include="{escape(str(self.package_cache))}/" />
  </ItemGroup>
  <!-- Only discovery is simulated. Assembly metadata, URL translation and
       source-path mapping continue through the real SDK targets. -->
  <Target Name="InitializeSourceControlInformationFromSourceControlManager">
    <PropertyGroup Condition="'$(FixtureState)' != 'no-repository'">
      <_GitRepositoryId>{escape(str(self.git_directory))}</_GitRepositoryId>
      <ScmRepositoryUrl>{URL}</ScmRepositoryUrl>
      <SourceRevisionId Condition="'$(FixtureState)' == 'healthy' and '$(SourceRevisionId)' == ''">{REVISION}</SourceRevisionId>
    </PropertyGroup>
    <ItemGroup Condition="'$(FixtureState)' == 'healthy' or '$(FixtureState)' == 'explicit-root'">
      <SourceRoot Include="{escape(str(self.worktree))}/" SourceControl="git"
                  RevisionId="{REVISION}" ScmRepositoryUrl="{URL}" />
    </ItemGroup>
  </Target>
</Project>
""", encoding="utf-8")

    def query(self, state: str = "packed", **properties: str) -> subprocess.CompletedProcess[str]:
        command = [
            self.dotnet, "msbuild", str(self.project), "-nologo", "-nodeReuse:false",
            "-t:AddSourceRevisionToInformationalVersion;_SetPathMapFromSourceRoots;_InitializeNuspecRepositoryInformationProperties;GenerateSourceLinkFile",
            "-getProperty:SourceRevisionId,InformationalVersion,PathMap,RepositoryCommit,SourceLink",
            "-getItem:SourceRoot", f"-p:FixtureState={state}",
            *[f"-p:{key}={value}" for key, value in properties.items()],
        ]
        return subprocess.run(command, cwd=self.worktree, env=self.environment,
                              capture_output=True, text=True, timeout=45, check=False)

    def result(self, completed: subprocess.CompletedProcess[str]) -> dict:
        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
        return json.loads(completed.stdout)

    def git_calls(self) -> list[dict]:
        return [json.loads(line) for line in self.calls.read_text().splitlines()] if self.calls.exists() else []

    def assert_root(self, result: dict, *, expected_count: int = 1) -> None:
        all_roots = result["Items"]["SourceRoot"]
        self.assertEqual(len(all_roots), expected_count)
        roots = [root for root in all_roots if root.get("SourceControl") == "git"]
        self.assertEqual(len(roots), 1)
        self.assertEqual(roots[0]["Identity"], str(self.worktree) + "/")
        self.assertEqual(roots[0]["RevisionId"], REVISION)
        self.assertEqual(roots[0]["SourceControl"], "git")
        self.assertEqual(roots[0]["ScmRepositoryUrl"], URL)
        self.assertEqual(roots[0]["RepositoryUrl"], URL)
        source_url = "https://raw.githubusercontent.com/example/fixture/" + REVISION + "/*"
        self.assertEqual(roots[0]["SourceLinkUrl"], source_url)
        source_link = Path(result["Properties"]["SourceLink"])
        if not source_link.is_absolute():
            source_link = self.project_directory / source_link
        self.assertEqual(json.loads(source_link.read_text()), {"documents": {"/_/*": source_url}})
        self.assertEqual(roots[0]["MappedPath"], "/_/")
        self.assertIn(str(self.worktree) + "/=/_/", result["Properties"]["PathMap"].split(","))

    def test_missing_sdk_metadata_restores_revision_and_deterministic_source_root(self) -> None:
        result = self.result(self.query())
        self.assertEqual(result["Properties"]["SourceRevisionId"], REVISION)
        self.assertEqual(result["Properties"]["InformationalVersion"], "1.2.3+" + REVISION)
        self.assertEqual(result["Properties"]["RepositoryCommit"], REVISION)
        self.assert_root(result)
        self.assertEqual(self.git_calls(), [
            {"args": ["rev-parse", "--absolute-git-dir"], "cwd": str(self.project_directory)},
            {"args": ["rev-parse", "--verify", "HEAD"], "cwd": str(self.project_directory)},
            {"args": ["rev-parse", "--show-toplevel"], "cwd": str(self.project_directory)},
        ])

    def test_healthy_sdk_metadata_is_unchanged_without_git_process(self) -> None:
        result = self.result(self.query("healthy"))
        self.assertEqual(result["Properties"]["SourceRevisionId"], REVISION)
        self.assertEqual(result["Properties"]["InformationalVersion"], "1.2.3+" + REVISION)
        self.assert_root(result)
        self.assertEqual(self.git_calls(), [])

    def test_nuget_source_root_does_not_hide_missing_git_metadata(self) -> None:
        result = self.result(self.query(FixtureAdditionalRoot="true"))
        self.assertEqual(result["Properties"]["SourceRevisionId"], REVISION)
        self.assertEqual(result["Properties"]["RepositoryCommit"], REVISION)
        self.assert_root(result, expected_count=2)
        package_roots = [root for root in result["Items"]["SourceRoot"] if not root.get("SourceControl")]
        self.assertEqual(len(package_roots), 1)
        self.assertEqual(package_roots[0]["Identity"], str(self.package_cache) + "/")
        self.assertEqual(package_roots[0]["MappedPath"], "/_1/")

    def test_healthy_and_recovered_metadata_produce_identical_dll_and_pdb(self) -> None:
        # Compile the same inputs through both paths. This catches differences
        # that equivalent-looking property queries can miss in compiler options.
        project_text = self.project.read_text()
        self.project.write_text(project_text.replace(
            '<Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />',
            '<ItemGroup><Compile Include="Probe.cs" /></ItemGroup>\n'
            '<Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'))
        (self.project_directory / "Probe.cs").write_text(
            'namespace MetadataFixture { public static class Probe { public static int Value => 42; } }\n')
        empty_feed = self.directory / "empty feed"
        empty_feed.mkdir()
        # This fixture has no Git index. Only the source-control metadata boundary
        # is simulated; untracked-file embedding needs a real repository gate.
        common = ["-p:NuGetAudit=false", "-p:EmbedUntrackedSources=false", "-p:FixtureAdditionalRoot=true", "-p:UseSharedCompilation=false",
                  "-nodeReuse:false", "-m:1"]
        commands = [
            [self.dotnet, "restore", str(self.project), "--source", str(empty_feed), *common],
            [self.dotnet, "build", str(self.project), "--no-restore", "--disable-build-servers",
             "-c", "Release", "-t:Rebuild", "-p:FixtureState=healthy", *common],
            [self.dotnet, "build", str(self.project), "--no-restore", "--disable-build-servers",
             "-c", "Release", "-t:Rebuild", "-p:FixtureState=packed", *common],
        ]
        healthy = None
        output = self.project_directory / "bin/Release/net8.0"
        for index, command in enumerate(commands):
            completed = subprocess.run(command, cwd=self.worktree, env=self.environment,
                                       capture_output=True, text=True, timeout=90, check=False)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            if index == 1:
                healthy = {name: (output / name).read_bytes() for name in ["Fixture.dll", "Fixture.pdb"]}
        self.assertIsNotNone(healthy)
        self.assertGreater(len(healthy["Fixture.dll"]), 0)
        self.assertEqual({name: (output / name).read_bytes() for name in healthy}, healthy)

    def test_explicit_revision_is_preserved_while_root_describes_actual_head(self) -> None:
        result = self.result(self.query(SourceRevisionId="explicit-revision"))
        self.assertEqual(result["Properties"]["SourceRevisionId"], "explicit-revision")
        self.assertEqual(result["Properties"]["InformationalVersion"], "1.2.3+explicit-revision")
        self.assertEqual(result["Properties"]["RepositoryCommit"], "explicit-revision")
        self.assert_root(result)

    def test_explicit_root_is_preserved_without_git_process(self) -> None:
        result = self.result(self.query("explicit-root", SourceRevisionId="explicit-revision"))
        self.assertEqual(result["Properties"]["SourceRevisionId"], "explicit-revision")
        self.assert_root(result)
        self.assertEqual(self.git_calls(), [])

    def test_no_repository_and_disabled_queries_do_not_invent_metadata(self) -> None:
        for state, properties in [
            ("no-repository", {}),
            ("packed", {"EnableSourceControlManagerQueries": "false"}),
        ]:
            with self.subTest(state=state):
                result = self.result(self.query(state, DeterministicSourcePaths="false", **properties))
                self.assertEqual(result["Properties"]["SourceRevisionId"], "")
                self.assertEqual(result["Properties"]["InformationalVersion"], "1.2.3")
                self.assertEqual(result["Items"]["SourceRoot"], [])
                self.assertEqual(self.git_calls(), [])

    def test_failed_git_query_fails_metadata_initialization(self) -> None:
        for failure, expected_calls in [("head", 2), ("root", 3)]:
            with self.subTest(failure=failure):
                self.calls.unlink(missing_ok=True)
                self.environment["HATIFECT_TEST_GIT_FAIL"] = failure
                completed = self.query(DeterministicSourcePaths="false")
                self.assertNotEqual(completed.returncode, 0)
                self.assertIn("exited with code 23", completed.stdout + completed.stderr)
                result = json.loads(completed.stdout)
                self.assertEqual(result["Properties"]["SourceRevisionId"], "")
                self.assertEqual(result["Items"]["SourceRoot"], [])
                self.assertEqual(len(self.git_calls()), expected_calls)

    def test_real_sdk_source_archive_does_not_query_git(self) -> None:
        # Exercise actual LocateRepository too, in a fresh directory without Git.
        content = self.project.read_text()
        start = content.index('  <Target Name="InitializeSourceControlInformationFromSourceControlManager">')
        end = content.index("  </Target>", start) + len("  </Target>")
        self.project.write_text(content[:start] + content[end:])
        result = self.result(self.query(DeterministicSourcePaths="false"))
        self.assertEqual(result["Properties"]["SourceRevisionId"], "")
        self.assertEqual(result["Properties"]["RepositoryCommit"], "")
        self.assertEqual(result["Items"]["SourceRoot"], [])
        self.assertEqual(self.git_calls(), [])

    def test_foreign_git_directory_or_worktree_cannot_publish_metadata(self) -> None:
        foreign = self.directory / "foreign worktree"
        foreign.mkdir()
        foreign_git = self.directory / "foreign git"
        foreign_git.mkdir()
        for directory, message in [
            (foreign_git, "Hatifect Git metadata query selected a different repository"),
            (self.git_directory, "Hatifect Git source root does not match this checkout"),
        ]:
            with self.subTest(directory=directory):
                self.environment["GIT_DIR"] = str(directory)
                self.environment["GIT_WORK_TREE"] = str(foreign)
                completed = self.query(DeterministicSourcePaths="false")
                self.assertNotEqual(completed.returncode, 0)
                self.assertIn(message, completed.stdout + completed.stderr)
                result = json.loads(completed.stdout)
                self.assertEqual(result["Properties"]["SourceRevisionId"], "")
                self.assertEqual(result["Items"]["SourceRoot"], [])

    def test_common_directory_override_fails_before_queries_or_metadata(self) -> None:
        # Git dir/root can still match A while common refs resolve HEAD from B.
        # A project property must not hide the actual process environment.
        foreign = self.directory / "foreign common directory"
        foreign.mkdir()
        self.environment["GIT_COMMON_DIR"] = str(foreign)
        completed = self.query(DeterministicSourcePaths="false", GIT_COMMON_DIR="")
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("Hatifect Git metadata recovery does not support GIT_COMMON_DIR", completed.stdout + completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual(result["Properties"]["SourceRevisionId"], "")
        self.assertEqual(result["Properties"]["RepositoryCommit"], "")
        self.assertEqual(result["Items"]["SourceRoot"], [])
        self.assertEqual(self.git_calls(), [])

    def test_malformed_revision_fails_before_source_root_is_published(self) -> None:
        self.environment["HATIFECT_TEST_GIT_REVISION"] = "not-a-revision"
        result = self.query(DeterministicSourcePaths="false")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Hatifect could not recover Git revision", result.stdout + result.stderr)

    def test_missing_source_root_fails_without_publishing_revision(self) -> None:
        self.environment["HATIFECT_TEST_GIT_ROOT"] = str(self.directory / "missing")
        completed = self.query(DeterministicSourcePaths="false")
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("Hatifect could not recover the Git source root", completed.stdout + completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual(result["Properties"]["SourceRevisionId"], "")
        self.assertEqual(result["Items"]["SourceRoot"], [])


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(BuildMetadataTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    raise SystemExit(0 if result.testsRun > 0 and result.wasSuccessful()
                     and not result.skipped and not result.expectedFailures else 1)
