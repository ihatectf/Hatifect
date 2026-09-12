using System;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Runtime.Layout;

internal static class UiDensityPolicy
{
    internal static float Factor(UiCollectionDensity density, UiSymbolId profile)
    {
        float factor = density switch
        {
            UiCollectionDensity.Default => 1f,
            UiCollectionDensity.Compact => 0.75f,
            UiCollectionDensity.Comfortable => 1.25f,
            _ => throw new ArgumentOutOfRangeException(nameof(density), density, "Unknown collection density.")
        };
        if (profile == UiPresentationProfiles.Compact.Id || profile == UiPresentationProfiles.Controller.Id)
            factor *= 0.9f;
        return factor;
    }
}
