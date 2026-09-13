using System;
using System.IO;
using Hatifect.Flow.Diagnostics;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowUiAcceptanceArtifactTests
{
    [Theory]
    [InlineData("directory")]
    [InlineData("screenshot")]
    [InlineData("ui-layer")]
    public void LinkedOutputRejectsBothEncodersBeforeWritingAnyEvidence(string target)
    {
        using var fixture = new OutputFixture();
        string screenshots = Path.Combine(fixture.Artifact, "screenshots");
        string external = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(external);
        string sentinel = Path.Combine(external, "retained.png");
        byte[] original = { 3, 1, 4, 1, 5 };
        File.WriteAllBytes(sentinel, original);
        string first = Path.Combine(screenshots, "flow-ui-frame.png");
        string second = Path.Combine(screenshots, "flow-ui-frame-ui-layer.png");
        if (target == "directory") Directory.CreateSymbolicLink(screenshots, external);
        else
        {
            Directory.CreateDirectory(screenshots);
            File.CreateSymbolicLink(target == "screenshot" ? first : second, sentinel);
        }
        int encodes = 0;

        Assert.Throws<InvalidOperationException>(() => FlowUiAcceptanceObservation.WritePixels(
            fixture.Artifact, "frame", _ => encodes++, _ => encodes++));

        Assert.Equal(0, encodes);
        Assert.Equal(original, File.ReadAllBytes(sentinel));
        Assert.Single(Directory.GetFiles(external));
        if (target != "screenshot") Assert.False(File.Exists(first));
        if (target != "ui-layer") Assert.False(File.Exists(second));
    }

    [Fact]
    public void OwnedOutputPersistsBothEncodersAndRejectsASecondCaptureBeforeEncoding()
    {
        using var fixture = new OutputFixture();
        byte[] canvas = { 7, 8, 9 }, layer = { 2, 4, 6 };
        var result = FlowUiAcceptanceObservation.WritePixels(fixture.Artifact, "Scale75",
            stream => stream.Write(canvas), stream => stream.Write(layer));

        Assert.Equal(Path.Combine(fixture.Artifact, "screenshots", "flow-ui-Scale75.png"), result.Screenshot);
        Assert.Equal(Path.Combine(fixture.Artifact, "screenshots", "flow-ui-Scale75-ui-layer.png"), result.Layer);
        Assert.Equal(canvas, File.ReadAllBytes(result.Screenshot));
        Assert.Equal(layer, File.ReadAllBytes(result.Layer));
        int encodes = 0;
        Assert.Throws<InvalidOperationException>(() => FlowUiAcceptanceObservation.WritePixels(
            fixture.Artifact, "Scale75", _ => encodes++, _ => encodes++));
        Assert.Equal(0, encodes);
        Assert.Equal(canvas, File.ReadAllBytes(result.Screenshot));
        Assert.Equal(layer, File.ReadAllBytes(result.Layer));
    }

    private sealed class OutputFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "hatifect-flow-ui-output-" + Guid.NewGuid().ToString("N"));
        internal string Artifact => Path.Combine(Root, "artifact");
        internal OutputFixture() => Directory.CreateDirectory(Artifact);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
