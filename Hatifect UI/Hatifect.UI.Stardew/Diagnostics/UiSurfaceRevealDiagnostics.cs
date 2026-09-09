using System;
using System.IO;
using System.Text.Json;
using Hatifect.UI.Runtime.Diagnostics;

namespace Hatifect.UI.Stardew;

/// <summary>Retains one failed exact-harness attempt; never observes sources or changes UI state.</summary>
internal static class UiSurfaceRevealDiagnostics
{
    internal static void RecordFailure(UiSurfaceRevealFailure failure)
    {
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled) return;
        string? artifacts = Environment.GetEnvironmentVariable("HATIFECT_TEST_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(artifacts)) return;
        string directory = Path.Combine(artifacts, "diagnostics");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ui-surface-reveal-failure.json");
        string json = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            runId = Environment.GetEnvironmentVariable("HATIFECT_TEST_RUN_ID"),
            scenario = Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO"),
            failure
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
