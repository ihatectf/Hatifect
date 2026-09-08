using System;
using System.Collections.Generic;
using Hatifect.Flow.Application;

namespace Hatifect.Flow.Diagnostics;

// Input origin is an external declaration. Native SMAPI counters alone never prove human input.
internal sealed record FlowPlayerInputStamp(string Origin, long Update, long PointerPressed,
    long PointerReleased, long TabPressed, long BackspacePressed);

internal sealed record FlowPlayerCommandTrace(int Sequence, string Kind, object Command,
    FlowPlayerInputStamp Input, string Before, FlowCommandResult? Result, string? After, string? Error);

// Exact-harness instrumentation only. Immutable captured state is supplied by the Flow inventory
// fixture during Update; this decorator does not read UI internals or inject/normalize input.
internal sealed class FlowPlayerInputJournal : IFlowNetworkApplication
{
    internal const int MaximumCommands = 32;
    private readonly IFlowNetworkApplication _owner;
    private readonly Func<FlowPlayerInputStamp> _input;
    private readonly Func<string> _capture;
    private readonly List<FlowPlayerCommandTrace> _entries = new(MaximumCommands);
    private bool _dispatching;

    internal FlowPlayerInputJournal(IFlowNetworkApplication owner, Func<FlowPlayerInputStamp> input, Func<string> capture)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Entries = _entries.AsReadOnly();
    }

    internal IReadOnlyList<FlowPlayerCommandTrace> Entries { get; }
    public event Action<long>? RevisionChanged
    { add => _owner.RevisionChanged += value; remove => _owner.RevisionChanged -= value; }
    public FlowSnapshot ReadSnapshot() => _owner.ReadSnapshot();
    public FlowNetworkSnapshot ReadNetwork() => _owner.ReadNetwork();
    public FlowRoutePreview PreviewRoute(Guid source, Guid destination) => _owner.PreviewRoute(source, destination);
    public IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station) => _owner.ReadInventory(station);
    public IReadOnlyList<FlowRecoveryIssue> ReadRecovery() => _owner.ReadRecovery();
    public FlowCommandResult Execute(FlowParcelCommand command) => Dispatch("parcel", command, () => _owner.Execute(command));
    public FlowCommandResult Execute(FlowNetworkCommand command) => Dispatch("network", command, () => _owner.Execute(command));
    public FlowCommandResult Execute(FlowSendCommand command) => Dispatch("send", command, () => _owner.Execute(command));
    public FlowCommandResult Execute(FlowRecoveryCommand command) => Dispatch("recovery", command, () => _owner.Execute(command));

    private FlowCommandResult Dispatch(string kind, object command, Func<FlowCommandResult> execute)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_dispatching) throw new InvalidOperationException("Input evidence cannot reenter command dispatch.");
        if (_entries.Count >= MaximumCommands) throw new InvalidOperationException("Input command evidence exceeded its fixed bound.");
        _dispatching = true;
        try
        {
            FlowPlayerInputStamp input = _input();
            string before = Capture();
            FlowCommandResult? result = null;
            string? after = null, failure = null;
            try
            {
                result = execute();
                after = Capture();
                return result;
            }
            catch (Exception error)
            {
                failure = error.ToString();
                throw;
            }
            finally
            {
                _entries.Add(new(_entries.Count + 1, kind, command, input, before, result, after, failure));
            }
        }
        finally { _dispatching = false; }
    }

    private string Capture()
    {
        string value = _capture();
        if (string.IsNullOrEmpty(value) || value.Length > 262144)
            throw new InvalidOperationException("Input evidence requires a nonempty bounded immutable state capture.");
        return value;
    }
}
