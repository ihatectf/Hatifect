using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

// Owns consumer semantics and an opaque framework surface; never game input or rendering.
internal sealed class ParcelSurface : IDisposable
{
    private readonly IFlowExperience _experience;
    private IUiSemanticSurfaceSession? _surface;
    private bool _closed;
    private bool _disposing;

    internal ParcelSurface(IFlowExperience experience) => _experience = experience;

    internal void Show(IUiSemanticSurfaceApi api)
    {
        if (_closed) throw new InvalidOperationException("The Flow surface is closed.");
        try
        {
            if (_surface is null)
            {
                _surface = api.CreateActiveMenuOverlay(_experience.Experience, new UiSemanticSurfaceOptions(_experience.Experience.Id));
                _surface.Closed += OnClosed;
            }
            _surface.Show();
        }
        catch
        {
            _closed = true;
            _experience.Dispose();
            throw;
        }
    }

    internal bool IsClosed => _closed;

    internal void Pump()
    {
        if (_closed) { Dispose(); return; }
        bool changed = _experience.Pump();
        if (!_experience.IsActive) { Dispose(); return; }
        if (changed) _surface?.Refresh();
        else _surface?.Synchronize();
    }

    public void Dispose()
    {
        _closed = true;
        _experience.Dispose();
        if (_disposing || _surface is null) return;
        _disposing = true;
        try
        {
            // Keep a failing handle so its owner can retry cleanup before another surface is opened.
            _surface.Dispose();
            _surface.Closed -= OnClosed;
            _surface = null;
        }
        finally { _disposing = false; }
    }

    private void OnClosed()
    {
        _closed = true;
        _experience.Dispose();
    }
}
