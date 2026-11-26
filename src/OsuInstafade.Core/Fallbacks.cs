using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OsuInstafade.Core;

/// <summary>
/// 스킨에 그림이 없을 때 osu! 기본 스킨 대신 쓰는 그림 (직접 그림, 고화질 기준).
/// osu! 공식 그림은 저작권 때문에 넣지 않는다.
/// </summary>
internal static class Fallbacks
{
    private static RgbaImage Render(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return RgbaImage.FromBitmapSource(rtb);
    }

    /// <summary>기본 서클 (흰색, 가운데가 살짝 어두운 원) — 고화질 256px</summary>
    public static RgbaImage HitCircle() => Render(256, 256, dc =>
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 200, 200, 200), 0.0),
                new GradientStop(Color.FromArgb(255, 235, 235, 235), 0.85),
                new GradientStop(Color.FromArgb(255, 255, 255, 255), 1.0),
            }
        };
        brush.Freeze();
        dc.DrawEllipse(brush, null, new Point(128, 128), 118, 118);
    });

    /// <summary>기본 서클 테두리 (흰 고리) — 고화질 256px</summary>
    public static RgbaImage HitCircleOverlay() => Render(256, 256, dc =>
    {
        var pen = new Pen(Brushes.White, 12);
        pen.Freeze();
        dc.DrawEllipse(null, pen, new Point(128, 128), 116, 116);
    });

    /// <summary>숫자 그림 (흰 글씨 + 옅은 그림자) — 고화질</summary>
    public static RgbaImage Digit(int digit)
    {
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var ft = new FormattedText(digit.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, face, 104, Brushes.White, 1.0);
        var geo = ft.BuildGeometry(new Point(0, 0));
        var b = geo.Bounds;
        int w = 80, h = 104;
        return Render(w, h, dc =>
        {
            dc.PushTransform(new TranslateTransform(w / 2.0 - (b.X + b.Width / 2), h / 2.0 - (b.Y + b.Height / 2)));
            var shadow = new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 6) { LineJoin = PenLineJoin.Round };
            shadow.Freeze();
            dc.DrawGeometry(null, shadow, geo);
            dc.DrawGeometry(Brushes.White, null, geo);
            dc.Pop();
        });
    }
}
