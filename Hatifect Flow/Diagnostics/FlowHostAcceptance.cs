using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using StardewModdingAPI;
using StardewValley;

namespace Hatifect.Flow.Diagnostics;

// Opt-in direct-process acceptance only. This driver never creates or saves a
// Stardew fixture; it loads the provisioner's owned copy and uses fake cargo.
internal sealed class FlowHostAcceptance : IDisposable
{
    private static readonly string[] CheckIds = { "loaded", "delivery", "pause", "idle", "lifecycle", "reload" };
    private static readonly StationId Source = new(Id(1));
    private static readonly StationId Destination = new(Id(2));
    private static readonly CargoId Cargo = new(Id(3));
    private static readonly ParcelId Parcel = new(Id(4));
    private static readonly CargoManifest Manifest = new("(O)378", 7);
    private readonly IMonitor _monitor;
    private const ulong SaveA = 4242424242;
    private const ulong SaveB = 4242424243;
    private readonly string _savePath;
    private readonly string? _secondSavePath;
    private readonly string _requestId;
    private readonly string _scenarioId;
    private readonly string _runtimeId;
    private Guid _firstPairId;
    private readonly DurableSaveSessionStore _store;
    private FlowSaveIdentity _currentIdentity;
    private string _coreDirectory = string.Empty;
    private string _providerDirectory = string.Empty;
    private PairSnapshot? _saveASnapshot;
    private PairSnapshot? _saveBSnapshot;
    private DurableFlowHost? _firstHost;
    private FlowRuntime? _firstRuntime;
    private readonly string _reportPath;
    private readonly string _diagnosticsPath;
    private readonly string _fingerprint;
    private readonly Dictionary<string, bool> _checks = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    private Stage _stage;
    private int _frames;
    private int _totalFrames;
    private int _loads;
    private bool _ownsPause;
    private bool _priorPause;
    private bool _disposed;
    private long _baselineTick;
    private long _baselineSearches;
    private long _lastLogicalTick;
    private int _lastReceipts;
    private int _idleActiveFrames;
    private byte[]? _coreBytes;
    private byte[]? _providerBytes;
    private DurableFlowHost? _oldHost;
    private FlowRuntime? _oldRuntime;

    private enum Stage { Startup, Loading, AwaitTransit, Paused, AwaitDelivery, Idle, Returning, Reload, SecondSave, Done, Exit }

    private FlowHostAcceptance(IModHelper helper, IMonitor monitor, string runId, string scenarioId, string savePath, string? secondSavePath, string artifact, string runtimeId)
    {
        _monitor = monitor;
        _requestId = runId;
        _scenarioId = scenarioId;
        _runtimeId = runtimeId;
        _savePath = savePath;
        _secondSavePath = secondSavePath;
        _store = new DurableSaveSessionStore(Path.Combine(artifact, "flow-save-store"));
        _reportPath = Path.Combine(helper.DirectoryPath, ".acceptance", "host-acceptance-report.json");
        _diagnosticsPath = Path.Combine(artifact, "diagnostics", "flow-host.json");
        _fingerprint = RuntimeFingerprint();
    }

    public string SessionIdentity => ValidateLoadedIdentity().Value.ToString("x16", CultureInfo.InvariantCulture);
    private bool IsIsolation => _secondSavePath is not null;
    private string ExpectedSavePath => IsIsolation && _loads == 1 ? _secondSavePath! : _savePath;
    private ulong ExpectedSaveId => IsIsolation && _loads == 1 ? SaveB : SaveA;

    public static FlowHostAcceptance? TryCreate(IModHelper helper, IMonitor monitor)
    {
        string? scenarioId = Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO");
        if (Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") != "1"
            || (scenarioId != "flow.route.basic" && scenarioId != "flow.save.isolation")) return null;
        AcceptanceRequest request = ReadAcceptanceRequest(helper, scenarioId!);
        return new FlowHostAcceptance(helper, monitor, request.RunId, scenarioId!, request.SavePath, request.SecondSavePath, request.Artifact, request.RuntimeId);
    }

    internal sealed record AcceptanceRequest(string RunId, string SavePath, string? SecondSavePath, string Artifact, string RuntimeId);

