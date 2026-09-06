import copy
import datetime as dt
import importlib.util
import json
import tempfile
import unittest
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('flow_resource_validator', ROOT / 'tools/live-harness/validate.py')
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)
RUN = '80f274eb-477b-4bfd-8196-46bf281d1590'
SESSIONS = [str(uuid.UUID(int=100 + i)) for i in range(7)]
PORTS = [str(uuid.UUID(int=200 + i)) for i in range(32)]


def counters(now=0, tick=0, pending=0, operations=0, effects=0, refresh=0, last=0, captures=0, searches=0):
    return dict(TickInvocations=tick, Now=now, PendingOperations=pending, RouteSearches=searches,
                ProcessedOperations=operations, LastTickProcessedOperations=last, RefreshRequests=refresh,
                CheckpointCaptures=captures, PhysicalApplyCalls=effects)


def timing():
    return dict(P50TickMs=0.01, P95TickMs=0.1, P99TickMs=0.2, MaxTickMs=0.3,
                AllocatedBytesTotal=0, MaximumAllocatedBytesPerTick=0)


def cost():
    return dict(ElapsedTicks=1000, AllocatedBytes=4096)


def resource(session, count, attempt, owner, saving=False, clean=False):
    def usage(used, limit):
        return dict(Used=used, Limit=limit)
    runtime = dict(Stations=usage(0 if clean else 32, 32), LifetimeLinks=usage(0 if clean else 40, 128),
                   ActiveLinks=0 if clean else 40, Cargo=usage(count, 256), Shipments=usage(count, 256),
                   Parcels=usage(count, 256), PendingOperations=usage(owner['PendingOperations'], 256),
                   RouteCache=usage(min(64, owner['RouteSearches']), 64), Events=usage(256 if count else 0, 256),
                   IssuedTransfers=usage(count * (attempt + 1) if attempt else 0, 4352),
                   RetiredTransfers=count * (attempt + 1) if attempt else 0, RouteSearches=owner['RouteSearches'],
                   MaxRouteVisits=32, MaxOperationsPerAdvance=64, MaxDeliveryAttempts=16)
    ports = []
    if not clean:
        for index, station in enumerate(PORTS):
            source = index == 0 or 2 <= index <= 8
            group = 0 if index == 0 else index - 1
            admitted = min(32, max(0, count - group * 32)) if source else 0
            receipts = count * attempt if index == 1 else admitted if attempt else 0
            ports.append(dict(StationId=station, Port=dict(Custody=usage(admitted if not attempt else 0, 1024), Receipts=usage(receipts, 4096))))
    return dict(SessionId=session, SaveId=4242424242, NetworkId=str(uuid.UUID(int=500)), Revision=10, Now=owner['Now'],
                State=3 if saving else 0, Runtime=runtime, Payloads=usage(count, 256), PayloadCharacters=usage(count * 1024, 256 * 65536),
                MaxCharactersPerPayload=65536, MaxCoreCheckpointBytes=64 * 1024 * 1024,
                MaximumObservedDeliveryAttempts=attempt, ParcelsAtAttemptLimit=count if attempt == 16 else 0,
                Ports=ports, AdmissionRejections=dict(Stations=0, LifetimeLinks=0, RetainedCargo=0), TickCounters=owner)


