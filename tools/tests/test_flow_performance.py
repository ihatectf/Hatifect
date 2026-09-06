import copy
import datetime as dt
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('flow_performance_validator', ROOT / 'tools/live-harness/validate.py')
assert SPEC is not None and SPEC.loader is not None
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)
RUN = '80f274eb-477b-4bfd-8196-46bf281d1590'
SESSION = '49b964bf-83d5-4636-b089-7c5022c1f0a6'


class FlowPerformanceTests(unittest.TestCase):
    def setUp(self):
        self.resolved = HARNESS.resolve_scenario(HARNESS.load_manifest(), 'flow.chest.performance', 'smoke')

    def validate(self, report, run_id=RUN):
        return HARNESS.validate_report(self.resolved, report, 0, expected_run_id=run_id)

    def test_fixed_complete_capture_passes_and_is_separate_from_ui_metrics(self):
        report = self.report()
        evidence = self.validate(report)
        self.assertEqual(report['FlowPerformance'], evidence['performance'])
        self.assertEqual(9, len(evidence['assertions']))
        self.assertTrue(all(value['status'] == 'PASS' for value in evidence['assertions']))
        self.assertIsNone(self.resolved['performance'])
        self.assertTrue(self.resolved['requiresSave'])
        self.assertEqual(['Hatifect.Flow'], self.resolved['requiredMods'])
        self.assertNotIn('flow.chest.performance', HARNESS.load_manifest()['all']['includes'])

    def test_every_host_check_and_bound_capture_is_required(self):
        report = self.report()
        for index, check in enumerate(report['HostChecks']):
            for missing in [False, True]:
                with self.subTest(check=check['Id'], missing=missing):
                    changed = copy.deepcopy(report)
                    if missing:
                        del changed['HostChecks'][index]
                    else:
                        changed['HostChecks'][index]['Passed'] = False
                    with self.assertRaises(HARNESS.HarnessError):
                        self.validate(changed)
        for key in ['FlowPerformance']:
            changed = copy.deepcopy(report)
            del changed[key]
            with self.assertRaises(HARNESS.HarnessError):
                self.validate(changed)
        for run in [None, '', 'invalid', '80f274eb-477b-4bfd-8196-46bf281d1591']:
            with self.subTest(run=run), self.assertRaises(HARNESS.HarnessError):
                self.validate(report, run)

    def test_rejects_foreign_identity_incomplete_work_and_budget_violations(self):
        changes = {
            'RunId': '80f274eb-477b-4bfd-8196-46bf281d1591', 'SessionId': '00000000-0000-0000-0000-000000000000',
            'WorldId': 4242424243, 'FormatVersion': True, 'ScenarioId': 'flow.chest.isolation',
            'RuntimeFingerprint': 'f' * 64, 'SaveTreeHash': 'x', 'GcOverride': True,
            'ThreadId': 2, 'StopwatchFrequency': 0, 'Runtime': '', 'OperatingSystem': '', 'Architecture': '',
            'Shipments': 79, 'Routes': 4, 'Stations': 7, 'MaxOperationsPerTick': 65, 'Loads': 2,
            'SavingEvents': 0, 'SavedEvents': 0, 'Paused.SessionId': RUN, 'Idle.ThreadId': 2,
            'Paused.WarmupFrames': 119, 'Paused.Frames': 599, 'Idle.Frames': 0,
            'Paused.TimePasses': True, 'Idle.TimePasses': False, 'Paused.CachedSnapshotUnchanged': False,
            'Paused.AllocatedBytesTotal': 1, 'Idle.MaximumAllocatedBytesPerTick': 1,
            'Paused.P95TickMs': 0.26, 'Idle.P99TickMs': 1.01, 'Paused.P50TickMs': 0.2,
            'Paused.MaxTickMs': 0.1, 'Paused.End.Now': 1, 'Idle.End.Now': 901,
            'Paused.End.TickInvocations': 721, 'Paused.Start.PendingOperations': 79,
            'Paused.End.ProcessedOperations': 1, 'Paused.End.RouteSearches': 4,
            'Paused.End.RefreshRequests': 1, 'Paused.End.CheckpointCaptures': 2,
            'Paused.End.PhysicalApplyCalls': 1, 'Paused.End.LastTickProcessedOperations': 1,
            'Due.MaximumOperationsPerTick': 63, 'Due.Frames': 1, 'Due.WorkTicks': 0,
            'Due.Delivered': 79, 'Due.Start.Now': 1, 'Due.End.PendingOperations': 1,
            'Due.End.ProcessedOperations': 239, 'Due.End.PhysicalApplyCalls': 159,
            'Due.End.RefreshRequests': 3, 'Due.End.RouteSearches': 4,
            'Idle.Start.TickInvocations': 1000, 'Idle.Start.PhysicalApplyCalls': 161,
        }
        for path, value in changes.items():
            with self.subTest(path=path, value=value):
                report = self.report()
                self.set_path(report['FlowPerformance'], path, value)
                with self.assertRaises(HARNESS.HarnessError):
                    self.validate(report)

    def test_rejects_nonfinite_boolean_negative_and_absent_metrics(self):
        report = self.report()['FlowPerformance']
        numeric_paths = []
        def collect(value, prefix=''):
            for key, item in value.items():
                path = prefix + key
                if isinstance(item, dict):
                    collect(item, path + '.')
                elif type(item) in (int, float):
                    numeric_paths.append(path)
        collect(report)
        for path in numeric_paths:
            for value in [True, -1, float('nan'), float('inf'), '0', None]:
                with self.subTest(path=path, value=value):
                    changed = self.report()
                    self.set_path(changed['FlowPerformance'], path, value)
                    with self.assertRaises(HARNESS.HarnessError):
                        self.validate(changed)

    def test_rejects_coordinated_but_impossible_due_work_counts(self):
        for work_ticks, last in [(2, 48), (241, 48), (4, 47), (180, 1)]:
            with self.subTest(work_ticks=work_ticks, last=last):
                report = self.report()
                captured = report['FlowPerformance']
                captured['Due']['WorkTicks'] = work_ticks
                captured['Due']['End']['LastTickProcessedOperations'] = last
                for counters in [captured['Due']['End'], captured['Idle']['Start'], captured['Idle']['End']]:
                    counters['RefreshRequests'] = work_ticks
                with self.assertRaises(HARNESS.HarnessError):
                    self.validate(report)

    def test_manifest_cannot_substitute_or_weaken_fixed_flow_workload(self):
        document = json.loads(HARNESS.DEFAULT_MANIFEST.read_text())
        index = next(i for i, item in enumerate(document['scenarios']) if item['id'] == 'flow.chest.performance')
        for field, value in [('flowPerformance.measurementFrames', 599), ('flowPerformance.warmupFrames', 0),
                             ('flowPerformance.maximumP99TickMs', 2), ('flowPerformance.maximumP99TickMs', True),
                             ('flowPerformance.shipments', 1), ('id', 'flow.chest.unknown'), ('kind', 'ui'),
                             ('requiresSave', False), ('includeInAll', True), ('performance', {}),
                             ('flowPerformance', None)]:
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temporary:
                changed = copy.deepcopy(document)
                self.set_path(changed['scenarios'][index], field, value)
                path = Path(temporary) / 'manifest.json'
                path.write_text(json.dumps(changed))
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.load_manifest(path)

    def test_finalizer_binds_existing_request_id_and_records_budget_assertion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            report = root / 'report.json'
            report.write_text(json.dumps(self.report()))
            args = SimpleNamespace(result=str(root / 'result.json'), artifact_root=str(root), started_at=0,
                                   manifest=str(HARNESS.DEFAULT_MANIFEST), scenario='flow.chest.performance',
                                   kind='smoke', report=str(report), run_id=RUN)
            self.assertEqual(0, HARNESS.command_finalize(args))
            result = json.loads(Path(args.result).read_text())
            self.assertEqual('PASS', result['status'])
            self.assertEqual(RUN, result['runId'])
            self.assertEqual('flow.chest.performance.budget', result['assertions'][-1]['id'])
            args.run_id = '80f274eb-477b-4bfd-8196-46bf281d1591'
            self.assertEqual(1, HARNESS.command_finalize(args))
            self.assertEqual('FAIL', json.loads(Path(args.result).read_text())['status'])

    @staticmethod
    def set_path(target, path, value):
        keys = path.split('.')
        for key in keys[:-1]:
            target = target[key]
        target[keys[-1]] = value

    def report(self):
        def counters(tick, now, queue=80, operations=0, effects=0, refreshes=0, last=0):
            return dict(TickInvocations=tick, Now=now, PendingOperations=queue, RouteSearches=3,
                        ProcessedOperations=operations, LastTickProcessedOperations=last, RefreshRequests=refreshes,
                        CheckpointCaptures=1, PhysicalApplyCalls=effects)
        def window(time_passes, start, end):
            return dict(SessionId=SESSION, ThreadId=1, WarmupFrames=120, Frames=600, TimePasses=time_passes,
                        Start=start, End=end, CachedSnapshotUnchanged=True, P50TickMs=0.01, P95TickMs=0.1,
                        P99TickMs=0.2, MaxTickMs=0.3, AllocatedBytesTotal=0, MaximumAllocatedBytesPerTick=0)
        paused = window(False, counters(120, 0), counters(720, 0))
        due = dict(SessionId=SESSION, ThreadId=1, Frames=180, WorkTicks=4, MaximumOperationsPerTick=64,
                   Start=counters(720, 0), End=counters(900, 180, 0, 240, 160, 4, 48), Delivered=80)
        idle = window(True, counters(1020, 300, 0, 240, 160, 4), counters(1620, 900, 0, 240, 160, 4))
        capture = dict(FormatVersion=1, RunId=RUN, ScenarioId='flow.chest.performance', SessionId=SESSION,
                       WorldId=4242424242, RuntimeFingerprint='a'*64, StopwatchFrequency=1_000_000, ThreadId=1,
                       Runtime='.NET 6 test fixture', OperatingSystem='test fixture', Architecture='X64',
                       ServerGc=False, GcOverride=False, Shipments=80, Routes=3, Stations=6, MaxOperationsPerTick=64,
                       Loads=1, SavingEvents=1, SavedEvents=1, SaveTreeHash='b'*64, Paused=paused, Due=due, Idle=idle)
        return dict(FormatVersion=3, PerformanceFormatVersion=3, CapturedAtUtc=dt.datetime.now(dt.timezone.utc).isoformat(),
                    RuntimeFingerprint='a'*64, RuntimeFingerprintAlgorithm='sha256-flow-runtime-v1',
                    HostChecks=[dict(Id=check, Passed=True) for check in self.resolved['checks']], Scenarios=[], FlowPerformance=capture)


if __name__ == '__main__':
    unittest.main()
