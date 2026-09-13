using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Projection;
using Hatifect.UI.Runtime.Registration;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class InvocationTests
{
    [Fact]
    public void TerminalInvocationPlansAndProjectsChildRegionsIntoShellSlots()
    {
        UiSymbolId id = RegistryTests.Id("storage");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(id, "Storage", () => Experience(id))
            .Freeze();
        var invocation = new UiInvocationService(registry);

        UiInvocationResult result = invocation.Invoke(id, UiPresentationProfiles.Wide);

        UiSymbolId inspector = result.Experience.Elements.Single(element => element.Name == "Inspector").Id;
        UiProjectedElement projected = result.Projection.Elements.Single(element => element.Element == inspector);
        Assert.Equal(UiHostKind.Terminal, result.Plan.Host.HostKind);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "region/Context"), projected.SourceRegion);
        Assert.Equal(UiHostSlots.Context, projected.HostSlot);
    }

    private static UiExperienceDefinition Experience(UiSymbolId id)
        => new UiExperienceBuilder(id, "Storage")
            .Browse("Items", new UiCollectionSource<string>(
                Array.Empty<string>(), item => id.Child($"item/{item}")))
            .Inspect("Inspector", new UiState<string?>(null))
            .Build();
}
