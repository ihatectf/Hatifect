using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace Hatifect.TestHarness;

// Activated by the required UI game adapter for both UI and Flow harness scenarios.
// The runner owns this policy for the isolated process and restores persisted options on exit.
internal sealed class AutomatedBackgroundProgress : IDisposable
{
    private readonly IModHelper _helper;
    private bool _disposed;

    private AutomatedBackgroundProgress(IModHelper helper)
    {
        _helper = helper;
        _helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
    }

    internal static AutomatedBackgroundProgress? TryAttach(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        if (Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_BACKGROUND_PROGRESS") != "1")
        {
            return null;
        }

        return new AutomatedBackgroundProgress(helper);
    }

    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        // Loading a save replaces Options, including its pause preference. Keep the requested
        // policy across load/title transitions without retaining any save's Options instance.
        if (!_disposed && Game1.options is { pauseWhenOutOfFocus: true } options)
        {
            options.pauseWhenOutOfFocus = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _helper.Events.GameLoop.UpdateTicking -= OnUpdateTicking;
    }
}
