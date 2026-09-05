#!/usr/bin/env python3
"""Validate and assemble the current Hatifect runtime package without deploying by default."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
CONTRACT_PATH = ROOT / 'Hatifect.Release.json'
MODULE_IDS = {'Hatifect.UI', 'Hatifect.ChestsAnywhereOverlay', 'Hatifect.Flow'}
RETIRED_ASSEMBLIES = {'Hatifect.UI.Runtime', 'Hatifect.UI.Experience', 'Hatifect.UI',
                      'Hatifect.UI.Experience', 'Hatifect', 'Hatifect.Flow'}


class ReleaseError(RuntimeError):
    pass


def _unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ReleaseError(f'duplicate JSON key: {key}')
        result[key] = value
    return result


def _json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=_unique_object)
    except (OSError, ValueError) as error:
        raise ReleaseError(f'{path}: {error}') from error
    if not isinstance(value, dict):
        raise ReleaseError(f'{path}: expected a JSON object')
    return value


def _relative(value: object) -> PurePosixPath:
    if not isinstance(value, str) or not value or any(char in value for char in '\\:\0'):
        raise ReleaseError(f'invalid relative path: {value!r}')
    path = PurePosixPath(value)
    if path.is_absolute() or '..' in path.parts or str(path) != value or value == '.':
        raise ReleaseError(f'invalid relative path: {value!r}')
    if any(part.startswith('.') for part in path.parts):
        raise ReleaseError(f'hidden runtime path: {value!r}')
    return path


def _file(root: Path, relative: str) -> Path:
    path = root.joinpath(*_relative(relative).parts)
    for member in (path, *path.parents):
        if member == root:
            break
        if member.is_symlink():
            raise ReleaseError(f'symbolic link is not allowed: {member}')
    if not path.is_file():
        raise ReleaseError(f'missing file: {path}')
    return path


def normalized_dependencies(items: object) -> list[tuple[str, bool, str | None]]:
    if not isinstance(items, list):
        raise ReleaseError('Dependencies must be an array')
    result = []
    for item in items:
        if not isinstance(item, dict) or not isinstance(item.get('UniqueID'), str) or not item['UniqueID']:
            raise ReleaseError('dependency requires a UniqueID')
        required = item.get('IsRequired', True)
        minimum = item.get('MinimumVersion')
        if type(required) is not bool or (minimum is not None and (not isinstance(minimum, str) or not minimum)):
            raise ReleaseError('invalid dependency requirement/version')
        result.append((item['UniqueID'], required, minimum))
    if len({item[0] for item in result}) != len(result):
        raise ReleaseError('duplicate dependency UniqueID')
    return sorted(result)


def validate_contract(contract: dict) -> None:
    if type(contract.get('FormatVersion')) is not int or contract['FormatVersion'] != 1:
        raise ReleaseError('unsupported release contract format')
    for key in ('PackageRoot', 'ArtifactName', 'ReleaseId', 'TargetFramework', 'MinimumApiVersion'):
        if not isinstance(contract.get(key), str) or not contract[key].strip():
            raise ReleaseError(f'release contract requires {key}')
    for key in ('PackageRoot', 'ArtifactName'):
        if len(_relative(contract[key]).parts) != 1:
            raise ReleaseError(f'{key} must be a filename')
    if not contract['ArtifactName'].endswith('.zip'):
        raise ReleaseError('ArtifactName must be a ZIP filename')
    if contract['TargetFramework'] != 'net6.0':
        raise ReleaseError('runtime package must target the configured net6.0 game runtime')
    modules = contract.get('Modules')
    if not isinstance(modules, list) or not modules:
        raise ReleaseError('release contract requires Modules')
    ids, keys, paths = set(), set(), []
    for module in modules:
        if not isinstance(module, dict):
            raise ReleaseError('module must be an object')
        for key in ('Key', 'Name', 'Path', 'Project', 'Manifest', 'UniqueID', 'Version', 'EntryDll'):
            if not isinstance(module.get(key), str) or not module[key]:
                raise ReleaseError(f'module requires {key}')
        path = _relative(module['Path'])
        folded_path = PurePosixPath(str(path).casefold())
        if any(folded_path == prior or folded_path in prior.parents or prior in folded_path.parents for prior in paths):
            raise ReleaseError('module paths overlap')
        paths.append(folded_path)
        if module['UniqueID'] in ids or module['Key'] in keys:
            raise ReleaseError('duplicate module identity/key')
        ids.add(module['UniqueID'])
        keys.add(module['Key'])
        project = _relative(module['Project'])
        if project.suffix != '.csproj' or path not in project.parents:
            raise ReleaseError('module Project must be a csproj within its module directory')
        if _relative(module['Manifest']) != path / 'manifest.json':
            raise ReleaseError('module Manifest must belong to its module directory')
        root_files = module.get('AllowedRootFiles')
        runtime_files = module.get('AllowedRuntimeFiles', [])
        directories = module.get('AllowedDirectories', [])
        if not isinstance(root_files, list) or not isinstance(runtime_files, list) or not isinstance(directories, list):
            raise ReleaseError('module file inventory must be an array')
        for directory in directories:
            if len(_relative(directory).parts) != 1:
                raise ReleaseError('runtime content directory must be one path segment')
        if len({name.casefold() for name in directories}) != len(directories):
            raise ReleaseError('duplicate runtime content directory')
        all_files = root_files + runtime_files
        if len(set(str(name).casefold() for name in all_files)) != len(all_files):
            raise ReleaseError('duplicate runtime file inventory')
        for name in root_files:
            item = _relative(name)
            if len(item.parts) != 1 or (item.suffix not in {'.dll', '.json'} and name != 'LICENSE'):
                raise ReleaseError(f'unsupported module-root file: {name}')
            if item.stem.casefold() in {assembly.casefold() for assembly in RETIRED_ASSEMBLIES}:
                raise ReleaseError(f'retired runtime assembly: {name}')
            if name.casefold().startswith('hatifect.ui.') and module['UniqueID'] != 'Hatifect.UI':
                raise ReleaseError('UI runtime assemblies have exactly one owner: Hatifect.UI')
        for name in runtime_files:
            item = _relative(name)
            if len(item.parts) < 2 or item.parts[0] not in directories or item.suffix != '.json':
                raise ReleaseError(f'unsupported runtime content file: {name}')
        if 'manifest.json' not in root_files or module['EntryDll'] not in root_files or not module['EntryDll'].endswith('.dll'):
            raise ReleaseError('module inventory must contain its manifest and entry assembly')
        normalized_dependencies(module.get('Dependencies', []))
    if ids != MODULE_IDS:
        raise ReleaseError(f'current runtime module identities must be {sorted(MODULE_IDS)}')
    followers = contract.get('VersionFollowers', [])
    if not isinstance(followers, list):
        raise ReleaseError('VersionFollowers must be an array')
    follower_paths = set()
    for follower in followers:
        if not isinstance(follower, dict) or follower.get('Module') not in keys:
            raise ReleaseError('unknown VersionFollower module')
        project = _relative(follower.get('Project'))
        if project.suffix != '.csproj' or str(project).casefold() in follower_paths:
            raise ReleaseError('invalid or duplicate VersionFollower project')
        follower_paths.add(str(project).casefold())
    versions = {module['UniqueID']: module['Version'] for module in modules}
    graph = {}
    for module in modules:
        dependencies = normalized_dependencies(module.get('Dependencies', []))
        for identity, required, minimum in dependencies:
            if identity.startswith('Hatifect.') and identity not in versions:
                raise ReleaseError(f'unknown runtime dependency: {identity}')
            if identity in versions and (not required or minimum != versions[identity]):
                raise ReleaseError(f'first-party dependency version/requirement mismatch: {identity}')
        graph[module['UniqueID']] = [identity for identity, _, _ in dependencies if identity in versions]
    def visit(identity: str, stack: set[str]) -> None:
        if identity in stack:
            raise ReleaseError('runtime dependency cycle')
        for dependency in graph[identity]:
            visit(dependency, stack | {identity})
    for identity in graph:
        visit(identity, set())


def load_contract(path: Path | None = None) -> dict:
    contract = _json(path or CONTRACT_PATH)
    validate_contract(contract)
    return contract


def _project_value(path: Path, name: str) -> str:
    values = [item.text.strip() for item in ET.parse(path).getroot().iter(name) if item.text and item.text.strip()]
    if len(values) != 1 or '$(' in values[0]:
        raise ReleaseError(f'{path}: expected exactly one literal {name}')
    return values[0]


def project_version(path: Path) -> str:
    return _project_value(path, 'Version')


def project_assembly_name(path: Path) -> str:
    return _project_value(path, 'AssemblyName')


def _manifest(module: dict, contract: dict, path: Path) -> None:
    manifest = _json(path)
    expected = {key: module[key] for key in ('Name', 'UniqueID', 'Version', 'EntryDll')}
    expected['MinimumApiVersion'] = contract['MinimumApiVersion']
    if 'MinimumGameVersion' in module:
        expected['MinimumGameVersion'] = module['MinimumGameVersion']
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise ReleaseError(f'{path}: {key}={manifest.get(key)!r}, expected {value!r}')
    if normalized_dependencies(manifest.get('Dependencies', [])) != normalized_dependencies(module.get('Dependencies', [])):
        raise ReleaseError(f'{path}: dependency contract mismatch')


def verify_i18n(module_root: Path, errors: list[str]) -> None:
    directory = module_root / 'i18n'
    if not directory.exists():
        return
    try:
        default = _json(_file(module_root, 'i18n/default.json'))
        if not all(isinstance(value, str) for value in default.values()):
            raise ReleaseError('localization values must be strings')
        for locale in sorted(directory.glob('*.json')):
            values = _json(_file(module_root, 'i18n/' + locale.name))
            if set(values) != set(default) or not all(isinstance(value, str) for value in values.values()):
                raise ReleaseError(f'{locale}: localization keys/values do not match default.json')
    except ReleaseError as error:
        errors.append(str(error))


def verify_source(contract: dict, root: Path | None = None) -> None:
    validate_contract(contract)
    root = root or ROOT
    errors = []
    for module in contract['Modules']:
        try:
            project = _file(root, module['Project'])
            if project_version(project) != module['Version']:
                raise ReleaseError(f'{project}: project/manifest Version mismatch')
            if project_assembly_name(project) + '.dll' != module['EntryDll']:
                raise ReleaseError(f'{project}: entry assembly mismatch')
            _manifest(module, contract, _file(root, module['Manifest']))
            for name in module['AllowedRootFiles'] + module.get('AllowedRuntimeFiles', []):
                if not name.endswith('.dll'):
                    path = _file(root, module['Path'] + '/' + name)
                    if path.suffix == '.json':
                        _json(path)
            verify_i18n(root / module['Path'], errors)
        except (ReleaseError, OSError, ET.ParseError) as error:
            errors.append(str(error))
    modules = {module['Key']: module for module in contract['Modules']}
    followers = contract.get('VersionFollowers', [])
    for follower in followers:
        try:
            if follower.get('Module') not in modules:
                raise ReleaseError('unknown VersionFollower module')
            if project_version(_file(root, follower['Project'])) != modules[follower['Module']]['Version']:
                raise ReleaseError(f"{follower['Project']}: version follower mismatch")
        except (ReleaseError, OSError, ET.ParseError) as error:
            errors.append(str(error))
    try:
        packages = _json(_file(root, 'Hatifect.UI.Packages.json'))
        package_dlls = {item['Id'] + '.dll' for item in packages['Packages']}
        ui = next(module for module in contract['Modules'] if module['UniqueID'] == 'Hatifect.UI')
        declared_dlls = {name for name in ui['AllowedRootFiles'] if name.endswith('.dll')}
        if package_dlls != declared_dlls or packages['Version'] != ui['Version']:
            raise ReleaseError('UI package catalog and runtime ownership/version differ')
    except (ReleaseError, KeyError, TypeError) as error:
        errors.append(str(error))
    if errors:
        raise ReleaseError('source release contract failed:\n- ' + '\n- '.join(errors))


def _managed_image(data: bytes) -> bool:
    """Check bounded PE/CLI header structure; this is not a signature/authenticity check."""
    try:
        if data[:2] != b'MZ':
            return False
        pe = struct.unpack_from('<I', data, 60)[0]
        if data[pe:pe + 4] != b'PE\0\0':
            return False
        count, = struct.unpack_from('<H', data, pe + 6)
        size, = struct.unpack_from('<H', data, pe + 20)
        optional = pe + 24
        magic, = struct.unpack_from('<H', data, optional)
        directories = {0x10B: 96, 0x20B: 112}.get(magic)
        if directories is None or count < 1 or count > 96 or size < directories + 120:
            return False
        cli, cli_size = struct.unpack_from('<II', data, optional + directories + 112)
        if cli_size < 72:
            return False
        def offset(rva: int, length: int) -> int:
            for index in range(count):
                section = optional + size + index * 40
                address, raw_size, raw_offset = struct.unpack_from('<III', data, section + 12)
                delta = rva - address
                if 0 <= delta and delta + length <= raw_size and raw_offset + delta + length <= len(data):
                    return raw_offset + delta
            raise ValueError('RVA is outside retained section bytes')
        header = offset(cli, 72)
        metadata, length = struct.unpack_from('<II', data, header + 8)
        metadata_offset = offset(metadata, length)
        return length >= 4 and data[metadata_offset:metadata_offset + 4] == b'BSJB'
    except (struct.error, ValueError):
        return False


def _walk(root: Path) -> tuple[set[str], set[str]]:
    if root.is_symlink() or not root.is_dir():
        raise ReleaseError(f'package root must be a real directory: {root}')
    files, directories = set(), set()
    for directory, names, filenames in os.walk(root, followlinks=False):
        for name in names + filenames:
            path = Path(directory) / name
            relative = path.relative_to(root).as_posix()
            mode = path.lstat().st_mode
            if stat.S_ISLNK(mode):
                raise ReleaseError(f'symbolic link in runtime package: {relative}')
            if stat.S_ISDIR(mode):
                directories.add(relative)
            elif stat.S_ISREG(mode):
                files.add(relative)
            else:
                raise ReleaseError(f'non-regular runtime entry: {relative}')
    return files, directories


def _inventory(contract: dict) -> set[str]:
    return {module['Path'] + '/' + name for module in contract['Modules']
            for name in module['AllowedRootFiles'] + module.get('AllowedRuntimeFiles', [])}


def verify_package(contract: dict, package_root: Path) -> None:
    validate_contract(contract)
    if package_root.name != contract['PackageRoot']:
        raise ReleaseError(f"package root must be named {contract['PackageRoot']}")
    actual_files, actual_directories = _walk(package_root)
    expected_files = _inventory(contract)
    expected_directories = {str(parent) for name in expected_files for parent in PurePosixPath(name).parents if str(parent) != '.'}
    errors = []
    for label, difference in (('missing runtime files', expected_files - actual_files),
                              ('unexpected runtime files', actual_files - expected_files),
                              ('unexpected runtime directories', actual_directories - expected_directories)):
        if difference:
            errors.append(label + ': ' + ', '.join(sorted(difference)))
    for module in contract['Modules']:
        try:
            base = package_root / module['Path']
            _manifest(module, contract, _file(base, 'manifest.json'))
            verify_i18n(base, errors)
            for name in module['AllowedRootFiles'] + module.get('AllowedRuntimeFiles', []):
                path = _file(base, name)
                if path.suffix == '.dll':
                    data = path.read_bytes()
                    if not _managed_image(data):
                        errors.append(f'invalid managed PE image: {path.relative_to(package_root)}')
                    tokens = RETIRED_ASSEMBLIES | {'UiStyleParser', 'FlowObject'}
                    if module['UniqueID'] == 'Hatifect.ChestsAnywhereOverlay':
                        tokens = tokens | {'Hatifect.Flow'}
                    for token in sorted(tokens):
                        if any((token + '\0').encode(encoding) in data for encoding in ('utf-8', 'utf-16-le')):
                            errors.append(f'retired binary dependency/type {token}: {path.relative_to(package_root)}')
                elif path.suffix == '.json':
                    _json(path)
        except ReleaseError as error:
            errors.append(str(error))
    if errors:
        raise ReleaseError('runtime package contract failed:\n- ' + '\n- '.join(errors))


def safe_release_base(path: Path, root: Path | None = None) -> Path:
    root = (root or ROOT).resolve()
    resolved = path.expanduser().resolve()
    temporary = {Path(tempfile.gettempdir()).resolve(), Path('/tmp').resolve()}
    allowed = resolved == root / 'artifacts' / 'package' or (
        resolved.name == 'release' and resolved.parent.name.startswith('hatifect-live-release.')
        and resolved.parent.parent in temporary)
    if not allowed or path.is_symlink():
        raise ReleaseError(f'unsafe staging root: {path}')
    return resolved


def _publish(candidate: Path, destination: Path) -> None:
    if destination.exists():
        _walk(destination)
        backup = candidate.parent / 'previous-runtime'
        destination.rename(backup)
        try:
            candidate.rename(destination)
        except OSError:
            backup.rename(destination)
            raise
        shutil.rmtree(backup)
    elif destination.is_symlink():
        raise ReleaseError(f'symbolic link at runtime destination: {destination}')
    else:
        candidate.rename(destination)


def assemble_package(contract: dict, release_base: Path, root: Path | None = None) -> Path:
    root = root or ROOT
    verify_source(contract, root)
    base = safe_release_base(release_base, root)
    base.mkdir(parents=True, exist_ok=True)
    package = base / contract['PackageRoot']
    with tempfile.TemporaryDirectory(prefix='.assemble-', dir=base) as temporary:
        candidate = Path(temporary) / contract['PackageRoot']
        candidate.mkdir()
        for module in contract['Modules']:
            output = PurePosixPath(module['Project']).parent / 'bin' / 'Release' / contract['TargetFramework']
            for name in module['AllowedRootFiles'] + module.get('AllowedRuntimeFiles', []):
                source = _file(root, str(output / name) if name.endswith('.dll') else module['Path'] + '/' + name)
                destination = candidate / module['Path'] / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, destination)
        verify_package(contract, candidate)
        _publish(candidate, package)
    return package


def isolated_mods_directory(path: Path, root: Path | None = None) -> Path:
    root = (root or ROOT).resolve()
    resolved = path.expanduser().resolve()
    parent = resolved.parent
    temporary = {Path(tempfile.gettempdir()).resolve(), Path('/tmp').resolve()}
    local = parent == root / '.smapi-test' or root / '.smapi-test' in parent.parents
    isolated = parent.parent in temporary and parent.name.startswith('hatifect-smapi-test.')
    if resolved.name != 'Mods' or not (local or isolated) or path.is_symlink():
        raise ReleaseError(f'deployment requires an explicit isolated Mods directory: {path}')
    return resolved


def deploy_isolated(contract: dict, package_root: Path, mods_directory: Path, root: Path | None = None) -> Path:
    verify_package(contract, package_root)
    mods = isolated_mods_directory(mods_directory, root)
    mods.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.deploy-', dir=mods) as temporary:
        candidate = Path(temporary) / contract['PackageRoot']
        shutil.copytree(package_root, candidate)
        verify_package(contract, candidate)
        destination = mods / contract['PackageRoot']
        _publish(candidate, destination)
    return destination


def make_archive(contract: dict, package_root: Path, output_dir: Path) -> tuple[Path, Path]:
    verify_package(contract, package_root)
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / contract['ArtifactName']
    checksum = archive.with_suffix('.zip.sha256')
    if output_dir.is_symlink() or archive.is_symlink() or checksum.is_symlink():
        raise ReleaseError('archive output must not be a symbolic link')
    with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as output:
        for relative in sorted(_inventory(contract)):
            info = zipfile.ZipInfo(contract['PackageRoot'] + '/' + relative, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            output.writestr(info, (package_root / relative).read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    checksum.write_text(hashlib.sha256(archive.read_bytes()).hexdigest() + '  ' + archive.name + '\n', encoding='utf-8')
    return archive, checksum


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('verify-source', 'list-projects'):
        commands.add_parser(name)
    for name, argument in (('check-output', 'release_base'), ('check-isolated', 'mods_directory'),
                           ('assemble', 'release_base'), ('verify-package', 'package_root')):
        commands.add_parser(name).add_argument(argument, type=Path)
    for name, second in (('make-archive', 'output_dir'), ('deploy-isolated', 'mods_directory')):
        command = commands.add_parser(name)
        command.add_argument('package_root', type=Path)
        command.add_argument(second, type=Path)
    args = parser.parse_args()
    try:
        if args.command == 'check-output':
            print(safe_release_base(args.release_base))
            return 0
        if args.command == 'check-isolated':
            print(isolated_mods_directory(args.mods_directory))
            return 0
        contract = load_contract()
        if args.command in {'verify-source', 'list-projects'}:
            verify_source(contract)
            if args.command == 'list-projects':
                print('\n'.join(module['Project'] for module in contract['Modules']))
            else:
                print('release source contract OK: ' + contract['ReleaseId'])
        elif args.command == 'verify-package':
            verify_package(contract, args.package_root)
            print('runtime package contract OK: ' + str(args.package_root))
        elif args.command == 'assemble':
            print(assemble_package(contract, args.release_base))
        elif args.command == 'make-archive':
            for result in make_archive(contract, args.package_root, args.output_dir):
                print(result)
        elif args.command == 'deploy-isolated':
            print(deploy_isolated(contract, args.package_root, args.mods_directory))
    except (ReleaseError, OSError, ET.ParseError) as error:
        print('release verification failed: ' + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
