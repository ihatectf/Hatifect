using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Runtime.Tests;
using Xunit;

namespace Hatifect.Flow.Tests;

// Match the production CreateActiveMenuOverlay host policy at the unchanged standard viewport.
[Trait("Category", "flow")]
public sealed class NetworkNativeCenteredOverlayTests
{
    [Fact]
    public void NetworkExperienceOpensAtTheNativeCenteredOverlayViewport()
    {
        using var application = new NetworkTestApplication();
        var id = new UiSymbolId("Hatifect.Flow", "network");
        using var experience = new NetworkExperience(id, application);
        using var host = new ExperienceTextProbe(experience.Experience, centeredOverlay: true);

        ExperienceTextSnapshot accepted = host.Compose("en");

        ExperienceTextAction send = Assert.Single(accepted.Actions,
            action => action.Action.Id == id.Child("action/send"));
        Assert.Equal(id.Child("action/send/scene/button"), send.Id);
        Assert.Contains(accepted.Values, value => value.SemanticName == "Result");
    }
}