    internal static AcceptanceRequest ReadAcceptanceRequest(IModHelper helper, string scenarioId)
    {
        Require(scenarioId is "flow.route.basic" or "flow.save.isolation" or "flow.chest.roundtrip", "Unknown Flow acceptance scenario.");
        Require(Required("HATIFECT_TEST_PROTOCOL_VERSION") == "1", "Unsupported harness protocol.");
        string runId = Required("HATIFECT_TEST_RUN_ID");
        Require(Guid.TryParseExact(runId, "D", out Guid parsed) && parsed.ToString("D") == runId,
            "The harness run ID must be a canonical UUID.");
        string isolated = Full(Required("HATIFECT_TEST_ISOLATED_ROOT"));
        string artifact = Full(Required("HATIFECT_TEST_ARTIFACTS"));
        string save = Full(Required("HATIFECT_SMAPI_TEST_SAVE"));
        Require(Full(helper.DirectoryPath) == Path.Combine(isolated, "Mods", "Hatifect", "Hatifect Flow"),
            "Flow acceptance requires the isolated Flow module directory.");
        Require(Path.GetDirectoryName(save) == Path.Combine(isolated, "config", "StardewValley", "Saves")
            && Path.GetFileName(save) == (scenarioId == "flow.chest.roundtrip" ? "HatifectHarness" + parsed.ToString("N") + "_4242424242" : "HatifectHarness_" + parsed.ToString("N")) && Directory.Exists(save),
            "The save must be this run's provisioned isolated working copy.");
        Require(Path.GetFileName(artifact) == runId && Directory.Exists(artifact), "Artifact directory identity mismatch.");
        RejectLinks(isolated); RejectLinks(helper.DirectoryPath); RejectLinks(save); RejectLinks(artifact);
        ValidateSaveTree(save);
        using JsonDocument request = ReadBoundedJson(Path.Combine(artifact, "request.json"));
        JsonElement r = request.RootElement;
        Require(r.GetProperty("protocolVersion").GetInt32() == 2
            && r.GetProperty("requestId").GetString() == runId
            && r.GetProperty("scenarioId").GetString() == scenarioId
            && Full(r.GetProperty("isolatedRoot").GetString()!) == isolated
            && Full(r.GetProperty("artifactDirectory").GetString()!) == artifact
            && Full(r.GetProperty("savePath").GetString()!) == save
            && artifact == Path.Combine(Full(r.GetProperty("repositoryRoot").GetString()!), "artifacts", "runtime", runId),
            "The artifact request does not bind this process, save and scenario.");
        string runtimeId = HashText(HashFile(typeof(Game1).Assembly.Location) + "\n"
            + HashFile(typeof(IModHelper).Assembly.Location) + "\n")[..24];
        ValidateSaveOwner(save, runId, runtimeId);
        string? secondSave = null;
        if (scenarioId == "flow.save.isolation")
        {
            string secondRunId = CompanionRunId(runId);
            secondSave = Path.Combine(Path.GetDirectoryName(save)!, "HatifectHarness_" + Guid.Parse(secondRunId).ToString("N"));
            ValidateSaveTree(secondSave);
            ValidateSaveOwner(secondSave, secondRunId, runtimeId);
        }
        return new AcceptanceRequest(runId, save, secondSave, artifact, runtimeId);
    }

    internal static void ValidateSaveOwner(string save, string runId, string runtimeId)
    {
        using JsonDocument owner = ReadBoundedJson(Path.Combine(save, ".hatifect-save-owner.json"));
        JsonElement o = owner.RootElement;
        Require(o.EnumerateObject().Count() == 6 && o.GetProperty("fixtureSchemaVersion").GetInt32() == 2
            && o.GetProperty("fixtureId").GetString() == "hatifect-golden-save"
            && o.GetProperty("runtimeId").GetString() == runtimeId
            && o.GetProperty("runId").GetString() == runId && o.GetProperty("state").GetString() == "Ready"
            && o.GetProperty("createdAtUtc").ValueKind == JsonValueKind.String,
            "The provisioned save ownership marker does not match the loaded runtime and run.");
    }

