using System;
using System.Collections.Generic;

namespace Hatifect.UI.Runtime.Activation;

internal enum UiActivationOwnership { BorrowedDefinition, SessionOwnedLifecycle }

internal readonly record struct UiCachedExperienceOwnership(UiSymbolId Experience, UiActivationOwnership Ownership);

// A value-only inventory of this activation session's cache, not a complete UI generation manifest.
// Transient values and registry-owned singletons are outside this cache and are never claimed here.
internal sealed class UiActivationOwnershipSnapshot
{
    internal UiActivationOwnershipSnapshot(int ownerThread, bool terminal, UiCachedExperienceOwnership[] entries)
    {
        OwnerThread = ownerThread;
        Terminal = terminal;
        CachedExperiences = Array.AsReadOnly(entries);
    }

    internal int OwnerThread { get; }
    internal bool Terminal { get; }
    internal IReadOnlyList<UiCachedExperienceOwnership> CachedExperiences { get; }
}
