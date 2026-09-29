using System.Drawing;
using System.Windows.Forms;

namespace ServerHealth.Gui;

/// <summary>Карточка оценки подсистемы (0–100) с цветовой индикацией и полосой.</summary>
public sealed class ScoreCard : Panel
{
    public string Title = "";
    public string Caption = "—";
    public int Value = -1;

    private static readonly Color CardColor = Color.FromArgb(30, 30, 44);
    private static readonly Color LineColor = Color.FromArgb(44, 44, 64);
    private static readonly Color TextColor = Color.FromArgb(232, 232, 242);
    private static readonly Color MutedColor = Color.FromArgb(150, 150, 175);

    public ScoreCard(string title)
    {
        Title = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = CardColor;
        Font = new Font("Segoe UI", 9F);
    }

    public void Set(int value, string caption)
    {
        Value = value;
        Caption = caption;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int W = ClientSize.Width, H = ClientSize.Height;

        using var bg = new SolidBrush(CardColor);
        g.FillRectangle(bg, 0, 0, W, H);
        using var pen = new Pen(LineColor);
        g.DrawRectangle(pen, 0, 0, W - 1, H - 1);

        TextRenderer.DrawText(g, Title, Font, new Rectangle(10, 6, W - 20, 16), MutedColor,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        Color valColor = Value < 0 ? MutedColor : Value >= 70 ? Color.FromArgb(76, 195, 138)
            : Value >= 40 ? Color.FromArgb(229, 192, 123) : Color.FromArgb(224, 108, 117);
        TextRenderer.DrawText(g, Value < 0 ? "—" : Value.ToString(), new Font("Segoe UI", 20F, FontStyle.Bold),
            new Rectangle(10, 20, W - 20, 36), valColor, TextFormatFlags.Left);
        if (Value >= 0)
            TextRenderer.DrawText(g, "/100", Font, new Rectangle(10, 34, W - 20, 16), MutedColor, TextFormatFlags.Right);

        // полоса
        int barY = H - 24;
        using var barBg = new SolidBrush(Color.FromArgb(45, 45, 68));
        g.FillRectangle(barBg, 10, barY, W - 20, 5);
        int w = (int)((W - 20) * Math.Max(0, Math.Min(100, Value)) / 100.0);
        using var barFg = new SolidBrush(valColor);
        if (w > 0) g.FillRectangle(barFg, 10, barY, w, 5);

        TextRenderer.DrawText(g, Caption, Font, new Rectangle(10, H - 17, W - 20, 14), MutedColor,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
