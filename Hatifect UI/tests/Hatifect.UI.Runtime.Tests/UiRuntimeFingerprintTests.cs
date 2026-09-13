using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class UiRuntimeFingerprintTests
{
    private const string SemanticRuntimeFingerprint =
        "ae6d34720e43ad671cb692072bbca3d156e9ad164033a301470dab5a564665ea";

    [Fact]
    public void SemanticRuntimeWithoutAssetsHasStableCompatibleIdentity()
    {
        using var runtime = new RuntimeInstallation();

        string fingerprint = UiRuntimeFingerprint.Compute(runtime.Root);

        Assert.Equal("sha256-runtime-v2", UiRuntimeFingerprint.Algorithm);
        Assert.Equal(SemanticRuntimeFingerprint, fingerprint);
        Assert.Equal(fingerprint, UiRuntimeFingerprint.Compute(runtime.Root));
    }

    [Theory]
    [InlineData("Hatifect.UI.Stardew.dll")]
    [InlineData("Hatifect.UI.Experience.dll")]
    [InlineData("Hatifect.UI.Language.dll")]
    [InlineData("Hatifect.UI.Planning.dll")]
    [InlineData("Hatifect.UI.Runtime.dll")]
    [InlineData("Hatifect.UI.Semantics.dll")]
    [InlineData("Hatifect.UI.Tooling.dll")]
    [InlineData("Hatifect.UI.DevTools.dll")]
    [InlineData("manifest.json")]
    public void MissingSemanticRuntimeFileRejectsIncompleteInstallation(string name)
    {
        using var runtime = new RuntimeInstallation();
        string missingPath = Path.Combine(runtime.Root, name);
        File.Delete(missingPath);

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => UiRuntimeFingerprint.Compute(runtime.Root));

        Assert.Equal(missingPath, error.FileName);
    }

    [Fact]
    public void AssetsFileRejectsMalformedInstallation()
    {
        using var runtime = new RuntimeInstallation();
        File.WriteAllText(Path.Combine(runtime.Root, "assets"), "not a directory");

        Assert.Throws<IOException>(() => UiRuntimeFingerprint.Compute(runtime.Root));
    }

    [Fact]
    public void OptionalNestedAssetsAndTheirContentParticipateInIdentity()
    {
        using var runtime = new RuntimeInstallation();
        string assetsRoot = Path.Combine(runtime.Root, "assets");
        string nestedDirectory = Path.Combine(assetsRoot, "icons");
        Directory.CreateDirectory(nestedDirectory);
        string assetPath = Path.Combine(nestedDirectory, "item.bin");
        File.WriteAllBytes(assetPath, new byte[] { 1, 2, 3, 4 });

        string withAsset = UiRuntimeFingerprint.Compute(runtime.Root);
        File.WriteAllBytes(assetPath, new byte[] { 4, 3, 2, 1 });
        string withChangedAsset = UiRuntimeFingerprint.Compute(runtime.Root);

        Assert.NotEqual(SemanticRuntimeFingerprint, withAsset);
        Assert.NotEqual(withAsset, withChangedAsset);
        Assert.Equal(withChangedAsset, UiRuntimeFingerprint.Compute(runtime.Root));

        File.Delete(assetPath);

        Assert.Equal(SemanticRuntimeFingerprint, UiRuntimeFingerprint.Compute(runtime.Root));
    }

    private sealed class RuntimeInstallation : IDisposable
    {
        private static readonly string[] SemanticAssemblies =
        {
            "Hatifect.UI.Stardew.dll",
            "Hatifect.UI.Experience.dll",
            "Hatifect.UI.Language.dll",
            "Hatifect.UI.Planning.dll",
            "Hatifect.UI.Runtime.dll",
            "Hatifect.UI.Semantics.dll",
            "Hatifect.UI.Tooling.dll",
            "Hatifect.UI.DevTools.dll"
        };

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "hatifect-ui-fingerprint-tests-" + Guid.NewGuid().ToString("N"));

        public RuntimeInstallation()
        {
            Directory.CreateDirectory(Root);
            foreach (string assembly in SemanticAssemblies)
                File.WriteAllText(Path.Combine(Root, assembly), "artifact:" + assembly + "\n");
            File.WriteAllText(Path.Combine(Root, "manifest.json"), "{}");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
