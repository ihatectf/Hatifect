"""Behavioral contracts for the three-module package and explicit isolated deployment."""
from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import release as release_tool  # noqa: E402


def write_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False) + '\n', encoding='utf-8')


def managed_image(marker: bytes = b'fixture') -> bytes:
    """A minimal PE/CLI header fixture, not a runnable .NET assembly."""
    data = bytearray(1024)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 60, 128)
    data[128:132] = b'PE\0\0'
    struct.pack_into('<H', data, 134, 1)  # one section
    struct.pack_into('<H', data, 148, 224)  # PE32 optional header
    struct.pack_into('<H', data, 152, 0x10B)
    struct.pack_into('<I', data, 244, 16)  # data directory count
    struct.pack_into('<II', data, 360, 0x2000, 72)  # CLI directory
    data[376:384] = b'.text\0\0\0'
    struct.pack_into('<IIII', data, 384, 512, 0x2000, 512, 512)
    struct.pack_into('<I', data, 512, 72)
    struct.pack_into('<II', data, 520, 0x2050, 16)  # CLI metadata directory
    data[592:596] = b'BSJB'
    return bytes(data) + marker


class ReleaseFixture(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix='hatifect-release-tests.')
        self.addCleanup(temporary.cleanup)
        self.workspace = Path(temporary.name).resolve()
        self.root = self.workspace / 'repository with spaces'
        self.root.mkdir()
        self.contract = release_tool.load_contract()
        self.modules = {module['UniqueID']: module for module in self.contract['Modules']}
        self.source_files = {}
        self.binaries = {}
        for module in self.contract['Modules']:
            project = self.root / module['Project']
            project.parent.mkdir(parents=True, exist_ok=True)
            project.write_text(
                '<Project><PropertyGroup>'
                f'<Version>{module["Version"]}</Version>'
                f'<AssemblyName>{Path(module["EntryDll"]).stem}</AssemblyName>'
                '</PropertyGroup></Project>', encoding='utf-8')
            for name in module['AllowedRootFiles'] + module['AllowedRuntimeFiles']:
                relative = module['Path'] + '/' + name
                if name.endswith('.dll'):
                    self.binaries[relative] = managed_image(relative.encode('utf-8'))
                    continue
                if name == 'manifest.json':
                    value = {key: module[key] for key in ('Name', 'UniqueID', 'Version', 'EntryDll')}
                    value['MinimumApiVersion'] = self.contract['MinimumApiVersion']
                    value['Dependencies'] = module['Dependencies']
                    if 'MinimumGameVersion' in module:
                        value['MinimumGameVersion'] = module['MinimumGameVersion']
                elif name.startswith('i18n/'):
                    value = {'open': 'Открыть' if name.endswith('ru.json') else 'Open', 'close': 'Close'}
                elif name.endswith('.json'):
                    value = {'Enabled': True}
                else:
                    self.source_files[relative] = b'Fixture license\n'
                    continue
                self.source_files[relative] = json.dumps(value, ensure_ascii=False).encode('utf-8')
        module_keys = {module['Key']: module for module in self.contract['Modules']}
        for follower in self.contract['VersionFollowers']:
            path = self.root / follower['Project']
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('<Project><PropertyGroup><Version>' + module_keys[follower['Module']]['Version']
                            + '</Version></PropertyGroup></Project>', encoding='utf-8')
        for relative, data in self.source_files.items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        ui = self.modules['Hatifect.UI']
        write_json(self.root / 'Hatifect.UI.Packages.json', {
            'Version': ui['Version'],
            'Packages': [{'Id': Path(name).stem} for name in ui['AllowedRootFiles'] if name.endswith('.dll')],
        })
        write_json(self.root / 'Hatifect.Release.json', self.contract)

    def package(self, parent: Path | None = None) -> Path:
        root = (parent or self.workspace / 'package') / 'Hatifect'
        for relative, data in (self.source_files | self.binaries).items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        return root

    def build_outputs(self) -> None:
        for module in self.contract['Modules']:
            output = (self.root / module['Project']).parent / 'bin/Release/net6.0'
            output.mkdir(parents=True, exist_ok=True)
            for name in module['AllowedRootFiles']:
                if name.endswith('.dll'):
                    (output / name).write_bytes(self.binaries[module['Path'] + '/' + name])

    def write_manifest(self, root: Path, identity: str, **changes: object) -> None:
        path = root / self.modules[identity]['Manifest']
        value = json.loads(path.read_text(encoding='utf-8'))
        value.update(changes)
        write_json(path, value)

    def assert_package_bytes(self, root: Path) -> None:
        actual = {path.relative_to(root).as_posix(): path.read_bytes()
                  for path in root.rglob('*') if path.is_file()}
        self.assertEqual(actual, self.source_files | self.binaries)


