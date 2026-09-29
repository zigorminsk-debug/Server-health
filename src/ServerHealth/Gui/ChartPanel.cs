using System.Drawing;
using System.Windows.Forms;

namespace ServerHealth.Gui;

/// <summary>
/// Панель живого графика: CPU крупно + мини-графики памяти и сети.
/// Данные поступают из MonitoringEngine.LiveSnapshot().
/// </summary>
public sealed class ChartPanel : Panel
{
    private List<LivePoint> _data = new List<LivePoint>();
    private readonly object _lock = new();
    private double _ramGb = 16;

    private static readonly Color CpuColor = Color.FromArgb(97, 175, 239);
    private static readonly Color RamColor = Color.FromArgb(76, 195, 138);
    private static readonly Color NetColor = Color.FromArgb(198, 120, 221);
    private static readonly Color GridColor = Color.FromArgb(44, 44, 64);
    private static readonly Color TextColor = Color.FromArgb(140, 140, 165);
    private static readonly Color BgColor = Color.FromArgb(24, 24, 36);

    public ChartPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = BgColor;
    }

    public void SetRam(double gb) { _ramGb = gb < 1 ? 16 : gb; }

    public void UpdateData(List<LivePoint> data)
    {
        lock (_lock) { _data = data ?? new List<LivePoint>(); }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        List<LivePoint> d;
        lock (_lock) d = _data;
        if (d.Count < 2)
        {
            TextRenderer.DrawText(g, "Нет данных — запустите мониторинг", Font, ClientRectangle, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int W = ClientSize.Width, H = ClientSize.Height;
        int mainH = Math.Max(80, (int)(H * 0.60));
        var cpuRect = new Rectangle(6, 6, W - 12, mainH - 12);
        int miniH = Math.Max(40, (H - mainH - 14) / 2 - 4);
        var ramRect = new Rectangle(6, mainH + 2, (W - 16) / 2, miniH);
        var netRect = new Rectangle(ramRect.Right + 4, mainH + 2, W - ramRect.Right - 10, miniH);

        DrawSeries(g, cpuRect, Select(d, p => p.Cpu), 100, CpuColor, "CPU, %", true);
        DrawSeries(g, ramRect, Select(d, p => p.AvailMb), (float)(_ramGb * 1024), RamColor, "RAM свободно, МБ", false);
        DrawSeries(g, netRect, Select(d, p => p.NetMbps), 0, NetColor, "Сеть, Мбит/с", false);
    }

    private static double[] Select(List<LivePoint> d, Func<LivePoint, double> f)
    {
        var r = new double[d.Count];
        for (int i = 0; i < d.Count; i++) r[i] = f(d[i]);
        return r;
    }

    private void DrawSeries(Graphics g, Rectangle rect, double[] vals, double fixedMax, Color color, string title, bool showScale)
    {
        using var bg = new SolidBrush(Color.FromArgb(28, 28, 42));
        g.FillRectangle(bg, rect);
        using var penBorder = new Pen(GridColor);
        g.DrawRectangle(penBorder, rect);

        var titleRect = new Rectangle(rect.X + 6, rect.Y + 3, rect.Width - 12, 14);
        TextRenderer.DrawText(g, title, Font, titleRect, TextColor, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        var plot = Rectangle.Inflate(rect, -6, -6);
        plot.Y += 10; plot.Height -= 10;
        if (plot.Height < 8 || plot.Width < 8) return;

        double max = fixedMax;
        if (max <= 0)
        {
            double mx = 1;
            foreach (var v in vals) if (IsFin(v) && v > mx) mx = v;
            max = mx * 1.2;
        }

        // сетка
        using var penGrid = new Pen(Color.FromArgb(38, 38, 56));
        for (int i = 0; i <= 3; i++)
        {
            int y = plot.Y + (int)(plot.Height * i / 3.0);
            g.DrawLine(penGrid, plot.Left, y, plot.Right, y);
            if (showScale)
                TextRenderer.DrawText(g, (max * (1 - i / 3.0)).ToString("0"), Font,
                    new Rectangle(rect.X, y - 7, plot.X - rect.X - 2, 14), TextColor, TextFormatFlags.Right);
        }

        // полилиния + заливка
        int n = vals.Length;
        Func<int, float> X = i => plot.Left + (float)plot.Width * i / Math.Max(1, n - 1);
        Func<double, float> Y = v => plot.Bottom - (float)plot.Height * (float)(Math.Clamp(IsFin(v) ? v : 0, 0, max) / max);

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddLine(X(0), plot.Bottom, X(0), Y(vals[0]));
        for (int i = 1; i < n; i++) path.AddLine(X(i - 1), Y(vals[i - 1]), X(i), Y(vals[i]));
        path.AddLine(X(n - 1), Y(vals[n - 1]), X(n - 1), plot.Bottom);
        path.CloseFigure();
        using var fillBrush = new SolidBrush(Color.FromArgb(28, color));
        g.FillPath(fillBrush, path);

        using var penLine = new Pen(color, 1.7f);
        for (int i = 1; i < n; i++)
            g.DrawLine(penLine, X(i - 1), Y(vals[i - 1]), X(i), Y(vals[i]));

        // последняя точка и значение
        float lx = X(n - 1), ly = Y(vals[n - 1]);
        g.FillEllipse(Brushes.White, lx - 2.5f, ly - 2.5f, 5, 5);
        g.FillEllipse(new SolidBrush(color), lx - 2f, ly - 2f, 4, 4);
        string last = vals[n - 1].ToString("0.#");
        TextRenderer.DrawText(g, last, new Font(Font, FontStyle.Bold),
            new Rectangle((int)lx - 46, (int)ly - 18, 46, 14), color, TextFormatFlags.Right);

        // время начала/конца
        TextRenderer.DrawText(g, "сейчас", Font, new Rectangle(rect.Right - 60, rect.Bottom - 16, 56, 14), TextColor, TextFormatFlags.Right);
    }

    private static bool IsFin(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
}
