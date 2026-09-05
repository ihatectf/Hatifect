using Hatifect.Flow.Domain.Shipments;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal static class ParcelExperience
{
    internal static UiExperienceDefinition Create(UiSymbolId id, Parcel parcel)
    {
        ArgumentNullException.ThrowIfNull(parcel);

        return new UiExperienceBuilder(id, "Hatifect Flow parcel")
            .Inspect("Parcel", new UiConstantSource<Parcel>(parcel))
            .Monitor("State", new UiConstantSource<string>(parcel.State.ToString()))
            .Build();
    }
}