def complete_report():
    # Synthetic validator fixture; actual runtime performance is never inferred from this report.
    restores = [dict(Index=1, HasAggregate=False, Resources=resource(SESSIONS[0], 0, 0, counters(), clean=True),
                     CheckpointBytes=0, CheckpointHash='', Read=cost(), Restore=cost())]
    saves, waves = [], []
    now = 0
    for index, (count, attempt) in enumerate([(32, 0), (128, 0), (256, 0), (256, 1), (256, 8), (256, 16)]):
        owner = counters(now=now, pending=count if not attempt else 0)
        if attempt:
            first = 1 if attempt == 1 else 2 if attempt == 8 else 9
            for current in range(first, attempt + 1):
                owner['PendingOperations'] = 256
                start = copy.deepcopy(owner)
                ticks, operations, effects = (12, 768, 512) if current == 1 else (4, 256, 256)
                owner = counters(now=owner['Now'] + ticks, tick=owner['TickInvocations'] + ticks,
                                 operations=owner['ProcessedOperations'] + operations,
                                 effects=owner['PhysicalApplyCalls'] + effects, refresh=owner['RefreshRequests'] + ticks, last=64)
                waves.append(dict(Attempt=current, SessionId=SESSIONS[index], ThreadId=1, Frames=ticks, WorkTicks=ticks,
                                  MaximumOperationsPerTick=64, Start=start, End=copy.deepcopy(owner), **timing()))
            now = owner['Now']
        owner['CheckpointCaptures'] = 1
        if index < 3:
            owner['RouteSearches'] = [72, 3, 4][index]
        row = dict(Index=index + 1, Resources=resource(SESSIONS[index], count, attempt, owner, saving=True),
                   CheckpointBytes=(index + 1) * 65536, CheckpointHash=str(index + 1) * 64, Capture=cost(), Write=cost())
        saves.append(row)
        restored = resource(SESSIONS[index + 1], count, attempt, counters(now=now, pending=count if not attempt else 0))
        restores.append(dict(Index=index + 2, HasAggregate=True, Resources=restored,
                             CheckpointBytes=row['CheckpointBytes'], CheckpointHash=row['CheckpointHash'], Read=cost(), Restore=cost()))
    before = copy.deepcopy(restores[6]['Resources'])
    before['AdmissionRejections']['RetainedCargo'] = 2
    final = copy.deepcopy(before)
    final['Now'] += 720
    final['TickCounters']['Now'] += 720
    final['TickCounters']['TickInvocations'] += 720
    idle = dict(SessionId=SESSIONS[6], ThreadId=1, WarmupFrames=120, Frames=600, TimePasses=True, CachedSnapshotUnchanged=True,
                Start=counters(now=now + 120, tick=120), End=copy.deepcopy(final['TickCounters']), **timing())
    routes = []
    searches, cache = 0, 0
    def route(kind, stations, origin, destination, hops, delta, new=True):
        nonlocal searches, cache
        cache = min(64, cache + 1) if new else cache
        routes.append(dict(Kind=kind, Stations=stations, Origin=origin, Destination=destination, Hops=hops,
                           SearchesBefore=searches, SearchesAfter=searches + delta, CacheCount=cache, Cost=cost()))
        searches += delta
    for stations in (2, 16, 32):
        route('cold', stations, 0, stations - 1, stations - 1, 1)
        route('hit', stations, 0, stations - 1, stations - 1, 0, False)
    pairs = [(a, b) for a in range(32) for b in range(a + 1, 32) if not (a == 0 and b in (1, 15, 31))]
    for a, b in pairs[:65]:
        route('churn', 32, a, b, b - a, 1)
    route('evicted', 32, 0, 2, 2, 1)
    route('anchor', 32, 0, 31, 31, 1)
    route('independent', 32, 0, 31, 31, 0, False)
    route('dependent', 32, 0, 31, 1, 1, False)
    capture = dict(FormatVersion=1, RunId=RUN, ScenarioId='flow.chest.resources', WorldId=4242424242,
                   RuntimeFingerprint='a' * 64, StopwatchFrequency=1_000_000, ThreadId=1, Runtime='.NET 6 fixture',
                   OperatingSystem='test fixture', Architecture='X64', ServerGc=False, GcOverride=False,
                   Shipments=256, Stations=32, SourceChests=8, RoutingQueries=75, Loads=7, Titles=6, SavingEvents=6, SavedEvents=6,
                   StationIds=list(PORTS),
                   SaveTreeHash='b' * 64, SendRefusals=2, RetryRefusals=256, ReturnRefusals=256,
                   Routes=routes, Saves=saves, Restores=restores, Waves=waves, BeforeIdle=before, Final=final, Idle=idle)
    return dict(FormatVersion=3, PerformanceFormatVersion=3, CapturedAtUtc=dt.datetime.now(dt.timezone.utc).isoformat(),
                RuntimeFingerprint='a' * 64, RuntimeFingerprintAlgorithm='sha256-flow-runtime-v1',
                HostChecks=[dict(Id=check, Passed=True) for check in HARNESS.FLOW_RESOURCE_CHECKS], Scenarios=[], FlowResources=capture)


