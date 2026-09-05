using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Registration;

public enum UiContributionKind
{
    Action,
    Route,
    Section
}

public sealed class UiContributionPointDescriptor
{
    public UiContributionPointDescriptor(
        UiSymbolId id,
        UiSymbolId owner,
        UiSymbolId region,
        params UiContributionKind[] allowedKinds)
    {
        if (!id.IsValid) throw new ArgumentException("A stable contribution point ID is required.", nameof(id));
        if (!owner.IsValid) throw new ArgumentException("A stable owner ID is required.", nameof(owner));
        if (!region.IsValid) throw new ArgumentException("A stable semantic region ID is required.", nameof(region));
        if (allowedKinds == null || allowedKinds.Length == 0)
            throw new ArgumentException("At least one contribution kind must be allowed.", nameof(allowedKinds));
        Id = id;
        Owner = owner;
        Region = region;
        AllowedKinds = Array.AsReadOnly((UiContributionKind[])allowedKinds.Clone());
    }

    public UiSymbolId Id { get; }
    public UiSymbolId Owner { get; }
    public UiSymbolId Region { get; }
    public IReadOnlyList<UiContributionKind> AllowedKinds { get; }
}

public abstract class UiContributionDescriptor
{
    protected UiContributionDescriptor(UiSymbolId id, UiSymbolId target, string title, int order)
    {
        if (!id.IsValid) throw new ArgumentException("A stable contribution ID is required.", nameof(id));
        if (!target.IsValid) throw new ArgumentException("A stable contribution target ID is required.", nameof(target));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A contribution title is required.", nameof(title));
        Id = id;
        Target = target;
        Title = title;
        Order = order;
    }

    public UiSymbolId Id { get; }
    public UiSymbolId Target { get; }
    public string Title { get; }
    public int Order { get; }
    public abstract UiContributionKind Kind { get; }
}

public sealed class UiActionContributionDescriptor : UiContributionDescriptor
{
    public UiActionContributionDescriptor(
        UiSymbolId id,
        UiSymbolId target,
        UiActionDefinition action,
        int order = 0)
        : base(id, target, action?.Title ?? throw new ArgumentNullException(nameof(action)), order)
        => Action = action;

    public UiActionDefinition Action { get; }
    public override UiContributionKind Kind => UiContributionKind.Action;
}

public sealed class UiRouteContributionDescriptor : UiContributionDescriptor
{
    public UiRouteContributionDescriptor(
        UiSymbolId id,
        UiSymbolId target,
        string title,
        UiSymbolId route,
        UiContributionKind kind = UiContributionKind.Route,
        int order = 0)
        : base(id, target, title, order)
    {
        if (!route.IsValid) throw new ArgumentException("A stable route ID is required.", nameof(route));
        if (kind is not (UiContributionKind.Route or UiContributionKind.Section))
            throw new ArgumentException("A route contribution must be a Route or Section.", nameof(kind));
        Route = route;
        Kind = kind;
    }

    public UiSymbolId Route { get; }
    public override UiContributionKind Kind { get; }
}
