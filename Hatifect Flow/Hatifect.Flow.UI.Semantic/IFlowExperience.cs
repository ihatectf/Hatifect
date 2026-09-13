using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal interface IFlowExperience : IDisposable
{
    UiExperienceDefinition Experience { get; }
    bool IsActive { get; }
    bool Pump();
}
