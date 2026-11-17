using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Engine = AutoClicker.Program.Engine;
using Native = AutoClicker.Program.Native;
using Settings = AutoClicker.Program.Settings;

namespace AutoClicker;

internal sealed class MainForm : Form
{
    private const double WindowOpacity = 0.92;

    private readonly bool _animations;

    private readonly TextBox _interval;
    private readonly TextBox _count;
    private readonly TextBox _targetX;
    private readonly TextBox _targetY;
    private readonly GlassButton _keyPill;
    private readonly GlassButton _startBtn;
    private readonly GlassButton _modeRow;
    private readonly GlassButton _buttonRow;
    private readonly GlassButton _pointRow;
    private readonly GlassButton _captureTile;
    private readonly GlassButton _limitRow;
    private readonly GlassButton _hideLink;
    private readonly GlassButton _exitLink;
    private readonly Label _helper;

    private readonly Font _brandFont;
    private readonly Font _metaFont;

    private readonly NotifyIcon _tray;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _trayToggle;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly System.Windows.Forms.Timer _capture;
    private readonly System.Windows.Forms.Timer _fade;
    private readonly Stopwatch _fadeWatch = new();

    private int _buttonIndex;
    private bool _holdOn;
    private bool _limitOn;
    private bool _fixedOn;
    private int _hotkeyIndex;
    private string _noteText = string.Empty;

    private CancellationTokenSource? _cts;
    private Engine? _engine;
    private Thread? _worker;
    private Settings _current;
    private long _lastClicks;

    // Ошибка регистрации не должна скрываться предупреждением о правах активного окна.
    private int _hotkeyFailure;
    private int _otherHotkeyFailure;
    private int _toggleHotkeyId = Program.HotToggle;
    private int _registeredToggleIndex = -1;
    private bool? _deliveryBlocked;
    private uint _foregroundPid;
    private bool _foregroundElevated;
    private readonly bool _selfElevated = Native.IsUserAnAdmin();

    private static readonly string[] ButtonLabels = ["левая", "правая", "средняя"];

    public MainForm(Settings defaults)
    {
        _current = defaults;
        _animations = AnimationsEnabled();
        _buttonIndex = defaults.ButtonName switch { "right" => 1, "middle" => 2, _ => 0 };
        _holdOn = defaults.HoldMode;
        _limitOn = defaults.Count > 0;
        _fixedOn = defaults.HasTarget;
        _hotkeyIndex = Program.SavedToggleIndex;

        Text = "Автокликер";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
        KeyPreview = true;
        Opacity = WindowOpacity;
        BackColor = Theme.BgBottom;
        ClientSize = new Size(S(720), S(328));
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer, true);

        _brandFont = Theme.UiFont(9);
        _metaFont = Theme.UiFont(9);

        _interval = NewInput(24, 52, 94, 32, 18, defaults.IntervalMs.ToString(), light: true);
        _interval.AccessibleName = "Интервал между кликами, миллисекунды";
        _interval.MaxLength = 5;
        _interval.TabIndex = 1;
        Controls.Add(_interval);

        _targetX = NewInput(430, 179, 80, 24, 9.5f, defaults.X.ToString());
        _targetX.AccessibleName = "Координата X";
        _targetX.MaxLength = 6;
        _targetX.TabIndex = 6;
        Controls.Add(_targetX);
        _targetY = NewInput(554, 179, 80, 24, 9.5f, defaults.Y.ToString());
        _targetY.AccessibleName = "Координата Y";
        _targetY.MaxLength = 6;
        _targetY.TabIndex = 7;
        Controls.Add(_targetY);
        _count = NewInput(280, 219, 112, 24, 9.5f, Math.Max(1, defaults.Count).ToString());
        _count.AccessibleName = "Лимит кликов";
        _count.MaxLength = 7;
        _count.TabIndex = 10;
        Controls.Add(_count);

        _keyPill = new GlassButton
        {
            Location = new Point(S(536), S(12)),
            Size = new Size(S(160), S(28)),
            Pill = true,
            TextSize = 9,
            TextColor = Theme.Dim,
            Text = SpacedKeyLabel,
            AccessibleName = "Сменить сочетание старт/стоп",
            TabIndex = 0,
        };
        _keyPill.Click += (_, _) => CycleHotkey();
        Controls.Add(_keyPill);

