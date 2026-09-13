using System.Reflection;

namespace Hatifect.ChestsAnywhereOverlay.Integration;

/// <summary>Side-effect-free startup guard for the pinned Chests Anywhere raw API shape.</summary>
internal static class ChestsAnywherePrivateApiShape
{
    internal static bool IsSupported(object? api)
    {
        if (api == null) return false;
        Type type = api.GetType();
        FieldInfo? getOverlay = FindField(type, "GetOverlay");
        return getOverlay != null
            && typeof(Delegate).IsAssignableFrom(getOverlay.FieldType)
            && FindMethod(type, "IsOverlayActive", parameterCount: 0) != null
            && FindMethod(type, "IsOverlayModal", parameterCount: 0) != null;
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static MethodInfo? FindMethod(Type? type, string name, int parameterCount)
    {
        while (type != null)
        {
            MethodInfo? method = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == parameterCount);
            if (method != null) return method;
            type = type.BaseType;
        }
        return null;
    }
}