class FlowResourceTests(unittest.TestCase):
    def setUp(self):
        self.resolved = HARNESS.resolve_scenario(HARNESS.load_manifest(), 'flow.chest.resources', 'smoke')

    def validate(self, report, run=RUN):
        return HARNESS.validate_report(self.resolved, report, 0, expected_run_id=run)

    @staticmethod
    def set_path(value, path, replacement):
        keys = path.split('.')
        for key in keys[:-1]:
            value = value[int(key)] if isinstance(value, list) else value[key]
        if isinstance(value, list):
            value[int(keys[-1])] = replacement
        else:
            value[keys[-1]] = replacement

    def test_complete_profile_requires_all_phases_and_reports_evidence(self):
        report = complete_report()
        result = self.validate(report)
        self.assertEqual(report['FlowResources'], result['performance'])
        self.assertEqual(12, len(result['assertions']))
        self.assertTrue(all(v['status'] == 'PASS' for v in result['assertions']))
        self.assertTrue(self.resolved['requiresSave'])
        self.assertEqual(['Hatifect.Flow'], self.resolved['requiredMods'])
        self.assertNotIn('flow.chest.resources', HARNESS.load_manifest()['all']['includes'])

    def test_rejects_missing_checks_and_foreign_request(self):
        for index in range(11):
            for missing in (False, True):
                with self.subTest(index=index, missing=missing):
                    report = complete_report()
                    if missing:
                        del report['HostChecks'][index]
                    else:
                        report['HostChecks'][index]['Passed'] = False
                    with self.assertRaises(HARNESS.HarnessError):
                        self.validate(report)
        for run in (None, '', SESSIONS[0]):
            with self.subTest(run=run), self.assertRaises(HARNESS.HarnessError):
                self.validate(complete_report(), run)

    def test_rejects_incomplete_growth_hidden_effects_and_changed_authority(self):
        changes = {'Loads': 6, 'Titles': 5, 'SendRefusals': 1, 'RetryRefusals': 255, 'ReturnRefusals': 0,
                   'RuntimeFingerprint': 'f' * 64, 'GcOverride': True, 'Idle.CachedSnapshotUnchanged': False,
                   'Routes': [], 'Saves': [], 'Restores': [], 'Waves': [],
                   'Routes.71.SearchesAfter': 0, 'Routes.73.Hops': 1, 'Routes.74.Hops': 31,
                   'Restores.6.Resources.TickCounters.PhysicalApplyCalls': 1,
                   'Restores.4.CheckpointHash': 'f' * 64, 'Restores.1.HasAggregate': False,
                   'Restores.1.Resources.SessionId': SESSIONS[0],
                   'Saves.5.Resources.Runtime.IssuedTransfers.Used': 4351,
                   'Saves.5.Resources.Ports.1.Port.Receipts.Used': 4095,
                   'Waves.15.End.PhysicalApplyCalls': 2047, 'Waves.8.SessionId': SESSIONS[4],
                   'Waves.7.Start.TickInvocations': 23, 'Idle.Start.Now': 999,
                   'Idle.P99TickMs': 1.01, 'Idle.P95TickMs': 0.251,
                   'Final.PayloadCharacters.Used': 262145, 'Final.TickCounters.PhysicalApplyCalls': 1,
                   'BeforeIdle.AdmissionRejections.RetainedCargo': 0}
        for path, replacement in changes.items():
            with self.subTest(path=path):
                report = complete_report()
                self.set_path(report['FlowResources'], path, replacement)
                with self.assertRaises(HARNESS.HarnessError):
                    self.validate(report)

    def test_rejects_coordinated_hidden_work_detached_routes_and_moved_receipt_owner(self):
        for variant in ('queued-work', 'detached-routes', 'moved-receipts', 'replaced-role', 'duplicate-role'):
            with self.subTest(variant=variant):
                report = complete_report()
                captured = report['FlowResources']
                if variant == 'queued-work':
                    for row in captured['Saves'][:3]:
                        row['Resources']['TickCounters'].update(ProcessedOperations=999, PhysicalApplyCalls=999,
                                                                RefreshRequests=999, LastTickProcessedOperations=64)
                elif variant == 'detached-routes':
                    first = captured['Saves'][0]['Resources']
                    first['TickCounters']['RouteSearches'] = first['Runtime']['RouteSearches'] = first['Runtime']['RouteCache']['Used'] = 0
                elif variant == 'moved-receipts':
                    for state in [captured['Saves'][5]['Resources'], captured['Restores'][6]['Resources'],
                                  captured['BeforeIdle'], captured['Final']]:
                        state['Ports'][1]['Port']['Receipts']['Used'], state['Ports'][9]['Port']['Receipts']['Used'] = 0, 4096
                else:
                    captured['StationIds'][1] = RUN if variant == 'replaced-role' else captured['StationIds'][0]
                with self.assertRaises(HARNESS.HarnessError):
                    self.validate(report)

    def test_numeric_measurements_reject_boolean_negative_nonfinite_and_missing_values(self):
        paths = []
        def collect(value, prefix=''):
            entries = enumerate(value) if isinstance(value, list) else value.items()
            for key, item in entries:
                path = prefix + str(key)
                if isinstance(item, (dict, list)):
                    collect(item, path + '.')
                elif type(item) in (int, float):
                    paths.append(path)
        report = complete_report()
        collect(report['FlowResources'])
        for path in paths:
            for replacement in (True, -1, float('nan'), float('inf'), '0', None):
                with self.subTest(path=path, replacement=replacement):
                    changed = copy.deepcopy(report)
                    self.set_path(changed['FlowResources'], path, replacement)
                    with self.assertRaises(HARNESS.HarnessError):
                        self.validate(changed)

    def test_manifest_cannot_substitute_check_authority_or_workload(self):
        original = json.loads(HARNESS.DEFAULT_MANIFEST.read_text())
        index = next(i for i, v in enumerate(original['scenarios']) if v['id'] == 'flow.chest.resources')
        changes = [('id', 'flow.chest.unknown'), ('requiresSave', False), ('includeInAll', True), ('kind', 'ui'),
                   ('requiredMods', []), ('flowPerformance', {}), ('flowResources', None)]
        for field in HARNESS.FLOW_RESOURCES:
            changes.append(('flowResources.' + field, None))
        checks = HARNESS.FLOW_RESOURCE_CHECKS
        for i in range(len(checks)):
            changes.append(('checks', checks[:i] + checks[i + 1:]))
        for path, replacement in changes:
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                document = copy.deepcopy(original)
                self.set_path(document['scenarios'][index], path, replacement)
                manifest = Path(temporary) / 'manifest.json'
                manifest.write_text(json.dumps(document))
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.load_manifest(manifest)


if __name__ == '__main__':
    unittest.main()
