using System;

namespace Hatifect.UI.Semantics;

public enum UiSemanticTypeKind
{
    Error = 0,
    Bool,
    Int,
    Float,
    String,
    Length,
    Duration,
    Region,
    Presentation,
    PresentationPattern,
    PresentationProfile,
    VisualState,
    SurfaceToken,
    ColorToken,
    SpaceToken,
    RadiusToken,
    MotionToken,
    TypographyToken,
    ElevationToken,
    TransformToken,
    Opacity,
    Border,
    Offset,
    Transition,
    EnumValue
}

public sealed record UiSemanticType(UiSemanticTypeKind Kind, UiSemanticType? ElementType = null)
{
    public static readonly UiSemanticType Error = new(UiSemanticTypeKind.Error);
    public static readonly UiSemanticType Bool = new(UiSemanticTypeKind.Bool);
    public static readonly UiSemanticType Int = new(UiSemanticTypeKind.Int);
    public static readonly UiSemanticType Float = new(UiSemanticTypeKind.Float);
    public static readonly UiSemanticType String = new(UiSemanticTypeKind.String);
    public static readonly UiSemanticType Length = new(UiSemanticTypeKind.Length);
    public static readonly UiSemanticType Duration = new(UiSemanticTypeKind.Duration);
    public static readonly UiSemanticType Region = new(UiSemanticTypeKind.Region);
    public static readonly UiSemanticType Presentation = new(UiSemanticTypeKind.Presentation);
    public static readonly UiSemanticType PresentationPattern = new(UiSemanticTypeKind.PresentationPattern);
    public static readonly UiSemanticType PresentationProfile = new(UiSemanticTypeKind.PresentationProfile);
    public static readonly UiSemanticType VisualState = new(UiSemanticTypeKind.VisualState);
    public static readonly UiSemanticType SurfaceToken = new(UiSemanticTypeKind.SurfaceToken);
    public static readonly UiSemanticType ColorToken = new(UiSemanticTypeKind.ColorToken);
    public static readonly UiSemanticType SpaceToken = new(UiSemanticTypeKind.SpaceToken);
    public static readonly UiSemanticType RadiusToken = new(UiSemanticTypeKind.RadiusToken);
    public static readonly UiSemanticType MotionToken = new(UiSemanticTypeKind.MotionToken);
    public static readonly UiSemanticType TypographyToken = new(UiSemanticTypeKind.TypographyToken);
    public static readonly UiSemanticType ElevationToken = new(UiSemanticTypeKind.ElevationToken);
    public static readonly UiSemanticType TransformToken = new(UiSemanticTypeKind.TransformToken);
    public static readonly UiSemanticType Opacity = new(UiSemanticTypeKind.Opacity);
    public static readonly UiSemanticType Border = new(UiSemanticTypeKind.Border);
    public static readonly UiSemanticType Offset = new(UiSemanticTypeKind.Offset);
    public static readonly UiSemanticType EnumValue = new(UiSemanticTypeKind.EnumValue);

    public static UiSemanticType TransitionOf(UiSemanticType elementType)
        => new(UiSemanticTypeKind.Transition, elementType ?? throw new ArgumentNullException(nameof(elementType)));

    public override string ToString() => Kind == UiSemanticTypeKind.Transition ? $"Transition<{ElementType}>" : Kind.ToString();
}

[Flags]
public enum UiPropertyEffects
{
    None = 0,
    Recompose = 1 << 0,
    Measure = 1 << 1,
    Arrange = 1 << 2,
    Render = 1 << 3
}
