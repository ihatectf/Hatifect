using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private readonly UiSelectableCollectionState<FlowRecoveryIssue> _recovery;

    private void AppendRecovery(UiExperienceBuilder builder, UiSymbolId id)
    {
        builder.Element(id.Child("element/recovery"), "Recovery", Text("Recovery diagnostics", "Диагностика восстановления"), _recovery, UiSourceTypes.Collection(FlowUiDataTypes.Recovery), UiCapabilities.Select)
            .Actions(id.Child("element/recovery-actions"), "RecoveryActions", Text("Recovery actions", "Действия восстановления"),
                new UiActionDefinition(id.Child("action/recover"), Text("Reconcile saved outcome", "Подтвердить сохранённый результат"), Recover,
                    () => IsActive && _snapshot.Transport.State == FlowApplicationState.RecoveryRequired && Selected(_recovery)?.CanReconcile == true
                        && _application.ReadSnapshot().SessionId == _snapshot.Transport.SessionId && _application.ReadSnapshot().Revision == _snapshot.Transport.Revision));
    }

    private void Recover()
    {
        FlowRecoveryIssue? issue = Selected(_recovery);
        if (issue is null) return;
        FlowCommandResult result = _application.Execute(new FlowRecoveryCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision, issue.ParcelId));
        _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Saved outcome reconciled", "Сохранённый результат подтверждён")
            : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
        _dirty = true;
        Pump();
    }
}
