using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AutoClicker;

/// Палитра и общие примитивы «стеклянной карточки». Цветаhair — белый с малой альфой,
/// никаких акцентных оттенков: подсветка только прозрачностью.
internal static class Theme
{
    public static readonly Color Fg = Color.FromArgb(218, 222, 230);
    public static readonly Color Dim = Color.FromArgb(146, 153, 168);
    public static readonly Color Dim2 = Color.FromArgb(88, 95, 112);
    public static readonly Color BgBottom = Color.FromArgb(14, 16, 20);

    // Подложка полей ввода: поля непрозрачны, поэтому берём усреднённый тон градиента.
    public static readonly Color InputBg = Color.FromArgb(17, 19, 24);

    public static Color Hair(float alpha = 0.06f) => Color.FromArgb((int)(alpha * 255), 255, 255, 255);

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t),
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static readonly string[] Families =
    [
        "Segoe UI Variable Text", "Segoe UI", "Roboto", "Arial",
    ];

    public static Font UiFont(float size, bool light = false, bool bold = false)
    {
        var style = bold ? FontStyle.Bold : FontStyle.Regular;
        foreach (var name in Families)
        {
            var resolved = light ? LightName(name) : name;
            try
            {
                // FontFamily только проверяем и сразу освобождаем: сам Font создаём по имени,
                // иначе нативный family-хэндл копился бы на каждой перерисовке кнопки.
                using var probe = new FontFamily(resolved);
                return new Font(resolved, size, style, GraphicsUnit.Point);
            }
            catch (ArgumentException)
            {
                // Семества нет на машине — пробуем следующее.
            }
        }

        return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }

    private static string LightName(string name)
    {
        // «Segoe UI Light» даёт вес около 300 на Windows 10; Variable Text отдельного лёгкого
        // семества не имеет, поэтому для главного поля пробуем именно лёгкий вариант.
        var light = name == "Segoe UI Variable Text" ? "Segoe UI Light" : name;
        try
        {
            using var probe = new FontFamily(light);
            return light;
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    /// Текст с разрядкой (letter-spacing): GDI+ не умеет tracking, рисуем по символам.
    public static void DrawTracked(Graphics g, string text, Font font, Color color, PointF origin, float trackingEm)
    {
        var spacing = trackingEm * font.SizeInPoints * g.DpiX / 72f;
        var x = origin.X;
        using var brush = new SolidBrush(color);
        using var format = StringFormat.GenericTypographic;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            g.DrawString(s, font, brush, new PointF(x, origin.Y), format);
            x += g.MeasureString(s, font, PointF.Empty, format).Width + spacing;
        }
    }
}

internal enum GlyphKind
{
    None,
    Minimize,
    Crosshair,
    Play,
    Stop,
    Mouse,
    Hold,
    Pin,
}

