using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Hatifect.Flow.Application;
using Hatifect.Flow.Sessions;

namespace Hatifect.Flow.Diagnostics;

internal readonly record struct FlowStationResources(Guid StationId, FlowPortResources Port);
internal readonly record struct FlowAdmissionRejections(long Stations, long LifetimeLinks, long RetainedCargo);
internal sealed record FlowGameResources(Guid SessionId, ulong SaveId, Guid NetworkId, long Revision, long Now,
    FlowApplicationState State, FlowRuntimeResources Runtime, FlowResourceUsage Payloads, FlowResourceUsage PayloadCharacters,
    int MaxCharactersPerPayload, int MaxCoreCheckpointBytes, int MaximumObservedDeliveryAttempts, int ParcelsAtAttemptLimit,
    IReadOnlyList<FlowStationResources> Ports, FlowAdmissionRejections AdmissionRejections, FlowTickCounters TickCounters)
{
    internal string Format()
    {
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant($"Flowline resources v1: session={SessionId:D}, save={SaveId}, network={NetworkId:D}, revision={Revision}, tick={Now}, state={State}"));
        Append("Stations", Runtime.Stations);
        Append("Lifetime links", Runtime.LifetimeLinks);
        text.AppendLine(FormattableString.Invariant($"Active links: {Runtime.ActiveLinks}. Removing links does not release lifetime capacity."));
        Append("Retained cargo", Runtime.Cargo); Append("Shipments", Runtime.Shipments); Append("Parcels", Runtime.Parcels);
        Append("Pending operations", Runtime.PendingOperations); Append("Route cache", Runtime.RouteCache); Append("Retained events", Runtime.Events);
        Append("Issued transfers", Runtime.IssuedTransfers);
        text.AppendLine(FormattableString.Invariant($"Retired transfers: {Runtime.RetiredTransfers}; route searches this load: {Runtime.RouteSearches}."));
        text.AppendLine(FormattableString.Invariant($"Per dispatch tick: {Runtime.MaxOperationsPerAdvance} operations; per route: {Runtime.MaxRouteVisits} visits; per parcel: {Runtime.MaxDeliveryAttempts} delivery/return attempts."));
        text.AppendLine(FormattableString.Invariant($"Maximum attempts observed: {MaximumObservedDeliveryAttempts}; parcels at attempt limit: {ParcelsAtAttemptLimit}."));
        Append("Saved item payloads", Payloads); Append("Saved item XML characters", PayloadCharacters);
        text.AppendLine(FormattableString.Invariant($"Per item XML: {MaxCharactersPerPayload} characters. Core checkpoint limit: {MaxCoreCheckpointBytes} bytes; item XML and game-save JSON overhead are separate."));
        text.AppendLine("Delivery, cancellation, taking items and reloading do not release retained cargo capacity. Full retention rejects new sends; do not delete receipts or edit the save to bypass it.");
        text.AppendLine(FormattableString.Invariant($"Admission limit refusals this load: stations={AdmissionRejections.Stations}, lifetime links={AdmissionRejections.LifetimeLinks}, cargo={AdmissionRejections.RetainedCargo}."));
        foreach (FlowStationResources station in Ports)
            text.AppendLine(FormattableString.Invariant($"Port {station.StationId:D}: historical custody={station.Port.Custody.Used}/{station.Port.Custody.Limit}, remaining={station.Port.Custody.Remaining}; receipts={station.Port.Receipts.Used}/{station.Port.Receipts.Limit}, remaining={station.Port.Receipts.Remaining}."));
        text.Append("Port custody is a journal projection, not the current physical chest inventory. Diagnostics perform no checkpoint, route search or physical transfer.");
        return text.ToString();

        void Append(string name, FlowResourceUsage usage)
            => text.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1}/{2}, remaining={3}.", name, usage.Used, usage.Limit, usage.Remaining));
    }
}

internal enum FlowAdmissionResource { Stations, LifetimeLinks, RetainedCargo }

internal sealed class FlowResourceLimitException : InvalidOperationException
{
    internal FlowResourceLimitException(FlowAdmissionResource resource, int used, int limit)
        : base(FormattableString.Invariant($"The save has reached its {Name(resource)} limit ({used}/{limit}). Retained cargo and lifetime links are not released by delivery, cancellation, removal or reload."))
    { Resource = resource; Used = used; Limit = limit; }

    internal FlowAdmissionResource Resource { get; }
    internal int Used { get; }
    internal int Limit { get; }

    private static string Name(FlowAdmissionResource resource) => resource switch
    {
        FlowAdmissionResource.Stations => "station", FlowAdmissionResource.LifetimeLinks => "lifetime link",
        FlowAdmissionResource.RetainedCargo => "retained cargo", _ => throw new ArgumentOutOfRangeException(nameof(resource))
    };
}
