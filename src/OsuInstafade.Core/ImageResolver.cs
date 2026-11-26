namespace OsuInstafade.Core;

internal sealed record ResolvedImage(RgbaImage Hd, string Path, bool WasHd);

/// <summary>
/// osu! 와 같은 우선순위로 스킨 그림을 찾아 고화질(@2x) 기준으로 맞춰 준다.
/// </summary>
internal sealed class ImageResolver
{
    private readonly SkinSource _src;
    private readonly List<string> _warnings;

    public ImageResolver(SkinSource src, List<string> warnings)
    {
        _src = src;
        _warnings = warnings;
    }

    public ResolvedImage? Resolve(string name, bool animatable)
    {
        var candidates = new List<string>();
        if (animatable) { candidates.Add($"{name}-0@2x.png"); candidates.Add($"{name}-0.png"); }
        candidates.Add($"{name}@2x.png");
        candidates.Add($"{name}.png");

        // 애니메이션: -0 이 있으면 osu! 는 애니메이션을 사용 → 고화질/일반 순서는 같은 그룹 안에서
        foreach (var c in candidates)
        {
            if (!_src.Exists(c)) continue;
            RgbaImage img;
            try
            {
                img = RgbaImage.Decode(_src.Read(c));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _warnings.Add($"그림을 읽을 수 없어 건너뜀: {c}");
                continue;
            }
            bool hd = c.EndsWith("@2x.png", StringComparison.OrdinalIgnoreCase);
            var hdImg = hd ? img : img.Resize(img.Width * 2, img.Height * 2);
            return new ResolvedImage(hdImg, _src.ActualName(c) ?? c, hd);
        }
        return null;
    }
}
