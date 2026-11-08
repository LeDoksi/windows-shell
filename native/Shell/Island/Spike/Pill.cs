using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WindowsShell.Island.Core;

namespace WindowsShell.Island.Spike;

/// Красная пилюля «● запись 0:12» по центру верхнего края монитора: слоистое окно поверх
/// всех, сквозное для мыши, без фокуса и не попадает в запись экрана (WDA_EXCLUDEFROMCAPTURE).
internal sealed class Pill : IDisposable
{
    readonly LayeredWindow _window = new(excludeFromCapture: true);
    long _raisedAt;

    public bool Visible => _window.Visible;

    public void Show(RectI monitor, double scale, string text)
    {
        using var bmp = Render(text, scale);
        var x = (int)(((long)monitor.Left + monitor.Right) / 2) - bmp.Width / 2;
        var y = monitor.Top + (int)Math.Round(6 * scale);
        _window.Show(bmp, x, y);
        // Игра тоже бывает «поверх всех»: раз в секунду поднимаемся над ней.
        if (Environment.TickCount64 - _raisedAt > 1000)
        {
            _raisedAt = Environment.TickCount64;
            _window.Raise();
        }
    }

    public void Hide() => _window.Hide();

    public void Dispose() => _window.Dispose();

    static Bitmap Render(string text, double scale)
    {
        var size = (float)(13 * scale);
        using var font = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var probe = new Bitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        var tw = pg.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width;
        var padX = (float)(12 * scale);
        var w = (int)Math.Ceiling(tw + padX * 2);
        var h = (int)Math.Ceiling(28 * scale);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var path = new GraphicsPath())
        {
            var d = h - 1f;
            path.AddArc(0, 0, d, d, 90, 180);
            path.AddArc(w - 1 - d, 0, d, d, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(240, 0xD9, 0x2D, 0x2D));
            g.FillPath(fill, path);
        }
        using var white = new SolidBrush(Color.White);
        var th = font.GetHeight(g);
        g.DrawString(text, font, white, padX, (h - th) / 2, StringFormat.GenericTypographic);
        return bmp;
    }
}
