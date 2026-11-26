using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OsuInstafade.Core;

/// <summary>
/// 단순한 32비트 이미지 (BGRA, 투명도는 곱하지 않은 값).
/// 읽기/쓰기는 Windows 기본 이미지 엔진(WIC)을, 크기 조절은 직접 구현한 Lanczos3 를 사용.
/// </summary>
public sealed class RgbaImage
{
    public const int MaxDimension = 8192;

    public int Width { get; }
    public int Height { get; }
    /// <summary>BGRA 순서, 한 줄 = Width*4 바이트</summary>
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    private RgbaImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    // ---------------- 읽기 / 쓰기 ----------------

    public static RgbaImage Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(ms,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("이미지 프레임이 없습니다.");
        BitmapSource frame = decoder.Frames[0];
        int w = frame.PixelWidth, h = frame.PixelHeight;
        if (w <= 0 || h <= 0 || w > MaxDimension || h > MaxDimension)
            throw new InvalidDataException($"이미지 크기가 비정상입니다 ({w}x{h}).");
        return FromBitmapSource(frame);
    }

    public static RgbaImage FromBitmapSource(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var px = new byte[w * h * 4];
        converted.CopyPixels(px, w * 4, 0);
        return new RgbaImage(w, h, px);
    }

    public byte[] EncodePng()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Width * 4);
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public BitmapSource ToBitmapSource()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------------- 검사 ----------------

    public bool IsFullyTransparent(byte threshold = 2)
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] > threshold) return false;
        return true;
    }

    // ---------------- 색 입히기 (osu! 와 같은 곱하기 방식) ----------------

    public RgbaImage Tinted(Rgb color)
    {
        var r = Clone();
        var p = r.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)((p[i] * color.B + 127) / 255);
            p[i + 1] = (byte)((p[i + 1] * color.G + 127) / 255);
            p[i + 2] = (byte)((p[i + 2] * color.R + 127) / 255);
        }
        return r;
    }

    // ---------------- 겹쳐 그리기 ----------------

    /// <summary>src 를 이 이미지의 (dx,dy) 위치에 일반 알파 합성(over)으로 그림</summary>
    public void DrawOver(RgbaImage src, int dx, int dy)
    {
        for (int sy = 0; sy < src.Height; sy++)
        {
            int ty = sy + dy;
            if (ty < 0 || ty >= Height) continue;
            for (int sx = 0; sx < src.Width; sx++)
            {
                int tx = sx + dx;
                if (tx < 0 || tx >= Width) continue;
                int si = (sy * src.Width + sx) * 4;
                int sa = src.Pixels[si + 3];
                if (sa == 0) continue;
                int ti = (ty * Width + tx) * 4;
                if (sa == 255)
                {
                    Pixels[ti] = src.Pixels[si];
                    Pixels[ti + 1] = src.Pixels[si + 1];
                    Pixels[ti + 2] = src.Pixels[si + 2];
                    Pixels[ti + 3] = 255;
                    continue;
                }
                float a = sa / 255f;
                float da = Pixels[ti + 3] / 255f;
                float outA = a + da * (1 - a);
                for (int c = 0; c < 3; c++)
                {
                    float v = (src.Pixels[si + c] * a + Pixels[ti + c] * da * (1 - a)) / outA;
                    Pixels[ti + c] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
                }
                Pixels[ti + 3] = (byte)Math.Clamp((int)MathF.Round(outA * 255), 0, 255);
            }
        }
    }

    /// <summary>가운데 맞춰 그리기</summary>
    public void DrawCentered(RgbaImage src)
        => DrawOver(src, (int)Math.Round((Width - src.Width) / 2.0, MidpointRounding.AwayFromZero),
                         (int)Math.Round((Height - src.Height) / 2.0, MidpointRounding.AwayFromZero));

    // ---------------- 크기 조절 (Lanczos3, 투명도 곱한 상태에서 계산 → 가장자리 번짐 없음) ----------------

    public RgbaImage Resize(int newWidth, int newHeight)
    {
        newWidth = Math.Max(1, newWidth);
        newHeight = Math.Max(1, newHeight);
        if (newWidth == Width && newHeight == Height) return Clone();

        // 1) 투명도를 곱한 float 배열로
        var src = new float[Width * Height * 4];
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            float a = Pixels[i + 3] / 255f;
            src[i] = Pixels[i] * a;
            src[i + 1] = Pixels[i + 1] * a;
            src[i + 2] = Pixels[i + 2] * a;
            src[i + 3] = Pixels[i + 3];
        }

        // 2) 가로 → 3) 세로
        var tmp = ResampleAxis(src, Width, Height, newWidth, horizontal: true);
        var dst = ResampleAxis(tmp, newWidth, Height, newHeight, horizontal: false);

        // 4) 다시 바이트로
        var result = new RgbaImage(newWidth, newHeight);
        var p = result.Pixels;
        for (int i = 0; i < dst.Length; i += 4)
        {
            float a = Math.Clamp(dst[i + 3], 0f, 255f);
            byte ab = (byte)MathF.Round(a);
            p[i + 3] = ab;
            if (ab == 0) { p[i] = p[i + 1] = p[i + 2] = 0; continue; }
            float inv = 1f / (a / 255f);
            p[i] = (byte)Math.Clamp(MathF.Round(dst[i] * inv), 0f, 255f);
            p[i + 1] = (byte)Math.Clamp(MathF.Round(dst[i + 1] * inv), 0f, 255f);
            p[i + 2] = (byte)Math.Clamp(MathF.Round(dst[i + 2] * inv), 0f, 255f);
        }
        return result;
    }

    private static float[] ResampleAxis(float[] src, int w, int h, int newLen, bool horizontal)
    {
        int oldLen = horizontal ? w : h;
        int outW = horizontal ? newLen : w;
        int outH = horizontal ? h : newLen;
        var dst = new float[outW * outH * 4];

        double scale = (double)newLen / oldLen;
        double filterScale = Math.Max(1.0, 1.0 / scale); // 축소할 때는 필터를 넓혀서 계단 현상 방지
        double support = 3.0 * filterScale;

        // 각 출력 위치마다 가중치 미리 계산
        var starts = new int[newLen];
        var weights = new float[newLen][];
        for (int o = 0; o < newLen; o++)
        {
            double center = (o + 0.5) / scale - 0.5;
            int start = (int)Math.Floor(center - support) + 1;
            int end = (int)Math.Ceiling(center + support);
            var ws = new List<float>();
            double sum = 0;
            for (int s = start; s < end; s++)
            {
                double wv = Lanczos3((s - center) / filterScale);
                ws.Add((float)wv);
                sum += wv;
            }
            if (Math.Abs(sum) < 1e-9) sum = 1;
            for (int k = 0; k < ws.Count; k++) ws[k] = (float)(ws[k] / sum);
            starts[o] = start;
            weights[o] = ws.ToArray();
        }

        int lines = horizontal ? h : w;
        Parallel.For(0, lines, line =>
        {
            for (int o = 0; o < newLen; o++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                var ws = weights[o];
                int start = starts[o];
                for (int k = 0; k < ws.Length; k++)
                {
                    int s = Math.Clamp(start + k, 0, oldLen - 1);
                    int si = horizontal ? (line * w + s) * 4 : (s * w + line) * 4;
                    float wv = ws[k];
                    b += src[si] * wv;
                    g += src[si + 1] * wv;
                    r += src[si + 2] * wv;
                    a += src[si + 3] * wv;
                }
                int di = horizontal ? (line * outW + o) * 4 : (o * outW + line) * 4;
                dst[di] = b; dst[di + 1] = g; dst[di + 2] = r; dst[di + 3] = a;
            }
        });

        // 링잉(가장자리 번쩍임) 방지: 색 ≤ 알파 로 제한
        for (int i = 0; i < dst.Length; i += 4)
        {
            float a = dst[i + 3];
            if (a <= 0f) { dst[i] = dst[i + 1] = dst[i + 2] = dst[i + 3] = 0f; continue; }
            if (a > 255f)
            {
                // 알파가 넘치면 색도 같은 비율로 줄여 색이 변하지 않게
                float f = 255f / a;
                dst[i] *= f; dst[i + 1] *= f; dst[i + 2] *= f;
                a = 255f;
            }
            dst[i + 3] = a;
            dst[i] = Math.Clamp(dst[i], 0f, a);
            dst[i + 1] = Math.Clamp(dst[i + 1], 0f, a);
            dst[i + 2] = Math.Clamp(dst[i + 2], 0f, a);
        }
        return dst;
    }

    private static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-8) return 1.0;
        if (x >= 3.0) return 0.0;
        double px = Math.PI * x;
        return 3.0 * Math.Sin(px) * Math.Sin(px / 3.0) / (px * px);
    }
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"{R},{G},{B}";
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith('#') && (text.Length == 7))
        {
            try
            {
                color = new Rgb(Convert.ToByte(text.Substring(1, 2), 16), Convert.ToByte(text.Substring(3, 2), 16), Convert.ToByte(text.Substring(5, 2), 16));
                return true;
            }
            catch { return false; }
        }
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)");
        if (!m.Success) return false;
        static byte C(string s) => (byte)Math.Clamp(int.TryParse(s, out var v) ? v : 0, 0, 255);
        color = new Rgb(C(m.Groups[1].Value), C(m.Groups[2].Value), C(m.Groups[3].Value));
        return true;
    }
}
