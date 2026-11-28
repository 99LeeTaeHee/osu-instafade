using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OsuInstafade.Core;

namespace OsuInstafade.Tests;

/// <summary>가짜 스킨을 만드는 도우미</summary>
internal sealed class FakeSkin : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "instafade_test_" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeSkin() => Directory.CreateDirectory(Dir);

    public static byte[] Png(int w, int h, byte r, byte g, byte b, byte a = 255, bool circle = true)
        => Sta.Run(() =>
        {
            var img = new RgbaImage(w, h);
            double cx = w / 2.0, cy = h / 2.0, rad = Math.Min(w, h) / 2.0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (circle && Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2) > rad * rad) continue;
                    int i = (y * w + x) * 4;
                    img.Pixels[i] = b; img.Pixels[i + 1] = g; img.Pixels[i + 2] = r; img.Pixels[i + 3] = a;
                }
            return img.EncodePng();
        });

    public FakeSkin Ini(string text, Encoding? enc = null) { Files["skin.ini"] = (enc ?? new UTF8Encoding(false)).GetBytes(text); return this; }
    public FakeSkin Add(string name, byte[] data) { Files[name] = data; return this; }

    /// <summary>기본 구성: 고화질 서클 + 숫자</summary>
    public FakeSkin Standard(bool hd = true, string prefix = "default")
    {
        int k = hd ? 2 : 1;
        string sfx = hd ? "@2x" : "";
        Add($"hitcircle{sfx}.png", Png(128 * k, 128 * k, 200, 200, 200));
        Add($"hitcircleoverlay{sfx}.png", Png(128 * k, 128 * k, 255, 255, 255, 255, circle: true));
        for (int i = 0; i <= 9; i++) Add($"{prefix}-{i}{sfx}.png", Png(30 * k, 40 * k, 255, 255, 255, 255, circle: false));
        Add("normal-hitnormal.wav", new byte[] { 1, 2, 3 });
        return this;
    }

    public string WriteFolder(string name = "My Skin")
    {
        string root = Path.Combine(Dir, name);
        foreach (var kv in Files)
        {
            string p = Path.Combine(root, kv.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, kv.Value);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    public string WriteOsk(string name = "My Skin", string? innerFolder = null)
    {
        string path = Path.Combine(Dir, name + ".osk");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var kv in Files)
        {
            var e = zip.CreateEntry((innerFolder != null ? innerFolder + "/" : "") + kv.Key);
            using var s = e.Open();
            s.Write(kv.Value);
        }
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}

internal static class Out
{
    public static Dictionary<string, byte[]> ReadOsk(string path)
    {
        var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            d.Add(e.FullName, ms.ToArray()); // 중복이면 예외 → 테스트 실패
        }
        return d;
    }

    public static byte[] Get(this Dictionary<string, byte[]> d, string name)
        => d.First(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public static bool Has(this Dictionary<string, byte[]> d, string name)
        => d.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static RgbaImage Img(this Dictionary<string, byte[]> d, string name)
        => Sta.Run(() => RgbaImage.Decode(d.Get(name)));

    public static SkinIni Ini(this Dictionary<string, byte[]> d) => SkinIni.Parse(d.Get("skin.ini"));
}

public class EngineTests
{
    private static void AssertInstafaded(Dictionary<string, byte[]> o, string prefix = "default")
    {
        Assert.True(o.Img("hitcircle.png").IsFullyTransparent());
        Assert.True(o.Img("hitcircle@2x.png").IsFullyTransparent());
        Assert.True(o.Img("hitcircleoverlay.png").IsFullyTransparent());
        Assert.True(o.Img("hitcircleoverlay@2x.png").IsFullyTransparent());
        Assert.True(o.Img($"{prefix}-0@2x.png").IsFullyTransparent());
        var one = o.Img($"{prefix}-1@2x.png");
        var oneSd = o.Img($"{prefix}-1.png");
        Assert.False(one.IsFullyTransparent());
        Assert.Equal(one.Width / 2, oneSd.Width);
        Assert.Equal(one.Height / 2, oneSd.Height);
        Assert.Equal(0, one.Width % 2);
        var ini = o.Ini();
        Assert.Equal((one.Width / 2).ToString(), ini.Get("HitCircleOverlap"));
        Assert.Equal(InstafadeEngine.Marker, ini.Lines[0]);
        Assert.EndsWith("(instafade)", ini.Get("Name"));
        Assert.DoesNotContain(ini.Lines, l => l.Contains("LTH"));
    }

    [Theory] // 1. 고화질만 / 일반 화질만
    [InlineData(true)]
    [InlineData(false)]
    public void HdAndSdSkins(bool hd)
    {
        using var s = new FakeSkin().Standard(hd).Ini("[General]\nName: Test\n[Colours]\nCombo1: 255,0,0\nCombo2: 0,255,0\n");
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        // 서클 128(SD) → HD 256 → ×1.25 = 320
        Assert.Equal(320, o.Img("default-1@2x.png").Width);
        Assert.Equal(160, r.HitCircleOverlap);
        var ini = o.Ini();
        Assert.Equal("255,0,0", ini.Get("Combo1"));
        Assert.Null(ini.Get("Combo2"));
        Assert.True(o.Has("normal-hitnormal.wav"));
    }

    [Fact] // 1. 섞인 스킨 (서클은 일반, 숫자는 고화질)
    public void MixedResolution()
    {
        using var s = new FakeSkin().Ini("[General]\nName: Mixed\n");
        s.Add("hitcircle.png", FakeSkin.Png(100, 100, 200, 200, 200));
        s.Add("hitcircleoverlay@2x.png", FakeSkin.Png(210, 210, 255, 255, 255));
        for (int i = 0; i <= 9; i++) s.Add($"default-{i}@2x.png", FakeSkin.Png(60, 80, 255, 255, 255, circle: false));
        var r = InstafadeEngine.Run(s.WriteFolder(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.Equal(Math.Round(210 * 1.25, MidpointRounding.AwayFromZero) + (Math.Round(210 * 1.25, MidpointRounding.AwayFromZero) % 2), o.Img("default-1@2x.png").Width);
    }

    [Fact] // 2. 서클·숫자 그림이 아예 없는 스킨
    public void MissingImagesUseFallback()
    {
        using var s = new FakeSkin().Ini("[General]\nName: Empty\n").Add("cursor.png", FakeSkin.Png(10, 10, 1, 1, 1));
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.NotEmpty(r.Analysis.Warnings);
        Assert.False(r.Analysis.HasOwnNumbers);
    }

    [Fact] // 3. 정사각형 아님, 1x1, 큰 그림
    public void OddSizes()
    {
        using var s = new FakeSkin().Ini("[General]\nName: Odd\n");
        s.Add("hitcircle@2x.png", FakeSkin.Png(1999, 1501, 200, 200, 200));
        s.Add("hitcircleoverlay.png", FakeSkin.Png(1, 1, 0, 0, 0, 0));
        for (int i = 0; i <= 9; i++) s.Add($"default-{i}.png", FakeSkin.Png(17, 33, 255, 255, 255, circle: false));
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        AssertInstafaded(Out.ReadOsk(r.OutputPath));
    }

    [Fact] // 4. 움직이는 overlay
    public void AnimatedOverlayFramesCleared()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: Anim\n");
        for (int f = 0; f < 3; f++) s.Add($"hitcircleoverlay-{f}@2x.png", FakeSkin.Png(256, 256, 255, 255, 255));
        s.Add("sliderstartcircle@2x.png", FakeSkin.Png(256, 256, 255, 255, 255));
        s.Add("sliderstartcircleoverlay-0.png", FakeSkin.Png(128, 128, 255, 255, 255));
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.True(r.Analysis.OverlayAnimated);
        for (int f = 0; f < 3; f++) Assert.True(o.Img($"hitcircleoverlay-{f}@2x.png").IsFullyTransparent());
        Assert.True(o.Img("sliderstartcircle@2x.png").IsFullyTransparent());
        Assert.True(o.Img("sliderstartcircleoverlay-0.png").IsFullyTransparent());
    }

    [Fact] // 5. HitCirclePrefix 가 하위 폴더 (역슬래시)
    public void PrefixInSubfolder()
    {
        using var s = new FakeSkin().Standard(true, "fonts/num").Ini("[General]\nName: Sub\n[Fonts]\nHitCirclePrefix: fonts\\num\n");
        var r = InstafadeEngine.Run(s.WriteFolder(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o, "fonts/num");
        Assert.True(r.Analysis.HasOwnNumbers);
    }

    [Fact] // 6. skin.ini 없음
    public void NoSkinIni()
    {
        using var s = new FakeSkin().Standard();
        var r = InstafadeEngine.Run(s.WriteOsk("NoIni"), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.Equal("NoIni (instafade)", o.Ini().Get("Name"));
        Assert.Equal("255,192,0", o.Ini().Get("Combo1"));
        Assert.Equal("latest", o.Ini().Get("Version", "General")); // 없으면 osu! 가 1.0 으로 취급
    }

    [Fact] // 6-2. skin.ini 가 있으면 Version 은 원본 그대로 (없으면 없는 채로)
    public void ExistingIniVersionUntouched()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: Old\n");
        var o = Out.ReadOsk(InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions()).OutputPath);
        Assert.Null(o.Ini().Get("Version"));
    }

    [Fact] // 6-3. 값 뒤 인라인 주석 (// ...) 은 osu! 처럼 무시
    public void InlineCommentsIgnored()
    {
        var ini = SkinIni.Parse(Encoding.UTF8.GetBytes("[Fonts]\nHitCirclePrefix: num // 숫자\nHitCircleOverlayAboveNumber: 0 // 아래\n"));
        Assert.Equal("num", ini.Get("HitCirclePrefix"));
        Assert.Equal("0", ini.Get("HitCircleOverlayAboveNumber"));

        using var s = new FakeSkin().Standard(true, "num").Ini("[General]\nName: Cmt\n[Fonts]\nHitCirclePrefix: num // 숫자\n");
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        Assert.True(r.Analysis.HasOwnNumbers);
        AssertInstafaded(Out.ReadOsk(r.OutputPath), "num");
    }

    [Fact] // 6. 한국어 ANSI(CP949) skin.ini + 깨진 줄
    public void Cp949IniAndGarbage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var s = new FakeSkin().Standard()
            .Ini("[General]\nName: 한글스킨\n쓰레기줄\n:::\n[Colours]\nCombo1: 10, 20, 30 // 주석\n", Encoding.GetEncoding(949));
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.Equal("한글스킨 (instafade)", o.Ini().Get("Name"));
        Assert.Equal("10,20,30", o.Ini().Get("Combo1"));
    }

    [Fact] // 7. 파일 이름 대소문자가 섞여도 중복 없이 교체
    public void MixedCaseFileNames()
    {
        using var s = new FakeSkin().Ini("[General]\nName: Case\n");
        s.Add("HitCircle@2x.PNG", FakeSkin.Png(256, 256, 200, 200, 200));
        s.Add("HITCIRCLEOVERLAY@2x.png", FakeSkin.Png(256, 256, 255, 255, 255));
        for (int i = 0; i <= 9; i++) s.Add($"Default-{i}@2x.png", FakeSkin.Png(60, 80, 255, 255, 255, circle: false));
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath); // 중복 항목이 있으면 여기서 예외
        Assert.Equal(o.Keys.Count, o.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        AssertInstafaded(o);
        Assert.True(r.Analysis.HasOwnNumbers);
    }

    [Fact] // 8. .osk 안에 폴더가 한 겹 더 있음
    public void NestedFolderInOsk()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: Nested\n");
        var r = InstafadeEngine.Run(s.WriteOsk("Nested", innerFolder: "Nested Skin"), new InstafadeOptions());
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.DoesNotContain(o.Keys, k => k.Contains('/') && !k.StartsWith("fonts"));
    }

    [Fact] // 9. 이미 적용된 스킨
    public void AlreadyInstafadedIsDetected()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: Twice\n");
        var first = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        Assert.True(InstafadeEngine.Analyze(first.OutputPath).AlreadyInstafaded);
        var ex = Assert.Throws<InstafadeException>(() => InstafadeEngine.Run(first.OutputPath, new InstafadeOptions()));
        Assert.Contains("이미", ex.Message);
    }

    [Theory] // 10. HitCircleOverlayAboveNumber 에 따라 겹치는 순서
    [InlineData(1, 255, 0)]
    [InlineData(0, 0, 255)]
    public void OverlayOrder(int above, byte expectR, byte expectG)
    {
        using var s = new FakeSkin().Ini($"[General]\nName: Order\nHitCircleOverlayAboveNumber: {above}\n");
        s.Add("hitcircle@2x.png", FakeSkin.Png(256, 256, 0, 0, 255));
        s.Add("hitcircleoverlay@2x.png", FakeSkin.Png(256, 256, 255, 0, 0, circle: false)); // 빨강 꽉 찬 사각형
        for (int i = 0; i <= 9; i++) s.Add($"default-{i}@2x.png", FakeSkin.Png(60, 80, 0, 255, 0, circle: false)); // 초록
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions { CircleColor = new Rgb(255, 255, 255) });
        var img = Out.ReadOsk(r.OutputPath).Img("default-1@2x.png");
        int c = ((img.Height / 2) * img.Width + img.Width / 2) * 4;
        Assert.Equal(expectR, img.Pixels[c + 2]);
        Assert.Equal(expectG, img.Pixels[c + 1]);
    }

    [Fact] // 11. 콤보 카운터 복구 (breakcore 와 같은 형태)
    public void ComboCounterRepair()
    {
        using var s = new FakeSkin().Standard()
            .Ini("[General]\nName: Combo\n[Fonts]\nComboPrefix: d\nHitCirclePrefix: default\n// x\n    ScorePrefix: score\n    ScoreOverlap: 2\n    ComboPrefix: score\n");
        for (int i = 0; i <= 9; i++) s.Add($"score-{i}.png", FakeSkin.Png(20, 30, 255, 255, 255, circle: false));
        var a = InstafadeEngine.Analyze(s.WriteOsk("c1"));
        Assert.True(a.ComboCounterBroken);
        var r = InstafadeEngine.Run(s.WriteOsk("c2"), new InstafadeOptions());
        Assert.True(r.ComboCounterFixed);
        var ini = Out.ReadOsk(r.OutputPath).Ini();
        Assert.Equal(new[] { "score" }, ini.GetAll("ComboPrefix"));
        Assert.Equal("2", ini.Get("ComboOverlap"));
    }

    [Fact] // 11-2. 정상 스킨은 콤보 설정을 건드리지 않음
    public void HealthyComboUntouched()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: Ok\n[Fonts]\nComboPrefix: score\n");
        for (int i = 0; i <= 9; i++) s.Add($"score-{i}.png", FakeSkin.Png(20, 30, 255, 255, 255, circle: false));
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions());
        Assert.False(r.ComboCounterFixed);
    }

    [Fact] // 12. 원본은 한 바이트도 안 바뀜 (osk + 폴더)
    public void OriginalUntouched()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: Orig\n");
        string osk = s.WriteOsk();
        string folder = s.WriteFolder();
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        string HashDir(string d) => string.Join("|", Directory.GetFiles(d, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(f => f + Hash(f)));
        string h1 = Hash(osk), h2 = HashDir(folder);
        InstafadeEngine.Run(osk, new InstafadeOptions());
        InstafadeEngine.Run(folder, new InstafadeOptions());
        Assert.Equal(h1, Hash(osk));
        Assert.Equal(h2, HashDir(folder));
    }

    [Fact] // 13. 한글·특수문자 경로, 결과 이름 겹치면 (2)
    public void KoreanPathAndUniqueOutput()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: 테스트\n");
        string osk = s.WriteOsk("내 스킨 #1 [최고]");
        var r1 = InstafadeEngine.Run(osk, new InstafadeOptions());
        var r2 = InstafadeEngine.Run(osk, new InstafadeOptions());
        Assert.EndsWith("내 스킨 #1 [최고] (instafade).osk", r1.OutputPath);
        Assert.EndsWith("내 스킨 #1 [최고] (instafade) (2).osk", r2.OutputPath);
    }

    [Fact] // 숫자 없음 옵션 + 히트 라이팅 끄기
    public void NoNumbersAndLighting()
    {
        using var s = new FakeSkin().Standard().Ini("[General]\nName: NoNum\n");
        var r = InstafadeEngine.Run(s.WriteOsk(), new InstafadeOptions { ShowNumbers = false, DisableHitLighting = true });
        var o = Out.ReadOsk(r.OutputPath);
        AssertInstafaded(o);
        Assert.Equal(o.Get("default-1@2x.png"), o.Get("default-7@2x.png"));
        Assert.True(o.Img("lighting.png").IsFullyTransparent());
    }

    [Fact] // 크기 조절 품질: 단색은 단색 그대로, 투명 가장자리 번짐 없음
    public void ResizeKeepsColours()
    {
        Sta.Run(() =>
        {
            var img = new RgbaImage(10, 10);
            for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = 10; img.Pixels[i + 1] = 200; img.Pixels[i + 2] = 50; img.Pixels[i + 3] = (byte)(i % 8 == 0 ? 255 : 0); }
            foreach (var (w, h) in new[] { (25, 25), (3, 7), (10, 10) })
            {
                var r = img.Resize(w, h);
                for (int i = 0; i < r.Pixels.Length; i += 4)
                {
                    if (r.Pixels[i + 3] < 8) continue;
                    Assert.InRange(r.Pixels[i], 0, 14);
                    Assert.InRange(r.Pixels[i + 1], 194, 206);
                    Assert.InRange(r.Pixels[i + 2], 45, 55);
                }
            }
            return 0;
        });
    }

    [Fact] // 실제 스킨 (breakcore) 이 있으면 확인
    public void RealBreakcoreSkin()
    {
        string? path = FindUp("breakcore.osk");
        if (path == null) return;
        string tmp = Path.Combine(Path.GetTempPath(), "instafade_real_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var a = InstafadeEngine.Analyze(path);
            Assert.False(a.AlreadyInstafaded);
            Assert.True(a.ComboCounterBroken);
            Assert.Equal(new Rgb(123, 150, 245), a.CircleColor);
            var r = InstafadeEngine.Run(path, new InstafadeOptions { OutputDirectory = tmp });
            var o = Out.ReadOsk(r.OutputPath);
            AssertInstafaded(o);
            Assert.Equal(310, o.Img("default-1@2x.png").Width);
            Assert.Equal(155, r.HitCircleOverlap);
            Assert.True(o.Img("sliderstartcircleoverlay.png").IsFullyTransparent());
            using var orig = ZipFile.OpenRead(path);
            Assert.True(o.Count >= orig.Entries.Count(e => e.Name.Length > 0));
        }
        finally { Directory.Delete(tmp, true); }
    }

    private static string? FindUp(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }
}
