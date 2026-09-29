using System.Drawing;
using System.Windows.Forms;

namespace ServerHealth.Gui;

/// <summary>
/// Панель живых графиков: CPU крупно + мини-графики памяти и сети.
/// Все подписи читабельны: заголовок и текущее значение — в шапке графика,
/// шкала — в отдельной колонке слева, время — в нижней полосе. Ничего не
/// рисуется поверх линий, наложения исключены компоновкой.
/// </summary>
public sealed class ChartPanel : Panel
{
    private List<LivePoint> _data = new List<LivePoint>();
    private readonly object _lock = new();
    private double _ramGb = 16;

    private static readonly Color BgColor = Color.FromArgb(26, 26, 40);
    private static readonly Color BorderColor = Color.FromArgb(50, 50, 76);
    private static readonly Color GridColor = Color.FromArgb(40, 40, 62);
    private static readonly Color AxisColor = Color.FromArgb(132, 132, 160);
    private static readonly Color TitleColor = Color.FromArgb(210, 210, 230);
    private static readonly Color MsgColor = Color.FromArgb(160, 160, 188);

    private static readonly Brush BgBrush = new SolidBrush(BgColor);
    private static readonly Pen BorderPen = new(BorderColor);
    private static readonly Pen GridPen = new(GridColor);

    private static readonly Font FTitle = new("Segoe UI", 11.5F, FontStyle.Bold);
    private static readonly Font FValue = new("Segoe UI", 11.5F, FontStyle.Bold);
    private static readonly Font FAxis = new("Segoe UI", 10.5F);
    private static readonly Font FTime = new("Segoe UI", 10F);
    private static readonly Font FMsg = new("Segoe UI", 12F);

    public ChartPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(20, 20, 31);
    }

    public void SetRam(double gb) { _ramGb = gb < 1 ? 16 : gb; }

    public void UpdateData(List<LivePoint> data)
    {
        lock (_lock) _data = data ?? new List<LivePoint>();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        List<LivePoint> d;
        lock (_lock) d = _data;
        int W = ClientSize.Width, H = ClientSize.Height;

        if (d.Count < 2)
        {
            TextRenderer.DrawText(g, "Нет данных — нажмите «Запустить мониторинг»", FMsg,
                ClientRectangle, MsgColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int mainH = Math.Max(130, (int)(H * 0.56));
        var cpuRect = new Rectangle(6, 6, W - 12, mainH - 6);
        int miniY = mainH + 10;
        int miniH = Math.Max(76, H - miniY - 8);
        int miniW = Math.Max(140, (W - 18) / 2);
        var ramRect = new Rectangle(6, miniY, miniW, miniH);
        var netRect = new Rectangle(ramRect.Right + 6, miniY, Math.Max(140, W - ramRect.Right - 12), miniH);

        var cpu = new double[d.Count];
        var ram = new double[d.Count];
        var net = new double[d.Count];
        for (int i = 0; i < d.Count; i++)
        {
            cpu[i] = d[i].Cpu;
            ram[i] = d[i].AvailMb;
            net[i] = d[i].NetMbps;
        }

        DrawChart(g, cpuRect, cpu, d, Color.FromArgb(97, 175, 239), "CPU, загрузка", 100, "%", true);
        DrawChart(g, ramRect, ram, d, Color.FromArgb(76, 195, 138), "RAM свободно", (float)(_ramGb * 1024), "МБ", false);
        DrawChart(g, netRect, net, d, Color.FromArgb(198, 120, 221), "Сеть (приём + передача)", 0, "Мбит/с", false);
    }

    private static string AxisFmt(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1000000) return (v / 1000000).ToString("0") + "M";
        if (a >= 10000) return (v / 1000).ToString("0.#") + "k";
        if (a >= 1000) return (v / 1000).ToString("0.##") + "k";
        if (a >= 100) return v.ToString("0");
        if (a >= 10) return v.ToString("0.#");
        return v.ToString("0.##");
    }

    private void DrawChart(Graphics g, Rectangle r, double[] vals, List<LivePoint> d,
        Color color, string title, float fixedMax, string unit, bool showTime)
    {
        g.FillRectangle(BgBrush, r);
        g.DrawRectangle(BorderPen, r);

        // ---- шапка: заголовок слева, текущее значение справа (не поверх линии)
        var head = new Rectangle(r.X + 10, r.Y + 5, r.Width - 20, 20);
        TextRenderer.DrawText(g, title, FTitle, head, TitleColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        string cur = AxisFmt(vals[vals.Length - 1]) + (unit.Length > 0 ? " " + unit : "");
        TextRenderer.DrawText(g, cur, FValue, head, color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // ---- область построения: слева колонка шкалы, снизу полоса времени
        var plot = new Rectangle(
            r.X + 56,
            r.Y + 30,
            r.Width - 56 - 12,
            r.Height - 30 - (showTime ? 24 : 10));
        if (plot.Width < 10 || plot.Height < 10) return;

        double max = fixedMax;
        if (max <= 0)
        {
            double mx = 1;
            foreach (var v in vals) if (IsFin(v) && v > mx) mx = v;
            max = mx * 1.2;
        }

        // сетка + подписи шкалы
        for (int i = 0; i <= 4; i++)
        {
            int y = plot.Y + (int)(plot.Height * i / 4.0);
            g.DrawLine(GridPen, plot.Left, y, plot.Right, y);
            string lbl = AxisFmt(max * (1 - i / 4.0));
            TextRenderer.DrawText(g, lbl, FAxis,
                new Rectangle(r.X + 8, y - 9, plot.X - r.X - 14, 18),
                AxisColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // линия + заливка
        int n = vals.Length;
        Func<int, float> X = i => plot.Left + (float)plot.Width * i / Math.Max(1, n - 1);
        Func<double, float> Y = v => plot.Bottom - (float)plot.Height * (float)(Math.Clamp(IsFin(v) ? v : 0, 0, max) / max);

        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            path.AddLine(X(0), plot.Bottom, X(0), Y(vals[0]));
            for (int i = 1; i < n; i++) path.AddLine(X(i - 1), Y(vals[i - 1]), X(i), Y(vals[i]));
            path.AddLine(X(n - 1), Y(vals[n - 1]), X(n - 1), plot.Bottom);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(28, color));
            g.FillPath(fill, path);
        }
        using (var pen = new Pen(color, 2F))
            for (int i = 1; i < n; i++)
                g.DrawLine(pen, X(i - 1), Y(vals[i - 1]), X(i), Y(vals[i]));
        g.FillEllipse(Brushes.White, X(n - 1) - 3, Y(vals[n - 1]) - 3, 6, 6);

        // ---- время (только у большого графика)
        if (showTime && d.Count > 0)
        {
            var timeRect = new Rectangle(plot.Left, plot.Bottom + 4, plot.Width, 16);
            TextRenderer.DrawText(g, d[0].Ts.ToString("HH:mm:ss"), FTime, timeRect, AxisColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, d[d.Count - 1].Ts.ToString("HH:mm:ss"), FTime, timeRect, AxisColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    private static bool IsFin(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
}