class ReleaseSourceTests(ReleaseFixture):
    def test_current_source_declares_three_modules_and_fourteen_runtime_assemblies(self) -> None:
        actual = {module['UniqueID']: sum(name.endswith('.dll') for name in module['AllowedRootFiles'])
                  for module in self.contract['Modules']}
        self.assertEqual(actual, {'Hatifect.UI': 8, 'Hatifect.ChestsAnywhereOverlay': 2, 'Hatifect.Flow': 4})
        release_tool.verify_source(self.contract, ROOT)
        flow = self.modules['Hatifect.Flow']
        self.assertIn('Hatifect.Flow.UI.Semantic.dll', flow['AllowedRootFiles'])
        self.assertEqual(flow['Dependencies'], [{'UniqueID': 'Hatifect.UI', 'IsRequired': True, 'MinimumVersion': '1.0.0-alpha.47'}])

    def test_source_validation_needs_current_projects_and_content_without_history_or_binaries(self) -> None:
        release_tool.verify_source(self.contract, self.root)
        self.assertEqual(list(self.root.rglob('*.dll')), [])
        self.assertEqual(list(self.root.rglob('*.md')), [])
        self.assertFalse((self.root / '.git').exists())

    def test_source_rejects_manifest_identity_version_entry_and_dependency_drift(self) -> None:
        original = (self.root / self.modules['Hatifect.ChestsAnywhereOverlay']['Manifest']).read_bytes()
        for changes in ({'UniqueID': 'Wrong.Mod'}, {'Version': '99.0.0'}, {'EntryDll': 'Wrong.dll'},
                        {'MinimumApiVersion': '1.0'}, {'MinimumGameVersion': '1.0'}, {'Dependencies': []}):
            with self.subTest(changes=changes):
                self.write_manifest(self.root, 'Hatifect.ChestsAnywhereOverlay', **changes)
                with self.assertRaises(release_tool.ReleaseError):
                    release_tool.verify_source(self.contract, self.root)
                (self.root / self.modules['Hatifect.ChestsAnywhereOverlay']['Manifest']).write_bytes(original)

    def test_source_rejects_project_version_assembly_and_version_follower_drift(self) -> None:
        paths = [self.root / self.modules['Hatifect.Flow']['Project'],
                 self.root / self.contract['VersionFollowers'][0]['Project']]
        for path in paths:
            original = path.read_text(encoding='utf-8')
            edits = [original.replace('<Version>', '<Version>9')]
            if '<AssemblyName>' in original:
                edits.append(original.replace('<AssemblyName>', '<AssemblyName>Wrong'))
            for content in edits:
                with self.subTest(path=path, content=content):
                    path.write_text(content, encoding='utf-8')
                    with self.assertRaises(release_tool.ReleaseError):
                        release_tool.verify_source(self.contract, self.root)
                    path.write_text(original, encoding='utf-8')

    def test_source_rejects_ui_catalog_version_and_ownership_drift(self) -> None:
        path = self.root / 'Hatifect.UI.Packages.json'
        original = json.loads(path.read_text(encoding='utf-8'))
        for value in (original | {'Version': '99.0.0'}, original | {'Packages': original['Packages'][:-1]}):
            with self.subTest(value=value), self.assertRaisesRegex(release_tool.ReleaseError, 'ownership/version'):
                write_json(path, value)
                release_tool.verify_source(self.contract, self.root)

    def test_localization_requires_matching_keys_and_string_values_in_source_and_package(self) -> None:
        package = self.package()
        relative = self.modules['Hatifect.ChestsAnywhereOverlay']['Path'] + '/i18n/ru.json'
        for values in ({'open': 'Открыть'}, {'open': 'Открыть', 'close': 'Закрыть', 'extra': 'x'},
                       {'open': 2, 'close': 'Закрыть'}):
            for root, validate in ((self.root, release_tool.verify_source), (package, release_tool.verify_package)):
                with self.subTest(values=values, root=root), self.assertRaisesRegex(release_tool.ReleaseError, 'localization'):
                    write_json(root / relative, values)
                    validate(self.contract, root)

    def test_duplicate_json_keys_are_rejected(self) -> None:
        path = self.root / self.modules['Hatifect.Flow']['Manifest']
        path.write_text('{"Name":"one","Name":"two"}', encoding='utf-8')
        with self.assertRaisesRegex(release_tool.ReleaseError, 'duplicate JSON key'):
            release_tool.verify_source(self.contract, self.root)

    def test_descriptor_rejects_unsafe_paths_duplicate_ownership_and_malformed_shapes(self) -> None:
        cases = []
        for path in ('../escape', '/escape', 'A/../escape', 'A\\escape', 'C:escape', 'A//escape', '.hidden'):
            contract = copy.deepcopy(self.contract)
            contract['Modules'][0]['Path'] = path
            cases.append(contract)
        for key, value in (('AllowedRootFiles', None), ('AllowedDirectories', 'i18n')):
            contract = copy.deepcopy(self.contract)
            contract['Modules'][0][key] = value
            cases.append(contract)
        for filename in ('Hatifect.UI.Runtime.dll', 'hatifect.ui.runtime.dll'):
            contract = copy.deepcopy(self.contract)
            contract['Modules'][1]['AllowedRootFiles'].append(filename)
            cases.append(contract)
        contract = copy.deepcopy(self.contract)
        contract['Modules'][0]['AllowedRootFiles'].append('Hatifect.UI.RUNTIME.dll')
        cases.append(contract)
        contract = copy.deepcopy(self.contract)
        contract['Modules'][1]['Path'] = 'hatifect ui/child'
        cases.append(contract)
        for value in (None, [{}], [{'Module': 'missing', 'Project': 'X.csproj'}]):
            cases.append(self.contract | {'VersionFollowers': value})
        cases.append(self.contract | {'FormatVersion': True})
        for index, contract in enumerate(cases):
            with self.subTest(index=index), self.assertRaises(release_tool.ReleaseError):
                release_tool.validate_contract(contract)

    def test_descriptor_rejects_missing_duplicate_unknown_or_cyclic_module_dependencies(self) -> None:
        cases = []
        contract = copy.deepcopy(self.contract)
        contract['Modules'].pop()
        cases.append(contract)
        contract = copy.deepcopy(self.contract)
        contract['Modules'].append(copy.deepcopy(contract['Modules'][0]))
        cases.append(contract)
        for dependency in ({'UniqueID': 'Hatifect.Unknown'}, {'UniqueID': 'Hatifect.UI', 'MinimumVersion': '0.1'},
                           {'UniqueID': 'Hatifect.UI', 'MinimumVersion': '1.0.0-alpha.30', 'IsRequired': False}):
            contract = copy.deepcopy(self.contract)
            contract['Modules'][1]['Dependencies'] = [dependency]
            cases.append(contract)
        contract = copy.deepcopy(self.contract)
        contract['Modules'][0]['Dependencies'] = [{
            'UniqueID': 'Hatifect.ChestsAnywhereOverlay', 'MinimumVersion': '1.0.0-alpha.1', 'IsRequired': True}]
        cases.append(contract)
        contract = copy.deepcopy(self.contract)
        contract['Modules'][1]['Dependencies'] *= 2
        cases.append(contract)
        for index, contract in enumerate(cases):
            with self.subTest(index=index), self.assertRaises(release_tool.ReleaseError):
                release_tool.validate_contract(contract)


