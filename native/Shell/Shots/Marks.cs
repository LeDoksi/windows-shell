using System.Collections.Immutable;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using D = System.Drawing;

namespace WindowsShell.Shots;

/// Пометки редактора (design-v2 п. 4) в пикселях исходного снимка. Рисуются поверх снимка
/// при каждом изменении (GDI+), поэтому что видно в редакторе, то и сохранится.
public abstract record Mark;
public sealed record ArrowMark(D.PointF A, D.PointF B, D.Color Color, float Width) : Mark;
public sealed record RectMark(D.RectangleF R, D.Color Color, float Width) : Mark;
public sealed record MarkerMark(ImmutableArray<D.PointF> Points, D.Color Color, float Width) : Mark;
public sealed record TextMark(D.PointF At, string Text, D.Color Color, float FontPx) : Mark;
public sealed record BlurMark(D.Rectangle R, int Block) : Mark;
public sealed record NumberMark(D.PointF At, int N, D.Color Color, float Radius) : Mark;

/// Состояние редактора: пометки по порядку и обрезка. Ctrl+Z / Ctrl+Y ходят по этим состояниям.
public sealed record EditState(ImmutableList<Mark> Marks, D.Rectangle? Crop)
{
    public static readonly EditState Empty = new(ImmutableList<Mark>.Empty, null);
    public int NextNumber => Marks.OfType<NumberMark>().Count() + 1;
}

public static class MarkPainter
{
    public const string FontFace = "Segoe UI";

    /// Маркер полупрозрачный, как текстовыделитель.
    public static D.Color MarkerColor(D.Color c) => D.Color.FromArgb(110, c);

    /// Снимок со всеми пометками и обрезкой.
    public static D.Bitmap Render(D.Bitmap source, EditState state)
    {
        var bmp = new D.Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bmp)) g.DrawImageUnscaled(source, 0, 0);
        foreach (var m in state.Marks)
        {
            if (m is BlurMark b) { Pixelate(bmp, b.R, b.Block); continue; }
            using var g = D.Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Draw(g, m);
        }
        if (state.Crop is not { } crop) return bmp;
        crop.Intersect(new D.Rectangle(0, 0, bmp.Width, bmp.Height));
        if (crop.Width < 1 || crop.Height < 1) return bmp;
        var cut = bmp.Clone(crop, PixelFormat.Format32bppArgb);
        bmp.Dispose();
        return cut;
    }

    static void Draw(D.Graphics g, Mark m)
    {
        switch (m)
        {
            case ArrowMark a:
                Arrow(g, a);
                break;
            case RectMark r:
            {
                using var pen = new D.Pen(r.Color, r.Width) { LineJoin = LineJoin.Round };
                g.DrawRectangle(pen, r.R.X, r.R.Y, r.R.Width, r.R.Height);
                break;
            }
            case MarkerMark mk:
            {
                using var pen = new D.Pen(MarkerColor(mk.Color), mk.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (mk.Points.Length >= 2) g.DrawLines(pen, mk.Points.ToArray());
                else if (mk.Points.Length == 1)
                {
                    using var brush = new D.SolidBrush(MarkerColor(mk.Color));
                    g.FillEllipse(brush, mk.Points[0].X - mk.Width / 2, mk.Points[0].Y - mk.Width / 2, mk.Width, mk.Width);
                }
                break;
            }
            case TextMark t:
            {
                // Тонкая тёмная обводка: текст читается на любом фоне.
                using var path = new GraphicsPath();
                using var family = new D.FontFamily(FontFace);
                path.AddString(t.Text, family, (int)D.FontStyle.Bold, t.FontPx, t.At, D.StringFormat.GenericTypographic);
                using var outline = new D.Pen(D.Color.FromArgb(150, 0, 0, 0), Math.Max(2, t.FontPx / 8)) { LineJoin = LineJoin.Round };
                g.DrawPath(outline, path);
                using var fill = new D.SolidBrush(t.Color);
                g.FillPath(fill, path);
                break;
            }
            case NumberMark n:
            {
                using var brush = new D.SolidBrush(n.Color);
                g.FillEllipse(brush, n.At.X - n.Radius, n.At.Y - n.Radius, n.Radius * 2, n.Radius * 2);
                using var family = new D.FontFamily(FontFace);
                using var font = new D.Font(family, n.Radius * 1.1f, D.FontStyle.Bold, D.GraphicsUnit.Pixel);
                using var text = new D.SolidBrush(Light(n.Color) ? D.Color.Black : D.Color.White);
                using var format = new D.StringFormat { Alignment = D.StringAlignment.Center, LineAlignment = D.StringAlignment.Center };
                g.DrawString(n.N.ToString(), font, text, new D.RectangleF(n.At.X - n.Radius, n.At.Y - n.Radius + 1, n.Radius * 2, n.Radius * 2), format);
                break;
            }
        }
    }

    public static bool Light(D.Color c) => c.R * 0.3 + c.G * 0.59 + c.B * 0.11 > 170;

    /// Острие стрелки: длина и половина ширины в пикселях для толщины линии.
    public static (float Length, float Half) Head(float width) => (Math.Max(14, width * 4.2f), Math.Max(7, width * 2.1f));

    static void Arrow(D.Graphics g, ArrowMark a)
    {
        var dx = a.B.X - a.A.X;
        var dy = a.B.Y - a.A.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        var (hl, hw) = Head(a.Width);
        if (hl > len) { hw *= len / hl; hl = len; }
        var ux = dx / len;
        var uy = dy / len;
        var baseX = a.B.X - ux * hl;
        var baseY = a.B.Y - uy * hl;
        using var pen = new D.Pen(a.Color, a.Width) { StartCap = LineCap.Round, EndCap = LineCap.Flat };
        g.DrawLine(pen, a.A.X, a.A.Y, baseX + ux * hl * 0.3f, baseY + uy * hl * 0.3f);
        using var brush = new D.SolidBrush(a.Color);
        g.FillPolygon(brush, [a.B, new D.PointF(baseX - uy * hw, baseY + ux * hw), new D.PointF(baseX + uy * hw, baseY - ux * hw)]);
    }

    /// Размытие = крупные квадраты (пикселизация): текст под ним не прочитать.
    static unsafe void Pixelate(D.Bitmap bmp, D.Rectangle r, int block)
    {
        r.Intersect(new D.Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width < 1 || r.Height < 1) return;
        block = Math.Max(2, block);
        var data = bmp.LockBits(r, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (var by = 0; by < r.Height; by += block)
            for (var bx = 0; bx < r.Width; bx += block)
            {
                var bw = Math.Min(block, r.Width - bx);
                var bh = Math.Min(block, r.Height - by);
                long sb = 0, sg = 0, sr = 0;
                for (var y = 0; y < bh; y++)
                {
                    var row = (byte*)data.Scan0 + (long)(by + y) * data.Stride + bx * 4;
                    for (var x = 0; x < bw; x++) { sb += row[x * 4]; sg += row[x * 4 + 1]; sr += row[x * 4 + 2]; }
                }
                var n = bw * bh;
                var color = 0xFF000000u | (uint)(sr / n) << 16 | (uint)(sg / n) << 8 | (uint)(sb / n);
                for (var y = 0; y < bh; y++)
                {
                    var row = (uint*)((byte*)data.Scan0 + (long)(by + y) * data.Stride) + bx;
                    for (var x = 0; x < bw; x++) row[x] = color;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