    private static string CompanionRunId(string runId)
    {
        int last = int.Parse(runId[^1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) ^ 1;
        return runId[..^1] + last.ToString("x", CultureInfo.InvariantCulture);
    }

    private FlowSaveIdentity ValidateLoadedIdentity()
    {
        Require(_stage == Stage.Loading && Context.IsMainPlayer && Context.IsWorldReady,
            "A save identity is available only during the authoritative requested load.");
        // SMAPI derives this logical name from the loaded game's base name and
        // game ID. It is not the UUID-owned physical input path passed to Load.
        string expectedLogicalName = "HatifectHarness_" + ExpectedSaveId.ToString(CultureInfo.InvariantCulture);
        Require(Game1.uniqueIDForThisGame == ExpectedSaveId
            && Constants.SaveFolderName == expectedLogicalName,
            $"Loaded game identity/name mismatch: actual={Game1.uniqueIDForThisGame}/{Constants.SaveFolderName}; expected={ExpectedSaveId}/{expectedLogicalName}; requested={ExpectedSavePath}.");
        ValidateSaveTree(ExpectedSavePath);
        ValidateSaveOwner(ExpectedSavePath, ExpectedSaveId == SaveB ? CompanionRunId(_requestId) : _requestId, _runtimeId);
        return new FlowSaveIdentity(Game1.uniqueIDForThisGame);
    }

    public DurableFlowSession OpenSession()
    {
        _currentIdentity = ValidateLoadedIdentity();
        string directory = _store.GetSaveDirectory(_currentIdentity);
        RejectLinks(directory);
        _coreDirectory = Path.Combine(directory, "core");
        _providerDirectory = Path.Combine(directory, "provider");
        if (Directory.Exists(directory))
            return _store.Open(_currentIdentity);
        var runtime = new FlowRuntime(new NetworkId(Id(8)));
        var source = new InMemoryCargoPort();
        source.Seed(Cargo, Manifest);
        runtime.AddStation(Source, source);
        runtime.RegisterCargo(Cargo, Source, Manifest);
        runtime.AddStation(Destination, new InMemoryCargoPort());
        runtime.AddLink(new LinkId(Id(5)), Source, Destination, 10, 8);
        runtime.CreateShipment(new ShipmentId(Id(6)), Source, Destination, Manifest,
            new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Reserved));
        runtime.SplitShipment(new ShipmentId(Id(6)), Parcel, Cargo);
        return _store.Create(_currentIdentity, runtime);
    }

    public void OnSaveLoaded(DurableFlowHost host, bool started)
    {
        Require(!_disposed && _stage == Stage.Loading && started && host.State == DurableFlowHostState.Active,
            "SaveLoaded did not create a fresh active Flow host.");
        _loads++;
        _lastLogicalTick = host.LogicalTick;
        if (_loads == 1)
        {
            RequireFresh(host);
            _firstPairId = DurableProviderCodec.Decode(File.ReadAllBytes(ProviderFile)).PairId;
            _firstHost = host;
            _firstRuntime = host.Runtime;
            host.Execute(runtime => Require(runtime.TryReserve(Parcel), "The fixture route could not reserve."));
            Require(host.Runtime.GetParcel(Parcel).State == ParcelState.Reserved, "The reserved parcel state was not retained.");
            _checks["loaded"] = true;
            _stage = Stage.AwaitTransit;
        }
        else if (IsIsolation && _loads == 2)
        {
            Require(!ReferenceEquals(host, _oldHost) && !ReferenceEquals(host.Runtime, _oldRuntime),
                "Save B reused the disposed save A host or runtime.");
            _saveASnapshot!.RequireUnchanged();
            RequireFresh(host);
            Require(DurableProviderCodec.Decode(File.ReadAllBytes(ProviderFile)).PairId != _firstPairId,
                "Different stable save identities reused the same durable pair identity.");
            _stage = Stage.SecondSave;
        }
        else
        {
            Require(_loads == (IsIsolation ? 3 : 2)
                && !ReferenceEquals(host, _oldHost) && !ReferenceEquals(host.Runtime, _oldRuntime)
                && !ReferenceEquals(host, _firstHost) && !ReferenceEquals(host.Runtime, _firstRuntime),
                "Reload reused a fenced host or runtime.");
            _saveASnapshot!.RequireUnchanged();
            RequireDelivered(host);
            if (IsIsolation)
            {
                _saveBSnapshot!.RequireUnchanged();
                _checks["save-isolation"] = true;
            }
            _checks["reload"] = true;
            WriteReport();
            _stage = Stage.Done;
        }
        _frames = 0;
    }

