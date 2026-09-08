using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.ChestsAnywhereOverlay.UI.Semantic;

internal sealed partial class ChestsAnywhereNavigatorExperienceSession
{
    internal enum NavigatorCommand { ChangeView, SelectCategory, Refresh, Open, Favorite, Close }
    internal sealed record NavigatorRequestContext(Guid PublicationId, long Version,
        ChestsAnywhereNavigatorMode Mode, string CategoryKey, UiSymbolId? StorageId,
        ChestsAnywhereStorageHandoff? Handoff);
    internal sealed record NavigatorActionRequest(NavigatorRequestContext Context, NavigatorCommand Command,
        ChestsAnywhereNavigatorMode Mode, string CategoryKey, ChestsAnywhereNavigatorStorage? Storage, bool PreserveView);
    internal sealed record NavigatorActionReceipt(Guid PublicationId, long PublicationVersion,
        long ProviderRevision, UiSymbolId? StorageId, string? StorageKey, bool Closed);

    internal static readonly UiSourceType<NavigatorActionRequest> RequestType = UiSourceTypes.Scalar<NavigatorActionRequest>(
        new("Hatifect.ChestsAnywhereOverlay", "data/navigator-action-request"), false);
    internal static readonly UiSourceType<NavigatorActionReceipt> ReceiptType = UiSourceTypes.Scalar<NavigatorActionReceipt>(
        new("Hatifect.ChestsAnywhereOverlay", "data/navigator-action-receipt"), false);
    private static UiActionMessage Message(string code, string english, string russian)
        => new(code, new UiLocalizedText(english, new Dictionary<string, string>
        { ["en"] = english, ["ru"] = russian, ["ru-RU"] = russian }));
    private static readonly UiActionRejection BusyReason = UiActionRejection.Localized(Message(
        "CA_BUSY", "Another navigator request is running.", "Выполняется другой запрос навигатора."));
    private static readonly UiActionRejection RetiredReason = UiActionRejection.Localized(Message(
        "CA_RETIRED", "This navigator is closed.", "Этот навигатор закрыт."));
    private static readonly UiActionRejection SelectionReason = UiActionRejection.Localized(Message(
        "CA_SELECTION", "Select an item first.", "Сначала выберите элемент."));
    private static readonly UiActionRejection StaleReason = UiActionRejection.Localized(Message(
        "CA_STALE", "The navigator changed. Try again.", "Навигатор изменился. Повторите действие."));
    private static readonly UiActionRejection OpenReason = UiActionRejection.Localized(Message(
        "CA_OPEN_REJECTED", "This storage could not be opened.", "Не удалось открыть это хранилище."));
    private static readonly UiActionMessage ErrorMessage = Message(
        "CA_ACTION_FAILED", "The navigator request failed.", "Не удалось выполнить запрос навигатора.");
    private static readonly UiActionAvailability BusyAvailability = UiActionAvailability.Disabled(BusyReason);
    private static readonly UiActionAvailability RetiredAvailability = UiActionAvailability.Disabled(RetiredReason);
    private static readonly UiActionAvailability SelectionAvailability = UiActionAvailability.Disabled(SelectionReason);

    private UiActionDefinition[] CreateActions(UiSymbolId id)
        => new[]
        {
            Action(id, "mode-categories", Text("navigator.categories", "Categories"), NavigatorCommand.ChangeView, ChestsAnywhereNavigatorMode.Category),
            Action(id, "mode-favorites", Text("navigator.favorites", "Favorites"), NavigatorCommand.ChangeView, ChestsAnywhereNavigatorMode.Favorites),
            Action(id, "mode-recent", Text("navigator.recent", "Recent"), NavigatorCommand.ChangeView, ChestsAnywhereNavigatorMode.Recent),
            Action(id, "select-category", Text("navigator.category.select", "Select category"), NavigatorCommand.SelectCategory),
            Action(id, "refresh", Text("navigator.refresh", "Refresh"), NavigatorCommand.Refresh),
            Action(id, "open", Text("navigator.open", "Open storage"), NavigatorCommand.Open),
            Action(id, "toggle-favorite", Text("navigator.favorite", "Toggle favorite"), NavigatorCommand.Favorite),
            Action(id, "close", Text("navigator.close", "Close"), NavigatorCommand.Close)
        };

    private UiActionDefinition Action(UiSymbolId id, string name, string title, NavigatorCommand command,
        ChestsAnywhereNavigatorMode mode = ChestsAnywhereNavigatorMode.Category)
        => new UiAction<NavigatorActionRequest, NavigatorActionReceipt>(id.Child($"action/{name}"), title,
            ExecuteTyped, UiActionConcurrency.RejectWhileRunning, () => Availability(command))
            .Bind(() =>
            {
                ThrowIfUnavailable();
                _publication.Capture();
                if (_requesting || _publication.IsPublishing)
                    throw new InvalidOperationException("UIP003: A navigator request cannot capture inside another request or publication observer.");
                return CaptureRequest(command, mode);
            }, owner: _publication);

    private UiActionAvailability Availability(NavigatorCommand command)
    {
        if (!Available) return RetiredAvailability;
        if (_requesting || _publication.IsPublishing) return BusyAvailability;
        if (command is NavigatorCommand.Open or NavigatorCommand.Favorite && !HasSelectedStorage()) return SelectionAvailability;
        if (command == NavigatorCommand.SelectCategory && !CanSelectCategory()) return SelectionAvailability;
        return UiActionAvailability.Available;
    }

