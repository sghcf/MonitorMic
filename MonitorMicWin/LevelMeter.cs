using System.Drawing.Drawing2D;

namespace MonitorMicWin;

/// <summary>
/// 自绘电平表：只在数值变化时重绘（双缓冲）。
/// 不用 ProgressBar——它的平滑填充动画在高刷屏上会触发全速重绘，越刷越卡。
/// </summary>
sealed class LevelMeter : Control
{
    float level;

    public float Level
    {
        get => level;
        set
        {
            var v = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(v - level) > 0.004f)
            {
                level = v;
                Invalidate();
            }
        }
    }

    public LevelMeter()
    {
        DoubleBuffered = true;
        Height = 16;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0, 0, Width - 1, Height - 1);
        using var bgPath = Rounded(rect, 7);
        using (var bgBrush = new SolidBrush(Color.FromArgb(40, 128, 128, 128)))
            g.FillPath(bgBrush, bgPath);
        if (level > 0.001f)
        {
            var fillRect = new RectangleF(0, 0, Math.Max(8, (Width - 1) * level), Height - 1);
            using var fgPath = Rounded(fillRect, 7);
            using var fgBrush = new LinearGradientBrush(
                new RectangleF(0, 0, Width, Height),
                Color.FromArgb(0x34, 0xC7, 0x59), Color.FromArgb(0xFF, 0x3B, 0x30),
                LinearGradientMode.Horizontal);
            g.FillPath(fgBrush, fgPath);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
