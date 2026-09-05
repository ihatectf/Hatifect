using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Exact identity of the source-owned Hatifect UI runtime that is actually loaded by Stardew.
/// Config and documentation are intentionally excluded; DLLs, manifest and any present assets are bound.
/// Keep this algorithm byte-for-byte compatible with tools/runtime_fingerprint.py.
/// </summary>
internal static class UiRuntimeFingerprint
{
    public const string Algorithm = "sha256-runtime-v2";

    private static readonly string[] RequiredRuntimeDlls =
    {
        "Hatifect.UI.Stardew.dll",
        "Hatifect.UI.Experience.dll",
        "Hatifect.UI.Language.dll",
        "Hatifect.UI.Planning.dll",
        "Hatifect.UI.Runtime.dll",
        "Hatifect.UI.Semantics.dll",
        "Hatifect.UI.Tooling.dll",
        "Hatifect.UI.DevTools.dll"
    };

    public static string Compute(string moduleRoot)
    {
        if (string.IsNullOrWhiteSpace(moduleRoot))
            throw new ArgumentException("A Hatifect UI module root is required.", nameof(moduleRoot));

        string root = Path.GetFullPath(moduleRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Hatifect UI runtime root does not exist: {root}");

        var files = new List<string>(RequiredRuntimeDlls.Length + 1);
        foreach (string name in RequiredRuntimeDlls.Append("manifest.json"))
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required Hatifect UI runtime file is missing: {name}", path);
            files.Add(path);
        }

        string assets = Path.Combine(root, "assets");
        if (File.Exists(assets))
            throw new IOException($"Hatifect UI runtime assets path must be a directory: {assets}");
        if (Directory.Exists(assets))
            files.AddRange(Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories));

        var lines = new List<string>(files.Count);
        foreach (string path in files.OrderBy(path => Relative(root, path), StringComparer.Ordinal))
        {
            string relative = Relative(root, path);
            string fileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            lines.Add(relative + "\t" + fileHash);
        }

        string payload = string.Join("\n", lines) + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
