using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OsuInstafade.Core;

public sealed record InstalledSkin(string Name, string Path);

/// <summary>이 PC 에 설치된 osu!(stable) 의 Skins 폴더를 찾는다.</summary>
public static class OsuLocator
{
    public static IReadOnlyList<string> FindSkinsFolders()
    {
        var dirs = new List<string>();
        void Try(string? osuDir)
        {
            if (string.IsNullOrWhiteSpace(osuDir)) return;
            try
            {
                string skins = System.IO.Path.Combine(osuDir, "Skins");
                if (Directory.Exists(skins) && !dirs.Contains(skins, StringComparer.OrdinalIgnoreCase)) dirs.Add(skins);
            }
            catch { /* 무시 */ }
        }

        Try(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!"));

        string[] keys =
        {
            @"osu\shell\open\command",
            @"osu!\shell\open\command",
            @"osustable.File.osk\shell\open\command",
            @"osustable.File.osz\shell\open\command",
            @"osu!\DefaultIcon",
        };
        foreach (var k in keys)
        {
            foreach (var root in new[] { Registry.CurrentUser.OpenSubKey(@"Software\Classes"), Registry.ClassesRoot })
            {
                try
                {
                    using var key = root?.OpenSubKey(k);
                    var cmd = key?.GetValue(null) as string;
                    var exe = ExtractExe(cmd);
                    if (exe != null) Try(System.IO.Path.GetDirectoryName(exe));
                }
                catch { /* 무시 */ }
            }
        }
        return dirs;
    }

    private static string? ExtractExe(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var m = Regex.Match(cmd, "\"([^\"]+?\\.exe)\"", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(cmd, @"^(.+?\.exe)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static IReadOnlyList<InstalledSkin> ListSkins()
    {
        var list = new List<InstalledSkin>();
        foreach (var skins in FindSkinsFolders())
        {
            try
            {
                foreach (var d in Directory.GetDirectories(skins))
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) continue;
                    list.Add(new InstalledSkin(new DirectoryInfo(d).Name, d));
                }
            }
            catch { /* 무시 */ }
        }
        return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
