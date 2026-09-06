using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ParcelExperienceTests
{
    [Theory]
    [InlineData(false, "Cargo", "State", "Scheduled")]
    [InlineData(true, "Груз", "Состояние", "Запланировано")]
    public void LiveProjection_ExecutesTypedActionAndUpdatesLocalizedState(bool russian, string cargo, string state, string scheduled)
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        using var view = Create(app, russian);
        Assert.Equal("Copper ore × 7", Value(view, cargo));
        UiActionDefinition reserve = view.Experience.Actions[0];
        Assert.True(reserve.CanExecute);

        Assert.True(reserve.TryExecute());
        Assert.False(reserve.CanExecute);
        Assert.True(view.Pump());

        Assert.Equal(scheduled, Value(view, state));
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.True(view.Experience.Actions[1].CanExecute);
        Assert.False(view.Pump());
    }

    [Fact]
    public void StaleAndDisposedExperience_CannotExecuteRetainedActions()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        using var view = Create(app);
        UiActionDefinition cancel = view.Experience.Actions[1];
        app.Execute(FlowApplicationTests.Command(app.ReadSnapshot(), FlowParcelAction.Reserve));
        Assert.False(cancel.TryExecute());
        view.Pump();
        Assert.True(cancel.CanExecute);
        view.Dispose();
        Assert.False(cancel.TryExecute());
        Assert.False(view.Pump());
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void Surface_CoalescesRevisionsAndClosesWithApplication()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        using var view = Create(app);
        var api = new SurfaceApi();
        using var surface = new ParcelSurface(view);
        surface.Show(api);
        surface.Pump();
        Assert.Equal(1, api.Session.Synchronizations);
        Assert.Equal(0, api.Session.Refreshes);
        app.Execute(FlowApplicationTests.Command(app.ReadSnapshot(), FlowParcelAction.Reserve));
        fixture.Runtime.AdvanceTo(4);
        app.Refresh();

        surface.Pump();
        surface.Pump();

        Assert.Equal("Delivered", Value(view, "State"));
        Assert.Equal(1, api.Session.Refreshes);
        Assert.Equal(2, api.Session.Synchronizations);
        app.Dispose();
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        surface.Pump();
        Assert.True(surface.IsClosed);
        Assert.Equal(1, api.Session.Disposals);
        Assert.False(api.Session.Visible);
    }

    [Fact]
    public void FailedShowAndCleanup_RetainHandleForRetryAndRetireActions()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        using var view = Create(app);
        var api = new SurfaceApi();
        api.Session.FailShow = true;
        api.Session.FailDispose = true;
        using var surface = new ParcelSurface(view);
        Assert.Throws<InvalidOperationException>(() => surface.Show(api));
        Assert.Throws<InvalidOperationException>(surface.Dispose);
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        api.Session.FailDispose = false;
        surface.Pump();
        Assert.Equal(2, api.Session.Disposals);
        Assert.Equal(1, api.Creations);
        Assert.False(api.Session.Visible);
    }

    [Fact]
    public void SemanticAssembly_DependsOnContractWithoutPlatformOrPersistence()
    {
        string[] forbidden = { "Stardew", "SMAPI", "MonoGame", "Microsoft.Xna", "Persistence" };
        Assert.DoesNotContain(typeof(ParcelExperience).Assembly.GetReferencedAssemblies(), reference =>
            forbidden.Any(name => reference.Name!.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static ParcelExperience Create(IFlowApplication app, bool russian = false)
        => new(new UiSymbolId("Hatifect.Flow", "parcel"), app, CheckpointFixture.Parcel.Value, russian,
            id => id == CheckpointFixture.Origin.Value ? "Mine" : "Farm", _ => "Copper ore");

    private static object? Value(ParcelExperience view, string name)
        => view.Experience.Elements.Single(element => element.Name == name).Source.UntypedValue?.ToString();

    private sealed class SurfaceApi : IUiSemanticSurfaceApi
    {
        internal SurfaceSession Session { get; } = new();
        internal int Creations { get; private set; }
        public int ApiVersion => 1;
        public IUiSemanticSurfaceAutomation Automation => throw new NotSupportedException();
        public IUiSemanticSurfaceSession CreateActiveMenuOverlay(UiExperienceDefinition experience, UiSemanticSurfaceOptions options)
        { Creations++; return Session; }
    }

    private sealed class SurfaceSession : IUiSemanticSurfaceSession
    {
        internal int Refreshes { get; private set; }
        internal int Synchronizations { get; private set; }
        internal int Disposals { get; private set; }
        internal bool FailShow { get; set; }
        internal bool FailDispose { get; set; }
        public bool Visible { get; private set; }
        public event Action? Closed;
        public event Action? Rendered { add { } remove { } }
        public void Show() { Visible = true; if (FailShow) throw new InvalidOperationException("show"); }
        public void Hide() { Visible = false; Closed?.Invoke(); }
        public void Configure(UiSemanticSurfaceOptions options) { }
        public void Refresh() => Refreshes++;
        public void Synchronize() => Synchronizations++;
        public void Dispose()
        {
            Disposals++;
            if (FailDispose) throw new InvalidOperationException("dispose");
            Hide();
        }
    }
}
