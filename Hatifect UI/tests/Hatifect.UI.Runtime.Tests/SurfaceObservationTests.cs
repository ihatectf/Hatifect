using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SurfaceObservationTests
{
    [Fact]
    public void CaptureUsesAcceptedTextAndAvailabilityWithoutReadingLiveSources()
    {
        using var fixture = new Fixture();
        fixture.Draw();
        int reads = fixture.Source.Reads;
        int availability = fixture.AvailabilityReads;
        var performance = fixture.Runtime.Performance;
        fixture.Source.Reject = true;
        fixture.RejectAvailability = true;
        UiSemanticSurfaceSnapshot snapshot = fixture.Capture();

        Assert.True(snapshot.Visible);
        Assert.False(snapshot.Retired);
        Assert.True(snapshot.IsAcceptedFrameRendered);
        Assert.False(snapshot.Truncated);
        Assert.False(snapshot.HasUnmappedContent);
        Assert.Same(fixture.Environment, snapshot.Environment);
        var status = Assert.Single(snapshot.Elements, row => row.SemanticId == fixture.Status);
        Assert.Equal("Status", status.Name);
        Assert.Equal("Accepted parcel", status.Value);
        Assert.Contains(snapshot.Texts, row => row.SemanticId == fixture.Status && row.Text == "Accepted parcel");
        var action = Assert.Single(snapshot.Elements, row => row.ActionId == fixture.Action.Id);
        Assert.Equal(fixture.Action.Id, action.SemanticId);
        Assert.Equal("Send", action.Name);
        Assert.True(action.Enabled);
        Assert.Equal(reads, fixture.Source.Reads);
        Assert.Equal(availability, fixture.AvailabilityReads);
        Assert.Equal(performance, fixture.Runtime.Performance);
    }

    [Fact]
    public void CompletedPassDoesNotFollowReentrantAcceptanceOrFailedRender()
    {
        using var fixture = new Fixture();
        UiSemanticSurfaceSnapshot before = fixture.Capture();
        Assert.Null(before.RenderedFrame);
        Assert.False(before.IsAcceptedFrameRendered);
        fixture.Platform.OnDraw = () =>
        {
            fixture.Platform.OnDraw = null;
            fixture.Source.Text = "New accepted parcel";
            fixture.Refresh();
        };
        fixture.Draw();
        UiSemanticSurfaceSnapshot reentered = fixture.Capture();
        Assert.Equal(before.AcceptedFrame, reentered.RenderedFrame);
        Assert.NotEqual(reentered.AcceptedFrame, reentered.RenderedFrame);
        Assert.False(reentered.IsAcceptedFrameRendered);
        Assert.Equal(1, reentered.CompletedRenderPass);
        Assert.Contains(reentered.Texts, row => row.SemanticId == fixture.Status && row.Text == "New accepted parcel");

        var failure = new InvalidOperationException("platform draw failed");
        fixture.Platform.OnDraw = () => throw failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fixture.Draw));
        UiSemanticSurfaceSnapshot failed = fixture.Capture();
        Assert.Equal(reentered.RenderedFrame, failed.RenderedFrame);
        Assert.Equal(reentered.CompletedRenderPass, failed.CompletedRenderPass);
        fixture.Platform.OnDraw = null;
        fixture.Draw();
        UiSemanticSurfaceSnapshot completed = fixture.Capture();
        Assert.True(completed.IsAcceptedFrameRendered);
        Assert.Equal(2, completed.CompletedRenderPass);
    }

    [Fact]
    public void AvailabilityOnlyFramesNeedTheirOwnCompletedPass()
    {
        using var fixture = new Fixture();
        fixture.Draw();
        UiSemanticSurfaceSnapshot before = fixture.Capture();
        fixture.Enabled = false;
        Assert.True(fixture.Runtime.PumpActions());
        UiSemanticSurfaceSnapshot changed = fixture.Capture();
        Assert.Equal(before.AcceptedFrame!.SceneVersion, changed.AcceptedFrame!.SceneVersion);
        Assert.True(changed.AcceptedFrame.FrameVersion > before.AcceptedFrame.FrameVersion);
        Assert.False(Assert.Single(changed.Elements, row => row.ActionId == fixture.Action.Id).Enabled);
        Assert.False(changed.IsAcceptedFrameRendered);
        Assert.Equal(before.RenderedFrame, changed.RenderedFrame);
        fixture.Draw();
        Assert.True(fixture.Capture().IsAcceptedFrameRendered);
    }

    [Fact]
    public void FailedPreparationRetainsAcceptedAndCompletedVersions()
    {
        using var fixture = new Fixture();
        fixture.Draw();
        UiSemanticSurfaceSnapshot before = fixture.Capture();
        fixture.Source.Text = "A replacement which needs more space";
        UiScene candidate = fixture.Compose();
        var failure = new InvalidOperationException("layout rejected");
        fixture.Platform.OnMeasure = () => throw failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => fixture.Runtime.Update(candidate, new UiRect(0, 0, 700, 500))));
        fixture.Platform.OnMeasure = null;
        UiSemanticSurfaceSnapshot after = fixture.Capture();
        Assert.Equal(before.AcceptedFrame, after.AcceptedFrame);
        Assert.Equal(before.RenderedFrame, after.RenderedFrame);
        Assert.Equal(before.Texts, after.Texts);
        Assert.Equal(before.Elements, after.Elements);
        Assert.True(after.IsAcceptedFrameRendered);
    }

    [Fact]
    public void CaptureIsImmutableBoundedAndReportsTruncation()
    {
        using var fixture = new Fixture();
        UiSemanticSurfaceSnapshot before = fixture.Capture();
        fixture.Source.Text = new string('x', UiSurfaceObservationState.MaxTextLength - 1) + "\U0001F680tail";
        fixture.Refresh();
        UiSemanticSurfaceSnapshot truncated = fixture.Capture();
        Assert.True(truncated.Truncated);
        string value = Assert.Single(truncated.Elements, row => row.SemanticId == fixture.Status).Value!;
        Assert.Equal(new string('x', UiSurfaceObservationState.MaxTextLength - 1), value);
        Assert.Contains(before.Texts, row => row.Text == "Accepted parcel");
        Assert.Throws<NotSupportedException>(() => ((IList<UiSemanticSurfaceText>)before.Texts).Clear());

        var accepted = fixture.Runtime.Scene;
        var textRecipe = Assert.Single(accepted.Root.Children.SelectMany(slot => slot.Children).OfType<UiSourceSceneNode>());
        var many = Enumerable.Range(0, UiSurfaceObservationState.MaxNodes + 10).Select(i =>
            (UiSceneNode)new UiTextSceneNode(Fixture.Id.Child("text/" + i), textRecipe.Role,
                textRecipe.Visual, "Row " + i) { SemanticId = Fixture.Id.Child("semantic/" + i) }).ToArray();
        var root = new UiHostSceneNode(accepted.Root.Id, accepted.Root.Role, accepted.Root.Visual,
            accepted.Root.Policy, many) { SemanticId = Fixture.Id };
        fixture.Runtime.Update(new UiScene(Fixture.Id, "Large fixture", root, accepted.MeasurementContext), new UiRect(0, 0, 960, 100000));
        UiSemanticSurfaceSnapshot bounded = fixture.Capture();
        Assert.True(bounded.Truncated);
        Assert.Equal(UiSurfaceObservationState.MaxRows, bounded.Elements.Count);
        Assert.InRange(bounded.Texts.Count, 0, UiSurfaceObservationState.MaxRows);
        Assert.Equal(Fixture.Id, bounded.Elements[0].SemanticId);
        Assert.Equal(Fixture.Id.Child("semantic/0"), bounded.Elements[1].SemanticId);
        Assert.DoesNotContain(bounded.Elements, row => row.Name == "Row 1025");
    }

    [Fact]
    public void UnknownSemanticOriginsAreReportedWithoutGuessingRendererSuffixes()
    {
        using var fixture = new Fixture();
        UiScene scene = fixture.Runtime.Scene;
        var textRecipe = Assert.Single(scene.Root.Children.SelectMany(slot => slot.Children).OfType<UiSourceSceneNode>());
        var node = new UiTextSceneNode(Fixture.Id.Child("pretend/element/Status/scene/component"),
            textRecipe.Role, textRecipe.Visual, "Unmapped");
        var root = new UiHostSceneNode(scene.Root.Id, scene.Root.Role, scene.Root.Visual, scene.Root.Policy, new[] { node });
        fixture.Runtime.Update(new UiScene(Fixture.Id, "Custom", root, scene.MeasurementContext), Fixture.Viewport);
        UiSemanticSurfaceSnapshot snapshot = fixture.Capture();
        Assert.True(snapshot.HasUnmappedContent);
        Assert.Null(Assert.Single(snapshot.Elements, row => row.Name == "Unmapped").SemanticId);
        Assert.Null(Assert.Single(snapshot.Texts, row => row.Text == "Unmapped").SemanticId);
    }

    [Fact]
    public void RetirementDropsContentAndKeepsDistinctIdentity()
    {
        using var fixture = new Fixture();
        fixture.Draw();
        UiSemanticSurfaceSnapshot before = fixture.Capture();
        UiSemanticSurfaceSnapshot retired = fixture.Observation.Retire();
        fixture.Runtime.Deactivate();
        fixture.Source.Reject = fixture.RejectAvailability = true;
        Assert.Same(retired, fixture.Observation.Capture(null!, null!, visible: true));
        Assert.Equal(before.InstanceId, retired.InstanceId);
        Assert.Equal(before.SurfaceId, retired.SurfaceId);
        Assert.True(retired.Retired);
        Assert.False(retired.Visible);
        Assert.Null(retired.Environment);
        Assert.Null(retired.AcceptedFrame);
        Assert.Null(retired.RenderedFrame);
        Assert.Empty(retired.Elements);
        Assert.Empty(retired.Texts);
        var successor = new UiSurfaceObservationState(Fixture.Id).Retire();
        Assert.NotEqual(retired.InstanceId, successor.InstanceId);
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => fixture.Observation.Capture(null!, null!, false)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Theory]
    [InlineData(4095, false)]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public void StringLimitIsExactAndDoesNotHideUnobservedPortals(int length, bool truncated)
    {
        using var fixture = new Fixture();
        fixture.Source.Text = new string('x', length);
        fixture.Refresh();
        UiSemanticSurfaceSnapshot snapshot = fixture.Observation.Capture(fixture.Runtime, fixture.Environment, true, 2);
        Assert.Equal(truncated, snapshot.Truncated);
        Assert.Equal(new string('x', Math.Min(length, 4096)),
            Assert.Single(snapshot.Elements, row => row.SemanticId == fixture.Status).Value);
        Assert.Equal(2, snapshot.UnobservedPortalCount);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly UiSymbolId Id = RegistryTests.Id("surface-observation");
        internal static readonly UiRect Viewport = new(0, 0, 960, 700);
        internal readonly Source Source = new();
        internal readonly Platform Platform = new();
        internal readonly UiSurfaceObservationState Observation = new(Id);
        internal readonly UiEnvironment Environment = new(new(960, 700), 1, UiInputMode.MouseKeyboard, "en", UiThemePresets.Dark().Id);
        internal readonly UiExperienceDefinition Experience;
        internal readonly UiActionDefinition Action;
        internal readonly UiHostRuntimeSession Runtime;
        private readonly UiInvocationResult _invocation;
        private readonly UiSceneComposer _composer;
        internal bool Enabled = true;
        internal bool RejectAvailability;
        internal int AvailabilityReads;
        internal UiSymbolId Status => Experience.Elements.Single(element => element.Alias == "Status").Id;
        internal Fixture()
        {
            Action = new UiActionDefinition(Id.Child("send"), "Send", () => { }, () =>
            {
                AvailabilityReads++;
                if (RejectAvailability) throw new InvalidOperationException("availability reread");
                return Enabled;
            });
            Experience = new UiExperienceBuilder(Id, "Parcel observation").Monitor("Status", Source).Actions("Actions", Action).Build();
            var registry = new UiRegistryBuilder().Window(Id, Experience.DisplayName, () => Experience).Freeze();
            _invocation = new UiInvocationService(registry).InvokeInEnvironment(Id, Environment);
            _composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
            Runtime = new UiHostRuntimeSession(Compose(), Viewport, Platform);
        }
        internal UiScene Compose() => _composer.Compose(_invocation, locale: Environment.Locale);
        internal void Refresh() => Runtime.Update(Compose(), Viewport);
        internal UiSemanticSurfaceSnapshot Capture() => Observation.Capture(Runtime, Environment, true);
        internal void Draw() { Runtime.Render(); Observation.CompleteRender(Runtime.LastCompletedRender); }
        public void Dispose() => Runtime.Deactivate();
    }

    private sealed class Source : IUiSemanticSource<string>
    {
        internal string Text = "Accepted parcel";
        internal bool Reject;
        internal int Reads;
        public string Value { get { Reads++; if (Reject) throw new InvalidOperationException("source reread"); return Text; } }
        public object UntypedValue => Value;
        public Type ValueType => typeof(string);
        public event Action? Changed { add { } remove { } }
    }

    private sealed class Platform : IUiPlatformBridge
    {
        internal Action? OnDraw;
        internal Action? OnMeasure;
        public UiSize Measure(string text, UiTypography typography, float width, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            return new(Math.Min(width, text.Length * 7), 20);
        }
        public void DrawSurface(UiSurfacePrimitive surface) => OnDraw?.Invoke();
        public void DrawText(UiTextPrimitive text) => OnDraw?.Invoke();
    }
}