    public void OnReturnedToTitle()
    {
        Require(_stage == Stage.Returning && _oldHost is not null && _oldRuntime is not null
            && _oldHost.State == DurableFlowHostState.Disposed, "ReturnedToTitle did not dispose the old host.");
        bool fenced = false;
        try { _oldRuntime!.AdvanceTo(_oldRuntime.Now); }
        catch (InvalidOperationException) { fenced = true; }
        Require(fenced, "The old runtime retained mutation authority.");
        CapturePair();
        using (var reopened = _store.Open(_currentIdentity))
        {
            RequirePairUnchanged();
            Require(reopened.Runtime.GetParcel(Parcel).State == (_currentIdentity.Value == SaveA ? ParcelState.Delivered : ParcelState.Created),
                "Stop changed the selected save's parcel state.");
        }
        var snapshot = PairSnapshot.Capture(_store.GetSaveDirectory(_currentIdentity));
        if (_currentIdentity.Value == SaveA) _saveASnapshot = snapshot;
        else
        {
            _saveBSnapshot = snapshot;
            _saveASnapshot!.RequireUnchanged();
        }
        _checks["lifecycle"] = true;
        _stage = Stage.Reload;
        _frames = 0;
    }

    public void Tick(DurableFlowHost? host)
    {
        if (_disposed) return;
        if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
        if (_stage == Stage.Done) { _stage = Stage.Exit; return; }
        if (++_totalFrames > 7200) throw new TimeoutException("Flow acceptance exceeded its bounded frame budget.");
        _frames++;
        if (host is not null && host.State == DurableFlowHostState.Active) _lastLogicalTick = host.LogicalTick;
        switch (_stage)
        {
            case Stage.Startup:
                if (_frames >= 30) BeginLoad();
                break;
            case Stage.Reload:
                BeginLoad();
                break;
            case Stage.SecondSave:
                Require(host is not null, "Save B host disappeared before its independent close.");
                RequireFresh(host!);
                _saveASnapshot!.RequireUnchanged();
                ReturnToTitle(host!);
                break;
            case Stage.AwaitTransit:
                Require(host is not null, "The active host disappeared before transit.");
                if (host!.Runtime.GetParcel(Parcel).State == ParcelState.InTransit)
                {
                    _baselineTick = host.LogicalTick;
                    _lastReceipts = 1;
                    CapturePair();
                    _priorPause = Game1.paused;
                    _ownsPause = true;
                    Game1.paused = true;
                    _frames = 0;
                    _stage = Stage.Paused;
                }
                break;
            case Stage.Paused:
                Require(host is not null && host.LogicalTick == _baselineTick, "Pause advanced the host clock.");
                RequirePairUnchanged();
                if (_frames >= 3)
                {
                    RestorePause();
                    _checks["pause"] = true;
                    _stage = Stage.AwaitDelivery;
                }
                break;
            case Stage.AwaitDelivery:
                Require(host is not null, "The active host disappeared before delivery.");
                if (host!.Runtime.GetParcel(Parcel).State == ParcelState.Delivered)
                {
                    RequireDelivered(host);
                    _checks["delivery"] = true;
                    _baselineTick = host.LogicalTick;
                    _baselineSearches = host.Runtime.RouteSearchCount;
                    _idleActiveFrames = 0;
                    CapturePair();
                    _frames = 0;
                    _stage = Stage.Idle;
                }
                break;
            case Stage.Idle:
                Require(host is not null && host.Runtime.RouteSearchCount == _baselineSearches,
                    "Idle updates changed routing.");
                long elapsed = host!.LogicalTick - _baselineTick;
                Require(elapsed == _idleActiveFrames || elapsed == _idleActiveFrames + 1,
                    "An idle frame advanced the logical clock more than once or moved it backwards.");
                if (elapsed > _idleActiveFrames) _idleActiveFrames++;
                RequirePairUnchanged();
                if (_idleActiveFrames >= 5)
                {
                    _checks["idle"] = true;
                    ReturnToTitle(host);
                }
                break;
        }
    }

