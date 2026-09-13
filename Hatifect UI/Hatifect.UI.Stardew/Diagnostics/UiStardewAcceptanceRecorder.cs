using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Stardew.Semantic;
using StardewModdingAPI;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Main-thread real-host acceptance recorder. The recorder never decides visual correctness itself:
/// semantic surfaces contribute measured operations up to each rendered frame, and
/// visual/typography/input checks remain explicit operator attestations.
/// </summary>
internal sealed class UiStardewAcceptanceRecorder
{
    private const int ReportFormatVersion = 3;
    private const int PerformanceFormatVersion = 3;
    private const string DefaultReportDirectory = ".acceptance";
    private const string DefaultReportFileName = "host-acceptance-report.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Dictionary<object, string> _semanticSurfaces = new();
    private readonly Dictionary<object, SemanticPendingFrame> _semanticFrames = new();
    private readonly Dictionary<object, long> _semanticLayoutBaselines = new();
    private readonly HostAcceptanceReport _report = new();
    private ActiveScenario? _activeScenario;
    private IModHelper? _helper;
    private IMonitor? _monitor;
    private string _hatifectVersion = string.Empty;
    private string _runtimeFingerprint = string.Empty;

    public static UiStardewAcceptanceRecorder Shared { get; } = new();

    private UiStardewAcceptanceRecorder()
    {
    }

    public bool IsCapturing => _activeScenario is not null;
    public string? ActiveScenarioId => _activeScenario?.Id;

    public void Configure(IModHelper helper, IMonitor monitor, string hatifectVersion)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _hatifectVersion = string.IsNullOrWhiteSpace(hatifectVersion) ? "unknown" : hatifectVersion.Trim();
        try
        {
            _runtimeFingerprint = UiRuntimeFingerprint.Compute(helper.DirectoryPath);
        }
        catch (Exception ex)
        {
            _runtimeFingerprint = string.Empty;
            _monitor.Log($"Hatifect UI runtime fingerprint could not be computed; host evidence will remain invalid: {ex.Message}", LogLevel.Error);
        }

