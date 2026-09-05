using StardewModdingAPI.Events;

namespace Hatifect.UI.Stardew;

/// <summary>One polling subscription, including cleanup after an event accessor partially succeeds.</summary>
internal sealed class UiSemanticAssetWatchBinding : IDisposable
{
    private readonly IGameLoopEvents _events;
    private readonly Func<bool> _isOwner;
    private readonly EventHandler<UpdateTickedEventArgs> _update;
    private bool _attached;
    private bool _active;

    internal UiSemanticAssetWatchBinding(IGameLoopEvents events, Func<bool> isOwner, Action poll)
    {
        _events = events;
        _isOwner = isOwner;
        _update = (_, _) => { if (_active && _isOwner()) poll(); };
    }

    internal void Activate()
    {
        if (!_isOwner()) throw new InvalidOperationException("Asset watches belong to their creating screen.");
        if (_active) return;
        if (_attached) throw new InvalidOperationException("Complete event cleanup before activation.");
        try
        {
            _attached = true;
            _events.UpdateTicked += _update;
            _active = true;
        }
        catch (Exception activation)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(activation, cleanup); }
            throw;
        }
    }

    public void Dispose()
    {
        _active = false;
        if (!_attached) return;
        _events.UpdateTicked -= _update;
        _attached = false;
    }
}