    public void Fail(Exception error)
    {
        RestorePause();
        _errors.Add(error.ToString());
        _checks["reload"] = false;
        _monitor.Log("Hatifect Flow acceptance failed: " + error, LogLevel.Error);
        try { WriteReport(); }
        finally { _stage = Stage.Exit; }
    }

    public void Dispose() { RestorePause(); _disposed = true; }

    private void BeginLoad()
    {
        _stage = Stage.Loading;
        _frames = 0;
        ValidateSaveTree(ExpectedSavePath);
        SaveGame.Load(Path.GetFileName(ExpectedSavePath));
        Game1.exitActiveMenu();
    }

    private void ReturnToTitle(DurableFlowHost host)
    {
        _oldHost = host;
        _oldRuntime = host.Runtime;
        _stage = Stage.Returning;
        RequestReturnToTitle();
    }

    private void RequireFresh(DurableFlowHost host)
    {
        var provider = DurableProviderCodec.Decode(File.ReadAllBytes(ProviderFile));
        Require(host.Runtime.NetworkId == new NetworkId(Id(8)) && provider.NetworkId == Id(8)
            && host.Runtime.GetParcel(Parcel).State == ParcelState.Created
            && host.Runtime.GetCargo(Cargo).Owner == CargoOwner.AtStation(Source)
            && host.GetPortSnapshot(Source).Inventory.Length == 1
            && host.GetPortSnapshot(Source).Inventory[0].CargoId == Cargo.Value
            && host.GetPortSnapshot(Source).Inventory[0].Manifest.ItemKey == Manifest.ItemKey
            && host.GetPortSnapshot(Source).Inventory[0].Manifest.Quantity == Manifest.Quantity
            && host.GetPortSnapshot(Destination).Inventory.Length == 0
            && provider.Revision == 0 && provider.Receipts.Length == 0,
            "A fresh save did not receive an independent fake batch with no transfer receipts.");
    }

    private void RequireDelivered(DurableFlowHost host)
    {
        var provider = DurableProviderCodec.Decode(File.ReadAllBytes(ProviderFile));
        _lastReceipts = provider.Receipts.Length;
        Require(host.Runtime.GetParcel(Parcel).State == ParcelState.Delivered
            && host.Runtime.GetCargo(Cargo).Owner == CargoOwner.AtStation(Destination)
            && host.GetPortSnapshot(Source).Inventory.Length == 0
            && host.GetPortSnapshot(Destination).Inventory.Length == 1
            && host.GetPortSnapshot(Destination).Inventory[0].CargoId == Cargo.Value
            && host.GetPortSnapshot(Destination).Inventory[0].Manifest.ItemKey == Manifest.ItemKey
            && host.GetPortSnapshot(Destination).Inventory[0].Manifest.Quantity == Manifest.Quantity
            && provider.Revision == 2 && provider.Receipts.Length == 2
            && provider.Receipts.All(receipt => receipt.CargoId == Cargo.Value
                && receipt.Key.ParcelId == Parcel.Value && receipt.Key.Attempt == 1
                && receipt.Result == (int)PortResult.Applied
                && receipt.Manifest.ItemKey == Manifest.ItemKey && receipt.Manifest.Quantity == Manifest.Quantity)
            && provider.Receipts.Count(receipt => receipt.Key.Kind == (int)PortTransferKind.Extract && receipt.StationId == Source.Value) == 1
            && provider.Receipts.Count(receipt => receipt.Key.Kind == (int)PortTransferKind.Deposit && receipt.StationId == Destination.Value) == 1,
            "Delivery did not preserve exactly one fake batch and its two transfer receipts.");
    }

