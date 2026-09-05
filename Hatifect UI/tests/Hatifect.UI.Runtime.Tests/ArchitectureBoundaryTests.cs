using System;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Validation;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Theory]
    [InlineData(typeof(UiExperienceBuilder))]
    [InlineData(typeof(UiPresentationPlanner))]
    [InlineData(typeof(UiInvocationService))]
    [InlineData(typeof(UiCompiler))]
    [InlineData(typeof(UiBuildValidator))]
    public void NewAssembliesDoNotReferenceLegacyOrPlatformUi(Type marker)
    {
        string[] forbidden =
        {
            "Hatifect.UI.Runtime",
            "Hatifect.UI.Experience",
            "Hatifect.UI.Stardew",
            "StardewModdingAPI",
            "Stardew Valley",
            "MonoGame.Framework"
        };
        string[] references = marker.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference => forbidden.Contains(reference, StringComparer.Ordinal));
    }

    [Fact]
    public void CandidateSceneContractsRemainInternalUntilDogfooding()
    {
        Assert.True(typeof(UiScene).IsNotPublic);
        Assert.True(typeof(UiSceneComposer).IsNotPublic);
        Assert.True(typeof(IUiPlatformBridge).IsNotPublic);
        Assert.True(typeof(IUiPlatformInputSession).IsNotPublic);
        Assert.True(typeof(UiHostRuntimeSession).IsNotPublic);
        Assert.True(typeof(UiPortalHostSession).IsNotPublic);
        Assert.True(typeof(UiPortalRequest).IsNotPublic);
        Assert.True(typeof(UiTerminalHostSession).IsNotPublic);
    }
}