        _report.FormatVersion = ReportFormatVersion;
        _report.PerformanceFormatVersion = PerformanceFormatVersion;
        _report.HatifectVersion = _hatifectVersion;
        _report.RuntimeFingerprintAlgorithm = UiRuntimeFingerprint.Algorithm;
        _report.RuntimeFingerprint = _runtimeFingerprint;
        _report.GameVersion = StardewValley.Game1.GetVersionString();
        _report.SmapiVersion = Constants.ApiVersion.ToString();
        LoadExistingReport();
    }

    public void RegisterSemanticSurface(object surface, string hostKind)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _semanticSurfaces[surface] = string.IsNullOrWhiteSpace(hostKind)
            ? "semantic-unknown"
            : hostKind.Trim();
        _semanticFrames.Remove(surface);
        _semanticLayoutBaselines.Remove(surface);
    }

    public void UnregisterSemanticSurface(object surface)
    {
        if (surface is null) return;
        _semanticSurfaces.Remove(surface);
        _semanticFrames.Remove(surface);
        _semanticLayoutBaselines.Remove(surface);
    }

    public SemanticOperationSample BeginSemanticOperation(object surface, long layoutBuilds)
    {
        if (_activeScenario is null || !_semanticSurfaces.ContainsKey(surface))
            return default;
        if (layoutBuilds < 0)
            throw new ArgumentOutOfRangeException(nameof(layoutBuilds));
        _semanticLayoutBaselines.TryAdd(surface, layoutBuilds);
        return new SemanticOperationSample(
            this,
            surface,
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread());
    }

    private void CompleteSemanticOperation(
        object surface,
        long startedTicks,
        long startedAllocatedBytes,
        bool completesFrame,
        long layoutBuilds)
    {
        ActiveScenario? active = _activeScenario;
        if (active is null || !_semanticSurfaces.TryGetValue(surface, out string? kind))
            return;

        if (!_semanticFrames.TryGetValue(surface, out SemanticPendingFrame? pending))
        {
            pending = new SemanticPendingFrame();
            _semanticFrames.Add(surface, pending);
        }

        pending.Ticks += Math.Max(0, Stopwatch.GetTimestamp() - startedTicks);
        pending.AllocatedBytes += Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - startedAllocatedBytes);
        if (!completesFrame)
            return;

        active.FrameTimesMs.Add(pending.Ticks * 1000d / Stopwatch.Frequency);
        active.AllocatedBytes += pending.AllocatedBytes;
        long previousLayoutBuilds = _semanticLayoutBaselines.TryGetValue(surface, out long baseline)
            ? baseline
            : layoutBuilds;
        long layoutExecutions = layoutBuilds >= previousLayoutBuilds
            ? layoutBuilds - previousLayoutBuilds
            : 0;
        if (layoutExecutions == 0)
        {
            active.MeasureCacheHits++;
            active.ArrangeCacheHits++;
        }
        else
        {
            active.MeasureExecutions += layoutExecutions;
            active.ArrangeExecutions += layoutExecutions;
        }
        _semanticLayoutBaselines[surface] = layoutBuilds;
        active.SurfaceKinds.Add(kind);
        active.ThemeIds.Add(UiSemanticStardewTheme.Id);
        active.DisplayMetrics.Add(CaptureSemanticDisplayMetrics());
        pending.Reset();
    }

    public void ExecuteCommand(string[] args)
    {
        if (_helper is null || _monitor is null)
            return;

        string action = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "status":
                LogStatus();
                return;

            case "begin":
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                {
                    _monitor.Log("Usage: hatifect_ui_acceptance begin <scenario-id>", LogLevel.Warn);
                    return;
                }
                BeginScenario(args[1]);
                return;

            case "end":
                EndScenario();
                return;

            case "check":
                RecordCheck(args);
                return;

            case "save":
                Save(args.Length >= 2 ? string.Join(" ", args.Skip(1)) : null);
                return;

            case "clear":
                Clear();
                return;

            default:
                _monitor.Log(
                    "Usage: hatifect_ui_acceptance [status|begin <scenario>|end|check <id> <pass|fail> [note]|save [path]|clear]",
                    LogLevel.Warn);
                return;
        }
    }

    internal void ClearAutomatedEvidence() => Clear();

    internal void BeginAutomatedScenario(string scenarioId) => BeginScenario(scenarioId);

    internal void EndAutomatedScenario() => EndScenario();

    internal void RecordAutomatedCheck(string checkId, bool passed, string note)
        => RecordCheck(new[] { "check", checkId, passed ? "pass" : "fail", note });

    internal void SaveAutomatedEvidence() => Save(null);

    private void BeginScenario(string scenarioId)
    {
        if (_monitor is null) return;
        string id = scenarioId.Trim();
        if (_activeScenario is not null)
        {
            _monitor.Log($"Acceptance scenario '{_activeScenario.Id}' is already running; end it before starting '{id}'.", LogLevel.Warn);
            return;
        }

        _activeScenario = new ActiveScenario(id);
        _semanticFrames.Clear();
        _semanticLayoutBaselines.Clear();

        _monitor.Log(
            $"Hatifect UI acceptance capture started for '{id}'. Registered semantic surfaces: {_semanticSurfaces.Count}. Exercise the scenario, then run 'hatifect_ui_acceptance end'.",
            LogLevel.Info);
    }

    private void EndScenario()
    {
        if (_monitor is null) return;
        ActiveScenario? active = _activeScenario;
        if (active is null)
        {
            _monitor.Log("No Hatifect UI acceptance scenario is running.", LogLevel.Warn);
            return;
        }

        _activeScenario = null;
        _semanticFrames.Clear();
        _semanticLayoutBaselines.Clear();
        PerformanceScenarioResult result = active.BuildResult();
        ReplaceScenario(result);
        Save(null, logSuccess: false);
        _monitor.Log(
            $"Acceptance scenario '{result.Id}' captured {result.Frames} rendered samples; p95={result.P95UiThreadMs:F3} ms, p99={result.P99UiThreadMs:F3} ms, allocations={result.SteadyStateAllocatedBytesPerFrame:F0} B/frame.",
            LogLevel.Info);
    }

    private void RecordCheck(string[] args)
    {
        if (_monitor is null) return;
        if (args.Length < 3)
        {
            _monitor.Log("Usage: hatifect_ui_acceptance check <check-id> <pass|fail> [note]", LogLevel.Warn);
            return;
        }

        string id = args[1].Trim();
        string verdict = args[2].Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || verdict is not ("pass" or "fail"))
        {
            _monitor.Log("Usage: hatifect_ui_acceptance check <check-id> <pass|fail> [note]", LogLevel.Warn);
            return;
        }

        string note = args.Length > 3 ? string.Join(" ", args.Skip(3)) : string.Empty;
        HostCheckResult result = new()
        {
            Id = id,
            Passed = verdict == "pass",
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            ThemeId = UiSemanticStardewTheme.Id,
            ReducedMotion = false,
            DisplayMetrics = CaptureDisplayMetrics(),
            Note = note
        };
        ReplaceCheck(result);
        Save(null, logSuccess: false);
        _monitor.Log(
            $"Host check '{id}' recorded as {(result.Passed ? "PASS" : "FAIL")} under theme '{result.ThemeId}'.",
            result.Passed ? LogLevel.Info : LogLevel.Warn);
    }

    private void LogStatus()
    {
        if (_monitor is null) return;
        string active = _activeScenario is null ? "none" : _activeScenario.Id;
        _monitor.Log(
            $"Hatifect UI acceptance: active={active}; registered semantic surfaces={_semanticSurfaces.Count}; " +
            $"captured scenarios={_report.Scenarios.Count}; " +
            $"host checks={_report.HostChecks.Count}; report='{DefaultReportPath()}'.",
            LogLevel.Info);
    }

    private void Clear()
    {
        if (_monitor is null) return;
        _activeScenario = null;
        _semanticFrames.Clear();
        _semanticLayoutBaselines.Clear();
        _report.Scenarios.Clear();
        _report.HostChecks.Clear();
        string path = DefaultReportPath();
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            _monitor.Log("Hatifect UI host-acceptance evidence cleared.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Could not remove host-acceptance report '{path}': {ex.Message}", LogLevel.Warn);
        }
    }

    private void Save(string? requestedPath, bool logSuccess = true)
    {
        if (_helper is null || _monitor is null) return;
        string path = ResolveReportPath(requestedPath);
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _report.FormatVersion = ReportFormatVersion;
            _report.PerformanceFormatVersion = PerformanceFormatVersion;
            _report.CapturedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            _report.HatifectVersion = _hatifectVersion;
            _report.RuntimeFingerprintAlgorithm = UiRuntimeFingerprint.Algorithm;
            _report.RuntimeFingerprint = _runtimeFingerprint;
            _report.GameVersion = StardewValley.Game1.GetVersionString();
            _report.SmapiVersion = Constants.ApiVersion.ToString();
            _report.ActiveThemeId = UiSemanticStardewTheme.Id;
            _report.ReducedMotion = false;
            File.WriteAllText(path, JsonSerializer.Serialize(_report, JsonOptions));
            if (logSuccess)
                _monitor.Log($"Hatifect UI host-acceptance report saved to '{path}'.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Could not save Hatifect UI host-acceptance report '{path}': {ex.Message}", LogLevel.Error);
        }
    }

    private void LoadExistingReport()
    {
        if (_monitor is null) return;
        string path = DefaultReportPath();
        if (!File.Exists(path)) return;
        try
        {
            HostAcceptanceReport? existing = JsonSerializer.Deserialize<HostAcceptanceReport>(File.ReadAllText(path), JsonOptions);
            if (existing is null
                || existing.FormatVersion != ReportFormatVersion
                || existing.PerformanceFormatVersion != PerformanceFormatVersion
                || !string.Equals(existing.HatifectVersion, _hatifectVersion, StringComparison.Ordinal)
                || !string.Equals(existing.RuntimeFingerprintAlgorithm, UiRuntimeFingerprint.Algorithm, StringComparison.Ordinal)
                || !string.Equals(existing.RuntimeFingerprint, _runtimeFingerprint, StringComparison.Ordinal))
            {
                _monitor.Log("Existing Hatifect UI host-acceptance report targets a different format/version and will not be reused.", LogLevel.Trace);
                return;
            }

            _report.Scenarios.Clear();
            _report.Scenarios.AddRange(existing.Scenarios ?? new List<PerformanceScenarioResult>());
            _report.HostChecks.Clear();
            _report.HostChecks.AddRange(existing.HostChecks ?? new List<HostCheckResult>());
        }
        catch (Exception ex)
        {
            _monitor.Log($"Existing Hatifect UI host-acceptance report could not be read: {ex.Message}", LogLevel.Warn);
        }
    }

    private string ResolveReportPath(string? requestedPath)
    {
        if (_helper is null) throw new InvalidOperationException("Acceptance recorder is not configured.");
        if (string.IsNullOrWhiteSpace(requestedPath))
            return DefaultReportPath();
        string value = requestedPath.Trim();
        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(_helper.DirectoryPath, value));
    }

    private string DefaultReportPath()
    {
        if (_helper is null) return DefaultReportFileName;
        return Path.Combine(_helper.DirectoryPath, DefaultReportDirectory, DefaultReportFileName);
    }

    private DisplayMetricsEvidence CaptureDisplayMetrics()
    {
        if (_semanticSurfaces.Count > 0)
            return CaptureSemanticDisplayMetrics();
        return DisplayMetricsEvidence.Default;
    }

    private static DisplayMetricsEvidence CaptureSemanticDisplayMetrics()
    {
        int logicalWidth = Math.Max(1, StardewValley.Game1.uiViewport.Width);
        int logicalHeight = Math.Max(1, StardewValley.Game1.uiViewport.Height);
        int pixelWidth = Math.Max(1, StardewValley.Game1.graphics.GraphicsDevice.Viewport.Width);
        int pixelHeight = Math.Max(1, StardewValley.Game1.graphics.GraphicsDevice.Viewport.Height);
        return new DisplayMetricsEvidence(
            logicalWidth,
            logicalHeight,
            (float)pixelWidth / logicalWidth,
            (float)pixelHeight / logicalHeight);
    }

    private void ReplaceScenario(PerformanceScenarioResult result)
    {
        int index = _report.Scenarios.FindIndex(item => string.Equals(item.Id, result.Id, StringComparison.Ordinal));
        if (index >= 0) _report.Scenarios[index] = result; else _report.Scenarios.Add(result);
    }

    private void ReplaceCheck(HostCheckResult result)
    {
        int index = _report.HostChecks.FindIndex(item => string.Equals(item.Id, result.Id, StringComparison.Ordinal));
        if (index >= 0) _report.HostChecks[index] = result; else _report.HostChecks.Add(result);
    }

    private sealed class ActiveScenario
    {
        public ActiveScenario(string id) => Id = id;
        public string Id { get; }
        public List<double> FrameTimesMs { get; } = new();
        public long AllocatedBytes { get; set; }
        public long MeasureExecutions { get; set; }
        public long MeasureCacheHits { get; set; }
        public long ArrangeExecutions { get; set; }
        public long ArrangeCacheHits { get; set; }
        public HashSet<string> SurfaceKinds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThemeIds { get; } = new(StringComparer.Ordinal);
        public HashSet<DisplayMetricsEvidence> DisplayMetrics { get; } = new();

        public PerformanceScenarioResult BuildResult()
        {
            double[] ordered = FrameTimesMs.OrderBy(value => value).ToArray();
            int frames = ordered.Length;
            return new PerformanceScenarioResult
            {
                Id = Id,
                Frames = frames,
                P95UiThreadMs = Percentile(ordered, 0.95),
                P99UiThreadMs = Percentile(ordered, 0.99),
                SteadyStateAllocatedBytesPerFrame = frames == 0 ? 0d : (double)AllocatedBytes / frames,
                MeasureCacheMissRatio = MissRatio(MeasureExecutions, MeasureCacheHits),
                ArrangeCacheMissRatio = MissRatio(ArrangeExecutions, ArrangeCacheHits),
                SurfaceKinds = SurfaceKinds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                ThemeIds = ThemeIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                DisplayMetrics = DisplayMetrics
                    .OrderBy(value => value.LogicalWidth)
                    .ThenBy(value => value.LogicalHeight)
                    .ThenBy(value => value.PixelScaleX)
                    .ThenBy(value => value.PixelScaleY)
                    .ToArray()
            };
        }

        private static double Percentile(IReadOnlyList<double> ordered, double percentile)
        {
            if (ordered.Count == 0) return 0d;
            int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
            return ordered[index];
        }

        private static double MissRatio(long executions, long hits)
        {
            long total = executions + hits;
            return total <= 0 ? 0d : (double)executions / total;
        }
    }

    private sealed class SemanticPendingFrame
    {
        public long Ticks { get; set; }
        public long AllocatedBytes { get; set; }

        public void Reset()
        {
            Ticks = 0;
            AllocatedBytes = 0;
        }
    }

    internal readonly struct SemanticOperationSample
    {
        private readonly UiStardewAcceptanceRecorder? _recorder;
        private readonly object? _surface;
        private readonly long _startedTicks;
        private readonly long _startedAllocatedBytes;

        internal SemanticOperationSample(
            UiStardewAcceptanceRecorder recorder,
            object surface,
            long startedTicks,
            long startedAllocatedBytes)
        {
            _recorder = recorder;
            _surface = surface;
            _startedTicks = startedTicks;
            _startedAllocatedBytes = startedAllocatedBytes;
        }

        public void Complete(bool completesFrame = false, long layoutBuilds = 0)
        {
            if (_recorder is null || _surface is null)
                return;
            if (completesFrame && layoutBuilds < 0)
                throw new ArgumentOutOfRangeException(nameof(layoutBuilds));
            _recorder.CompleteSemanticOperation(
                _surface,
                _startedTicks,
                _startedAllocatedBytes,
                completesFrame,
                layoutBuilds);
        }
    }

    private sealed class HostAcceptanceReport
    {
        public int FormatVersion { get; set; } = ReportFormatVersion;
        public int PerformanceFormatVersion { get; set; } = UiStardewAcceptanceRecorder.PerformanceFormatVersion;
        public string CapturedAtUtc { get; set; } = string.Empty;
        public string HatifectVersion { get; set; } = string.Empty;
        public string RuntimeFingerprintAlgorithm { get; set; } = UiRuntimeFingerprint.Algorithm;
        public string RuntimeFingerprint { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;
        public string SmapiVersion { get; set; } = string.Empty;
        public string ActiveThemeId { get; set; } = string.Empty;
        public bool ReducedMotion { get; set; }
        public List<PerformanceScenarioResult> Scenarios { get; set; } = new();
        public List<HostCheckResult> HostChecks { get; set; } = new();
    }

    private sealed class PerformanceScenarioResult
    {
        public string Id { get; set; } = string.Empty;
        public int Frames { get; set; }
        public double P95UiThreadMs { get; set; }
        public double P99UiThreadMs { get; set; }
        public double SteadyStateAllocatedBytesPerFrame { get; set; }
        public double MeasureCacheMissRatio { get; set; }
        public double ArrangeCacheMissRatio { get; set; }
        public string[] SurfaceKinds { get; set; } = Array.Empty<string>();
        public string[] ThemeIds { get; set; } = Array.Empty<string>();
        public DisplayMetricsEvidence[] DisplayMetrics { get; set; } = Array.Empty<DisplayMetricsEvidence>();
    }

    private sealed class HostCheckResult
    {
        public string Id { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public string CapturedAtUtc { get; set; } = string.Empty;
        public string ThemeId { get; set; } = string.Empty;
        public bool ReducedMotion { get; set; }
        public DisplayMetricsEvidence DisplayMetrics { get; set; } = DisplayMetricsEvidence.Default;
        public string Note { get; set; } = string.Empty;
    }

    private readonly record struct DisplayMetricsEvidence(int LogicalWidth, int LogicalHeight, float PixelScaleX, float PixelScaleY)
    {
        public static DisplayMetricsEvidence Default { get; } = new(1, 1, 1f, 1f);
    }
}
