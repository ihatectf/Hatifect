using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Actions;

internal readonly record struct UiActionOwnershipEntry(UiSymbolId Action, UiActionState State, bool IsRetired, int WaitingCount);

// A value-only inventory of this generation's bound actions, not a complete UI generation manifest.
// A retired/disposed dispatcher remains inspectable with its terminal state; no request/result data escapes.
internal sealed class UiActionDispatcherOwnershipSnapshot
{
    internal UiActionDispatcherOwnershipSnapshot(
        Guid sessionId, Guid generationId, int ownerThread, bool terminal, UiActionOwnershipEntry[] entries)
    {
        SessionId = sessionId;
        GenerationId = generationId;
        OwnerThread = ownerThread;
        Terminal = terminal;
        Actions = Array.AsReadOnly(entries);
    }

    internal Guid SessionId { get; }
    internal Guid GenerationId { get; }
    internal int OwnerThread { get; }
    internal bool Terminal { get; }
    internal IReadOnlyList<UiActionOwnershipEntry> Actions { get; }
}