    private string CoreFile => Path.Combine(_coreDirectory, DurableFlowSession.CheckpointFileName);
    private string ProviderFile => Path.Combine(_providerDirectory, DurableCargoProvider.ImageFileName);
    private void CapturePair() { _coreBytes = File.ReadAllBytes(CoreFile); _providerBytes = File.ReadAllBytes(ProviderFile); }
    private void RequirePairUnchanged()
    {
        Require(_coreBytes is not null && _providerBytes is not null
            && _coreBytes.AsSpan().SequenceEqual(File.ReadAllBytes(CoreFile))
            && _providerBytes.AsSpan().SequenceEqual(File.ReadAllBytes(ProviderFile)), "The durable pair changed unexpectedly.");
    }
    private void RestorePause() { if (_ownsPause) { Game1.paused = _priorPause; _ownsPause = false; } }

    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O");
        AtomicJson(_reportPath, new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(),
            Scenarios = Array.Empty<object>(),
            HostChecks = (IsIsolation ? CheckIds.Append("save-isolation") : CheckIds).Select(id => new
            {
                Id = _scenarioId + "." + id, Passed = _checks.TryGetValue(id, out bool passed) && passed,
                CapturedAtUtc = captured, Note = _errors.Count == 0 ? "Real host lifecycle with isolated fake cargo." : string.Join("\n", _errors)
            }).ToArray()
        });
        AtomicJson(_diagnosticsPath, new
        {
            requestId = _requestId, scenarioId = _scenarioId, capturedAtUtc = captured,
            saveIdentity = _currentIdentity.Value, sessionIdentity = _currentIdentity.Value.ToString("x16", CultureInfo.InvariantCulture),
            frames = _totalFrames, loads = _loads, stage = _stage.ToString(), logicalTick = _lastLogicalTick,
            receipts = _lastReceipts,
            errors = _errors.ToArray()
        });
    }

    private sealed class PairSnapshot
    {
        private readonly string _directory;
        private readonly byte[][] _bytes;
        private static readonly string[] Files =
        {
            DurableSaveSessionStore.BindingFileName,
            Path.Combine("core", DurableFlowSession.CheckpointFileName),
            Path.Combine("provider", DurableCargoProvider.ImageFileName)
        };

        private PairSnapshot(string directory, byte[][] bytes) { _directory = directory; _bytes = bytes; }
        public static PairSnapshot Capture(string directory) => new(directory,
            Files.Select(file => File.ReadAllBytes(Path.Combine(directory, file))).ToArray());
        public void RequireUnchanged()
        {
            for (int i = 0; i < Files.Length; i++)
            {
                Require(_bytes[i].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(_directory, Files[i]))),
                    "Switching saves changed an inactive save's durable binding or pair.");
            }
        }
    }

    internal static void AtomicJson(string path, object value)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }

    internal static string RuntimeFingerprint()
    {
        Assembly[] assemblies = { typeof(FlowHostAcceptance).Assembly, typeof(FlowRuntime).Assembly, typeof(DurableFlowSession).Assembly };
        return HashText(string.Concat(assemblies.OrderBy(assembly => Path.GetFileName(assembly.Location), StringComparer.Ordinal)
            .Select(assembly => Path.GetFileName(assembly.Location) + "\t" + HashFile(assembly.Location) + "\n")));
    }
    internal static string HashFile(string path)
    { RejectLinks(path); using var stream = File.OpenRead(path); using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant(); }
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonDocument ReadBoundedJson(string path)
    {
        RejectLinks(path);
        using var input = File.OpenRead(path);
        Require(input.Length is > 0 and <= 65536, "The harness marker exceeds its size bound.");
        return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
    }
    internal static void RejectLinks(string path)
    {
        for (string? current = Full(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            Require(info.LinkTarget is null && (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0),
                "Harness paths must not traverse symbolic links.");
        }
    }
    internal static void ValidateSaveTree(string save)
    {
        Require(Directory.Exists(save), "The provisioned save directory is missing.");
        RejectLinks(save);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((save, 0));
        int entries = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                Require(++entries <= 4096, "The provisioned save tree exceeds its entry bound.");
                RejectLinks(path);
                if (Directory.Exists(path))
                {
                    Require(directory.Depth < 16, "The provisioned save tree exceeds its depth bound.");
                    pending.Push((path, directory.Depth + 1));
                }
            }
        }
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing harness setting: " + name);
    internal static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void RequestReturnToTitle()
    {
        MethodInfo method = typeof(Game1).GetMethod("ExitToTitle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            null, new[] { typeof(Action) }, null) ?? throw new MissingMethodException(typeof(Game1).FullName, "ExitToTitle(Action)");
        method.Invoke(method.IsStatic ? null : Game1.game1, new object?[] { null });
    }
}
