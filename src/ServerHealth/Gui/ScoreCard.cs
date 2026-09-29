using System.Drawing;
using System.Windows.Forms;

namespace ServerHealth.Gui;

/// <summary>
/// Карточка подсистемы: крупное текущее значение (обновляется на каждом замере,
/// с первых секунд работы), полоса нагрузки и бейдж «оценка цикла 0–100» после
/// формирования отчёта.
/// </summary>
public sealed class ScoreCard : Panel
{
    public string Title;

    private string _big = "—";
    private string _caption = "ожидание данных…";
    private int _load = -1;                 // 0..100, -1 = полоса не показывается
    private string? _scoreNote;             // «оценка цикла: 87/100 · норма»
    private Color _scoreColor = MutedColor;

    private static readonly Color CardColor = Color.FromArgb(30, 30, 44);
    private static readonly Color LineColor = Color.FromArgb(46, 46, 68);
    private static readonly Color TextColor = Color.FromArgb(238, 238, 246);
    private static readonly Color MutedColor = Color.FromArgb(152, 152, 178);
    private static readonly Color Ok = Color.FromArgb(76, 195, 138);
    private static readonly Color Warn = Color.FromArgb(229, 192, 123);
    private static readonly Color Bad = Color.FromArgb(224, 108, 117);
    private static readonly Color BarBg = Color.FromArgb(50, 50, 76);

    private static readonly Font FTitle = new("Segoe UI", 11.5F);
    private static readonly Font FBig = new("Segoe UI", 20F, FontStyle.Bold);
    private static readonly Font FCap = new("Segoe UI", 11F);
    private static readonly Font FScore = new("Segoe UI", 11F, FontStyle.Bold);

    public ScoreCard(string title)
    {
        Title = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = CardColor;
    }

    /// <summary>Живое значение: big — крупный текст, load — нагрузка 0–100 (-1 = нет полосы).</summary>
    public void SetLive(string bigValue, int load, string caption)
    {
        _big = bigValue;
        _load = load;
        _caption = caption ?? "";
        Invalidate();
    }

    /// <summary>Оценка цикла (показывается в правом нижнем углу, живые значения продолжают обновляться).</summary>
    public void SetScore(int score, string word)
    {
        _scoreNote = "оценка " + score + "/100 · " + word;
        _scoreColor = score >= 70 ? Ok : score >= 40 ? Warn : Bad;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int W = ClientSize.Width, H = ClientSize.Height;

        using (var bg = new SolidBrush(CardColor)) g.FillRectangle(bg, 0, 0, W, H);
        using (var p = new Pen(LineColor)) g.DrawRectangle(p, 0, 0, W - 1, H - 1);

        // заголовок
        TextRenderer.DrawText(g, Title, FTitle, new Rectangle(12, 7, W - 24, 18), MutedColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // крупное текущее значение
        TextRenderer.DrawText(g, _big, FBig, new Rectangle(12, 25, W - 24, 36), TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // полоса нагрузки
        int barY = H - 31;
        var barRect = new Rectangle(12, barY, Math.Max(10, W - 24), 6);
        using (var barBg = new SolidBrush(BarBg)) g.FillRectangle(barBg, barRect);
        if (_load > 0)
        {
            Color loadColor = _load >= 85 ? Bad : _load >= 60 ? Warn : Ok;
            int w = (int)(barRect.Width * Math.Min(100, _load) / 100.0);
            using var fg = new SolidBrush(loadColor);
            g.FillRectangle(fg, barRect.X, barRect.Y, w, barRect.Height);
        }

        // подпись слева + оценка цикла справа
        var capRect = new Rectangle(12, H - 22, W - 24, 17);
        TextRenderer.DrawText(g, _caption, FCap, capRect, MutedColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (_scoreNote != null)
            TextRenderer.DrawText(g, _scoreNote, FScore, capRect, _scoreColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
