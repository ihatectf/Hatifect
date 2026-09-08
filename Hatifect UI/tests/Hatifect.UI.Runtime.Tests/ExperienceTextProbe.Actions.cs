using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Tests;

public sealed partial class ExperienceTextProbe
{
    public bool Invoke(UiActionDefinition action)
        => !_disposed && _host?.Root.Actions.Invoke(action) == true;

    public bool CanInvoke(UiActionDefinition action)
    {
        if (_disposed || _host is null) return false;
        _host.PumpActions();
        return _host.Root.Actions.CanInvoke(action);
    }

    public ExperienceActionStatus? ActionStatus(UiActionDefinition action)
    {
        if (_disposed || _host is null) return null;
        _host.PumpActions();
        var status = _host.Root.Actions.Status(action);
        return status is null ? null : new(status.State, status.Outcome, status.Reason,
            status.Error, status.FailureMessage);
    }
}

public sealed record ExperienceActionStatus(UiActionState State, UiActionOutcome? Outcome,
    UiActionRejection? Rejection, Exception? Error, UiActionMessage? FailureMessage);