/// Стеклянная кнопка/пилюля/плитка: сама рисует скругление, волосяную рамку и hover,
/// чтобы не бороться со стандартной темой WinForms. Текст не выделяется.
internal sealed class GlassButton : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 13;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Pill { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float TextSize { get; set; } = 12.5f;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Bold { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; } = Theme.Fg;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GlyphKind Glyph { get; set; } = GlyphKind.None;
    /// Строка-результат в языке webtop: без подложки и рамки, подсветка только hover-фоном.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Flat { get; set; }
    /// Вторая строка строки-результата; её наличие включает раскладку «плитка + заголовок + подпись».
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SubText { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? RightText { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StringAlignment TextAlign { get; set; } = StringAlignment.Center;

    private bool _hover;
    private bool _pressed;

    public GlassButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        BackColor = Theme.BgBottom;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Focus(); }
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (Enabled && keyData is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void WndProc(ref Message message)
    {
        if (Program.Native.IsMouseButtonMessage((uint)message.Msg)
            && Program.Native.GetMessageExtraInfo() == Program.OwnClickTag) return;
        base.WndProc(ref message);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessibility(this);

    private sealed class ButtonAccessibility(GlassButton owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => "Нажать";
        public override void DoDefaultAction()
        {
            if (owner.Enabled) owner.OnClick(EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scale = g.DpiX / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.Round(r, Pill ? r.Height / 2 : 8 * scale);
        if (!Flat)
        {
            using var brush = new SolidBrush(Theme.InputBg);
            g.FillPath(brush, path);
            using var pen = new Pen(Theme.Hair(0.09f), 1);
            g.DrawPath(pen, path);
        }
        if (Enabled && (_hover || _pressed))
        {
            using var brush = new SolidBrush(Theme.Hair(_pressed ? 0.08f : 0.04f));
            g.FillPath(brush, path);
        }
        if (Enabled && Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Hair(0.45f), scale);
            g.DrawPath(pen, path);
        }
        var color = Enabled ? TextColor : Theme.Dim2;
        if (SubText is not null)
        {
            PaintRow(g, color, scale);
            return;
        }
        var hasText = !string.IsNullOrEmpty(Text);
        var glyphSize = 16 * scale;
        var inset = (Flat ? 8 : 12) * scale;
        if (Glyph != GlyphKind.None)
        {
            var gx = hasText ? inset : (Width - glyphSize) / 2f;
            DrawGlyph(g, Glyph, new RectangleF(gx, (Height - glyphSize) / 2f, glyphSize, glyphSize), color);
        }
        if (!hasText) return;

        using var font = Theme.UiFont(TextSize, bold: Bold);
        using var textBrush = new SolidBrush(color);
        var left = Glyph != GlyphKind.None ? inset + glyphSize + 8 * scale : inset;
        using var format = new StringFormat
        {
            Alignment = Glyph != GlyphKind.None ? StringAlignment.Near : TextAlign,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(Text, font, textBrush, new RectangleF(left, 0, Width - left - inset, Height), format);
    }

    private void PaintRow(Graphics g, Color color, float scale)
    {
        var tile = 36 * scale;
        var tr = new RectangleF(10 * scale, (Height - tile) / 2f, tile, tile);
        using (var tilePath = Theme.Round(tr, 9 * scale))
        using (var tileBrush = new SolidBrush(Theme.Blend(Theme.InputBg, Color.White, 0.035f)))
        using (var tilePen = new Pen(Theme.Hair(0.08f), 1))
        {
            g.FillPath(tileBrush, tilePath);
            g.DrawPath(tilePen, tilePath);
        }
        DrawGlyph(g, Glyph, new RectangleF(tr.X + 10 * scale, tr.Y + 10 * scale, 16 * scale, 16 * scale), color);
        var left = 56 * scale;
        var rightWidth = string.IsNullOrEmpty(RightText) ? 0 : 210 * scale;
        var textWidth = Width - left - 12 * scale - rightWidth;
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using (var font = Theme.UiFont(TextSize, bold: Bold))
        using (var brush = new SolidBrush(color))
            g.DrawString(Text, font, brush, new RectangleF(left, 5 * scale, textWidth, 23 * scale), format);
        using (var font = Theme.UiFont(9))
        using (var brush = new SolidBrush(Enabled ? Theme.Dim : Theme.Dim2))
            g.DrawString(SubText, font, brush, new RectangleF(left, 28 * scale, textWidth, 20 * scale), format);
        if (string.IsNullOrEmpty(RightText)) return;
        using var hintFont = Theme.UiFont(9);
        using var hintBrush = new SolidBrush(Theme.Dim);
        format.Alignment = StringAlignment.Far;
        g.DrawString(RightText, hintFont, hintBrush,
            new RectangleF(Width - 8 * scale - rightWidth, 0, rightWidth, Height), format);
    }

    private static void DrawGlyph(Graphics g, GlyphKind kind, RectangleF r, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 20f, r.Height / 20f);
        DrawGlyphCore(g, kind, new RectangleF(0, 0, 20, 20), color);
        g.Restore(state);
    }

    private static void DrawGlyphCore(Graphics g, GlyphKind kind, RectangleF r, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        switch (kind)
        {
            case GlyphKind.Minimize:
                g.DrawLine(pen, r.X + 3, cy, r.Right - 3, cy);
                break;
            case GlyphKind.Crosshair:
                var rad = r.Width / 2 - 2;
                g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
                g.DrawLine(pen, cx, r.Y, cx, r.Y + 3);
                g.DrawLine(pen, cx, r.Bottom - 3, cx, r.Bottom);
                g.DrawLine(pen, r.X, cy, r.X + 3, cy);
                g.DrawLine(pen, r.Right - 3, cy, r.Right, cy);
                break;
            case GlyphKind.Play:
                using (var brush = new SolidBrush(color))
                {
                    var pts = new[]
                    {
                        new PointF(r.X + 4, r.Y + 2),
                        new PointF(r.Right - 3, cy),
                        new PointF(r.X + 4, r.Bottom - 2),
                    };
                    g.FillPolygon(brush, pts);
                }
                break;
            case GlyphKind.Stop:
                using (var brush = new SolidBrush(color))
                {
                    var inset = r.Width * 0.2f;
                    g.FillRectangle(brush, r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
                }
                break;
            case GlyphKind.Mouse:
            case GlyphKind.Hold:
                var mr = new RectangleF(r.X + r.Width * 0.22f, r.Y, r.Width * 0.56f, r.Height);
                using (var mousePath = Theme.Round(mr, mr.Width / 2))
                {
                    if (kind == GlyphKind.Hold)
                    {
                        var saved = g.Save();
                        g.SetClip(new RectangleF(mr.X - 2, mr.Y - 2, mr.Width + 4, mr.Height * 0.5f), CombineMode.Intersect);
                        using var brush = new SolidBrush(color);
                        g.FillPath(brush, mousePath);
                        g.Restore(saved);
                        g.DrawPath(pen, mousePath);
                    }
                    else
                    {
                        g.DrawPath(pen, mousePath);
                        g.DrawLine(pen, r.X + r.Width / 2, r.Y + 2, r.X + r.Width / 2, r.Y + r.Height * 0.38f);
                    }
                }
                break;
            case GlyphKind.Pin:
                var pr = r.Width / 2 - 2;
                g.DrawEllipse(pen, cx - pr, cy - pr, pr * 2, pr * 2);
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, cx - 2, cy - 2, 4, 4);
                }
                break;
        }
    }
}