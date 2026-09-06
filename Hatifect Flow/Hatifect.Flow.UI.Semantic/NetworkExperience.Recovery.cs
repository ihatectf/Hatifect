using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private readonly FlowSelectionSource<FlowRecoveryIssue> _recovery;

    private void AppendRecovery(UiExperienceBuilder builder, UiSymbolId id)
    {
        builder.Element(id.Child("element/recovery"), "Recovery", Text("Recovery diagnostics", "Диагностика восстановления"), _recovery, UiSourceTypes.Collection(FlowUiDataTypes.Recovery), UiCapabilities.Select)
            .Actions(id.Child("element/recovery-actions"), "RecoveryActions", Text("Recovery actions", "Действия восстановления"),
                new UiActionDefinition(id.Child("action/recover"), Text("Reconcile saved outcome", "Подтвердить сохранённый результат"),
                    () => RequestCommand(Recover, () => Selected(_recovery)?.CanReconcile == true, FlowApplicationState.RecoveryRequired),
                    () => Available(() => Selected(_recovery)?.CanReconcile == true, FlowApplicationState.RecoveryRequired)));

    }

    private void Recover()
    {
        FlowRecoveryIssue? issue = Selected(_recovery);
        if (issue is null) return;
        FlowCommandResult result = _application.Execute(new FlowRecoveryCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision, issue.ParcelId));
        Complete(result, Text("Saved outcome reconciled", "Сохранённый результат подтверждён"));
    }
}