class ReleasePackageTests(ReleaseFixture):
    def test_exact_runtime_package_passes_and_every_required_assembly_is_mandatory(self) -> None:
        package = self.package()
        release_tool.verify_package(self.contract, package)
        for relative, data in self.binaries.items():
            path = package / relative
            with self.subTest(relative=relative):
                path.unlink()
                with self.assertRaisesRegex(release_tool.ReleaseError, 'missing runtime files'):
                    release_tool.verify_package(self.contract, package)
                path.write_bytes(data)
        release_tool.verify_package(self.contract, package)

    def test_package_rejects_unlisted_sources_binaries_and_empty_directories(self) -> None:
        package = self.package()
        for relative in ('Hatifect UI/Source.cs', 'Hatifect Flow/Foreign.dll',
                         'Hatifect UI/Hatifect.UI.Runtime.dll', 'extra.json', 'README.md'):
            path = package / relative
            path.write_text('unexpected', encoding='utf-8')
            with self.subTest(relative=relative), self.assertRaisesRegex(release_tool.ReleaseError, 'unexpected runtime files'):
                release_tool.verify_package(self.contract, package)
            path.unlink()
        (package / 'empty').mkdir()
        with self.assertRaisesRegex(release_tool.ReleaseError, 'unexpected runtime directories'):
            release_tool.verify_package(self.contract, package)

    def test_package_rejects_manifest_drift_and_wrong_root_name(self) -> None:
        package = self.package()
        self.write_manifest(package, 'Hatifect.Flow', Version='99.0.0')
        with self.assertRaisesRegex(release_tool.ReleaseError, 'Version='):
            release_tool.verify_package(self.contract, package)
        renamed = package.rename(package.with_name('Different'))
        with self.assertRaisesRegex(release_tool.ReleaseError, 'root must be named'):
            release_tool.verify_package(self.contract, renamed)

    def test_package_rejects_symlinked_file_directory_root_and_nonregular_entries(self) -> None:
        package = self.package()
        alias = self.workspace / 'alias' / 'Hatifect'
        alias.parent.mkdir()
        alias.symlink_to(package, target_is_directory=True)
        with self.assertRaisesRegex(release_tool.ReleaseError, 'real directory'):
            release_tool.verify_package(self.contract, alias)
        for relative, directory in (('extra-link', True), ('Hatifect UI/config.json', False)):
            link = package / relative
            link.symlink_to(self.root if directory else self.root / 'Hatifect.Release.json', target_is_directory=directory)
            with self.subTest(relative=relative), self.assertRaisesRegex(release_tool.ReleaseError, 'symbolic link'):
                release_tool.verify_package(self.contract, package)
            link.unlink()
        if hasattr(os, 'mkfifo'):
            os.mkfifo(package / 'fifo')
            with self.assertRaisesRegex(release_tool.ReleaseError, 'non-regular'):
                release_tool.verify_package(self.contract, package)

    def test_package_rejects_truncated_or_invalid_managed_images(self) -> None:
        package = self.package()
        relative = next(iter(self.binaries))
        valid = self.binaries[relative]
        variants = [b'', b'MZ', valid[:300], b'XX' + valid[2:]]
        for position in (128, 152, 360, 592):
            corrupted = bytearray(valid)
            corrupted[position:position + 4] = b'\0' * 4
            variants.append(bytes(corrupted))
        for index, image in enumerate(variants):
            with self.subTest(index=index), self.assertRaisesRegex(release_tool.ReleaseError, 'invalid managed PE'):
                (package / relative).write_bytes(image)
                release_tool.verify_package(self.contract, package)

    def test_package_rejects_forbidden_flow_dependency_in_both_encodings(self) -> None:
        package = self.package()
        module = self.modules['Hatifect.ChestsAnywhereOverlay']
        for name in (name for name in module['AllowedRootFiles'] if name.endswith('.dll')):
            for token in ('Hatifect.Flow',):
                for encoding in ('utf-8', 'utf-16-le'):
                    path = package / module['Path'] / name
                    with self.subTest(name=name, token=token, encoding=encoding):
                        path.write_bytes(managed_image((token + '\0').encode(encoding)))
                        with self.assertRaisesRegex(release_tool.ReleaseError, 'forbidden binary dependency/type'):
                            release_tool.verify_package(self.contract, package)
                        path.write_bytes(self.binaries[module['Path'] + '/' + name])

    def test_archive_is_deterministic_and_contains_only_verified_runtime_content(self) -> None:
        package = self.package()
        archive, checksum = release_tool.make_archive(self.contract, package, self.workspace / 'archive')
        first = archive.read_bytes()
        for path in package.rglob('*'):
            if path.is_file():
                os.utime(path, (1234567890, 1234567890))
        release_tool.make_archive(self.contract, package, archive.parent)
        self.assertEqual(archive.read_bytes(), first)
        self.assertEqual(checksum.read_text(encoding='utf-8'), hashlib.sha256(first).hexdigest() + '  ' + archive.name + '\n')
        with zipfile.ZipFile(archive) as result:
            self.assertEqual(set(result.namelist()), {'Hatifect/' + relative for relative in self.source_files | self.binaries})
            self.assertEqual(result.read('Hatifect/Hatifect Flow/Hatifect.Flow.Core.dll'),
                             self.binaries['Hatifect Flow/Hatifect.Flow.Core.dll'])

    def test_assembly_uses_built_outputs_and_replaces_only_owned_runtime(self) -> None:
        self.build_outputs()
        base = self.root / 'artifacts/package'
        package = release_tool.assemble_package(self.contract, base, self.root)
        self.assertEqual(package, base / 'Hatifect')
        self.assert_package_bytes(package)
        marker = base / 'unrelated.log'
        marker.write_text('keep', encoding='utf-8')
        (package / 'old.tmp').write_text('old', encoding='utf-8')
        release_tool.assemble_package(self.contract, base, self.root)
        self.assert_package_bytes(package)
        self.assertEqual(marker.read_text(encoding='utf-8'), 'keep')
        self.assertEqual(sorted(path.name for path in base.iterdir()), ['Hatifect', 'unrelated.log'])

    def test_failed_assembly_preserves_previous_package_and_cleans_temporary_files(self) -> None:
        self.build_outputs()
        base = self.root / 'artifacts/package'
        package = release_tool.assemble_package(self.contract, base, self.root)
        module = self.modules['Hatifect.Flow']
        output = (self.root / module['Project']).parent / 'bin/Release/net6.0' / module['EntryDll']
        for invalid in (None, b'bad-image'):
            if invalid is None:
                output.unlink()
            else:
                output.write_bytes(invalid)
            with self.subTest(invalid=invalid), self.assertRaises(release_tool.ReleaseError):
                release_tool.assemble_package(self.contract, base, self.root)
            self.assert_package_bytes(package)
            self.assertEqual([path.name for path in base.iterdir()], ['Hatifect'])

    def test_assembly_rejects_symlinked_build_outputs_without_creating_runtime(self) -> None:
        self.build_outputs()
        module = self.modules['Hatifect.Flow']
        output = (self.root / module['Project']).parent / 'bin/Release/net6.0' / module['EntryDll']
        target = self.workspace / 'outside.dll'
        output.rename(target)
        output.symlink_to(target)
        base = self.root / 'artifacts/package'
        with self.assertRaisesRegex(release_tool.ReleaseError, 'symbolic link'):
            release_tool.assemble_package(self.contract, base, self.root)
        self.assertFalse((base / 'Hatifect').exists())
        self.assertEqual(target.read_bytes(), self.binaries[module['Path'] + '/' + module['EntryDll']])

    def test_staging_and_deployment_paths_accept_only_owned_or_isolated_locations(self) -> None:
        expected = self.root / 'artifacts/package'
        self.assertEqual(release_tool.safe_release_base(expected, self.root), expected)
        for path in (self.root, self.workspace / 'release', self.root / 'artifacts/other', self.root / 'Mods'):
            with self.subTest(path=path), self.assertRaises(release_tool.ReleaseError):
                release_tool.safe_release_base(path, self.root)
        with tempfile.TemporaryDirectory(prefix='hatifect-live-release.') as temporary:
            path = Path(temporary).resolve() / 'release'
            self.assertEqual(release_tool.safe_release_base(path, self.root), path)
        with tempfile.TemporaryDirectory(prefix='hatifect-smapi-test.') as temporary:
            path = Path(temporary).resolve() / 'Mods'
            self.assertEqual(release_tool.isolated_mods_directory(path, self.root), path)
        for path in (self.root / 'Mods', self.workspace / 'Stardew Valley/Mods', self.root / '.smapi-test-other/Mods'):
            with self.subTest(path=path), self.assertRaises(release_tool.ReleaseError):
                release_tool.isolated_mods_directory(path, self.root)

    def test_isolated_deployment_replaces_runtime_and_preserves_other_mods(self) -> None:
        package = self.package()
        mods = self.root / '.smapi-test/Mods'
        sibling = mods / 'OtherMod/marker.txt'
        sibling.parent.mkdir(parents=True)
        sibling.write_text('unchanged', encoding='utf-8')
        deployed = release_tool.deploy_isolated(self.contract, package, mods, self.root)
        self.assertEqual(deployed, mods / 'Hatifect')
        self.assert_package_bytes(deployed)
        (deployed / 'old.tmp').write_text('old', encoding='utf-8')
        release_tool.deploy_isolated(self.contract, package, mods, self.root)
        self.assert_package_bytes(deployed)
        self.assertEqual(sibling.read_text(encoding='utf-8'), 'unchanged')
        normal_mods = self.workspace / 'Stardew Valley/Mods'
        with self.assertRaisesRegex(release_tool.ReleaseError, 'explicit isolated'):
            release_tool.deploy_isolated(self.contract, package, normal_mods, self.root)
        self.assertFalse(normal_mods.exists())

    def test_isolated_deployment_rejects_linked_destination_and_preserves_target(self) -> None:
        package = self.package()
        mods = self.root / '.smapi-test/Mods'
        mods.mkdir(parents=True)
        (mods / 'Hatifect').symlink_to(package, target_is_directory=True)
        with self.assertRaisesRegex(release_tool.ReleaseError, 'real directory'):
            release_tool.deploy_isolated(self.contract, package, mods, self.root)
        self.assert_package_bytes(package)
        self.assertTrue((mods / 'Hatifect').is_symlink())

    def test_publication_rename_failure_restores_the_previous_runtime(self) -> None:
        self.build_outputs()
        base = self.root / 'artifacts/package'
        package = release_tool.assemble_package(self.contract, base, self.root)
        original_rename = Path.rename

        def fail_replacement(path: Path, target: Path) -> Path:
            if path.name == 'Hatifect' and path.parent.name.startswith('.assemble-'):
                raise OSError('simulated replacement failure')
            return original_rename(path, target)

        with mock.patch.object(Path, 'rename', fail_replacement), self.assertRaisesRegex(OSError, 'replacement failure'):
            release_tool.assemble_package(self.contract, base, self.root)
        self.assert_package_bytes(package)
        self.assertEqual([path.name for path in base.iterdir()], ['Hatifect'])