    private NavigatorActionRequest CaptureRequest(NavigatorCommand command,
        ChestsAnywhereNavigatorMode mode = ChestsAnywhereNavigatorMode.Category,
        string? categoryKey = null, bool preserveView = true)
    {
        UiPublicationView view = _publication.Capture();
        NavigatorProjection projection = _projection.Read(view);
        UiSymbolId? storageId = _storages.Read(view).SelectedItemId;
        var context = new NavigatorRequestContext(view.PublicationId, view.Version, _mode.Read(view),
            _selectedCategory.Read(view), storageId, _handoff.Read(view));
        ChestsAnywhereNavigatorStorage? storage = null;
        if (command is NavigatorCommand.Open or NavigatorCommand.Favorite)
            storage = projection.Storages.FirstOrDefault(item => item.Id == storageId)
                ?? throw new InvalidOperationException("No storage is selected.");
        if (command == NavigatorCommand.ChangeView && !Enum.IsDefined(typeof(ChestsAnywhereNavigatorMode), mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (command == NavigatorCommand.SelectCategory)
        {
            if (categoryKey is null)
                categoryKey = projection.Categories.FirstOrDefault(item => item.Id == _categories.Read(view).SelectedItemId)?.Key;
            if (string.IsNullOrWhiteSpace(categoryKey)) throw new ArgumentException("A category key is required.", nameof(categoryKey));
            if (!projection.Categories.Any(item => string.Equals(item.Key, categoryKey, StringComparison.Ordinal)))
                throw new ArgumentException($"Unknown storage category '{categoryKey}'.", nameof(categoryKey));
        }
        return new(context, command, mode, categoryKey ?? context.CategoryKey, storage, preserveView);
    }

    private ValueTask<UiActionResult<NavigatorActionReceipt>> ExecuteTyped(NavigatorActionRequest request, CancellationToken cancellation)
    {
        if (!Available) return new(UiActionResult<NavigatorActionReceipt>.Cancelled(UiActionCancellationReason.OwnerRetired));
        if (cancellation.IsCancellationRequested) return new(UiActionResult<NavigatorActionReceipt>.Cancelled());
        if (_requesting || _publication.IsPublishing) return new(UiActionResult<NavigatorActionReceipt>.Rejected(BusyReason));
        try
        {
            UiActionResult<NavigatorActionReceipt>? result = null;
            Request(() => result = Current(request.Context) ? ExecuteRequest(request)
                : UiActionResult<NavigatorActionReceipt>.Rejected(StaleReason));
            return new(result!);
        }
        catch (Exception error)
        {
            return new(UiActionResult<NavigatorActionReceipt>.Failure(error, ErrorMessage));
        }
    }

    private bool Current(NavigatorRequestContext context)
        => Available && context.PublicationId == _publication.Id && context.Version == _publication.Version;

    private void RequireCurrent(NavigatorRequestContext context)
    {
        ThrowIfUnavailable();
        if (!Current(context)) throw new InvalidOperationException("The navigator publication changed before the provider result could be published.");
    }

    private UiActionResult<NavigatorActionReceipt> ExecuteRequest(NavigatorActionRequest request)
    {
        RequireCurrent(request.Context);
        if (request.Command == NavigatorCommand.Close)
        {
            long version = _publication.Version;
            long revision = _projection.Value.Revision;
            _completed = true;
            _publication.Dispose();
            _onClose?.Invoke();
            return UiActionResult<NavigatorActionReceipt>.Success(new(_publication.Id, version, revision, null, null, true));
        }
        NavigatorProjection next;
        bool preserveView = request.PreserveView;
        bool replaceHandoff = false;
        bool opened = true;
        ChestsAnywhereStorageHandoff? handoff = null;
        switch (request.Command)
        {
            case NavigatorCommand.ChangeView:
                next = Project(_port.ChangeView(request.Mode, request.CategoryKey));
                preserveView = false;
                break;
            case NavigatorCommand.SelectCategory:
                next = Project(_port.ChangeView(ChestsAnywhereNavigatorMode.Category, request.CategoryKey));
                preserveView = false;
                break;
            case NavigatorCommand.Refresh:
                next = Project(_port.Refresh());
                break;
            case NavigatorCommand.Favorite:
                next = Project(_port.ToggleFavorite(request.Storage!.Key));
                break;
            case NavigatorCommand.Open:
                ChestsAnywhereNavigatorMutationResult result = _port.RequestOpenStorage(request.Storage!.Key);
                next = Project(result.Snapshot);
                opened = result.Succeeded;
                replaceHandoff = true;
                if (opened) handoff = new(request.Storage.Id, request.Storage.Key);
                break;
            default: throw new InvalidOperationException("Unknown navigator command.");
        }
        // Provider effect and publication share the cross-action Request guard. A retired or
        // changed owner cannot receive the result; any committed provider effect remains real.
        ApplyProjection(next, request.Context, preserveView, handoff, replaceHandoff);
        return opened
            ? UiActionResult<NavigatorActionReceipt>.Success(new(_publication.Id, _publication.Version,
                next.Revision, request.Storage?.Id, request.Storage?.Key, false))
            : UiActionResult<NavigatorActionReceipt>.Rejected(OpenReason);
    }
}
