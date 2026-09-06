#!/usr/bin/env python3
"""Versioned, owned isolated-save fixture provisioning for live acceptance."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
import re
import shutil
import stat
import tempfile
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


FIXTURE_SCHEMA_VERSION = 2
LEGACY_BOOTSTRAP_OWNER_SCHEMA_VERSION = 1
FIXTURE_ID = "hatifect-golden-save"
SYNTHETIC_PLAYER_NAME = "HatifectHarness"
SYNTHETIC_FARM_NAME = "Automation"
SYNTHETIC_FAVORITE_THING = "Determinism"
SYNTHETIC_UNIQUE_ID = 4242424242
BOOTSTRAP_SAVE_NAME = f"{SYNTHETIC_PLAYER_NAME}_{SYNTHETIC_UNIQUE_ID}"
ACCEPTANCE_STORAGE_LOCATION_NAME = "Farm"
ACCEPTANCE_STORAGE_TILE_X = 64
ACCEPTANCE_STORAGE_TILE_Y = 15
ACCEPTANCE_STORAGE_ITEM_ID = "(O)388"
ACCEPTANCE_STORAGE_ITEM_STACK = 10
BOOTSTRAP_EXISTING_FIXTURE_SENTINEL = "HARNESS-SAVE-FIXTURE-EXISTS"
PRIVATE_DIRECTORY_MODE = 0o700
PRIVATE_FILE_MODE = 0o600
MAX_DOCUMENT_BYTES = 256 * 1024
MANIFEST_FIELDS = {
    "fixtureSchemaVersion",
    "fixtureId",
    "runtimeId",
    "stardewVersion",
    "smapiVersion",
    "stardewAssemblySha256",
    "smapiAssemblySha256",
    "saveDirectoryName",
    "syntheticIdentity",
    "files",
    "checksum",
    "reloadVerified",
    "acceptanceStorage",
    "bootstrapRunId",
    "createdAtUtc",
}
IDENTITY_FIELDS = {"playerName", "farmName", "favoriteThing", "uniqueMultiplayerId"}
FILE_FIELDS = {"path", "size", "sha256"}
OWNER_FIELDS = {
    "fixtureSchemaVersion", "fixtureId", "runtimeId", "runId", "state", "createdAtUtc"
}
RECEIPT_FIELDS = {
    "fixtureSchemaVersion",
    "runId",
    "savePath",
    "playerName",
    "farmName",
    "favoriteThing",
    "uniqueMultiplayerId",
    "stardewVersion",
    "smapiVersion",
    "reloadVerified",
    "acceptanceStorage",
}
RESERVATION_FIELDS = {
    "fixtureSchemaVersion", "fixtureId", "runtimeId", "runId", "targetPath", "createdAtUtc"
}
ACCEPTANCE_STORAGE_FIELDS = {"locationName", "tile", "chest", "contents"}
ACCEPTANCE_STORAGE_TILE_FIELDS = {"x", "y"}
ACCEPTANCE_STORAGE_CHEST_FIELDS = {
    "kind",
    "playerChest",
    "fridge",
    "giftbox",
    "globalInventoryId",
    "specialChestType",
}
ACCEPTANCE_STORAGE_CONTENT_FIELDS = {"qualifiedItemId", "stack"}


class SaveProvisioningError(ValueError):
    def __init__(self, assertion_id: str, message: str) -> None:
        super().__init__(message)
        self.assertion_id = assertion_id


def _timestamp() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _read_json(path: Path) -> Any:
    size = path.stat().st_size
    if size <= 0 or size > MAX_DOCUMENT_BYTES:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", f"Invalid save document size: {path}")
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def _atomic_write_json(path: Path, payload: dict[str, Any], *, replace: bool = False) -> None:
    parent = _real_directory(path.parent)
    path = parent / path.name
    encoded = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
    if len(encoded) > MAX_DOCUMENT_BYTES:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", f"Save document is too large: {path}")
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        os.fchmod(descriptor, PRIVATE_FILE_MODE)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        if replace:
            os.replace(temporary, path)
        else:
            try:
                os.link(temporary, path)
            except FileExistsError as error:
                raise SaveProvisioningError(
                    "HARNESS-SAVE-COLLISION", f"Refusing to overwrite existing save state: {path}"
                ) from error
            temporary.unlink()
        descriptor = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


def _real_directory(path: Path, *, create: bool = False) -> Path:
    if create:
        path.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
        raise SaveProvisioningError("HARNESS-SAVE-PATH", f"Unsafe isolated-save directory: {path}")
    os.chmod(path, PRIVATE_DIRECTORY_MODE)
    return path.resolve(strict=True)


def _owned_directory_chain(root: Path, *parts: str) -> Path:
    current = _real_directory(root)
    for part in parts:
        if not part or Path(part).name != part:
            raise SaveProvisioningError("HARNESS-SAVE-PATH", "Invalid isolated-save directory segment.")
        candidate = current / part
        try:
            candidate.mkdir(mode=PRIVATE_DIRECTORY_MODE)
        except FileExistsError:
            pass
        current = _real_directory(candidate)
    return current


def _contained(root: Path, candidate: Path) -> Path:
    resolved_root = root.resolve(strict=True)
    resolved = candidate.resolve(strict=False)
    try:
        resolved.relative_to(resolved_root)
    except ValueError as error:
        raise SaveProvisioningError(
            "HARNESS-SAVE-PATH", f"Save path escapes isolated ownership root: {candidate}"
        ) from error
    return resolved


def _canonical_run_id(run_id: str) -> str:
    try:
        parsed = uuid.UUID(run_id)
    except (ValueError, TypeError) as error:
        raise SaveProvisioningError("HARNESS-SAVE-RUN-ID", "Save run ID must be a canonical UUID.") from error
    if str(parsed) != run_id:
        raise SaveProvisioningError("HARNESS-SAVE-RUN-ID", "Save run ID must be a canonical UUID.")
    return run_id


def _save_root(isolated_root: Path) -> Path:
    return _owned_directory_chain(isolated_root, "config", "StardewValley", "Saves")


def _fixture_parent(isolated_root: Path) -> Path:
    return _owned_directory_chain(isolated_root, "fixtures", "saves", f"v{FIXTURE_SCHEMA_VERSION}")


def _runtime_fingerprint(smapi_path: Path) -> dict[str, str]:
    smapi = smapi_path.resolve(strict=True)
    if not os.access(smapi, os.X_OK):
        raise SaveProvisioningError("HARNESS-SAVE-RUNTIME", f"SMAPI executable is unavailable: {smapi}")
    game = smapi.parent / "Stardew Valley.dll"
    api = smapi.parent / "StardewModdingAPI.dll"
    if not game.is_file() or game.is_symlink() or not api.is_file() or api.is_symlink():
        raise SaveProvisioningError(
            "HARNESS-SAVE-RUNTIME", "The configured SMAPI directory has no real Stardew/SMAPI assemblies."
        )
    game_sha = _sha256(game)
    api_sha = _sha256(api)
    runtime_id = hashlib.sha256(f"{game_sha}\n{api_sha}\n".encode("ascii")).hexdigest()[:24]
    return {
        "runtimeId": runtime_id,
        "stardewAssemblySha256": game_sha,
        "smapiAssemblySha256": api_sha,
    }


def _inventory(save: Path) -> list[dict[str, Any]]:
    items: list[dict[str, Any]] = []
    for path in sorted(save.rglob("*")):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", f"Save fixture contains a symlink: {path}")
        if stat.S_ISDIR(info.st_mode):
            continue
        if not stat.S_ISREG(info.st_mode):
            raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", f"Save fixture contains a special file: {path}")
        relative = path.relative_to(save).as_posix()
        if relative == ".hatifect-save-owner.json":
            continue
        items.append({"path": relative, "size": info.st_size, "sha256": _sha256(path)})
    if not items:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Save fixture contains no game save files.")
    return items


def _inventory_checksum(files: list[dict[str, Any]]) -> str:
    digest = hashlib.sha256()
    for item in files:
        digest.update(item["path"].encode("utf-8"))
        digest.update(b"\0")
        digest.update(str(item["size"]).encode("ascii"))
        digest.update(b"\0")
        digest.update(item["sha256"].encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def _fixture_path(isolated_root: Path, runtime_id: str) -> Path:
    if not re.fullmatch(r"[0-9a-f]{24}", runtime_id):
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Invalid save runtime identity.")
    return _fixture_parent(isolated_root) / f"{FIXTURE_ID}-{runtime_id}"


def _validate_fixture_for_runtime(
    isolated_root: Path,
    runtime_id: str,
    fingerprint: dict[str, str] | None = None,
) -> tuple[dict[str, Any], Path]:
    fixture = _fixture_path(isolated_root, runtime_id)
    if not fixture.exists():
        raise SaveProvisioningError(
            "HARNESS-SAVE-MISSING", "The requested versioned golden save fixture is unavailable."
        )
    fixture = _real_directory(fixture)
    if (fixture / ".hatifect-fixture-building.json").exists():
        raise SaveProvisioningError(
            "HARNESS-SAVE-BOOTSTRAP-INTERRUPTED", "Golden save fixture publication was interrupted."
        )
    manifest_path = fixture / "manifest.json"
    try:
        manifest = _read_json(manifest_path)
    except (OSError, json.JSONDecodeError) as error:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", f"Golden save manifest is unreadable: {error}") from error
    if not isinstance(manifest, dict) or set(manifest) != MANIFEST_FIELDS:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Golden save manifest field set is invalid.")
    identity = manifest["syntheticIdentity"]
    files = manifest["files"]
    acceptance_storage = manifest["acceptanceStorage"]
    stardew_sha = manifest["stardewAssemblySha256"]
    smapi_sha = manifest["smapiAssemblySha256"]
    derived_runtime_id = (
        hashlib.sha256(f"{stardew_sha}\n{smapi_sha}\n".encode("ascii")).hexdigest()[:24]
        if isinstance(stardew_sha, str)
        and re.fullmatch(r"[0-9a-f]{64}", stardew_sha)
        and isinstance(smapi_sha, str)
        and re.fullmatch(r"[0-9a-f]{64}", smapi_sha)
        else None
    )
    if (
        manifest["fixtureSchemaVersion"] != FIXTURE_SCHEMA_VERSION
        or manifest["fixtureId"] != FIXTURE_ID
        or manifest["runtimeId"] != runtime_id
        or derived_runtime_id != runtime_id
        or (
            fingerprint is not None
            and (
                stardew_sha != fingerprint["stardewAssemblySha256"]
                or smapi_sha != fingerprint["smapiAssemblySha256"]
            )
        )
        or not isinstance(manifest["stardewVersion"], str)
        or not manifest["stardewVersion"]
        or not isinstance(manifest["smapiVersion"], str)
        or not manifest["smapiVersion"]
        or manifest["reloadVerified"] is not True
        or not _is_acceptance_storage(acceptance_storage)
        or not isinstance(identity, dict)
        or set(identity) != IDENTITY_FIELDS
        or identity != _synthetic_identity()
        or not isinstance(files, list)
        or not files
        or any(not isinstance(item, dict) or set(item) != FILE_FIELDS for item in files)
        or not isinstance(manifest["checksum"], str)
        or not re.fullmatch(r"[0-9a-f]{64}", manifest["checksum"])
        or not isinstance(manifest["createdAtUtc"], str)
        or not manifest["createdAtUtc"]
    ):
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Golden save manifest values are invalid.")
    try:
        _canonical_run_id(manifest["bootstrapRunId"])
    except SaveProvisioningError as error:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Golden save bootstrap identity is invalid.") from error
    name = manifest["saveDirectoryName"]
    if not isinstance(name, str) or not name or Path(name).name != name:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Golden save directory identity is invalid.")
    save = _real_directory(fixture / "save" / name)
    observed = _inventory(save)
    if observed != files or _inventory_checksum(observed) != manifest["checksum"]:
        raise SaveProvisioningError("HARNESS-SAVE-CORRUPT", "Golden save checksum validation failed.")
    return manifest, save


def validate_fixture(isolated_root: Path, smapi_path: Path) -> tuple[dict[str, Any], Path]:
    fingerprint = _runtime_fingerprint(smapi_path)
    fixture = _fixture_path(isolated_root, fingerprint["runtimeId"])
    if not fixture.exists():
        other = list(_fixture_parent(isolated_root).glob(f"{FIXTURE_ID}-*"))
        code = "HARNESS-SAVE-VERSION-MISMATCH" if other else "HARNESS-SAVE-MISSING"
        raise SaveProvisioningError(code, "No compatible versioned golden save fixture is available.")
    return _validate_fixture_for_runtime(isolated_root, fingerprint["runtimeId"], fingerprint)


def _synthetic_identity() -> dict[str, Any]:
    return {
        "playerName": SYNTHETIC_PLAYER_NAME,
        "farmName": SYNTHETIC_FARM_NAME,
        "favoriteThing": SYNTHETIC_FAVORITE_THING,
        "uniqueMultiplayerId": SYNTHETIC_UNIQUE_ID,
    }


def _acceptance_storage() -> dict[str, Any]:
    return {
        "locationName": ACCEPTANCE_STORAGE_LOCATION_NAME,
        "tile": {"x": ACCEPTANCE_STORAGE_TILE_X, "y": ACCEPTANCE_STORAGE_TILE_Y},
        "chest": {
            "kind": "ordinary-player-chest",
            "playerChest": True,
            "fridge": False,
            "giftbox": False,
            "globalInventoryId": None,
            "specialChestType": "None",
        },
        "contents": [{"qualifiedItemId": ACCEPTANCE_STORAGE_ITEM_ID, "stack": ACCEPTANCE_STORAGE_ITEM_STACK}],
    }


def _is_acceptance_storage(document: Any) -> bool:
    if not isinstance(document, dict) or set(document) != ACCEPTANCE_STORAGE_FIELDS:
        return False
    tile = document["tile"]
    chest = document["chest"]
    contents = document["contents"]
    return (
        document["locationName"] == ACCEPTANCE_STORAGE_LOCATION_NAME
        and isinstance(tile, dict)
        and set(tile) == ACCEPTANCE_STORAGE_TILE_FIELDS
        and isinstance(tile["x"], int)
        and not isinstance(tile["x"], bool)
        and tile["x"] == ACCEPTANCE_STORAGE_TILE_X
        and isinstance(tile["y"], int)
        and not isinstance(tile["y"], bool)
        and tile["y"] == ACCEPTANCE_STORAGE_TILE_Y
        and isinstance(chest, dict)
        and set(chest) == ACCEPTANCE_STORAGE_CHEST_FIELDS
        and chest == _acceptance_storage()["chest"]
        and isinstance(contents, list)
        and len(contents) == 1
        and isinstance(contents[0], dict)
        and set(contents[0]) == ACCEPTANCE_STORAGE_CONTENT_FIELDS
        and isinstance(contents[0]["stack"], int)
        and not isinstance(contents[0]["stack"], bool)
        and contents == _acceptance_storage()["contents"]
    )


def _working_name(run_id: str, scenario_id: str = "") -> str:
    token = uuid.UUID(_canonical_run_id(run_id)).hex
    # Stardew loads the base before '_' and saves base + '_' + world identity.
    return f"HatifectHarness{token}_4242424242" if scenario_id in {"flow.chest.roundtrip", "flow.chest.crash-after-save", "flow.chest.crash-after-delivery", "flow.chest.crash-after-unsaved-extraction", "flow.chest.crash-after-unsaved-delivery", "flow.chest.cancellation"} else f"HatifectHarness_{token}"


def plan_working_copy(isolated_root: Path, smapi_path: Path, run_id: str, scenario_id: str = "") -> Path:
    run_id = _canonical_run_id(run_id)
    manifest, _ = validate_fixture(isolated_root, smapi_path)
    destination = _save_root(isolated_root) / _working_name(run_id, scenario_id)
    if destination.exists():
        try:
            _validate_owner(
                destination,
                manifest["runtimeId"],
                run_id,
                allowed_states=("Provisioning", "Ready"),
            )
        except SaveProvisioningError as error:
            raise SaveProvisioningError("HARNESS-SAVE-COLLISION", str(error)) from error
        raise SaveProvisioningError(
            "HARNESS-SAVE-INTERRUPTED", f"An owned working copy already exists for run {run_id}."
        )
    return destination


def _owner(runtime_id: str, run_id: str, state: str = "Ready") -> dict[str, Any]:
    return {
        "fixtureSchemaVersion": FIXTURE_SCHEMA_VERSION,
        "fixtureId": FIXTURE_ID,
        "runtimeId": runtime_id,
        "runId": _canonical_run_id(run_id),
        "state": state,
        "createdAtUtc": _timestamp(),
    }


def _validate_owner(
    path: Path,
    runtime_id: str,
    run_id: str,
    *,
    allowed_states: tuple[str, ...] = ("Ready",),
    allowed_schema_versions: tuple[int, ...] = (FIXTURE_SCHEMA_VERSION,),
) -> dict[str, Any]:
    try:
        document = _read_json(path / ".hatifect-save-owner.json")
    except (OSError, json.JSONDecodeError) as error:
        raise SaveProvisioningError(
            "HARNESS-SAVE-OWNERSHIP", f"Working save has no valid ownership marker: {path}"
        ) from error
    if (
        not isinstance(document, dict)
        or set(document) != OWNER_FIELDS
        or document["fixtureSchemaVersion"] not in allowed_schema_versions
        or document["fixtureId"] != FIXTURE_ID
        or document["runtimeId"] != runtime_id
        or document["runId"] != run_id
        or document["state"] not in allowed_states
    ):
        raise SaveProvisioningError("HARNESS-SAVE-OWNERSHIP", "Working save ownership does not match this run.")
    return document


def prepare_working_copy(isolated_root: Path, smapi_path: Path, run_id: str, scenario_id: str = "") -> Path:
    run_id = _canonical_run_id(run_id)
    manifest, source = validate_fixture(isolated_root, smapi_path)
    root = _save_root(isolated_root)
    destination = plan_working_copy(isolated_root, smapi_path, run_id, scenario_id)
    source_before = _inventory(source)
    try:
        destination.mkdir(mode=PRIVATE_DIRECTORY_MODE)
        _atomic_write_json(
            destination / ".hatifect-save-owner.json",
            _owner(manifest["runtimeId"], run_id, "Provisioning"),
        )
        for source_item in source.iterdir():
            target_item = destination / source_item.name
            if source_item.is_dir():
                shutil.copytree(source_item, target_item, symlinks=False)
            else:
                shutil.copy2(source_item, target_item, follow_symlinks=False)
        source_name = source.name
        for path in sorted(destination.iterdir()):
            if path.is_file() and (path.name == source_name or path.name.startswith(source_name + "_")):
                path.rename(path.with_name(destination.name + path.name[len(source_name):]))
        _atomic_write_json(
            destination / ".hatifect-save-owner.json",
            _owner(manifest["runtimeId"], run_id),
            replace=True,
        )
    except FileExistsError as error:
        raise SaveProvisioningError("HARNESS-SAVE-COLLISION", f"Working save path appeared concurrently: {destination}") from error
    except Exception:
        if destination.exists():
            try:
                _validate_owner(
                    destination,
                    manifest["runtimeId"],
                    run_id,
                    allowed_states=("Provisioning", "Ready"),
                )
            except SaveProvisioningError:
                pass
            else:
                shutil.rmtree(destination)
        raise
    if _inventory(source) != source_before:
        raise SaveProvisioningError("HARNESS-SAVE-IMMUTABILITY", "Golden save changed during provisioning.")
    validate_working_copy(isolated_root, destination, manifest["runtimeId"], run_id, scenario_id)
    return destination


def validate_working_copy(
    isolated_root: Path, save_path: Path, runtime_id: str, run_id: str, scenario_id: str = ""
) -> Path:
    root = _save_root(isolated_root)
    candidate = _contained(root, save_path)
    if candidate.parent != root or candidate.name != _working_name(run_id, scenario_id):
        raise SaveProvisioningError("HARNESS-SAVE-PATH", "Working save is not this run's direct isolated child.")
    candidate = _real_directory(candidate)
    _validate_owner(candidate, runtime_id, run_id)
    return candidate


def flow_secondary_run_id(run_id: str) -> str:
    """Fixed second-copy identity for flow.save.isolation; no new request authority."""
    return str(uuid.UUID(int=uuid.UUID(_canonical_run_id(run_id)).int ^ 1))


def _reseed_flow_secondary(isolated_root: Path, save: Path, runtime_id: str, run_id: str) -> None:
    candidate = validate_working_copy(isolated_root, save, runtime_id, run_id)
    primary = candidate / candidate.name
    info = primary.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_uid != os.getuid() or info.st_size > 16 * 1024 * 1024:
        raise SaveProvisioningError("HARNESS-SAVE-IDENTITY", "Unsafe secondary save XML file.")
    original = primary.read_bytes()
    old = b"<uniqueIDForThisGame>4242424242</uniqueIDForThisGame>"
    new = b"<uniqueIDForThisGame>4242424243</uniqueIDForThisGame>"
    try:
        if b"<!DOCTYPE" in original or b"<!ENTITY" in original:
            raise ValueError("XML declarations are not permitted.")
        document = ET.fromstring(original)
        direct = document.findall("uniqueIDForThisGame")
        if (document.tag != "SaveGame" or len(direct) != 1
                or len(list(document.iter("uniqueIDForThisGame"))) != 1
                or direct[0].text != "4242424242" or len(direct[0]) != 0
                or direct[0].attrib or original.count(old) != 1):
            raise ValueError("Expected exactly one original synthetic game identity.")
        updated = original.replace(old, new, 1)
        if ET.fromstring(updated).find("uniqueIDForThisGame").text != "4242424243":
            raise ValueError("The replacement must change the actual game identity element.")
    except (ET.ParseError, ValueError) as error:
        raise SaveProvisioningError("HARNESS-SAVE-IDENTITY", f"Invalid secondary save XML: {error}") from error
    # Preserve namespace prefixes and xsi:type QName values byte-for-byte.
    fd, temporary = tempfile.mkstemp(prefix=".flow-identity-", dir=candidate)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(updated)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, primary)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def prepare_flow_secondary(isolated_root: Path, smapi_path: Path, parent_run_id: str) -> Path:
    run_id = flow_secondary_run_id(parent_run_id)
    manifest, _ = validate_fixture(isolated_root, smapi_path)
    # A collision fails before we acquire cleanup authority over this path.
    destination = prepare_working_copy(isolated_root, smapi_path, run_id)
    try:
        _reseed_flow_secondary(isolated_root, destination, manifest["runtimeId"], run_id)
    except BaseException as error:
        try:
            cleanup_working_copy(isolated_root, destination, manifest["runtimeId"], run_id)
        except Exception as cleanup_error:
            raise SaveProvisioningError("HARNESS-SAVE-CLEANUP", f"Secondary preparation failed: {error}; cleanup failed: {cleanup_error}") from error
        raise
    return destination


def cleanup_working_copy(isolated_root: Path, save_path: Path, runtime_id: str, run_id: str, scenario_id: str = "") -> None:
    candidate = validate_working_copy(isolated_root, save_path, runtime_id, run_id, scenario_id)
    shutil.rmtree(candidate)
    if candidate.exists():
        raise SaveProvisioningError("HARNESS-SAVE-CLEANUP", f"Owned working save was not removed: {candidate}")


def cleanup_bootstrap_save(
    isolated_root: Path,
    save_path: Path,
    runtime_id: str,
    run_id: str,
    *,
    allowed_schema_versions: tuple[int, ...] = (FIXTURE_SCHEMA_VERSION,),
) -> None:
    root = _save_root(isolated_root)
    candidate = _contained(root, save_path)
    if candidate.parent != root or candidate.name != BOOTSTRAP_SAVE_NAME:
        raise SaveProvisioningError("HARNESS-SAVE-PATH", "Bootstrap save is not the exact reserved child.")
    candidate = _real_directory(candidate)
    _validate_owner(
        candidate,
        runtime_id,
        run_id,
        allowed_schema_versions=allowed_schema_versions,
    )
    shutil.rmtree(candidate)
    if candidate.exists():
        raise SaveProvisioningError("HARNESS-SAVE-CLEANUP", f"Owned bootstrap save was not removed: {candidate}")


def bootstrap_reservation_path(isolated_root: Path, run_id: str) -> Path:
    state = _owned_directory_chain(isolated_root, ".runtime")
    return state / f"save-bootstrap-{uuid.UUID(_canonical_run_id(run_id)).hex}.json"


def bootstrap_preflight(isolated_root: Path, smapi_path: Path, run_id: str) -> Path | None:
    run_id = _canonical_run_id(run_id)
    fingerprint = _runtime_fingerprint(smapi_path)
    fixture = _fixture_path(isolated_root, fingerprint["runtimeId"])
    if fixture.exists():
        validate_fixture(isolated_root, smapi_path)
        return None
    target = _save_root(isolated_root) / BOOTSTRAP_SAVE_NAME
    reservation = bootstrap_reservation_path(isolated_root, run_id)
    outstanding = sorted(reservation.parent.glob("save-bootstrap-*.json"))
    if outstanding:
        raise SaveProvisioningError(
            "HARNESS-SAVE-BOOTSTRAP-INTERRUPTED",
            f"An interrupted bootstrap reservation must be recovered first: {outstanding[0]}",
        )
    if target.exists():
        raise SaveProvisioningError(
            "HARNESS-SAVE-COLLISION", f"Bootstrap target exists and will not be overwritten: {target}"
        )
    _atomic_write_json(
        reservation,
        {
            "fixtureSchemaVersion": FIXTURE_SCHEMA_VERSION,
            "fixtureId": FIXTURE_ID,
            "runtimeId": fingerprint["runtimeId"],
            "runId": run_id,
            "targetPath": str(target),
            "createdAtUtc": _timestamp(),
        },
    )
    return target


def _validate_reservation(isolated_root: Path, run_id: str) -> tuple[dict[str, Any], Path]:
    run_id = _canonical_run_id(run_id)
    path = bootstrap_reservation_path(isolated_root, run_id)
    try:
        document = _read_json(path)
    except (OSError, json.JSONDecodeError) as error:
        raise SaveProvisioningError(
            "HARNESS-SAVE-BOOTSTRAP-INTERRUPTED", f"Bootstrap reservation is unavailable: {error}"
        ) from error
    target = _save_root(isolated_root) / BOOTSTRAP_SAVE_NAME
    if (
        not isinstance(document, dict)
        or set(document) != RESERVATION_FIELDS
        or document["fixtureSchemaVersion"] != FIXTURE_SCHEMA_VERSION
        or document["fixtureId"] != FIXTURE_ID
        or document["runId"] != run_id
        or not isinstance(document["runtimeId"], str)
        or not re.fullmatch(r"[0-9a-f]{24}", document["runtimeId"])
        or document["targetPath"] != str(target)
        or not isinstance(document["createdAtUtc"], str)
        or not document["createdAtUtc"]
    ):
        raise SaveProvisioningError("HARNESS-SAVE-BOOTSTRAP-INVALID", "Bootstrap reservation is malformed.")
    return document, path


def recover_interrupted_bootstrap(isolated_root: Path, smapi_path: Path, run_id: str) -> Path:
    reservation, reservation_path = _validate_reservation(isolated_root, run_id)
    fixture = _fixture_path(isolated_root, reservation["runtimeId"])
    if fixture.exists():
        fixture = _real_directory(fixture)
        building_path = fixture / ".hatifect-fixture-building.json"
        if building_path.is_file():
            building = _read_json(building_path)
            if building != reservation:
                raise SaveProvisioningError(
                    "HARNESS-SAVE-OWNERSHIP", "Interrupted fixture ownership does not match its reservation."
                )
            shutil.rmtree(fixture)
        else:
            _validate_fixture_for_runtime(isolated_root, reservation["runtimeId"])
    target = _save_root(isolated_root) / BOOTSTRAP_SAVE_NAME
    if target.exists():
        cleanup_bootstrap_save(
            isolated_root,
            target,
            reservation["runtimeId"],
            run_id,
            allowed_schema_versions=(
                FIXTURE_SCHEMA_VERSION,
                LEGACY_BOOTSTRAP_OWNER_SCHEMA_VERSION,
            ),
        )
    reservation_path.unlink()
    return target


def bootstrap_environment(isolated_root: Path, run_id: str, artifact: Path) -> dict[str, str]:
    reservation, _ = _validate_reservation(isolated_root, run_id)
    return {
        "HATIFECT_TEST_SAVE_BOOTSTRAP_PATH": reservation["targetPath"],
        "HATIFECT_TEST_SAVE_BOOTSTRAP_RECEIPT": str(artifact / "save-bootstrap-receipt.json"),
        "HATIFECT_TEST_SAVE_RUNTIME_ID": reservation["runtimeId"],
    }


def finalize_bootstrap(
    isolated_root: Path, smapi_path: Path, run_id: str, receipt_path: Path
) -> Path:
    run_id = _canonical_run_id(run_id)
    fingerprint = _runtime_fingerprint(smapi_path)
    reservation_path = bootstrap_reservation_path(isolated_root, run_id)
    try:
        reservation, _ = _validate_reservation(isolated_root, run_id)
        receipt = _read_json(receipt_path)
    except (OSError, json.JSONDecodeError) as error:
        raise SaveProvisioningError("HARNESS-SAVE-BOOTSTRAP-INTERRUPTED", f"Bootstrap evidence is incomplete: {error}") from error
    if (
        not isinstance(receipt, dict)
        or set(receipt) != RECEIPT_FIELDS
        or receipt["fixtureSchemaVersion"] != FIXTURE_SCHEMA_VERSION
        or receipt["runId"] != run_id
        or receipt["reloadVerified"] is not True
        or not _is_acceptance_storage(receipt["acceptanceStorage"])
        or not isinstance(receipt["savePath"], str)
        or not receipt["savePath"]
        or not isinstance(receipt["stardewVersion"], str)
        or not receipt["stardewVersion"]
        or not isinstance(receipt["smapiVersion"], str)
        or not receipt["smapiVersion"]
        or {key: receipt[key] for key in ("playerName", "farmName", "favoriteThing", "uniqueMultiplayerId")}
        != _synthetic_identity()
        or reservation.get("runtimeId") != fingerprint["runtimeId"]
    ):
        raise SaveProvisioningError("HARNESS-SAVE-BOOTSTRAP-INVALID", "Bootstrap receipt failed identity or reload validation.")
    source = _contained(_save_root(isolated_root), Path(receipt["savePath"]))
    if source.parent != _save_root(isolated_root) or source.name != BOOTSTRAP_SAVE_NAME:
        raise SaveProvisioningError("HARNESS-SAVE-PATH", "Bootstrap save is outside its reserved exact path.")
    source = _real_directory(source)
    _validate_owner(source, fingerprint["runtimeId"], run_id)
    files = _inventory(source)
    fixture = _fixture_path(isolated_root, fingerprint["runtimeId"])
    if fixture.exists():
        raise SaveProvisioningError("HARNESS-SAVE-COLLISION", "Golden fixture appeared concurrently.")
    try:
        fixture.mkdir(mode=PRIVATE_DIRECTORY_MODE)
    except FileExistsError as error:
        raise SaveProvisioningError("HARNESS-SAVE-COLLISION", "Golden fixture appeared concurrently.") from error
    try:
        _atomic_write_json(fixture / ".hatifect-fixture-building.json", reservation)
        save_target = fixture / "save" / source.name
        save_target.parent.mkdir(parents=True, mode=PRIVATE_DIRECTORY_MODE)
        shutil.copytree(source, save_target, ignore=shutil.ignore_patterns(".hatifect-save-owner.json"))
        copied = _inventory(save_target)
        if copied != files:
            raise SaveProvisioningError("HARNESS-SAVE-IMMUTABILITY", "Bootstrap save changed while packaging.")
        manifest = {
            "fixtureSchemaVersion": FIXTURE_SCHEMA_VERSION,
            "fixtureId": FIXTURE_ID,
            "runtimeId": fingerprint["runtimeId"],
            "stardewVersion": receipt["stardewVersion"],
            "smapiVersion": receipt["smapiVersion"],
            "stardewAssemblySha256": fingerprint["stardewAssemblySha256"],
            "smapiAssemblySha256": fingerprint["smapiAssemblySha256"],
            "saveDirectoryName": source.name,
            "syntheticIdentity": _synthetic_identity(),
            "files": copied,
            "checksum": _inventory_checksum(copied),
            "reloadVerified": True,
            "acceptanceStorage": _acceptance_storage(),
            "bootstrapRunId": run_id,
            "createdAtUtc": _timestamp(),
        }
        _atomic_write_json(fixture / "manifest.json", manifest)
        (fixture / ".hatifect-fixture-building.json").unlink()
    except Exception:
        # Leave the exact reservation-bound partial fixture for typed recovery.
        raise
    validate_fixture(isolated_root, smapi_path)
    cleanup_bootstrap_save(isolated_root, source, fingerprint["runtimeId"], run_id)
    reservation_path.unlink()
    return fixture


def _write_blocked(args: argparse.Namespace, error: SaveProvisioningError) -> None:
    if not getattr(args, "result", None):
        return
    validator_path = Path(__file__).with_name("validate.py")
    spec = importlib.util.spec_from_file_location("hatifect_save_result_writer", validator_path)
    if spec is None or spec.loader is None:
        return
    validator = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validator)
    artifact = Path(args.artifact_directory)
    validator.write_result(
        Path(args.result),
        "BLOCKED",
        args.scenario,
        str(error),
        run_id=args.run_id,
        assertions=[{
            "id": error.assertion_id,
            "status": "BLOCKED",
            "subject": args.scenario,
            "expected": "An owned, compatible isolated save lifecycle is available.",
            "actual": str(error),
        }],
        exceptions=[{"type": type(error).__name__, "message": str(error)}],
        artifacts=validator.collect_artifacts(artifact),
    )


def _write_existing_fixture_pass(args: argparse.Namespace) -> None:
    validator_path = Path(__file__).with_name("validate.py")
    spec = importlib.util.spec_from_file_location("hatifect_save_result_writer", validator_path)
    if spec is None or spec.loader is None:
        raise SaveProvisioningError(
            "HARNESS-SAVE-LOCAL-INFRA", "The result writer for existing fixture evidence is unavailable."
        )
    validator = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validator)
    artifact = Path(args.artifact_directory)
    manifest, save = validate_fixture(Path(args.isolated_root), Path(args.smapi_path))
    diagnostic = {
        "operation": "golden-save-bootstrap-existing",
        "status": "PASS",
        "assertionId": BOOTSTRAP_EXISTING_FIXTURE_SENTINEL,
        "runId": args.run_id,
        "fixturePath": str(save.parent.parent),
        "fixtureRuntimeId": manifest["runtimeId"],
        "fixtureChecksum": manifest["checksum"],
        "acceptanceStorage": manifest["acceptanceStorage"],
        "completedAtUtc": _timestamp(),
    }
    diagnostics = _owned_directory_chain(artifact, "diagnostics")
    _atomic_write_json(diagnostics / "save-provisioning.json", diagnostic)
    message = (
        "A compatible checksum-valid, reload-verified golden save fixture already exists; "
        "idempotent bootstrap reuse passed without modifying it."
    )
    validator.write_result(
        Path(args.result),
        "PASS",
        args.scenario,
        message,
        run_id=args.run_id,
        assertions=[
            validator._assertion(
                "save.bootstrap.reload",
                "PASS",
                args.scenario,
                "The compatible fixture was created and reload-verified through the official bootstrap path.",
                message,
            ),
            validator._assertion(
                BOOTSTRAP_EXISTING_FIXTURE_SENTINEL,
                "PASS",
                args.scenario,
                "An existing fixture is compatible, checksum-valid, reload-verified, and has exact acceptance-storage metadata.",
                message,
            ),
        ],
        artifacts=validator.collect_artifacts(artifact),
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    for command in ("plan", "prepare", "bootstrap-preflight"):
        child = subparsers.add_parser(command)
        child.add_argument("--isolated-root", required=True)
        child.add_argument("--smapi-path", required=True)
        child.add_argument("--run-id", required=True)
        child.add_argument("--scenario", required=True)
        child.add_argument("--artifact-directory", required=True)
        child.add_argument("--result", required=True)
    recovery = subparsers.add_parser("recover-bootstrap")
    recovery.add_argument("--isolated-root", required=True)
    recovery.add_argument("--smapi-path", required=True)
    recovery.add_argument("--run-id", required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.command == "plan":
            print(plan_working_copy(Path(args.isolated_root), Path(args.smapi_path), args.run_id, args.scenario))
        elif args.command == "prepare":
            print(prepare_working_copy(Path(args.isolated_root), Path(args.smapi_path), args.run_id, args.scenario))
        elif args.command == "bootstrap-preflight":
            target = bootstrap_preflight(Path(args.isolated_root), Path(args.smapi_path), args.run_id)
            if target is None:
                _write_existing_fixture_pass(args)
                print(BOOTSTRAP_EXISTING_FIXTURE_SENTINEL)
            else:
                print(target)
        else:
            print(recover_interrupted_bootstrap(
                Path(args.isolated_root), Path(args.smapi_path), args.run_id
            ))
        return 0
    except (SaveProvisioningError, OSError, json.JSONDecodeError) as caught:
        error = caught if isinstance(caught, SaveProvisioningError) else SaveProvisioningError(
            "HARNESS-SAVE-LOCAL-INFRA", f"Isolated-save provisioning failed: {caught}"
        )
        _write_blocked(args, error)
        print(f"{error.assertion_id}: {error}", file=os.sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
