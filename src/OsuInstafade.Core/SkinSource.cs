using System.IO;
using System.IO.Compression;

namespace OsuInstafade.Core;

/// <summary>
/// 스킨의 파일 목록. .osk(압축 파일)와 스킨 폴더를 똑같이 다룬다.
/// 원본은 읽기만 한다. 경로는 스킨 최상위 기준, '/' 구분, 대소문자 무시.
/// </summary>
public sealed class SkinSource : IDisposable
{
    public string InputPath { get; }
    public bool IsArchive { get; }
    /// <summary>파일/폴더 이름 기준 이름 (확장자 제외)</summary>
    public string BaseName { get; }

    private readonly ZipArchive? _zip;
    private readonly FileStream? _zipStream;
    // 상대 경로 → 읽기 함수. 원래 순서를 유지하기 위해 리스트도 함께 보관
    private readonly Dictionary<string, Func<byte[]>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public IReadOnlyList<string> Files => _order;

    public const long MaxFileSize = 512L * 1024 * 1024;

    private SkinSource(string inputPath, bool isArchive, string baseName, ZipArchive? zip, FileStream? zipStream)
    {
        InputPath = inputPath;
        IsArchive = isArchive;
        BaseName = baseName;
        _zip = zip;
        _zipStream = zipStream;
    }

    public static SkinSource Open(string path)
    {
        path = Path.GetFullPath(path.Trim().Trim('"'));
        if (Directory.Exists(path)) return OpenFolder(path);
        if (File.Exists(path)) return OpenArchive(path);
        throw new InstafadeException("스킨 파일이나 폴더를 찾을 수 없습니다.", path);
    }

    private static SkinSource OpenFolder(string folder)
    {
        string root = folder;
        if (!File.Exists(Path.Combine(root, "skin.ini")))
        {
            // 폴더 안에 스킨 폴더가 한 겹 더 있는 경우
            var subs = Directory.GetDirectories(root);
            var withIni = subs.Where(d => File.Exists(Path.Combine(d, "skin.ini"))).ToList();
            bool hasLooseImages = Directory.EnumerateFiles(root).Any(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            if (withIni.Count == 1 && !hasLooseImages) root = withIni[0];
        }
        var src = new SkinSource(folder, false, new DirectoryInfo(folder).Name, null, null);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string full = file;
            src.Add(rel, () =>
            {
                var len = new FileInfo(full).Length;
                if (len > MaxFileSize) throw new InstafadeException("스킨 안에 너무 큰 파일이 있습니다.", rel);
                return File.ReadAllBytes(full);
            });
        }
        if (src._order.Count == 0) throw new InstafadeException("폴더가 비어 있습니다.", folder);
        return src;
    }

    private static SkinSource OpenArchive(string file)
    {
        FileStream fs;
        ZipArchive zip;
        try
        {
            fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            throw new InstafadeException("올바른 .osk(압축) 파일이 아닙니다.", file);
        }
        var src = new SkinSource(file, true, Path.GetFileNameWithoutExtension(file), zip, fs);

        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        string Norm(ZipArchiveEntry e) => e.FullName.Replace('\\', '/').TrimStart('/');

        // skin.ini 가 있는 가장 얕은 폴더를 스킨 최상위로 사용
        string rootPrefix = "";
        var ini = entries.Select(Norm).Where(n => n.Equals("skin.ini", StringComparison.OrdinalIgnoreCase) || n.EndsWith("/skin.ini", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n.Count(c => c == '/')).FirstOrDefault();
        if (ini != null) rootPrefix = ini[..^"skin.ini".Length];
        else
        {
            // skin.ini 가 없으면: 모든 파일이 폴더 하나 안에 있을 때 그 폴더를 최상위로
            var tops = entries.Select(e => Norm(e).Split('/')).Where(p => p.Length > 1).Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tops.Count == 1 && entries.All(e => Norm(e).Contains('/'))) rootPrefix = tops[0] + "/";
        }

        foreach (var e in entries)
        {
            string n = Norm(e);
            if (rootPrefix.Length > 0)
            {
                if (!n.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                n = n[rootPrefix.Length..];
            }
            if (n.Contains("..")) continue; // 위험한 경로 무시
            var entry = e;
            string rel = n;
            src.Add(n, () =>
            {
                if (entry.Length > MaxFileSize) throw new InstafadeException("스킨 안에 너무 큰 파일이 있습니다.", rel);
                using var s = entry.Open();
                using var ms = new MemoryStream((int)Math.Max(0, Math.Min(entry.Length, int.MaxValue)));
                s.CopyTo(ms);
                return ms.ToArray();
            });
        }
        if (src._order.Count == 0) throw new InstafadeException(".osk 파일 안에 아무것도 없습니다.", file);
        return src;
    }

    private void Add(string rel, Func<byte[]> reader)
    {
        if (_files.ContainsKey(rel)) return; // 대소문자만 다른 중복은 첫 번째만
        _files[rel] = reader;
        _order.Add(rel);
    }

    public bool Exists(string rel) => _files.ContainsKey(Normalize(rel));

    /// <summary>실제 파일 이름(원래 대소문자)</summary>
    public string? ActualName(string rel)
        => _order.FirstOrDefault(n => n.Equals(Normalize(rel), StringComparison.OrdinalIgnoreCase));

    public byte[] Read(string rel) => _files[Normalize(rel)]();

    public byte[]? ReadOrNull(string rel) => Exists(rel) ? Read(rel) : null;

    public static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

    public void Dispose()
    {
        _zip?.Dispose();
        _zipStream?.Dispose();
    }
}

public sealed class InstafadeException : Exception
{
    public string? Detail { get; }
    public InstafadeException(string message, string? detail = null) : base(message) => Detail = detail;
}
