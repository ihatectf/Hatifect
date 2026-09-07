using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

/// <summary>
/// Optional additive API for exact TestHarness observation. Request this interface from the UI
/// mod and create observed active-menu overlays through that same API instance. Existing surface
/// and automation interfaces remain unchanged; production observation is disabled.
/// </summary>
public interface IUiSemanticSurfaceObservationApi : IUiSemanticSurfaceApi
{
    IUiSemanticSurfaceObservation Observation { get; }
}

/// <summary>Read-only, owning-UI-thread observation of active-menu overlays in the exact harness.</summary>
public interface IUiSemanticSurfaceObservation
{
    bool IsEnabled { get; }

    /// <summary>
    /// Copy already accepted root content without reading sources, composing, laying out, pumping or
    /// dispatching input. Rejects disabled automation, foreign handles, foreign threads/screens and
    /// visible overlays that have lost their native menu owner, without synchronizing their lifecycle.
    /// Retired owned handles return only immutable identity/lifecycle information.
    /// </summary>
    UiSemanticSurfaceSnapshot Capture(IUiSemanticSurfaceSession session);
}

/// <summary>Identity of an accepted scene and its prepared render frame within one surface instance.</summary>
public sealed record UiSemanticSurfaceFrame(long SceneVersion, long FrameVersion);

/// <summary>One already materialized accessibility row. Node IDs are opaque; SemanticId is its origin.</summary>
public sealed record UiSemanticSurfaceElement(
    UiSymbolId Node, UiSymbolId? SemanticId, string Role, string? Name, string? Value,
    bool Enabled, UiSymbolId? ActionId);

/// <summary>
/// Text submitted by a prepared render frame. A node can have multiple rows (label/supporting text).
/// This is the full submitted string, before platform wrapping, clipping or ellipsis.
/// </summary>
public sealed record UiSemanticSurfaceText(UiSymbolId Node, UiSymbolId? SemanticId, string Text);

/// <summary>
/// Immutable observation with bounded row counts and display text. Each row collection has at most
/// 256 entries; Name, Value and Text have at most 4096 UTF-16 code units, with explicit truncation.
/// Opaque semantic identities and the accepted environment are preserved without truncation.
/// Null semantic origins are explicitly unmapped.
/// CompletedRenderPass counts successful owned surface passes, not global game/backbuffer frames.
/// Nested portals are not projected; UnobservedPortalCount makes that limitation explicit.
/// </summary>
public sealed class UiSemanticSurfaceSnapshot
{
    internal UiSemanticSurfaceSnapshot(Guid instanceId, UiSymbolId surfaceId, UiSymbolId? experienceId,
        bool visible, bool retired, int unobservedPortalCount, UiEnvironment? environment, UiSemanticSurfaceFrame? acceptedFrame,
        long completedRenderPass, UiSemanticSurfaceFrame? renderedFrame,
        UiSemanticSurfaceElement[] elements, UiSemanticSurfaceText[] texts, bool truncated, bool hasUnmappedContent)
    {
        InstanceId = instanceId;
        SurfaceId = surfaceId;
        ExperienceId = experienceId;
        Visible = visible;
        Retired = retired;
        UnobservedPortalCount = unobservedPortalCount;
        Environment = environment;
        AcceptedFrame = acceptedFrame;
        CompletedRenderPass = completedRenderPass;
        RenderedFrame = renderedFrame;
        Elements = Array.AsReadOnly(elements);
        Texts = Array.AsReadOnly(texts);
        Truncated = truncated;
        HasUnmappedContent = hasUnmappedContent;
    }

    public Guid InstanceId { get; }
    public UiSymbolId SurfaceId { get; }
    public UiSymbolId? ExperienceId { get; }
    public bool Visible { get; }
    public bool Retired { get; }
    public int UnobservedPortalCount { get; }
    public UiEnvironment? Environment { get; }
    public UiSemanticSurfaceFrame? AcceptedFrame { get; }
    public long CompletedRenderPass { get; }
    public UiSemanticSurfaceFrame? RenderedFrame { get; }
    public bool IsAcceptedFrameRendered => !Retired && AcceptedFrame is not null && AcceptedFrame == RenderedFrame;
    public IReadOnlyList<UiSemanticSurfaceElement> Elements { get; }
    public IReadOnlyList<UiSemanticSurfaceText> Texts { get; }
    public bool Truncated { get; }
    public bool HasUnmappedContent { get; }
}