        _startBtn = new GlassButton
        {
            Location = new Point(S(596), S(52)),
            Size = new Size(S(100), S(32)),
            Flat = true,
            TextSize = 10,
            Text = "старт",
            Glyph = GlyphKind.Play,
            TabIndex = 2,
        };
        _startBtn.Click += (_, _) => ToggleRunning();
        Controls.Add(_startBtn);

        _modeRow = new GlassButton
        {
            Location = new Point(S(16), S(104)),
            Size = new Size(S(688), S(56)),
            Flat = true,
            Bold = true,
            TextSize = 10.5f,
            Glyph = GlyphKind.Play,
            Text = "клики",
            AccessibleName = "Сменить режим: клики или зажатие",
            TabIndex = 3,
        };
        _modeRow.Click += (_, _) => { _holdOn = !_holdOn; UpdateControls(); };
        Controls.Add(_modeRow);

        _buttonRow = NewOption(16, 172, 176, "левая кнопка");
        _buttonRow.AccessibleName = "Сменить кнопку мыши";
        _buttonRow.TabIndex = 4;
        _buttonRow.Click += (_, _) => { _buttonIndex = (_buttonIndex + 1) % 3; UpdateControls(); };
        Controls.Add(_buttonRow);

        _pointRow = NewOption(200, 172, 196, "где курсор");
        _pointRow.AccessibleName = "Сменить цель: курсор или фиксированная точка";
        _pointRow.TabIndex = 5;
        _pointRow.Click += (_, _) => { _fixedOn = !_fixedOn; UpdateControls(); };
        Controls.Add(_pointRow);

        _captureTile = new GlassButton
        {
            Location = new Point(S(656), S(172)),
            Size = new Size(S(40), S(32)),
            Flat = true,
            Glyph = GlyphKind.Crosshair,
            TextColor = Theme.Dim,
            AccessibleName = "Запомнить позицию курсора через три секунды",
            TabIndex = 8,
        };
        _captureTile.Click += (_, _) => BeginCapture();
        Controls.Add(_captureTile);

        _limitRow = NewOption(16, 212, 176, "без лимита");
        _limitRow.AccessibleName = "Включить или выключить лимит кликов";
        _limitRow.TabIndex = 9;
        _limitRow.Click += (_, _) => { _limitOn = !_limitOn; UpdateControls(); };
        Controls.Add(_limitRow);

        _helper = new Label
        {
            Location = new Point(S(24), S(252)),
            Size = new Size(S(672), S(36)),
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            ForeColor = Theme.Dim,
            Font = Theme.UiFont(9),
            UseMnemonic = false,
        };
        Controls.Add(_helper);

        _hideLink = NewOption(548, 292, 80, "скрыть");
        _hideLink.TabIndex = 11;
        _hideLink.Click += (_, _) => HideToTray();
        Controls.Add(_hideLink);
        _exitLink = NewOption(636, 292, 68, "выйти");
        _exitLink.TabIndex = 12;
        _exitLink.Click += (_, _) => Close();
        Controls.Add(_exitLink);

        foreach (var input in new[] { _interval, _targetX, _targetY, _count })
        {
            input.GotFocus += (_, _) => Invalidate();
            input.LostFocus += (_, _) => NormalizeInputs();
        }

        _iconOn = MakeStateIcon(Color.FromArgb(0, 175, 90));
        _iconOff = MakeStateIcon(Color.FromArgb(150, 150, 150));
        _tray = new NotifyIcon { Icon = _iconOff, Visible = true, Text = "Автокликер: выключен" };

        // Меню в трее — путь без клавиатуры: работает и над админ-окном, и когда клавиши заняты.
        _trayToggle = new ToolStripMenuItem();
        _trayToggle.Click += (_, _) => ToggleRunning();
        var trayCapture = new ToolStripMenuItem("взять позицию курсора");
        trayCapture.Click += (_, _) => BeginCapture();
        var trayExit = new ToolStripMenuItem("выход");
        trayExit.Click += (_, _) => Close();
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.AddRange(new ToolStripItem[] { _trayToggle, trayCapture, new ToolStripSeparator(), trayExit });
        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _refresh = new System.Windows.Forms.Timer { Interval = 100 };
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();

        _capture = new System.Windows.Forms.Timer { Interval = 3000 };
        _capture.Tick += (_, _) => { _capture.Stop(); CaptureCursorPoint(); };

        _fade = new System.Windows.Forms.Timer { Interval = 16 };
        _fade.Tick += (_, _) =>
        {
            var t = _fadeWatch.ElapsedMilliseconds / 160f;
            if (t >= 1) { Opacity = WindowOpacity; _fade.Stop(); }
            else Opacity = WindowOpacity * t;
        };

