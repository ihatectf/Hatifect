using System.Threading;
using Hatifect.UI.Experience;

namespace Hatifect.UI.Stardew;

/// <summary>Coalesces source notifications without reading source values or entering the UI host.</summary>
internal sealed class UiSemanticSourceChangeBinding : IDisposable
{
    private readonly IUiSemanticSource[] _sources;
    private readonly bool[] _attached;
    private long _version;
    private long _acceptedVersion;
    private volatile bool _active;
    private bool _activating;
    private bool _detaching;
    private bool _activationCancelled;
    private bool _disposed;

    internal UiSemanticSourceChangeBinding(IEnumerable<IUiSemanticSource> sources)
    {
        _sources = new HashSet<IUiSemanticSource>(sources, ReferenceEqualityComparer.Instance).ToArray();
        _attached = new bool[_sources.Length];
    }

    internal long Version => Volatile.Read(ref _version);
    internal bool HasChanges => _active && Version != Volatile.Read(ref _acceptedVersion);

    internal void Accept(long version) => Volatile.Write(ref _acceptedVersion, version);

    internal void Activate()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiSemanticSourceChangeBinding));
        if (_active) return;
        if (_activating) throw new InvalidOperationException("Source subscriptions are already being activated.");
        if (_detaching || Array.IndexOf(_attached, true) >= 0)
            throw new InvalidOperationException("Complete source subscription cleanup before activation.");
        _activating = true;
        _activationCancelled = false;
        try
        {
            for (int index = 0; index < _sources.Length; index++)
            {
                // An event accessor may attach and then throw, or retire this binding reentrantly.
                _attached[index] = true;
                try { _sources[index].Changed += OnChanged; }
                finally { if (_activationCancelled) _attached[index] = true; }
                if (_disposed) throw new ObjectDisposedException(nameof(UiSemanticSourceChangeBinding));
                if (_activationCancelled) throw new InvalidOperationException("Source subscription activation was cancelled.");
            }
            _active = true;
            // Re-read on first Show, including changes made since surface construction or in add accessors.
            Interlocked.Increment(ref _version);
        }
        catch (Exception activation)
        {
            try { Deactivate(); }
            catch (Exception cleanup) { throw new AggregateException(activation, cleanup); }
            throw;
        }
        finally { _activating = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        Deactivate();
    }

    // A rejected first Show has not dismissed its session and may retry against a new native owner.
    internal void Deactivate()
    {
        _active = false;
        _activationCancelled = true;
        if (_detaching) return;
        _detaching = true;
        List<Exception>? failures = null;
        try
        {
            for (int index = 0; index < _sources.Length; index++)
            {
                if (!_attached[index]) continue;
                try { _sources[index].Changed -= OnChanged; _attached[index] = false; }
                catch (Exception error) { (failures ??= new()).Add(error); }
            }
        }
        finally { _detaching = false; }
        if (failures is not null) throw new AggregateException("Semantic source subscriptions failed to release.", failures);
    }

    private void OnChanged()
    {
        if (_active) Interlocked.Increment(ref _version);
    }
}
