using System.Text;
using System.Text.RegularExpressions;

namespace OsuInstafade.Core;

/// <summary>
/// skin.ini 를 줄 단위로 보관하면서 읽고 고치는 클래스.
/// 손대지 않는 줄(주석, 공백, 다른 설정)은 원래 모습 그대로 유지한다.
/// </summary>
public sealed class SkinIni
{
    private readonly List<string> _lines;

    private SkinIni(List<string> lines) => _lines = lines;

    public static SkinIni Empty() => new(new List<string>());

    static SkinIni()
    {
        // 한국어 ANSI(CP949) 같은 옛 인코딩 지원
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static SkinIni Parse(byte[]? data)
    {
        if (data == null || data.Length == 0) return Empty();
        string text = DecodeText(data);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        // 마지막 빈 줄 하나는 파일 끝 줄바꿈이므로 제거
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return new SkinIni(lines);
    }

    internal static string DecodeText(byte[] data)
    {
        // BOM 확인
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        // BOM 없는 UTF-8 인지 엄격하게 검사
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(949).GetString(data); }
            catch { return Encoding.Latin1.GetString(data); }
        }
    }

    public IReadOnlyList<string> Lines => _lines;

    private readonly record struct Entry(int Index, string Section, string Key, string Value);

    private static readonly Regex SectionRx = new(@"^\s*\[(?<name>[^\]]*)\]\s*$", RegexOptions.Compiled);

    private IEnumerable<Entry> Entries()
    {
        string section = "";
        for (int i = 0; i < _lines.Count; i++)
        {
            string raw = _lines[i];
            string t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("//") || t.StartsWith(';')) continue;
            var m = SectionRx.Match(t);
            if (m.Success) { section = m.Groups["name"].Value.Trim(); continue; }
            int colon = t.IndexOf(':');
            if (colon <= 0) continue;
            string key = t[..colon].Trim();
            string value = t[(colon + 1)..];
            // osu! 와 같이 값 뒤의 인라인 주석(// ...)은 무시
            int comment = value.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment];
            value = value.Trim();
            yield return new Entry(i, section, key, value);
        }
    }

    private static bool SectionMatch(string actual, string? wanted)
        => wanted == null || string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>값 읽기 (같은 키가 여러 번 있으면 첫 번째)</summary>
    public string? Get(string key, string? section = null)
        => Entries().Where(e => SectionMatch(e.Section, section) && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Value).FirstOrDefault();

    public IReadOnlyList<string> GetAll(string key, string? section = null)
        => Entries().Where(e => SectionMatch(e.Section, section) && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Value).ToList();

    public bool ContainsText(string text) => _lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>[Colours] 의 Combo1~Combo8 을 번호 순서대로</summary>
    public IReadOnlyList<Rgb> GetComboColours()
    {
        var list = new SortedDictionary<int, Rgb>();
        foreach (var e in Entries())
        {
            var m = Regex.Match(e.Key, @"^Combo(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[1].Value);
            if (n < 1 || n > 8 || list.ContainsKey(n)) continue;
            if (Rgb.TryParse(e.Value, out var c)) list[n] = c;
        }
        return list.Values.ToList();
    }

    /// <summary>조건에 맞는 키의 줄을 모두 지움</summary>
    public void RemoveAll(Func<string, bool> keyPredicate, string? section = null)
    {
        var idx = Entries().Where(e => SectionMatch(e.Section, section) && keyPredicate(e.Key)).Select(e => e.Index).OrderByDescending(i => i).ToList();
        foreach (var i in idx) _lines.RemoveAt(i);
    }

    public void Remove(string key, string? section = null)
        => RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase), section);

    /// <summary>키를 모든 곳에서 지우고, 지정한 섹션 끝에 새 값으로 넣음 (섹션이 없으면 만듦)</summary>
    public void Set(string section, string key, string value)
    {
        Remove(key);
        AddToSection(section, $"{key}: {value}");
    }

    public void AddToSection(string section, string line)
    {
        int header = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var m = SectionRx.Match(_lines[i].Trim());
            if (m.Success && m.Groups["name"].Value.Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) { header = i; break; }
        }
        if (header < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add(line);
            return;
        }
        // 섹션의 마지막 설정 줄 바로 뒤에 넣음 (다음 섹션 앞의 빈 줄/주석은 그대로)
        int insertAt = header + 1;
        for (int i = header + 1; i < _lines.Count; i++)
        {
            string t = _lines[i].Trim();
            if (SectionRx.IsMatch(t)) break;
            if (t.Length > 0 && !t.StartsWith("//") && !t.StartsWith(';')) insertAt = i + 1;
        }
        _lines.Insert(insertAt, line);
    }

    public void PrependLine(string line) => _lines.Insert(0, line);

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l).Append("\r\n");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    public override string ToString() => string.Join("\r\n", _lines);
}
