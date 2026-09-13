using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Hatifect.UI.DevTools.Tests;

public sealed class DevToolsArchitectureTests
{
    [Fact]
    public void DevToolsDoNotReferenceGamePlatformAssemblies()
    {
        string[] forbidden =
        {
            "Hatifect.UI.Stardew",
            "StardewModdingAPI",
            "Stardew Valley",
            "MonoGame.Framework"
        };
        string[] references = typeof(UiInspector).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference => forbidden.Contains(reference, StringComparer.Ordinal));
    }

    [Fact]
    public void ProvisionalInspectorDoesNotFreezeRuntimeOrSceneAsPublicSdk()
    {
        Assert.False(typeof(UiInspector).IsPublic);
        Assert.False(typeof(UiInspectorSnapshot).IsPublic);
        Assert.False(typeof(Hatifect.UI.Runtime.Diagnostics.UiRuntimeDiagnosticSnapshot).IsPublic);
        Assert.False(typeof(Hatifect.UI.Runtime.Scene.UiScene).IsPublic);
        Assert.False(typeof(Hatifect.UI.Runtime.Platform.UiHostRuntimeSession).IsPublic);
        Assert.DoesNotContain(
            typeof(UiInspector).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType == typeof(Hatifect.UI.Runtime.Scene.UiScene) ||
                         parameter.ParameterType == typeof(Hatifect.UI.Runtime.Platform.UiHostRuntimeSession));
    }
}