class ReleaseCommandTests(ReleaseFixture):
    def setUp(self) -> None:
        super().setUp()
        self.build_outputs()
        tools = self.root / 'tools'
        tools.mkdir()
        shutil.copyfile(ROOT / 'release.sh', self.root / 'release.sh')
        shutil.copyfile(ROOT / 'tools/release.py', tools / 'release.py')
        self.log = self.workspace / 'calls.jsonl'
        stub = '''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
with open(os.environ['CALL_LOG'], 'a', encoding='utf-8') as output:
    output.write(json.dumps([Path(sys.argv[0]).name, *sys.argv[1:]]) + '\\n')
if Path(sys.argv[0]).name == os.environ.get('FAIL_TOOL'):
    raise SystemExit(23)
if Path(sys.argv[0]).name == 'rc_readiness.py':
    path = Path(sys.argv[3])
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({'TestsPassed': '--tests-passed' in sys.argv}))
'''
        for name in ('validation.py', 'hatifect-test', 'rc_readiness.py'):
            path = tools / name
            path.write_text(stub, encoding='utf-8')
            path.chmod(0o755)
        self.env = {key: value for key, value in os.environ.items() if not key.startswith('HATIFECT_')}
        self.env['CALL_LOG'] = str(self.log)

    def run_release(self, **environment: str) -> subprocess.CompletedProcess:
        return subprocess.run(['bash', str(self.root / 'release.sh')], cwd=self.workspace,
                              env=self.env | environment, capture_output=True, text=True, timeout=30)

    def calls(self) -> list[list[str]]:
        return [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()] if self.log.exists() else []

    def test_release_defaults_to_no_deploy_and_never_rebuilds_after_candidate_tests(self) -> None:
        normal_mods = self.workspace / 'Stardew Valley/Mods'
        result = self.run_release(HATIFECT_MODS_DIR=str(normal_mods))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls()
        self.assertEqual([call[0] for call in calls],
                         ['validation.py', 'hatifect-test', 'rc_readiness.py'])
        self.assertEqual(calls[0], ['validation.py', 'build', '--platform'])
        self.assertEqual(calls[1], ['hatifect-test', 'all', '--platform', '--no-build'])
        base = self.root / 'artifacts/package'
        self.assert_package_bytes(base / 'Hatifect')
        self.assertEqual(json.loads((base / 'artifacts/rc-build-evidence.json').read_text()), {'TestsPassed': True})
        self.assertTrue((base / 'artifacts' / self.contract['ArtifactName']).is_file())
        self.assertFalse(normal_mods.exists())
        self.assertIn('Deployment is disabled.', result.stdout)

    def test_skipping_tests_does_not_claim_test_completion(self) -> None:
        result = self.run_release(HATIFECT_SKIP_TESTS='1')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn('hatifect-test', [call[0] for call in self.calls()])
        evidence = self.root / 'artifacts/package/artifacts/rc-build-evidence.json'
        self.assertEqual(json.loads(evidence.read_text()), {'TestsPassed': False})

    def test_release_fails_before_build_or_output_on_unsafe_paths_and_invalid_flags(self) -> None:
        cases = ({'HATIFECT_RELEASE_BASE': str(self.root)}, {'HATIFECT_SKIP_TESTS': 'yes'},
                 {'HATIFECT_SKIP_DEPLOY': 'yes'}, {'HATIFECT_SKIP_DEPLOY': '0'},
                 {'HATIFECT_SKIP_DEPLOY': '0', 'HATIFECT_MODS_DIR': str(self.workspace / 'Stardew Valley/Mods')})
        for environment in cases:
            with self.subTest(environment=environment):
                result = self.run_release(**environment)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.calls(), [])
                self.assertFalse((self.root / 'artifacts').exists())

    def test_failed_build_or_tests_never_publish_a_package_or_passed_evidence(self) -> None:
        for tool in ('validation.py', 'hatifect-test'):
            with self.subTest(tool=tool):
                result = self.run_release(FAIL_TOOL=tool)
                self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
                self.assertEqual(self.calls()[-1][0], tool)
                self.assertNotIn('rc_readiness.py', [call[0] for call in self.calls()])
                self.assertFalse((self.root / 'artifacts').exists())
                self.log.unlink()

    def test_release_explicitly_deploys_only_to_an_isolated_mods_directory(self) -> None:
        mods = self.root / '.smapi-test/Mods'
        result = self.run_release(HATIFECT_SKIP_DEPLOY='0', HATIFECT_MODS_DIR=str(mods))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assert_package_bytes(mods / 'Hatifect')
        self.assert_package_bytes(self.root / 'artifacts/package/Hatifect')


if __name__ == '__main__':
    unittest.main()
