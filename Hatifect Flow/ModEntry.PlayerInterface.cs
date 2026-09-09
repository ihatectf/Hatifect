using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using Hatifect.Flow.Sessions;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow;

public sealed partial class ModEntry
{
    private FlowConfig _config = new();
    private IUiSemanticHostApi? _flowHostUi;

    private void OnFlowButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        // The trigger belongs to Flow; an open native menu and all in-surface input belong to UI.
        if (_config.OpenNetwork?.JustPressed() != true) return;
        try
        {
            bool free = Context.IsPlayerFree, authority = IsGameAuthority(), menuOpen = Game1.activeClickableMenu is not null;
            _chestAcceptance?.ObserveOrdinaryEntry(free, authority, menuOpen, _gameSession is not null);
            if (!free || !authority || menuOpen || _gameSession is not { } session) return;
            Helper.Input.SuppressActiveKeybinds(_config.OpenNetwork);
            IUiSemanticHostApi api = _flowHostUi
                ?? throw new InvalidOperationException("The Hatifect UI standalone host API is unavailable.");
            Vector2 tile = Helper.Input.GetCursorPosition().GrabTile;
            Game1.currentLocation.Objects.TryGetValue(tile, out StardewValley.Object? item);
            session.PreparePlayerTarget(Game1.currentLocation.NameOrUniqueName, (int)tile.X, (int)tile.Y, item as Chest);
            OpenPlayerNetwork(session, api);
            _chestAcceptance?.ConfirmOrdinaryOpening();
        }
        catch (Exception error)
        {
            ReportGameFailure(error);
            Game1.showRedMessage(LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru
                ? "Не удалось открыть Flowline. Подробности — в журнале SMAPI."
                : "Flowline could not open. See the SMAPI log for details.");
        }
    }

    private NetworkExperience OpenPlayerNetwork(FlowGameSession session, IUiSemanticHostApi api)
    {
        var application = _chestAcceptance?.ForOrdinaryEntry(session) ?? session;
        var experience = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), application,
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru,
            key => ItemRegistry.GetDataOrErrorItem(key).DisplayName);
        try { CloseParcelSurface(); }
        catch { experience.Dispose(); throw; }
        _parcelSurface = new ParcelSurface(experience, UiSemanticHostKind.Window);
        _parcelSurface.Show(api);
        return experience;
    }
}
