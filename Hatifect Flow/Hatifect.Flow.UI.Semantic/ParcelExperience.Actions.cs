using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class ParcelExperience
{
    internal sealed record ParcelActionRequest(Guid PublicationId, long PublicationVersion, FlowParcelCommand Command);

    internal static readonly UiSourceType<ParcelActionRequest> ActionRequestType = UiSourceTypes.Scalar<ParcelActionRequest>(
        new("Hatifect.Flow", "data/parcel-action-request"), false);
    internal static readonly UiSourceType<FlowCommandResult> ActionResultType = UiSourceTypes.Scalar<FlowCommandResult>(
        new("Hatifect.Flow", "data/command-result"), false);

    private UiActionDefinition TypedAction(UiSymbolId id, string key, FlowParcelAction action, string title)
        => new UiAction<ParcelActionRequest, FlowCommandResult>(id.Child("action/" + key), title,
            ExecuteTyped, UiActionConcurrency.RejectWhileRunning, () => ActionAvailability(action))
            .Bind(() => CaptureAction(action), owner: _publication);

    private ParcelActionRequest CaptureAction(FlowParcelAction action)
    {
        UiPublicationView view = _publication.Capture();
        FlowSnapshot snapshot = _projection.Read(view).Snapshot;
        return new(view.PublicationId, view.Version,
            new(snapshot.SessionId, snapshot.Revision, _parcelId ?? Guid.Empty, action));
    }

    private UiActionAvailability ActionAvailability(FlowParcelAction action)
    {
        if (_disposed || _publication.IsDisposed) return Disabled(FlowRejectionCode.SessionClosed);
        if (!CanRequest) return Disabled(FlowRejectionCode.OperationPending);
        if (Snapshot.State != FlowApplicationState.Active) return Disabled(Snapshot.Code);
        if (Parcel is not { } parcel) return Disabled(FlowRejectionCode.ParcelNotFound);
        if (!parcel.Availability[action].Available) return Disabled(parcel.Availability[action].Code);
        return Can(action) ? UiActionAvailability.Available : Disabled(FlowRejectionCode.StaleRevision);
    }

    private ValueTask<UiActionResult<FlowCommandResult>> ExecuteTyped(ParcelActionRequest request, CancellationToken cancellation)
    {
        if (_disposed || _publication.IsDisposed)
            return new(UiActionResult<FlowCommandResult>.Cancelled(UiActionCancellationReason.OwnerRetired));
        if (cancellation.IsCancellationRequested) return new(UiActionResult<FlowCommandResult>.Cancelled());
        if (!CanRequest) return new(Rejected(FlowRejectionCode.OperationPending));
        _publication.Capture();
        _requesting = true;
        try
        {
            FlowParcelCommand command = request.Command;
            if (request.PublicationId != _publication.Id || request.PublicationVersion != _publication.Version
                || command.SessionId != Snapshot.SessionId || command.ExpectedRevision != Snapshot.Revision
                || command.ParcelId != _parcelId || !CurrentAllows(command.Action))
                return new(Rejected(FlowRejectionCode.StaleRevision));
            FlowCommandResult result = _application.Execute(command);
            // The domain result is retained independently of host completion. A failed later
            // projection can retry publication without repeating the command or its effects.
            if (!_disposed && !_publication.IsDisposed)
            {
                _pendingResult = result;
                _dirty = true;
            }
            return new(result.Status switch
            {
                FlowCommandStatus.Applied => UiActionResult<FlowCommandResult>.Success(result),
                FlowCommandStatus.Faulted => UiActionResult<FlowCommandResult>.DomainFailure(FlowActionMessages.Message(result.Code, _russianFallback)),
                _ => Rejected(result.Code)
            });
        }
        catch (Exception error)
        {
            return new(UiActionResult<FlowCommandResult>.Failure(error,
                FlowActionMessages.Message(FlowRejectionCode.Faulted, _russianFallback)));
        }
        finally { _requesting = false; }
    }

    private UiActionAvailability Disabled(FlowRejectionCode code) => FlowActionMessages.Disabled(code, _russianFallback);
    private UiActionResult<FlowCommandResult> Rejected(FlowRejectionCode code)
        => UiActionResult<FlowCommandResult>.Rejected(Disabled(code).Reason!);
}
