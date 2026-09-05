using System;

namespace Hatifect.Flow.Sessions;

// SMAPI invokes game-loop events for individual split-screen contexts. Only the screen that
// opened the authoritative session may advance/save/close it; another screen cannot replace it.
internal sealed class FlowSessionOwner : IDisposable
{
    private int? _screen;
    private FlowGameSession? _session;
    internal FlowGameSession? ForScreen(int screen) => _screen == screen ? _session : null;
    internal FlowGameSession? Current => _session;

    internal FlowGameSession? Open(int screen, bool authoritative, Func<FlowGameSession> create)
    {
        if (!authoritative) return null;
        if (_session is not null && _screen != screen)
            throw new InvalidOperationException("Another screen already owns the authoritative Flow session.");
        Close(screen);
        FlowGameSession session = create();
        _screen = screen;
        _session = session;
        return session;
    }

    internal void Close(int screen)
    {
        if (_screen != screen) return;
        _session?.Dispose();
        _session = null;
        _screen = null;
    }
    public void Dispose()
    {
        if (_screen is int screen) Close(screen);
    }
}
