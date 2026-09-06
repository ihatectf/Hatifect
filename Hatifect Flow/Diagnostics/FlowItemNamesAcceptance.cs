using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Hatifect.Flow.Inventory;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Request-owned, read-only native content acceptance; never opens a production transport session.
internal sealed class FlowItemNamesAcceptance : IDisposable
{
    internal const string Scenario = "flow.ui.names";
    private static readonly string[] Checks = { "loaded", "english", "russian", "capture-state", "retained", "restored", "read-only" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    private readonly List<object> _captures = new(2);
    private LocalizedContentManager.LanguageCode _originalLanguage;
    private ModLanguage? _originalModLanguage;
    private string? _originalLocale;
    private readonly string _saveHash;
    private UiLocalizedText? _englishCapture;
    private Stage _stage;
    private int _frames;
    private bool _languageOwned;
    private bool _disposed;

    private enum Stage { Startup, Loading, English, Russian, Verify, Exit }

    private FlowItemNamesAcceptance(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
        _request = ReadAcceptanceRequest(helper, Scenario);
        _fingerprint = RuntimeFingerprint();
        _saveHash = FlowAcceptanceSaveTree.Fingerprint(_request.SavePath);
    }

    internal static FlowItemNamesAcceptance? TryCreate(IModHelper helper, IMonitor monitor)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor) : null;

    internal void OnSaveLoaded()
    {
        Require(_stage == Stage.Loading && Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer,
            "Item names acceptance requires the requested single-player load.");
        Require(Game1.uniqueIDForThisGame == 4242424242 && Constants.SaveFolderName == "HatifectHarness_4242424242",
            "Item names acceptance loaded a foreign logical world.");
        ValidateSaveTree(_request.SavePath);
        ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
        RequireSaveUnchanged();
        _passed.Add("loaded");
        _originalLanguage = LocalizedContentManager.CurrentLanguageCode;
        _originalModLanguage = LocalizedContentManager.CurrentModLanguage;
        _originalLocale = LocalizedContentManager.LanguageCodeString(_originalLanguage);
        _languageOwned = true;
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        _stage = Stage.English;
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 7200, "Item names acceptance exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                    _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath));
                    Game1.exitActiveMenu();
                    break;
                case Stage.English:
                    _englishCapture = Capture(LocalizedContentManager.LanguageCode.en, "Copper Ore");
                    _passed.Add("english");
                    LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.ru;
                    _stage = Stage.Russian;
                    break;
                case Stage.Russian:
                    var russian = Capture(LocalizedContentManager.LanguageCode.ru, "Медная руда");
                    Require(_englishCapture!.Fallback == "Copper Ore" && russian.Fallback == "Медная руда"
                        && _englishCapture.Resolve("en") == russian.Resolve("en")
                        && _englishCapture.Resolve("ru-RU") == russian.Resolve("ru-RU"),
                        "A retained item catalog changed with a later native locale/capture.");
                    _passed.Add("russian");
                    _passed.Add("retained");
                    _passed.Add("capture-state");
                    RestoreLanguage();
                    _stage = Stage.Verify;
                    break;
                case Stage.Verify:
                    RequireOriginalLanguage();
                    RequireSaveUnchanged();
                    _passed.Add("restored");
                    _passed.Add("read-only");
                    WriteReport();
                    _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private UiLocalizedText Capture(LocalizedContentManager.LanguageCode language, string fallback)
    {
        Require(LocalizedContentManager.CurrentLanguageCode == language, "The driver's selected ambient locale is not active.");
        // Warm only the ordinary fallback: a native ItemRegistry cache miss can legitimately populate its ambient cache.
        string nativeName = ItemRegistry.GetDataOrErrorItem("(O)378").DisplayName;
        var map = LocalizedContentManager.localizedAssetNames.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        UiLocalizedText names = FlowItemNames.Capture("(O)378");
        Require(nativeName == fallback && names.Fallback == fallback, "The real ItemRegistry fallback did not follow the selected locale.");
        Require(names.Resolve("en") == "Copper Ore" && names.Resolve("en-US") == "Copper Ore"
            && names.Resolve("ru") == "Медная руда" && names.Resolve("ru-RU") == "Медная руда",
            "Exact English/Russian native name capture failed.");
        Require(LocalizedContentManager.CurrentLanguageCode == language
            && map.SequenceEqual(LocalizedContentManager.localizedAssetNames.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
            "Capturing names changed the current locale or the public game's localized asset map after fallback warmup.");
        _captures.Add(new { ambient = language.ToString(), fallback = names.Fallback,
            english = names.Resolve("en"), russian = names.Resolve("ru-RU"), publicAssetMapEntries = map.Length });
        return names;
    }

    private void RequireSaveUnchanged()
        => Require(_saveHash == FlowAcceptanceSaveTree.Fingerprint(_request.SavePath), "Read-only name capture changed the copied save tree.");

    private void RestoreLanguage()
    {
        if (!_languageOwned) return;
        if (_originalLanguage == LocalizedContentManager.LanguageCode.mod)
            LocalizedContentManager.SetModLanguage(_originalModLanguage
                ?? throw new InvalidOperationException("The original custom language descriptor is unavailable."));
        else
            LocalizedContentManager.CurrentLanguageCode = _originalLanguage;
        RequireOriginalLanguage();
        _languageOwned = false;
    }

    private void RequireOriginalLanguage()
        => Require(LocalizedContentManager.CurrentLanguageCode == _originalLanguage
            && ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _originalModLanguage)
            && LocalizedContentManager.LanguageCodeString(_originalLanguage) == _originalLocale,
            "The original language identity and custom descriptor were not restored.");

    internal void Fail(Exception error)
    {
        _errors.Add(error.ToString());
        _monitor.Log("Flowline " + Scenario + " failed: " + error, LogLevel.Error);
        try { RestoreLanguage(); }
        catch (Exception restore) { _errors.Add("Locale restoration failed: " + restore); }
        try { WriteReport(); }
        finally { _stage = Stage.Exit; }
    }

    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        AtomicJson(Path.Combine(_helper.DirectoryPath, ".acceptance", "host-acceptance-report.json"), new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(), Scenarios = Array.Empty<object>(),
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id),
                CapturedAtUtc = captured, Note = _errors.Count == 0 ? "Actual read-only ItemRegistry/SMAPI EN/RU capture, retained catalog and restored locale." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-item-names.json"), new
        {
            requestId = _request.RunId, scenarioId = Scenario, frames = _frames,
            originalLocale = _originalLocale, restored = !_languageOwned,
            initialSaveHash = _saveHash, captures = _captures.ToArray(), errors = _errors.ToArray()
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        RestoreLanguage();
        _disposed = true;
    }
}
