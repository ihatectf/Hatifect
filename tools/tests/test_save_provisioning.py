import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "live-harness" / "save_provisioning.py"
SPEC = importlib.util.spec_from_file_location("hatifect_save_provisioning", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
SAVE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SAVE)


class SaveProvisioningTests(unittest.TestCase):
    def test_real_flow_roundtrip_uses_canonical_owned_name_and_keeps_golden_bytes(self) -> None:
        original = b'<SaveGame><uniqueIDForThisGame>4242424242</uniqueIDForThisGame></SaveGame>'
        self._bootstrap(save_xml=original)
        manifest, golden = SAVE.validate_fixture(self.isolated, self.smapi)
        before = SAVE._inventory(golden)
        for owning_scenario in ('flow.chest.roundtrip', 'flow.chest.cancellation', 'flow.chest.return', 'flow.chest.crash-after-return', 'flow.chest.performance', 'flow.chest.resources'):
            with self.subTest(owning_scenario=owning_scenario):
                run_id = str(uuid.uuid4())
                path = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id, owning_scenario)
                self.assertEqual(f'HatifectHarness{uuid.UUID(run_id).hex}_4242424242', path.name)
                self.assertEqual(original, (path / path.name).read_bytes())
                self.assertEqual(before, SAVE._inventory(golden))
                self.assertEqual(path, SAVE.validate_working_copy(self.isolated, path, manifest['runtimeId'], run_id, owning_scenario))
                for scenario in ('', 'flow.route.basic', 'flow.save.isolation', 'flow.chest.unknown'):
                    with self.subTest(scenario=scenario), self.assertRaises(SAVE.SaveProvisioningError):
                        SAVE.cleanup_working_copy(self.isolated, path, manifest['runtimeId'], run_id, scenario)
                self.assertTrue(path.exists())
                SAVE.cleanup_working_copy(self.isolated, path, manifest['runtimeId'], run_id, owning_scenario)
                self.assertFalse(path.exists())
                self.assertEqual(before, SAVE._inventory(golden))

    def test_roundtrip_collision_does_not_acquire_foreign_cleanup_authority(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        path = SAVE.plan_working_copy(self.isolated, self.smapi, run_id, 'flow.chest.roundtrip')
        path.mkdir()
        (path / 'foreign').write_text('preserve')
        with self.assertRaises(SAVE.SaveProvisioningError):
            SAVE.prepare_working_copy(self.isolated, self.smapi, run_id, 'flow.chest.roundtrip')
        self.assertEqual('preserve', (path / 'foreign').read_text())

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.isolated = self.root / "isolated"
        self.isolated.mkdir()
        self.smapi = self.root / "game" / "StardewModdingAPI"
        self.smapi.parent.mkdir()
        self.smapi.write_text("launcher", encoding="utf-8")
        self.smapi.chmod(0o700)
        (self.smapi.parent / "Stardew Valley.dll").write_bytes(b"game-v1")
        (self.smapi.parent / "StardewModdingAPI.dll").write_bytes(b"smapi-v1")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _bootstrap(self, run_id: str | None = None, save_xml: bytes = b"complete-save") -> tuple[str, Path]:
        run_id = run_id or str(uuid.uuid4())
        target = SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        target.mkdir()
        (target / target.name).write_bytes(save_xml)
        (target / "SaveGameInfo").write_text("save-info", encoding="utf-8")
        fingerprint = SAVE._runtime_fingerprint(self.smapi)
        SAVE._atomic_write_json(
            target / ".hatifect-save-owner.json",
            SAVE._owner(fingerprint["runtimeId"], run_id),
        )
        receipt = self.root / f"receipt-{run_id}.json"
        SAVE._atomic_write_json(
            receipt,
            {
                "fixtureSchemaVersion": SAVE.FIXTURE_SCHEMA_VERSION,
                "runId": run_id,
                "savePath": str(target),
                "playerName": SAVE.SYNTHETIC_PLAYER_NAME,
                "farmName": SAVE.SYNTHETIC_FARM_NAME,
                "favoriteThing": SAVE.SYNTHETIC_FAVORITE_THING,
                "uniqueMultiplayerId": SAVE.SYNTHETIC_UNIQUE_ID,
                "stardewVersion": "1.6.test",
                "smapiVersion": "4.1.test",
                "reloadVerified": True,
                "acceptanceStorage": SAVE._acceptance_storage(),
            },
        )
        fixture = SAVE.finalize_bootstrap(self.isolated, self.smapi, run_id, receipt)
        return run_id, fixture

    def test_flow_secondary_identity_preserves_uuid_and_all_other_save_bytes(self) -> None:
        original = b'<?xml version="1.0"?><SaveGame xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><uniqueIDForThisGame>4242424242</uniqueIDForThisGame><other xsi:type="Thing">4242424242</other></SaveGame>'
        self._bootstrap(save_xml=original)
        manifest, golden = SAVE.validate_fixture(self.isolated, self.smapi)
        run_id = "11111111-1111-4111-8111-11111111111e"
        self.assertEqual(SAVE.flow_secondary_run_id(run_id), "11111111-1111-4111-8111-11111111111f")
        primary = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
        secondary = SAVE.prepare_flow_secondary(self.isolated, self.smapi, run_id)
        self.assertNotEqual(primary, secondary)
        self.assertEqual((secondary / secondary.name).read_bytes(), original.replace(b">4242424242</uniqueID", b">4242424243</uniqueID"))
        self.assertEqual((secondary / "SaveGameInfo").read_bytes(), b"save-info")
        self.assertEqual((primary / primary.name).read_bytes(), original)
        self.assertEqual((golden / golden.name).read_bytes(), original)
        self.assertEqual(SAVE.validate_fixture(self.isolated, self.smapi)[0], manifest)
        SAVE.validate_working_copy(self.isolated, secondary, manifest["runtimeId"], SAVE.flow_secondary_run_id(run_id))

    def test_ui_save_switch_derives_owned_world_and_preserves_primary_and_golden(self) -> None:
        self._assert_isolation_copies("semantic.actions.save-switch", SAVE.prepare_secondary)

    def test_production_isolation_derives_two_world_names_and_preserves_every_other_byte(self) -> None:
        self._assert_isolation_copies("flow.chest.isolation", SAVE.prepare_flow_secondary)

    def _assert_isolation_copies(self, scenario, prepare_secondary) -> None:
        original = b'<SaveGame xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><uniqueIDForThisGame>4242424242</uniqueIDForThisGame><other xsi:type="Thing">4242424242</other></SaveGame>'
        self._bootstrap(save_xml=original)
        manifest, golden = SAVE.validate_fixture(self.isolated, self.smapi)
        before = SAVE._inventory(golden)
        run_id = str(uuid.uuid4())
        secondary_id = SAVE.flow_secondary_run_id(run_id)
        primary = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id, scenario)
        first = SAVE._inventory(primary)
        secondary = prepare_secondary(self.isolated, self.smapi, run_id, scenario)
        self.assertEqual(primary.name, f"HatifectHarness{uuid.UUID(run_id).hex}_4242424242")
        self.assertEqual(secondary.name, f"HatifectHarness{uuid.UUID(secondary_id).hex}_4242424243")
        self.assertEqual((secondary / secondary.name).read_bytes(), original.replace(b">4242424242</uniqueID", b">4242424243</uniqueID"))
        self.assertEqual((secondary / "SaveGameInfo").read_bytes(), b"save-info")
        self.assertEqual(SAVE._inventory(primary), first)
        self.assertEqual(SAVE._inventory(golden), before)
        SAVE.validate_working_copy(self.isolated, secondary, manifest["runtimeId"], secondary_id, scenario, role="secondary")
        for path, identity, wrong_scenario, role in ((primary, run_id, scenario, "secondary"),
                (secondary, secondary_id, scenario, "primary"), (secondary, secondary_id, "flow.chest.roundtrip", "secondary"),
                (secondary, run_id, scenario, "secondary"), (secondary, secondary_id, scenario, "arbitrary")):
            with self.subTest(path=path, role=role, scenario=wrong_scenario), self.assertRaises(SAVE.SaveProvisioningError):
                SAVE.cleanup_working_copy(self.isolated, path, manifest["runtimeId"], identity, wrong_scenario, role=role)
        self.assertTrue(primary.exists())
        self.assertTrue(secondary.exists())
        SAVE.cleanup_working_copy(self.isolated, secondary, manifest["runtimeId"], secondary_id, scenario, role="secondary")
        SAVE.cleanup_working_copy(self.isolated, primary, manifest["runtimeId"], run_id, scenario)
        self.assertFalse(primary.exists())
        self.assertFalse(secondary.exists())
        self.assertEqual(SAVE._inventory(golden), before)

    def test_ui_save_switch_reseed_failure_and_collision_release_only_acquired_copy(self) -> None:
        self._assert_secondary_failure_ownership("semantic.actions.save-switch", SAVE.prepare_secondary)

    def test_production_secondary_collision_and_invalid_xml_preserve_existing_paths(self) -> None:
        self._assert_secondary_failure_ownership("flow.chest.isolation", SAVE.prepare_flow_secondary)

    def _assert_secondary_failure_ownership(self, scenario, prepare_secondary) -> None:
        self._bootstrap(save_xml=b"not-xml")
        run_id = str(uuid.uuid4())
        primary = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id, scenario)
        before = SAVE._inventory(primary)
        secondary_id = SAVE.flow_secondary_run_id(run_id)
        secondary = primary.parent / SAVE._working_name(secondary_id, scenario, role="secondary")
        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            prepare_secondary(self.isolated, self.smapi, run_id, scenario)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-IDENTITY")
        self.assertFalse(secondary.exists())
        self.assertEqual(SAVE._inventory(primary), before)
        secondary.mkdir()
        (secondary / "foreign").write_text("preserve")
        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            prepare_secondary(self.isolated, self.smapi, run_id, scenario)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-COLLISION")
        self.assertEqual((secondary / "foreign").read_text(), "preserve")
        self.assertEqual(SAVE._inventory(primary), before)

    def test_secondary_preparation_rejects_unadmitted_scenarios_before_copying(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        for scenario in ("", "semantic.actions.reload", "semantic.actions.save-switch.extra", "flow.ui.isolation"):
            with self.subTest(scenario=scenario), mock.patch.object(SAVE, "prepare_working_copy") as prepare:
                with self.assertRaises(SAVE.SaveProvisioningError) as caught:
                    SAVE.prepare_secondary(self.isolated, self.smapi, run_id, scenario)
                self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-PATH")
                prepare.assert_not_called()
        self.assertEqual(SAVE.secondary_run_id(run_id), SAVE.flow_secondary_run_id(run_id))
        self.assertNotEqual(SAVE.secondary_run_id(run_id), run_id)

    def test_flow_secondary_rejects_invalid_xml_and_cleans_only_new_copy(self) -> None:
        invalid = [b"not-xml", b"<SaveGame/>", b"<Farmer><uniqueIDForThisGame>4242424242</uniqueIDForThisGame></Farmer>", b"<SaveGame><uniqueIDForThisGame>1</uniqueIDForThisGame></SaveGame>", b"<SaveGame><nested><uniqueIDForThisGame>4242424242</uniqueIDForThisGame></nested></SaveGame>", b"<SaveGame><uniqueIDForThisGame>4242424242</uniqueIDForThisGame><uniqueIDForThisGame>4242424242</uniqueIDForThisGame></SaveGame>"]
        invalid.append(b"<SaveGame><!--<uniqueIDForThisGame>4242424242</uniqueIDForThisGame>--><uniqueIDForThisGame><![CDATA[4242424242]]></uniqueIDForThisGame></SaveGame>")
        for payload in invalid:
            with self.subTest(xml=payload):
                # Each fixture is independently checksum-valid; XML identity validation must reject it.
                isolated = self.isolated
                self.isolated = self.root / str(uuid.uuid4())
                self.isolated.mkdir()
                try:
                    self._bootstrap(save_xml=payload)
                    run_id = str(uuid.uuid4())
                    primary = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
                    with self.assertRaises(SAVE.SaveProvisioningError) as caught:
                        SAVE.prepare_flow_secondary(self.isolated, self.smapi, run_id)
                    self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-IDENTITY")
                    self.assertEqual((primary / primary.name).read_bytes(), payload)
                    self.assertFalse((primary.parent / SAVE._working_name(SAVE.flow_secondary_run_id(run_id))).exists())
                finally:
                    self.isolated = isolated

    def test_flow_secondary_collision_is_not_deleted_or_reseeded(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        secondary = SAVE.prepare_working_copy(self.isolated, self.smapi, SAVE.flow_secondary_run_id(run_id))
        before = {p.name: p.read_bytes() for p in secondary.iterdir()}
        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.prepare_flow_secondary(self.isolated, self.smapi, run_id)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-INTERRUPTED")
        self.assertEqual({p.name: p.read_bytes() for p in secondary.iterdir()}, before)

    def test_flow_secondary_reseed_rejects_symlink_without_touching_target(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        working = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
        target = self.root / "foreign.xml"
        target.write_bytes(b"preserve")
        primary = working / working.name
        primary.unlink()
        primary.symlink_to(target)
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]
        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE._reseed_flow_secondary(self.isolated, working, runtime_id, run_id)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-IDENTITY")
        self.assertEqual(target.read_bytes(), b"preserve")

    def test_manifest_validation_accepts_reload_verified_acceptance_storage_fixture(self) -> None:
        _, fixture = self._bootstrap()
        manifest, source = SAVE.validate_fixture(self.isolated, self.smapi)

        self.assertEqual(manifest["fixtureSchemaVersion"], 2)
        self.assertEqual(manifest["fixtureId"], SAVE.FIXTURE_ID)
        self.assertTrue(manifest["reloadVerified"])
        self.assertEqual(set(manifest), SAVE.MANIFEST_FIELDS)
        self.assertEqual(manifest["acceptanceStorage"], SAVE._acceptance_storage())
        self.assertEqual(source.parent, fixture / "save")

    def test_existing_verified_fixture_is_idempotent_bootstrap_pass_without_mutation(self) -> None:
        _, fixture = self._bootstrap()
        manifest_path = fixture / "manifest.json"
        manifest_before = manifest_path.read_bytes()
        run_id = str(uuid.uuid4())
        artifact = self.root / "artifacts" / run_id
        artifact.mkdir(parents=True)
        result = artifact / "result.json"

        completed = subprocess.run(
            [
                os.sys.executable,
                str(MODULE_PATH),
                "bootstrap-preflight",
                "--isolated-root", str(self.isolated),
                "--smapi-path", str(self.smapi),
                "--run-id", run_id,
                "--scenario", "save.bootstrap",
                "--artifact-directory", str(artifact),
                "--result", str(result),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        document = json.loads(result.read_text(encoding="utf-8"))
        diagnostic = json.loads(
            (artifact / "diagnostics" / "save-provisioning.json").read_text(encoding="utf-8")
        )

        self.assertEqual(completed.returncode, 0)
        self.assertEqual(completed.stdout.strip(), SAVE.BOOTSTRAP_EXISTING_FIXTURE_SENTINEL)
        self.assertEqual(document["status"], "PASS")
        self.assertEqual(
            [assertion["id"] for assertion in document["assertions"]],
            ["save.bootstrap.reload", SAVE.BOOTSTRAP_EXISTING_FIXTURE_SENTINEL],
        )
        self.assertTrue(all(assertion["status"] == "PASS" for assertion in document["assertions"]))
        self.assertEqual(diagnostic["status"], "PASS")
        self.assertEqual(diagnostic["acceptanceStorage"], SAVE._acceptance_storage())
        self.assertEqual(manifest_path.read_bytes(), manifest_before)
        self.assertFalse(SAVE.bootstrap_reservation_path(self.isolated, run_id).exists())

    def test_bootstrap_rejects_receipt_without_exact_acceptance_storage_metadata(self) -> None:
        run_id = str(uuid.uuid4())
        target = SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        target.mkdir()
        (target / target.name).write_text("complete-save", encoding="utf-8")
        (target / "SaveGameInfo").write_text("save-info", encoding="utf-8")
        fingerprint = SAVE._runtime_fingerprint(self.smapi)
        SAVE._atomic_write_json(
            target / ".hatifect-save-owner.json",
            SAVE._owner(fingerprint["runtimeId"], run_id),
        )
        receipt = {
            "fixtureSchemaVersion": SAVE.FIXTURE_SCHEMA_VERSION,
            "runId": run_id,
            "savePath": str(target),
            "playerName": SAVE.SYNTHETIC_PLAYER_NAME,
            "farmName": SAVE.SYNTHETIC_FARM_NAME,
            "favoriteThing": SAVE.SYNTHETIC_FAVORITE_THING,
            "uniqueMultiplayerId": SAVE.SYNTHETIC_UNIQUE_ID,
            "stardewVersion": "1.6.test",
            "smapiVersion": "4.1.test",
            "reloadVerified": True,
            "acceptanceStorage": SAVE._acceptance_storage(),
        }
        receipt["acceptanceStorage"]["chest"]["globalInventoryId"] = "forbidden"
        receipt_path = self.root / f"receipt-{run_id}.json"
        SAVE._atomic_write_json(receipt_path, receipt)

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.finalize_bootstrap(self.isolated, self.smapi, run_id, receipt_path)

        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-BOOTSTRAP-INVALID")
        self.assertFalse(SAVE._fixture_path(self.isolated, fingerprint["runtimeId"]).exists())

    def test_checksum_validation_rejects_modified_fixture(self) -> None:
        self._bootstrap()
        _, source = SAVE.validate_fixture(self.isolated, self.smapi)
        (source / source.name).write_text("changed", encoding="utf-8")

        with self.assertRaisesRegex(SAVE.SaveProvisioningError, "checksum") as caught:
            SAVE.validate_fixture(self.isolated, self.smapi)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-CORRUPT")

    def test_runtime_version_mismatch_does_not_replace_old_fixture(self) -> None:
        _, fixture = self._bootstrap()
        (self.smapi.parent / "Stardew Valley.dll").write_bytes(b"game-v2")

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.validate_fixture(self.isolated, self.smapi)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-VERSION-MISMATCH")
        self.assertTrue(fixture.is_dir())

    def test_v2_bootstrap_publishes_beside_and_never_mutates_v1_fixture(self) -> None:
        legacy = self.isolated / "fixtures" / "saves" / "v1" / "hatifect-golden-save-legacy"
        legacy.mkdir(parents=True)
        sentinel = legacy / "immutable-v1"
        sentinel.write_text("preserve", encoding="utf-8")

        _, fixture = self._bootstrap()

        self.assertEqual(fixture.parent.name, "v2")
        self.assertEqual(sentinel.read_text(encoding="utf-8"), "preserve")

    def test_fixture_rejects_noncanonical_acceptance_storage_metadata(self) -> None:
        _, fixture = self._bootstrap()
        manifest_path = fixture / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["acceptanceStorage"]["contents"].append(
            {"qualifiedItemId": "(O)390", "stack": 1}
        )
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.validate_fixture(self.isolated, self.smapi)

        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-CORRUPT")

    def test_missing_fixture_is_typed(self) -> None:
        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.validate_fixture(self.isolated, self.smapi)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-MISSING")

    def test_corrupted_manifest_is_typed(self) -> None:
        _, fixture = self._bootstrap()
        (fixture / "manifest.json").write_text("{}\n", encoding="utf-8")

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.validate_fixture(self.isolated, self.smapi)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-CORRUPT")

    def test_working_copy_path_is_deterministic_and_unique_per_run(self) -> None:
        self._bootstrap()
        first = str(uuid.uuid4())
        second = str(uuid.uuid4())
        first_path = SAVE.prepare_working_copy(self.isolated, self.smapi, first)
        second_path = SAVE.prepare_working_copy(self.isolated, self.smapi, second)

        self.assertEqual(first_path.name, SAVE._working_name(first))
        self.assertNotEqual(first_path, second_path)

    def test_client_plan_does_not_create_working_copy_before_request_acceptance(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        planned = SAVE.plan_working_copy(self.isolated, self.smapi, run_id)

        self.assertFalse(planned.exists())
        self.assertEqual(
            SAVE.prepare_working_copy(self.isolated, self.smapi, run_id),
            planned,
        )

    def test_working_copy_has_exact_ownership_marker(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        path = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
        marker = json.loads((path / ".hatifect-save-owner.json").read_text(encoding="utf-8"))

        self.assertEqual(set(marker), SAVE.OWNER_FIELDS)
        self.assertEqual(marker["runId"], run_id)
        self.assertEqual(marker["state"], "Ready")

    def test_unowned_destination_collision_is_never_overwritten(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        collision = SAVE._save_root(self.isolated) / SAVE._working_name(run_id)
        collision.mkdir()
        sentinel = collision / "personal"
        sentinel.write_text("keep", encoding="utf-8")

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-COLLISION")
        self.assertEqual(sentinel.read_text(encoding="utf-8"), "keep")

    def test_interrupted_bootstrap_reservation_is_typed(self) -> None:
        run_id = str(uuid.uuid4())
        SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-BOOTSTRAP-INTERRUPTED")

    def test_interrupted_bootstrap_recovery_removes_only_marked_target(self) -> None:
        run_id = str(uuid.uuid4())
        target = SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        target.mkdir()
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]
        SAVE._atomic_write_json(
            target / ".hatifect-save-owner.json", SAVE._owner(runtime_id, run_id)
        )

        recovered = SAVE.recover_interrupted_bootstrap(self.isolated, self.smapi, run_id)
        self.assertEqual(recovered, target)
        self.assertFalse(target.exists())
        self.assertFalse(SAVE.bootstrap_reservation_path(self.isolated, run_id).exists())

    def test_interrupted_bootstrap_recovery_accepts_exact_legacy_owner_marker(self) -> None:
        run_id = str(uuid.uuid4())
        target = SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        assert target is not None
        target.mkdir()
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]
        owner = SAVE._owner(runtime_id, run_id)
        owner["fixtureSchemaVersion"] = SAVE.LEGACY_BOOTSTRAP_OWNER_SCHEMA_VERSION
        SAVE._atomic_write_json(target / ".hatifect-save-owner.json", owner)

        recovered = SAVE.recover_interrupted_bootstrap(self.isolated, self.smapi, run_id)

        self.assertEqual(recovered, target)
        self.assertFalse(target.exists())
        self.assertFalse(SAVE.bootstrap_reservation_path(self.isolated, run_id).exists())

    def test_interrupted_bootstrap_recovery_rejects_unknown_owner_schema(self) -> None:
        run_id = str(uuid.uuid4())
        target = SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        assert target is not None
        target.mkdir()
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]
        owner = SAVE._owner(runtime_id, run_id)
        owner["fixtureSchemaVersion"] = 0
        SAVE._atomic_write_json(target / ".hatifect-save-owner.json", owner)

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.recover_interrupted_bootstrap(self.isolated, self.smapi, run_id)

        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-OWNERSHIP")
        self.assertTrue(target.is_dir())
        self.assertTrue(SAVE.bootstrap_reservation_path(self.isolated, run_id).is_file())

    def test_interrupted_fixture_publication_requires_matching_reservation(self) -> None:
        run_id = str(uuid.uuid4())
        SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        reservation, _ = SAVE._validate_reservation(self.isolated, run_id)
        fixture = SAVE._fixture_path(self.isolated, reservation["runtimeId"])
        fixture.mkdir()
        SAVE._atomic_write_json(
            fixture / ".hatifect-fixture-building.json", reservation
        )

        SAVE.recover_interrupted_bootstrap(self.isolated, self.smapi, run_id)
        self.assertFalse(fixture.exists())

    def test_recovery_validates_published_fixture_by_reserved_runtime_after_runtime_change(self) -> None:
        run_id = str(uuid.uuid4())
        target = SAVE.bootstrap_preflight(self.isolated, self.smapi, run_id)
        assert target is not None
        target.mkdir()
        (target / target.name).write_text("complete-save", encoding="utf-8")
        (target / "SaveGameInfo").write_text("save-info", encoding="utf-8")
        fingerprint = SAVE._runtime_fingerprint(self.smapi)
        SAVE._atomic_write_json(
            target / ".hatifect-save-owner.json",
            SAVE._owner(fingerprint["runtimeId"], run_id),
        )
        receipt = self.root / f"receipt-{run_id}.json"
        SAVE._atomic_write_json(
            receipt,
            {
                "fixtureSchemaVersion": SAVE.FIXTURE_SCHEMA_VERSION,
                "runId": run_id,
                "savePath": str(target),
                "playerName": SAVE.SYNTHETIC_PLAYER_NAME,
                "farmName": SAVE.SYNTHETIC_FARM_NAME,
                "favoriteThing": SAVE.SYNTHETIC_FAVORITE_THING,
                "uniqueMultiplayerId": SAVE.SYNTHETIC_UNIQUE_ID,
                "stardewVersion": "1.6.test",
                "smapiVersion": "4.1.test",
                "reloadVerified": True,
                "acceptanceStorage": SAVE._acceptance_storage(),
            },
        )

        with mock.patch.object(
            SAVE,
            "cleanup_bootstrap_save",
            side_effect=RuntimeError("interrupted after fixture publication"),
        ):
            with self.assertRaisesRegex(RuntimeError, "interrupted after fixture publication"):
                SAVE.finalize_bootstrap(self.isolated, self.smapi, run_id, receipt)

        fixture = SAVE._fixture_path(self.isolated, fingerprint["runtimeId"])
        self.assertTrue(fixture.is_dir())
        (self.smapi.parent / "Stardew Valley.dll").write_bytes(b"game-v2")

        recovered = SAVE.recover_interrupted_bootstrap(self.isolated, self.smapi, run_id)

        self.assertEqual(recovered, target)
        self.assertFalse(target.exists())
        self.assertFalse(SAVE.bootstrap_reservation_path(self.isolated, run_id).exists())
        self.assertTrue(fixture.is_dir())
        manifest, _ = SAVE._validate_fixture_for_runtime(
            self.isolated, fingerprint["runtimeId"]
        )
        self.assertEqual(manifest["runtimeId"], fingerprint["runtimeId"])

    def test_copy_on_run_renames_primary_save_file(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        working = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)

        self.assertTrue((working / working.name).is_file())
        self.assertFalse((working / SAVE.BOOTSTRAP_SAVE_NAME).exists())
        self.assertTrue((working / "SaveGameInfo").is_file())

    def test_golden_fixture_remains_immutable_after_copy(self) -> None:
        self._bootstrap()
        manifest_before, source = SAVE.validate_fixture(self.isolated, self.smapi)
        SAVE.prepare_working_copy(self.isolated, self.smapi, str(uuid.uuid4()))
        manifest_after, _ = SAVE.validate_fixture(self.isolated, self.smapi)

        self.assertEqual(manifest_after["checksum"], manifest_before["checksum"])
        self.assertEqual(manifest_after["acceptanceStorage"], SAVE._acceptance_storage())
        self.assertTrue((source / source.name).is_file())

    def test_cleanup_removes_only_owned_copy(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        working = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]

        SAVE.cleanup_working_copy(self.isolated, working, runtime_id, run_id)
        self.assertFalse(working.exists())
        SAVE.validate_fixture(self.isolated, self.smapi)

    def test_cleanup_refuses_path_outside_isolated_save_root(self) -> None:
        self._bootstrap()
        outside = self.root / "user-save"
        outside.mkdir()
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]

        with self.assertRaises(SAVE.SaveProvisioningError) as caught:
            SAVE.cleanup_working_copy(self.isolated, outside, runtime_id, str(uuid.uuid4()))
        self.assertEqual(caught.exception.assertion_id, "HARNESS-SAVE-PATH")
        self.assertTrue(outside.is_dir())

    def test_repeated_sequential_runs_get_fresh_copies(self) -> None:
        self._bootstrap()
        runtime_id = SAVE._runtime_fingerprint(self.smapi)["runtimeId"]
        for _ in range(2):
            run_id = str(uuid.uuid4())
            working = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
            (working / working.name).write_text("mutated working save", encoding="utf-8")
            SAVE.cleanup_working_copy(self.isolated, working, runtime_id, run_id)

        _, source = SAVE.validate_fixture(self.isolated, self.smapi)
        self.assertEqual((source / source.name).read_text(encoding="utf-8"), "complete-save")

    def test_parallel_same_run_cannot_create_two_working_copies(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())

        def prepare() -> str:
            try:
                SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
                return "created"
            except SAVE.SaveProvisioningError:
                return "blocked"

        with ThreadPoolExecutor(max_workers=2) as executor:
            outcomes = sorted(executor.map(lambda _: prepare(), range(2)))
        self.assertEqual(outcomes, ["blocked", "created"])

    def test_cli_missing_fixture_writes_typed_harness_env_save_result(self) -> None:
        run_id = str(uuid.uuid4())
        artifact = self.root / "artifacts" / run_id
        artifact.mkdir(parents=True)
        result = artifact / "result.json"
        completed = subprocess.run(
            [
                os.sys.executable,
                str(MODULE_PATH),
                "prepare",
                "--isolated-root", str(self.isolated),
                "--smapi-path", str(self.smapi),
                "--run-id", run_id,
                "--scenario", "semantic.locale-scale-theme",
                "--artifact-directory", str(artifact),
                "--result", str(result),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        document = json.loads(result.read_text(encoding="utf-8"))

        self.assertEqual(completed.returncode, 2)
        self.assertEqual(document["status"], "BLOCKED")
        self.assertEqual(document["assertions"][0]["id"], "HARNESS-SAVE-MISSING")

    def test_cli_missing_fixture_for_all_writes_typed_blocked_result(self) -> None:
        run_id = str(uuid.uuid4())
        artifact = self.root / "artifacts" / run_id
        artifact.mkdir(parents=True)
        result = artifact / "result.json"
        completed = subprocess.run(
            [
                os.sys.executable,
                str(MODULE_PATH),
                "prepare",
                "--isolated-root", str(self.isolated),
                "--smapi-path", str(self.smapi),
                "--run-id", run_id,
                "--scenario", "all",
                "--artifact-directory", str(artifact),
                "--result", str(result),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        document = json.loads(result.read_text(encoding="utf-8"))

        self.assertEqual(completed.returncode, 2)
        self.assertEqual(document["scenario"], "all")
        self.assertEqual(document["status"], "BLOCKED")
        self.assertEqual(document["assertions"][0]["id"], "HARNESS-SAVE-MISSING")

    def test_provisioned_copy_validates_for_gameplay_transition(self) -> None:
        self._bootstrap()
        run_id = str(uuid.uuid4())
        working = SAVE.prepare_working_copy(self.isolated, self.smapi, run_id)
        manifest, _ = SAVE.validate_fixture(self.isolated, self.smapi)

        validated = SAVE.validate_working_copy(
            self.isolated, working, manifest["runtimeId"], run_id
        )
        self.assertEqual(validated, working)


if __name__ == "__main__":
    unittest.main()
