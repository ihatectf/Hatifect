using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

internal static class FlowAcceptanceSaveTree
{
    internal static string Fingerprint(string path)
    {
        ValidateSaveTree(path); var lines = new List<string>(); long total = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(path, entry);
            if (Directory.Exists(entry)) lines.Add("D\t" + relative + "\n");
            else
            {
                long size = new FileInfo(entry).Length; total = checked(total + size);
                Require(size <= 64 * 1024 * 1024 && total <= 256 * 1024 * 1024, "Save tree hashing exceeds its byte bound.");
                lines.Add("F\t" + relative + "\t" + HashFile(entry) + "\n");
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
    }
}
