using System;

namespace Hatifect.Flow.Diagnostics;

// Budgets synchronous detached-evidence reads on the owning update thread.
// Permission to poll is not an acceptance result: every granted poll must read
// and validate the current evidence. No documents or successful results are cached.
internal sealed class FlowPlayerEvidencePoll
{
    internal const int IntervalTicks = 60;
    private int _lastTick = -1;
    private long _nextTick;

    internal bool TryBegin(int tick)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (tick < _lastTick) throw new InvalidOperationException("Evidence polling tick moved backwards.");
        _lastTick = tick;
        if (tick < _nextTick) return false;
        // Use a long so even the largest valid tick cannot wrap the poll budget.
        // Missed intervals grant one poll, never a catch-up batch of file reads.
        _nextTick = (long)tick + IntervalTicks;
        return true;
    }
}

internal enum FlowPlayerReopenTargetState
{
    Waiting,
    RequestWarp,
    Ready
}

// A save/reload can restore the player/camera away from the original fixture even
// though the station tiles themselves remain valid. Final native reopening must not
// publish a stale/offscreen local coordinate. Re-establish the known empty standing
// tile and require its projected point to be stable inside the current UI viewport.
internal sealed class FlowPlayerReopenTargetGate
{
    internal const int RequiredStableSamples = 2;
    private bool _warpRequested;
    private int _stableSamples;
    private double _lastX = double.NaN, _lastY = double.NaN;

    internal FlowPlayerReopenTargetState Observe(bool playerAtStanding, double x, double y, int width, int height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "Reopen target coordinates must be finite.");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Reopen viewport must be positive.");

        if (!playerAtStanding)
        {
            ResetStableSamples();
            if (_warpRequested) return FlowPlayerReopenTargetState.Waiting;
            _warpRequested = true;
            return FlowPlayerReopenTargetState.RequestWarp;
        }

        _warpRequested = false;
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            ResetStableSamples();
            return FlowPlayerReopenTargetState.Waiting;
        }

        if (_stableSamples > 0 && x == _lastX && y == _lastY)
            _stableSamples = Math.Min(RequiredStableSamples, _stableSamples + 1);
        else
        {
            _lastX = x;
            _lastY = y;
            _stableSamples = 1;
        }
        return _stableSamples >= RequiredStableSamples
            ? FlowPlayerReopenTargetState.Ready
            : FlowPlayerReopenTargetState.Waiting;
    }

    private void ResetStableSamples()
    {
        _stableSamples = 0;
        _lastX = _lastY = double.NaN;
    }
}
