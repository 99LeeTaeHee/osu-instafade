using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace OsuInstafade.Core;

public sealed class InstafadeOptions
{
    /// <summary>서클 위 숫자 표시</summary>
    public bool ShowNumbers { get; set; } = true;
    /// <summary>서클 색 (null 이면 스킨의 Combo1)</summary>
    public Rgb? CircleColor { get; set; }
    /// <summary>왼쪽 아래 콤보 카운터 숫자 자동 복구</summary>
    public bool FixComboCounter { get; set; } = true;
    /// <summary>히트 라이팅(칠 때 번쩍이는 빛) 끄기</summary>
    public bool DisableHitLighting { get; set; }
    /// <summary>결과 저장 폴더 (null 이면 원본과 같은 폴더)</summary>
    public string? OutputDirectory { get; set; }
}

public sealed class SkinAnalysis
{
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string BaseName { get; init; }
    public required bool IsArchive { get; init; }
    public required bool IsHd { get; init; }
    public required bool HasOwnCircle { get; init; }
    public required bool HasOwnNumbers { get; init; }
    public required bool OverlayAnimated { get; init; }
    public required bool AlreadyInstafaded { get; init; }
    public required bool ComboCounterBroken { get; init; }
    public required IReadOnlyList<Rgb> ComboColours { get; init; }
    public required Rgb CircleColor { get; init; }
    /// <summary>변경 전 서클 모습 (숫자 제외, 고화질 크기)</summary>
    public required RgbaImage PreviewBefore { get; init; }
    /// <summary>변경 전 숫자 1 (서클 위에 0.8배로 그려지는 크기)</summary>
    public required RgbaImage PreviewNumber { get; init; }
    /// <summary>변경 후 모습 (같은 크기 기준)</summary>
    public required RgbaImage PreviewAfter { get; init; }
    /// <summary>어프로치 서클 (없으면 null)</summary>
    public RgbaImage? PreviewApproach { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class InstafadeResult
{
    public required string OutputPath { get; init; }
    public required SkinAnalysis Analysis { get; init; }
    public required int HitCircleOverlap { get; init; }
    public required int NumberImageWidthHd { get; init; }
    public required int NumberImageHeightHd { get; init; }
    public required int ChangedFileCount { get; init; }
    public required bool ComboCounterFixed { get; init; }
}

public static class InstafadeEngine
{
    /// <summary>결과 skin.ini 맨 위에 넣는 표시 (이미 적용된 스킨 감지에도 사용)</summary>
    public const string Marker = "// instafade applied";
    public const string NameSuffix = " (instafade)";
    public static readonly Rgb DefaultCombo1 = new(255, 192, 0);

    private static readonly byte[] TransparentPng = new RgbaImage(1, 1).EncodePngOnSta();

    /// <summary>스킨을 분석하고 미리보기를 만든다 (파일은 만들지 않음)</summary>
    public static SkinAnalysis Analyze(string path, InstafadeOptions? options = null)
        => Sta.Run(() =>
        {
            using var src = SkinSource.Open(path);
            return Build(src, options ?? new InstafadeOptions(), full: false, progress: null).Analysis;
        });

    /// <summary>잔상 제거 스킨(.osk)을 새로 만든다. 원본은 건드리지 않는다.</summary>
    public static InstafadeResult Run(string path, InstafadeOptions options, IProgress<string>? progress = null)
        => Sta.Run(() =>
        {
            progress?.Report("스킨을 읽는 중…");
            using var src = SkinSource.Open(path);
            var b = Build(src, options, full: true, progress);
            if (b.Analysis.AlreadyInstafaded)
                throw new InstafadeException("이미 잔상 제거가 적용된 스킨입니다. 원래 스킨으로 다시 시도해 주세요.");

            string outPath = MakeOutputPath(src, options);
            progress?.Report("새 .osk 파일을 만드는 중…");
            WriteOsk(src, b.Overrides, outPath);

            progress?.Report("결과를 점검하는 중…");
            Verify(outPath, b);

            progress?.Report("완료");
            return new InstafadeResult
            {
                OutputPath = outPath,
                Analysis = b.Analysis,
                HitCircleOverlap = b.Overlap,
                NumberImageWidthHd = b.CanvasW,
                NumberImageHeightHd = b.CanvasH,
                ChangedFileCount = b.Overrides.Count,
                ComboCounterFixed = b.ComboFixed,
            };
        });

    // ------------------------------------------------------------------

    private sealed class BuildOutput
    {
        public required SkinAnalysis Analysis;
        public Dictionary<string, byte[]> Overrides = new(StringComparer.OrdinalIgnoreCase);
        public int Overlap, CanvasW, CanvasH;
        public string Prefix = "default";
        public bool ComboFixed;
    }

    private static readonly string[] ClearAlways = { "hitcircle", "hitcircleoverlay" };
    private static readonly string[] ClearIfPresent = { "sliderstartcircle", "sliderstartcircleoverlay" };
    private static readonly Regex FrameRx = new(@"^(hitcircle|hitcircleoverlay|sliderstartcircle|sliderstartcircleoverlay)-\d+(@2x)?\.png$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static BuildOutput Build(SkinSource src, InstafadeOptions opt, bool full, IProgress<string>? progress)
    {
        var warnings = new List<string>();
        var ini = SkinIni.Parse(src.ReadOrNull("skin.ini"));
        if (!src.Exists("skin.ini")) warnings.Add("skin.ini 가 없어 새로 만듭니다.");

        string name = ini.Get("Name", "General") ?? ini.Get("Name") ?? src.BaseName;
        string author = ini.Get("Author", "General") ?? ini.Get("Author") ?? "";
        string prefix = NormPrefix(ini.Get("HitCirclePrefix", "Fonts") ?? ini.Get("HitCirclePrefix") ?? "default");
        bool overlayAbove = ParseBool(ini.Get("HitCircleOverlayAboveNumber") ?? ini.Get("HitCircleOverlayAboveNumer"), true);
        var colours = ini.GetComboColours();
        Rgb color = opt.CircleColor ?? (colours.Count > 0 ? colours[0] : DefaultCombo1);

        progress?.Report("서클 그림을 찾는 중…");
        var resolver = new ImageResolver(src, warnings);
        var hcR = resolver.Resolve("hitcircle", animatable: false);
        var ovR = resolver.Resolve("hitcircleoverlay", animatable: true);

        var numbers = new RgbaImage[10];
        bool ownNumbers = true;
        for (int i = 0; i <= 9; i++)
        {
            var r = resolver.Resolve($"{prefix}-{i}", animatable: false);
            if (r == null) { ownNumbers = false; numbers[i] = Fallbacks.Digit(i); }
            else numbers[i] = r.Hd;
        }
        if (!ownNumbers) warnings.Add($"서클 숫자 그림({prefix}-0~9) 중 없는 것이 있어 기본 글꼴로 대신 그립니다.");

        bool already = ini.ContainsText(Marker) ||
                       (hcR != null && ovR != null && hcR.Hd.IsFullyTransparent() && ovR.Hd.IsFullyTransparent()
                        && ownNumbers && numbers[0].IsFullyTransparent() && !numbers[1].IsFullyTransparent());

        RgbaImage hc = hcR?.Hd ?? Fallbacks.HitCircle();
        RgbaImage ov = ovR?.Hd ?? Fallbacks.HitCircleOverlay();
        if (hcR == null) warnings.Add("hitcircle 그림이 없어 기본 모양으로 대신합니다.");
        if (ovR == null) warnings.Add("hitcircleoverlay 그림이 없어 기본 모양으로 대신합니다.");

        // --- 서클 합치기 ---
        progress?.Report("서클과 숫자를 합치는 중…");
        var tinted = hc.Tinted(color);
        static int S(int v) => Math.Max(1, (int)Math.Round(v * 1.25, MidpointRounding.AwayFromZero));
        var bigHc = tinted.Resize(S(tinted.Width), S(tinted.Height));
        var bigOv = ov.Resize(S(ov.Width), S(ov.Height));

        int cw = Math.Max(bigHc.Width, bigOv.Width);
        int ch = Math.Max(bigHc.Height, bigOv.Height);
        if (opt.ShowNumbers)
            for (int i = 1; i <= 9; i++) { cw = Math.Max(cw, numbers[i].Width); ch = Math.Max(ch, numbers[i].Height); }
        cw += cw % 2; ch += ch % 2; // 일반 화질(절반)로 정확히 나누어지게 짝수로

        RgbaImage Compose(int digit)
        {
            var c = new RgbaImage(cw, ch);
            c.DrawCentered(bigHc);
            if (opt.ShowNumbers)
            {
                if (overlayAbove) { c.DrawCentered(numbers[digit]); c.DrawCentered(bigOv); }
                else { c.DrawCentered(bigOv); c.DrawCentered(numbers[digit]); }
            }
            else c.DrawCentered(bigOv);
            return c;
        }

        // --- 미리보기 (같은 화면 크기 기준) ---
        int bw = Math.Max(tinted.Width, ov.Width), bh = Math.Max(tinted.Height, ov.Height);
        var before = new RgbaImage(bw, bh);
        before.DrawCentered(tinted);
        // 변경 전: 서클(퍼지며 사라짐)과 숫자(바로 사라짐)를 따로 보여 주기 위해 분리
        var n1 = numbers[1].Resize(Math.Max(1, (int)Math.Round(numbers[1].Width * 0.8)), Math.Max(1, (int)Math.Round(numbers[1].Height * 0.8)));
        before.DrawCentered(ov);
        var composed1 = Compose(1);
        var after = composed1.Resize(Math.Max(1, (int)Math.Round(cw * 0.8)), Math.Max(1, (int)Math.Round(ch * 0.8)));
        var approach = resolver.Resolve("approachcircle", animatable: false)?.Hd.Tinted(color);

        // --- 콤보 카운터 점검 ---
        var comboPrefixes = ini.GetAll("ComboPrefix").Select(NormPrefix).ToList();
        string scorePrefix = NormPrefix(ini.Get("ScorePrefix") ?? "score");
        bool FontExists(string p) => src.Exists($"{p}-0.png") || src.Exists($"{p}-0@2x.png");
        bool comboBroken = comboPrefixes.Count > 0 &&
                           (comboPrefixes.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 || !FontExists(comboPrefixes[0]));

        var analysis = new SkinAnalysis
        {
            Name = name,
            Author = author,
            BaseName = src.BaseName,
            IsArchive = src.IsArchive,
            IsHd = hcR?.WasHd ?? false,
            HasOwnCircle = hcR != null || ovR != null,
            HasOwnNumbers = ownNumbers,
            OverlayAnimated = src.Exists("hitcircleoverlay-0.png") || src.Exists("hitcircleoverlay-0@2x.png"),
            AlreadyInstafaded = already,
            ComboCounterBroken = comboBroken && FontExists(scorePrefix),
            ComboColours = colours,
            CircleColor = color,
            PreviewBefore = before,
            PreviewNumber = n1,
            PreviewAfter = after,
            PreviewApproach = approach,
            Warnings = warnings,
        };
        var output = new BuildOutput { Analysis = analysis, Prefix = prefix, CanvasW = cw, CanvasH = ch, Overlap = cw / 2 };
        if (!full || already) return output;

        // --- 숫자 그림 저장 ---
        var ov_ = output.Overrides;
        void SavePair(RgbaImage hd, string baseName)
        {
            ov_[$"{baseName}@2x.png"] = hd.EncodePng();
            ov_[$"{baseName}.png"] = hd.Resize(hd.Width / 2, hd.Height / 2).EncodePng();
        }
        SavePair(composed1, $"{prefix}-1");
        for (int i = 2; i <= 9; i++) SavePair(Compose(i), $"{prefix}-{i}");
        SavePair(new RgbaImage(cw, ch), $"{prefix}-0"); // 10, 20… 에서 서클이 한 번만 보이게

        // --- 잔상이 생기는 그림을 투명하게 ---
        progress?.Report("잔상 그림을 지우는 중…");
        foreach (var n in ClearAlways)
        {
            ov_[$"{n}.png"] = TransparentPng;
            ov_[$"{n}@2x.png"] = TransparentPng;
        }
        foreach (var n in ClearIfPresent)
            foreach (var f in new[] { $"{n}.png", $"{n}@2x.png" })
                if (src.Exists(f)) ov_[f] = TransparentPng;
        foreach (var f in src.Files)
            if (!f.Contains('/') && FrameRx.IsMatch(f)) ov_[f] = TransparentPng;
        if (opt.DisableHitLighting)
        {
            ov_["lighting.png"] = TransparentPng;
            ov_["lighting@2x.png"] = TransparentPng;
        }

        // --- skin.ini ---
        ini.RemoveAll(k => Regex.IsMatch(k, @"^Combo\d+$", RegexOptions.IgnoreCase));
        ini.AddToSection("Colours", $"Combo1: {color}");
        ini.Set("Fonts", "HitCircleOverlap", output.Overlap.ToString());
        if (ini.Get("HitCirclePrefix") == null) ini.AddToSection("Fonts", $"HitCirclePrefix: {prefix}");
        string newName = name.EndsWith(NameSuffix.Trim(), StringComparison.OrdinalIgnoreCase) ? name : name + NameSuffix;
        ini.Set("General", "Name", newName);

        if (opt.FixComboCounter && analysis.ComboCounterBroken)
        {
            string scoreOverlap = ini.Get("ScoreOverlap") ?? "0";
            ini.Remove("ComboPrefix");
            ini.Remove("ComboOverlap");
            ini.AddToSection("Fonts", $"ComboPrefix: {scorePrefix}");
            ini.AddToSection("Fonts", $"ComboOverlap: {scoreOverlap}");
            output.ComboFixed = true;
        }
        ini.PrependLine(Marker);
        ov_["skin.ini"] = ini.ToBytes();
        return output;
    }

    // ------------------------------------------------------------------

    private static string NormPrefix(string p)
    {
        p = p.Trim().Trim('"').Replace('\\', '/').TrimStart('/');
        return p.Length == 0 ? "default" : p;
    }

    private static bool ParseBool(string? v, bool def)
    {
        if (v == null) return def;
        v = v.Trim();
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return def;
    }

    private static string MakeOutputPath(SkinSource src, InstafadeOptions opt)
    {
        string dir = opt.OutputDirectory ?? Path.GetDirectoryName(src.InputPath.TrimEnd('\\', '/')) ?? Environment.CurrentDirectory;
        if (!CanWrite(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        string baseName = string.Concat(src.BaseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (baseName.Length == 0) baseName = "skin";
        string candidate = Path.Combine(dir, baseName + NameSuffix + ".osk");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{baseName}{NameSuffix} ({n}).osk");
        return candidate;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, $".instafade_{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static readonly HashSet<string> StoredExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".mp3", ".ogg", ".gif", ".webp" };

    private static void WriteOsk(SkinSource src, IReadOnlyDictionary<string, byte[]> overrides, string outPath)
    {
        string tmp = outPath + ".part";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string rel, byte[] data)
                {
                    var level = StoredExt.Contains(Path.GetExtension(rel)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var e = zip.CreateEntry(rel, level);
                    using var s = e.Open();
                    s.Write(data, 0, data.Length);
                    written.Add(rel);
                }
                foreach (var rel in src.Files)
                    Add(rel, overrides.TryGetValue(rel, out var d) ? d : src.Read(rel));
                foreach (var kv in overrides)
                    if (!written.Contains(kv.Key)) Add(kv.Key, kv.Value);
            }
            File.Move(tmp, outPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 무시 */ }
            throw;
        }
    }

    private static void Verify(string outPath, BuildOutput b)
    {
        string? problem = null;
        try
        {
            using var check = SkinSource.Open(outPath);
            var one = RgbaImage.Decode(check.Read($"{b.Prefix}-1@2x.png"));
            if (one.Width != b.CanvasW || one.Height != b.CanvasH) problem = "숫자 그림 크기가 맞지 않습니다.";
            else if (!RgbaImage.Decode(check.Read("hitcircle.png")).IsFullyTransparent()) problem = "hitcircle 이 투명하지 않습니다.";
            else if (!RgbaImage.Decode(check.Read($"{b.Prefix}-0.png")).IsFullyTransparent()) problem = "0 숫자 그림이 투명하지 않습니다.";
            else
            {
                var ini = SkinIni.Parse(check.Read("skin.ini"));
                if (!ini.ContainsText(Marker) || ini.Get("HitCircleOverlap") != b.Overlap.ToString()) problem = "skin.ini 가 올바르게 저장되지 않았습니다.";
            }
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }
        if (problem != null)
        {
            try { File.Delete(outPath); } catch { /* 무시 */ }
            throw new InstafadeException("결과 점검에 실패해 파일을 지웠습니다: " + problem);
        }
    }
}

/// <summary>WPF 그림 기능은 STA 스레드가 필요하므로 전용 스레드에서 실행</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return func();
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}

internal static class RgbaImageStaExtensions
{
    public static byte[] EncodePngOnSta(this RgbaImage img) => Sta.Run(img.EncodePng);
}