        UpdateControls();
        UpdateKeyLabels();
        RefreshStatus();
    }

    private int S(int value) => (int)MathF.Round(value * DeviceDpi / 96f);

    private string SpacedKeyLabel => ToggleKeyLabel.Replace("+", " + ");

    private TextBox NewInput(int x, int y, int w, int h, float size, string text, bool light = false) => new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Theme.BgBottom,
        ForeColor = Theme.Fg,
        Font = Theme.UiFont(size, light: light),
        Text = text,
        Location = new Point(S(x), S(y)),
        Size = new Size(S(w), S(h)),
    };

    private GlassButton NewOption(int x, int y, int width, string text) => new()
    {
        Location = new Point(S(x), S(y)),
        Size = new Size(S(width), S(32)),
        Flat = true,
        TextSize = 9.5f,
        TextColor = Theme.Dim,
        TextAlign = StringAlignment.Near,
        Text = text,
    };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRegion();
        RegisterHotkeys();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FadeIn();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) ApplyRegion();
    }

    private void ApplyRegion()
    {
        var old = Region;
        using var path = Theme.Round(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), S(24));
        Region = new Region(path);
        old?.Dispose();
    }

    private void FadeIn()
    {
        if (!_animations) { Opacity = WindowOpacity; return; }
        Opacity = 0;
        _fadeWatch.Restart();
        _fade.Start();
    }

    private void HideToTray() => Hide();

    private void RegisterHotkeys()
    {
        var toggle = Program.ToggleHotkeys[_hotkeyIndex];
        var failure = 0;
        _registeredToggleIndex = -1;
        _otherHotkeyFailure = 0;

        foreach (var (id, mods, vk) in new (int, uint, uint)[]
                 {
                     (_toggleHotkeyId, toggle.Mods, toggle.Vk),
                     (Program.HotCoords, Program.ModCtrlAlt, Program.VkF8),
                     (Program.HotQuit, Program.ModCtrlAlt, Program.VkF10),
                 })
        {
            Native.UnregisterHotKey(Handle, id);
            if (Native.RegisterHotKey(Handle, id, mods | Program.ModNoRepeat, vk))
            {
                if (id == _toggleHotkeyId) _registeredToggleIndex = _hotkeyIndex;
            }
            else
            {
                var error = Marshal.GetLastPInvokeError();
                if (failure == 0) failure = error;
                if (id != _toggleHotkeyId && _otherHotkeyFailure == 0) _otherHotkeyFailure = error;
            }
        }

        _hotkeyFailure = failure;
        _deliveryBlocked = null;
        RefreshHelper();
    }

    private void CycleHotkey()
    {
        if (!IsHandleCreated) return;
        var nextId = _toggleHotkeyId == Program.HotToggle ? Program.HotToggleAlternate : Program.HotToggle;
        var error = 0;
        for (var offset = 1; offset <= Program.ToggleHotkeys.Length; offset++)
        {
            var next = (_hotkeyIndex + offset) % Program.ToggleHotkeys.Length;
            if (next == _registeredToggleIndex) continue;
            var toggle = Program.ToggleHotkeys[next];
            if (!Native.RegisterHotKey(Handle, nextId, toggle.Mods | Program.ModNoRepeat, toggle.Vk))
            {
                error = Marshal.GetLastPInvokeError();
                continue;
            }

            // Рабочую клавишу снимаем только после успешной регистрации замены.
            Native.UnregisterHotKey(Handle, _toggleHotkeyId);
            _toggleHotkeyId = nextId;
            _hotkeyIndex = next;
            _registeredToggleIndex = next;
            _hotkeyFailure = _otherHotkeyFailure;
            _deliveryBlocked = null;
            Program.SaveToggleIndex(next);
            if (_engine is not null) _engine.ToggleKeyName = toggle.Label;
            _noteText = $"старт/стоп — {ToggleKeyLabel}; занятые сочетания пропускаются.";
            UpdateKeyLabels();
            RefreshHelper();
            return;
        }

        if (_registeredToggleIndex >= 0)
            _noteText = $"другие сочетания заняты ({error}); работает {ToggleKeyLabel}.";
        else
            _hotkeyFailure = error;
        RefreshHelper();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, _toggleHotkeyId);
        Native.UnregisterHotKey(Handle, Program.HotCoords);
        Native.UnregisterHotKey(Handle, Program.HotQuit);
        _registeredToggleIndex = -1;
        base.OnHandleDestroyed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideToTray();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Системной рамки нет: бесрамочное окно таскаем за верхнюю полосу-заголовок.
        if (e.Button == MouseButtons.Left && e.Y < S(48))
        {
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Native.WmHotkey)
        {
            switch ((int)message.WParam)
            {
                case Program.HotToggle:
                case Program.HotToggleAlternate:
                    ToggleRunning();
                    break;
                case Program.HotCoords:
                    CaptureCursorPoint();
                    break;
                case Program.HotQuit:
                    Close();
                    break;
            }
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        _capture.Dispose();
        _fade.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayMenu.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
        _brandFont.Dispose();
        _metaFont.Dispose();
        _engine = null;
        ShutdownEngine();
        base.OnFormClosed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Theme.BgBottom);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1), S(24)))
        using (var pen = new Pen(Theme.Hair(0.08f), 1))
        {
            g.DrawPath(pen, path);
        }

        Theme.DrawTracked(g, "autoclicker", _brandFont, Theme.Dim, new PointF(S(24), S(20)), 0.12f);

        var running = _engine is not null;
        var clicks = running ? _engine!.Clicks : _lastClicks;
        var countLabel = _current.HoldMode ? "зажатий" : "кликов";
        var meta = running ? $"включён · {countLabel} {clicks}" : "выключен";
        using var brush = new SolidBrush(Theme.Dim);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(meta, _metaFont, brush, new RectangleF(S(144), S(20), S(376), S(20)), format);
        g.DrawString(_holdOn ? "пауза не нужна в режиме зажатия" : "мс между кликами", _metaFont, brush,
            new RectangleF(S(128), S(61), S(450), S(20)), format);

        using (var pen = new Pen(_interval.Focused ? Theme.Hair(0.45f) : Theme.Hair(0.12f), 1))
            g.DrawLine(pen, S(24), S(92), S(696), S(92));

        if (_fixedOn)
        {
            g.DrawString("x", _metaFont, brush, new PointF(S(410), S(180)));
            g.DrawString("y", _metaFont, brush, new PointF(S(534), S(180)));
        }
        else
        {
            g.DrawString("прицел — запомнить точку", _metaFont, brush, new PointF(S(430), S(181)));
        }

        var limitHint = _holdOn ? "отпустится при остановке" : _limitOn ? "после" : "кликать до остановки";
        g.DrawString(limitHint, _metaFont, brush, new PointF(S(208), S(220)));
        if (_limitOn && !_holdOn)
            g.DrawString("кликов", _metaFont, brush, new PointF(S(408), S(220)));

        foreach (var input in new[] { _targetX, _targetY, _count })
        {
            if (!input.Visible) continue;
            using var pen = new Pen(input.Focused ? Theme.Hair(0.45f) : Theme.Hair(0.12f), 1);
            g.DrawLine(pen, input.Left, input.Bottom + S(3), input.Right, input.Bottom + S(3));
        }
        g.DrawString("Esc — спрятать", _metaFont, brush, new PointF(S(24), S(300)));
    }

    private static Icon MakeStateIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 1, 1, 13, 13);
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    /// Хоткей исходит от живого нажатия — глушить его нельзя ни при каких интервалах.
    private void ToggleRunning()
    {
        if (_engine is null) Start();
        else { Stop(); ShowBalloon("выключен."); }
        RefreshStatus();
    }

    private void Start()
    {
        if (_capture.Enabled)
        {
            _noteText = "сначала дождись захвата точки.";
            ShowBalloon(_noteText);
            RefreshHelper();
            return;
        }

        // Убираем окно с дороги до первого клика, иначе первый синтетический клик
        // приходится в собственные контролы. Возврат — клик по значку в трее.
        Hide();

        _current = BuildSettings();
        _lastClicks = 0;
        _cts = new CancellationTokenSource();
        _engine = new Engine(_current, _cts)
        {
            ToggleKeyName = ToggleKeyLabel,
            Report = ShowNote,
            LimitReached = msg => OnUi(() =>
            {
                _lastClicks = _engine?.Clicks ?? _lastClicks;
                _noteText = msg;
                RefreshHelper();
                ShowBalloon(msg);
            }),
        };

        _worker = new Thread(_engine.Run) { IsBackground = true, Name = "clicks" };
        _engine.Toggle();
        _worker.Start();

        UpdateKeyLabels();
        _noteText = _current.HoldMode
            ? $"кнопка будет зажата до остановки — {ToggleKeyLabel} или кнопка отпустят её."
            : _current.Count > 0
                ? $"лимит: {_current.Count} — после него кликер сам погаснет, это не поломка."
                : $"клики идут без лимита — {ToggleKeyLabel} или кнопка остановят.";
        if (!_current.HasTarget && !_current.HoldMode)
            _noteText = $"клики идут за курсором · {ToggleKeyLabel} — остановить.";

        RefreshHelper();
        ShowBalloon($"включён и свёрнут. {ToggleKeyLabel} — стоп, клик по значку в трее — окно.");
    }

    private void ShowBalloon(string message)
    {
        // Всплывашка нужна, только когда окно скрыто в трее: иначе сообщение видно в карточке.
        if (Visible) return;
        _tray.ShowBalloonTip(3000, "Автокликер", message, ToolTipIcon.Info);
    }

    private void Stop()
    {
        _lastClicks = _engine?.Clicks ?? _lastClicks;
        _engine = null;
        ShutdownEngine();
        UpdateKeyLabels();
    }

    private string ToggleKeyLabel => Program.ToggleHotkeys[_hotkeyIndex].Label;

    private void UpdateKeyLabels()
    {
        var running = _engine is not null;
        _startBtn.Text = running ? "стоп" : "старт";
        _startBtn.Glyph = running ? GlyphKind.Stop : GlyphKind.Play;
        _startBtn.Invalidate();
        _keyPill.Text = SpacedKeyLabel;
        _keyPill.Invalidate();
        _trayToggle.Text = (running ? "стоп (" : "старт (") + ToggleKeyLabel + ")";
        UpdateControls();
    }

    private void UpdateControls()
    {
        var idle = _engine is null;
        var interval = ParseInt(_interval, 10, 60_000, _current.IntervalMs);
        _modeRow.Text = _holdOn ? "зажатие кнопки" : "автоматические клики";
        _modeRow.Glyph = _holdOn ? GlyphKind.Hold : GlyphKind.Play;
        _modeRow.SubText = _holdOn ? "держать кнопку до остановки" : $"один клик каждые {interval} мс";
        _modeRow.RightText = idle ? "нажми, чтобы сменить режим" : $"{ToggleKeyLabel} — стоп";

        _buttonRow.Text = $"{ButtonLabels[_buttonIndex]} кнопка";
        _pointRow.Text = _fixedOn ? "фиксированная точка" : "где курсор";
        _limitRow.Text = _holdOn ? "без лимита" : _limitOn ? "лимит включён" : "без лимита";

        _modeRow.Enabled = idle;
        _buttonRow.Enabled = idle;
        _pointRow.Enabled = idle;
        _captureTile.Enabled = idle && !_capture.Enabled;
        _interval.Enabled = idle && !_holdOn;
        _limitRow.Enabled = idle && !_holdOn;
        _count.Visible = _limitOn && !_holdOn;
        _count.Enabled = idle && _limitOn && !_holdOn;
        _targetX.Visible = _fixedOn;
        _targetY.Visible = _fixedOn;
        _targetX.Enabled = idle && _fixedOn;
        _targetY.Enabled = idle && _fixedOn;

        _modeRow.Invalidate();
        _buttonRow.Invalidate();
        _pointRow.Invalidate();
        _limitRow.Invalidate();
        Invalidate();
    }

    /// Нижняя строка всегда отвечает за главное: либо причину молчания клавиш/кликов, либо подсказку.
    private void RefreshHelper()
    {
        if (_hotkeyFailure != 0)
        {
            _helper.ForeColor = Theme.Fg;
            _helper.Text = $"горячие клавиши заняты: {Program.Win32Message(_hotkeyFailure)}";
            return;
        }

        if (_deliveryBlocked == true)
        {
            _helper.ForeColor = Theme.Fg;
            _helper.Text = "окно от администратора активно: клики не доходят";
            return;
        }

        _helper.ForeColor = Theme.Dim;
        _helper.Text = _noteText.Length > 0
            ? _noteText
            : "нажми на настройку, чтобы изменить · при старте окно спрячется в трей";
    }

    private void BeginCapture()
    {
        if (_engine is not null)
        {
            ShowNote("останови кликер перед сменой точки.");
            return;
        }
        _capture.Stop();
        _noteText = "три секунды: поставь курсор в точку клика.";
        RefreshHelper();
        ShowBalloon(_noteText);
        _capture.Start();
        UpdateControls();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        FadeIn();
        Activate();
    }

    /// Поток кликов может звать Cancel() на исчерпанном лимите, а Cancel() на уже
    /// освобождённом источнике бросает ObjectDisposedException вне UI-потока. Поэтому
    /// сначала ждём выхода потока, и только потом освобождаем источник.
    private void ShutdownEngine()
    {
        var cts = _cts;
        var worker = _worker;
        _cts = null;
        _worker = null;
        if (cts is null) return;
        cts.Cancel();
        if (worker is null || worker.Join(1000)) cts.Dispose();
    }

    private Settings BuildSettings()
    {
        var (down, up, name) = _buttonIndex switch
        {
            1 => (Native.RightDown, Native.RightUp, "right"),
            2 => (Native.MiddleDown, Native.MiddleUp, "middle"),
            _ => (Native.LeftDown, Native.LeftUp, "left"),
        };

        return new Settings
        {
            IntervalMs = ParseInt(_interval, 10, 60_000, _current.IntervalMs),
            DownFlag = down,
            UpFlag = up,
            ButtonName = name,
            HoldMode = _holdOn,
            Count = _holdOn ? 0 : _limitOn ? ParseInt(_count, 1, 1_000_000, Math.Max(1, _current.Count)) : 0,
            HasTarget = _fixedOn,
            X = ParseInt(_targetX, -32_768, 32_768, _current.X),
            Y = ParseInt(_targetY, -32_768, 32_768, _current.Y),
        };
    }

    private void NormalizeInputs()
    {
        _interval.Text = ParseInt(_interval, 10, 60_000, _current.IntervalMs).ToString();
        _targetX.Text = ParseInt(_targetX, -32_768, 32_768, _current.X).ToString();
        _targetY.Text = ParseInt(_targetY, -32_768, 32_768, _current.Y).ToString();
        _count.Text = ParseInt(_count, 1, 1_000_000, Math.Max(1, _current.Count)).ToString();
        UpdateControls();
    }

    private void CaptureCursorPoint()
    {
        _capture.Stop();
        UpdateControls();
        if (_engine is not null)
        {
            ShowNote("останови кликер перед сменой точки.");
            return;
        }
        if (!Native.GetCursorPos(out var point))
        {
            _noteText = "не удалось прочитать позицию курсора.";
            RefreshHelper();
            return;
        }
        _targetX.Text = point.X.ToString();
        _targetY.Text = point.Y.ToString();
        _fixedOn = true;
        UpdateControls();
        _noteText = $"запомнена точка {point.X},{point.Y}.";
        RefreshHelper();
    }

    private void RefreshStatus()
    {
        // Движок сам завершается на лимите кликов: приводим интерфейс в согласие.
        if (_engine is not null && _cts is not null && _cts.IsCancellationRequested) Stop();

        var running = _engine is not null;
        Text = running ? "Автокликер — включён" : "Автокликер — выключен";
        _tray.Icon = running ? _iconOn : _iconOff;
        _tray.Text = $"Автокликер: {(running ? "включён" : "выключен")} · {(_current.HoldMode ? "зажатий" : "кликов")} {(running ? _engine!.Clicks : _lastClicks)}";
        Invalidate();
        UpdateTrustWarning();
    }

    /// Клики через SendInput не доходят до активного окна с правами выше (UIPI). Кликер от
    /// администратора такие окна видит на равных, поэтому там предупреждение было бы ложным.
    private void UpdateTrustWarning()
    {
        if (_hotkeyFailure != 0 || _selfElevated) return;

        var window = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return;

        // Права процесса фиксированы при создании: токен смотрим один раз на pid.
        if (pid != _foregroundPid)
        {
            _foregroundPid = pid;
            _foregroundElevated = Native.IsForegroundWindowElevated(window);
        }

        if (_foregroundElevated == _deliveryBlocked) return;

        _deliveryBlocked = _foregroundElevated;
        RefreshHelper();
    }

    private void ShowNote(string message) => OnUi(() =>
    {
        _noteText = message;
        RefreshHelper();
    });

    /// Всё, что рабочий поток хочет показать, проходит отсюда: напрямую трогать
    /// контролы из `clicks` нельзя, а окно к этому моменту может уже закрываться.
    private void OnUi(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Окно закрывается ровно в момент отчёта — показывать некуда.
        }
    }

    private static int ParseInt(TextBox box, int min, int max, int fallback) =>
        int.TryParse(box.Text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;

    private static bool AnimationsEnabled()
    {
        var enabled = true;
        SystemParametersInfo(0x1042, 0, ref enabled, 0);
        return enabled;
    }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}