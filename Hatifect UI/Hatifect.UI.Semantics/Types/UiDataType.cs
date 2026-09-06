namespace Hatifect.UI.Semantics;

/// <summary>Data shape is independent of the presentation language's visual value types.</summary>
public enum UiDataShape { Scalar, Collection, Selection, Form, Validation, Action }

/// <summary>Transport-neutral nominal data contract. Nullability is explicit, including reference values.</summary>
public sealed record UiDataType(
    UiSymbolId TypeId,
    UiDataShape Shape,
    bool Nullable,
    UiDataType? ItemType = null,
    UiDataType? InputType = null,
    UiDataType? ResultType = null,
    UiDataType? TargetType = null,
    UiSymbolId? TargetCapability = null)
{
    public bool Accepts(UiDataType source)
        => Compare(this, source, allowRequiredSource: true);

    public bool Equals(UiDataType? other) => other is not null && Compare(this, other, allowRequiredSource: false);

    public override int GetHashCode()
    {
        if (ItemType is null && InputType is null && ResultType is null && TargetType is null)
            return HashCode.Combine(TypeId, Shape, Nullable, TargetCapability, 0, 0, 0, 0);
        var hashes = new Dictionary<UiDataType, int>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(UiDataType Type, bool Expanded)>();
        pending.Push((this, false));
        while (pending.TryPop(out var entry))
        {
            UiDataType type = entry.Type;
            if (hashes.ContainsKey(type)) continue;
            if (!entry.Expanded)
            {
                pending.Push((type, true));
                Push(type.ItemType); Push(type.InputType); Push(type.ResultType); Push(type.TargetType);
            }
            else hashes[type] = HashCode.Combine(type.TypeId, type.Shape, type.Nullable, type.TargetCapability,
                Hash(type.ItemType), Hash(type.InputType), Hash(type.ResultType), Hash(type.TargetType));
        }
        return hashes[this];
        void Push(UiDataType? type) { if (type is not null && !hashes.ContainsKey(type)) pending.Push((type, false)); }
        int Hash(UiDataType? type) => type is null ? 0 : hashes[type];
    }

    private static bool Compare(UiDataType accepted, UiDataType source, bool allowRequiredSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(accepted, source)) return true;
        if (accepted.TypeId != source.TypeId || accepted.Shape != source.Shape || accepted.TargetCapability != source.TargetCapability
            || (accepted.Nullable != source.Nullable && !(allowRequiredSource && accepted.Nullable))) return false;
        if (accepted.ItemType is null && source.ItemType is null && accepted.InputType is null && source.InputType is null
            && accepted.ResultType is null && source.ResultType is null && accepted.TargetType is null && source.TargetType is null) return true;
        var pending = new Stack<(UiDataType Accepted, UiDataType Source, bool Root)>();
        var visited = new Dictionary<UiDataType, HashSet<UiDataType>>(ReferenceEqualityComparer.Instance);
        pending.Push((accepted, source, true));
        while (pending.TryPop(out var pair))
        {
            if (ReferenceEquals(pair.Accepted, pair.Source)) continue;
            if (!visited.TryGetValue(pair.Accepted, out var sources))
                visited.Add(pair.Accepted, sources = new(ReferenceEqualityComparer.Instance));
            if (!sources.Add(pair.Source)) continue;
            UiDataType left = pair.Accepted, right = pair.Source;
            if (left.TypeId != right.TypeId || left.Shape != right.Shape || left.TargetCapability != right.TargetCapability
                || (left.Nullable != right.Nullable && !(pair.Root && allowRequiredSource && left.Nullable))) return false;
            if (!Push(left.ItemType, right.ItemType) || !Push(left.InputType, right.InputType)
                || !Push(left.ResultType, right.ResultType) || !Push(left.TargetType, right.TargetType)) return false;
        }
        return true;
        bool Push(UiDataType? left, UiDataType? right)
        {
            if (left is null || right is null) return left is null && right is null;
            pending.Push((left, right, false));
            return true;
        }
    }
}

public static class UiDataTypes
{
    public static readonly UiSymbolId StringId = new("Hatifect.UI", "data/string");
    public static readonly UiSymbolId BooleanId = new("Hatifect.UI", "data/boolean");
    public static readonly UiSymbolId NumberId = new("Hatifect.UI", "data/number");
    public static readonly UiSymbolId SymbolId = new("Hatifect.UI", "data/symbol");
    public static readonly UiSymbolId UnitId = new("Hatifect.UI", "data/unit");
    public static readonly UiDataType String = Scalar(StringId, false);
    public static readonly UiDataType Boolean = Scalar(BooleanId, false);
    public static readonly UiDataType Number = Scalar(NumberId, false);
    public static readonly UiDataType Symbol = Scalar(SymbolId, false);
    public static readonly UiDataType Unit = Scalar(UnitId, false);

    public static UiDataType Scalar(UiSymbolId id, bool nullable) => new(id, UiDataShape.Scalar, nullable);
    public static UiDataType Collection(UiDataType item) => new(item.TypeId, UiDataShape.Collection, false, item);
    public static UiDataType Selection(UiDataType item) => new(SymbolId, UiDataShape.Selection, true, item);
    public static UiDataType Form(UiSymbolId schema) => new(schema, UiDataShape.Form, false);
    public static UiDataType Validation(UiSymbolId schema) => new(schema, UiDataShape.Validation, false);
    public static UiDataType Action(UiSymbolId id, UiDataType input, UiDataType result,
        UiDataType? target = null, UiSymbolId? targetCapability = null)
        => new(id, UiDataShape.Action, false, InputType: input, ResultType: result,
            TargetType: target, TargetCapability: targetCapability);
}
