using System.Text;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.HotReload;

/// <summary>Compiles both documents privately; the owning host publishes assets at scene acceptance.</summary>
internal sealed class UiSemanticLiveAssets
{
    internal const int MaximumDocumentBytes = 1024 * 1024;
    private readonly Dictionary<UiSymbolId, Entry> _entries;
    private readonly UiCompiler _compiler = new(UiSemanticCatalog.CreateFoundation());
    private readonly Action<UiSymbolId, UiTerminalSectionAssets, Action> _apply;
    private bool _applying;
    private long _version;

    internal UiSemanticLiveAssets(IEnumerable<UiExperienceDefinition> experiences,
        Action<UiSymbolId, UiTerminalSectionAssets, Action> apply)
    {
        _entries = experiences.ToDictionary(experience => experience.Id, experience => new Entry(experience));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    internal UiTerminalSectionAssets For(UiSymbolId experience) => Find(experience).Assets;
    internal UiSemanticReloadResult Failure(UiSymbolId experience, string code, string message)
        => new(experience, false, false, _version, Array.AsReadOnly(new[] { new UiSemanticAssetDiagnostic(code, message) }));

    internal UiSemanticReloadResult Reload(UiSymbolId experience, string? presentation, string? visual)
    {
        Entry entry = Find(experience);
        if (_applying) throw new InvalidOperationException("A live asset update is already in progress.");
        if (presentation == entry.Presentation && visual == entry.Visual)
            return new(experience, true, false, _version, Array.Empty<UiSemanticAssetDiagnostic>());
        if ((presentation != null && Encoding.UTF8.GetByteCount(presentation) > MaximumDocumentBytes) ||
            (visual != null && Encoding.UTF8.GetByteCount(visual) > MaximumDocumentBytes))
            return Failure(experience, "LUI4100", "Each live document is limited to 1 MiB.");
        var diagnostics = new List<UiSemanticAssetDiagnostic>();
        bool valid = true;
        T? Compile<T>(string? text, string kind) where T : UiBoundDefinition
        {
            if (text == null) return null;
            UiCompilationResult result = _compiler.Compile(text, entry.Experience.CreateBindingContext(), $"{experience}#{kind}");
            foreach (var diagnostic in result.Diagnostics)
                diagnostics.Add(new(diagnostic.Id, diagnostic.Message, diagnostic.SourceName));
            if (!result.IsValid) { valid = false; return null; }
            if (result.Definition is T definition) return definition;
            valid = false;
            diagnostics.Add(new("LUI4101", $"Expected a {kind} document."));
            return null;
        }
        var candidate = new UiTerminalSectionAssets(Compile<UiPresentationDefinition>(presentation, "presentation"),
            Compile<UiVisualDefinition>(visual, "visual"));
        if (!valid) return new(experience, false, false, _version, diagnostics.AsReadOnly());
        _applying = true;
        bool accepted = false;
        bool accepting = true;
        int ownerThread = Environment.CurrentManagedThreadId;
        try
        {
            // The callback is private framework metadata publication, invoked exactly once
            // after scene acceptance and before cancellation. No candidate is staged in For.
            _apply(experience, candidate, Accept);
            if (!accepted) throw new InvalidOperationException("The host did not accept the live asset candidate.");
            return new(experience, true, true, _version, diagnostics.AsReadOnly());
        }
        catch (Exception error) when (!accepted && error is InvalidOperationException or ArgumentException)
        {
            return Failure(experience, "LUI4102", error.Message);
        }
        finally { accepting = false; _applying = false; }

        void Accept()
        {
            if (!accepting || accepted || Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("The live asset acceptance is no longer available on this thread.");
            entry.Assets = candidate;
            entry.Presentation = presentation;
            entry.Visual = visual;
            _version++;
            accepted = true;
        }
    }

    private Entry Find(UiSymbolId experience) => _entries.TryGetValue(experience, out Entry? entry) ? entry
        : throw new ArgumentException("The Experience does not belong to this session.", nameof(experience));
    private sealed class Entry
    {
        internal Entry(UiExperienceDefinition experience) => Experience = experience;
        internal UiExperienceDefinition Experience { get; }
        internal UiTerminalSectionAssets Assets { get; set; } = new();
        internal string? Presentation { get; set; }
        internal string? Visual { get; set; }
    }
}

/// <summary>File callbacks only mark dirty; IO, compile and events run during Poll on the UI thread.</summary>
internal sealed class UiSemanticAssetWatches : IDisposable
{
    private Watch[] _watches = Array.Empty<Watch>();
    private bool _disposed;
    private int _nextWatch;

    internal IDisposable Add(UiSymbolId experience, string? presentation, string? visual,
        Action<string?, string?> reload, Action<Exception> failed)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiSemanticAssetWatches));
        if (_watches.Length >= 16) throw new InvalidOperationException("The session watch limit is 16.");
        if (_watches.Any(watch => watch.Experience == experience))
            throw new InvalidOperationException("This Experience already has an asset watch.");
        var watch = new Watch(experience, presentation, visual, reload, failed);
        _watches = _watches.Append(watch).ToArray();
        return new WatchLease(this, watch);
    }
    internal void Poll()
    {
        if (_disposed) return;
        Watch[] snapshot = _watches;
        for (int attempt = 0; attempt < snapshot.Length; attempt++)
        {
            int index = _nextWatch % snapshot.Length;
            _nextWatch = (index + 1) % snapshot.Length;
            if (snapshot[index].Poll()) break;
        }
    }
    public void Dispose()
    {
        foreach (Watch watch in _watches) watch.Dispose();
        _watches = Array.Empty<Watch>();
        _disposed = true;
    }
    private void Remove(Watch watch)
    {
        watch.Dispose();
        _watches = _watches.Where(item => !ReferenceEquals(item, watch)).ToArray();
    }
    private sealed class WatchLease : IDisposable
    {
        private UiSemanticAssetWatches? _owner;
        private readonly Watch _watch;
        internal WatchLease(UiSemanticAssetWatches owner, Watch watch) { _owner = owner; _watch = watch; }
        public void Dispose() { _owner?.Remove(_watch); _owner = null; }
    }
    private sealed class Watch : IDisposable
    {
        private readonly string? _presentation;
        private readonly string? _visual;
        private readonly Action<string?, string?> _reload;
        private readonly Action<Exception> _failed;
        private readonly List<FileSystemWatcher> _watchers = new();
        private int _dirty = 1;
        private int _retryTicks;
        private int _retries;
        private bool _disposed;
        internal UiSymbolId Experience { get; }

        internal Watch(UiSymbolId experience, string? presentation, string? visual,
            Action<string?, string?> reload, Action<Exception> failed)
        {
            Experience = experience;
            if (presentation == null && visual == null) throw new ArgumentException("At least one asset path is required.");
            _presentation = presentation == null ? null : Path.GetFullPath(presentation);
            _visual = visual == null ? null : Path.GetFullPath(visual);
            _reload = reload;
            _failed = failed;
            try
            {
                foreach (string path in new[] { _presentation, _visual }.OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                    { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                    _watchers.Add(watcher);
                    watcher.Changed += OnChanged;
                    watcher.Created += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += OnError;
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch { Dispose(); throw; }
        }
        internal bool Poll()
        {
            if (_disposed) return false;
            if (Interlocked.Exchange(ref _dirty, 0) != 0) { _retries = 0; _retryTicks = 0; }
            else if (_retryTicks == 0 || --_retryTicks > 0) return false;
            string? presentation;
            string? visual;
            try { presentation = Read(_presentation); visual = Read(_visual); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // A writer can release an exclusive handle without another file notification.
                // Retry at 30/60/120 UI polls, then wait for a new file event.
                if (_retries < 3) _retryTicks = 30 << _retries++;
                _failed(error);
                return true;
            }
            _retries = 0;
            _reload(presentation, visual);
            return true;
        }
        private static string? Read(string? path)
        {
            if (path == null) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > UiSemanticLiveAssets.MaximumDocumentBytes) throw new IOException("Live asset exceeds 1 MiB.");
            byte[] data = new byte[(int)stream.Length];
            int read = 0;
            while (read < data.Length)
            {
                int count = stream.Read(data, read, data.Length - read);
                if (count == 0) throw new IOException("Live asset changed during reading.");
                read += count;
            }
            if (stream.ReadByte() != -1) throw new IOException("Live asset changed during reading.");
            string text = new UTF8Encoding(false, true).GetString(data);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        private void OnChanged(object sender, FileSystemEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
        private void OnRenamed(object sender, RenamedEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
        private void OnError(object sender, ErrorEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
