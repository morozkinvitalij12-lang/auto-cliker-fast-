using System.Runtime.InteropServices;
using Engine = AutoClicker.Program.Engine;
using Native = AutoClicker.Program.Native;
using Settings = AutoClicker.Program.Settings;

namespace AutoClicker;

internal sealed class MainForm : Form
{
    private readonly NumericUpDown _interval;
    private readonly ComboBox _button;
    private readonly NumericUpDown _count;
    private readonly CheckBox _limit;
    private readonly CheckBox _fixedPoint;
    private readonly NumericUpDown _targetX;
    private readonly NumericUpDown _targetY;
    private readonly Button _toggle;
    private readonly Panel _lamp;
    private readonly Label _stateLabel;
    private readonly Label _status;
    private readonly Label _note;
    private readonly NotifyIcon _tray;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly Label _hints;

    private CancellationTokenSource? _cts;
    private Engine? _engine;
    private Thread? _worker;
    private Settings _current;
    private long _lastClicks;
    private bool _ownClickSeen;
    private bool _hotkeysRegistered = true;
    private bool? _deliveryBlocked;
    private uint _foregroundPid;
    private bool _foregroundElevated;
    private readonly bool _selfElevated = Native.IsUserAnAdmin();

    private const string DefaultHints =
        "Ctrl+Alt+F3 — старт/стоп    Ctrl+Alt+F8 — координаты    Ctrl+Alt+F10 — выход";

    public MainForm(Settings defaults)
    {
        _current = defaults;

        Text = "Автокликер";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(410, 326);

        Controls.Add(new Label { Text = "Интервал, мс", Location = new Point(14, 17), AutoSize = true });
        _interval = NewNumber(10, 60_000, defaults.IntervalMs);
        _interval.Location = new Point(200, 14);
        Controls.Add(_interval);

        Controls.Add(new Label { Text = "Кнопка", Location = new Point(14, 47), AutoSize = true });
        _button = new ComboBox
        {
            Location = new Point(200, 41),
            Size = new Size(110, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _button.Items.AddRange(new object[] { "Левая", "Правая", "Средняя" });
        _button.SelectedIndex = defaults.ButtonName switch { "right" => 1, "middle" => 2, _ => 0 };
        Controls.Add(_button);

        // Лимит по умолчанию выключен: включённое поле со значением «1» раньше останавливало
        // кликер после одного клика, и это выглядело как «выключает сам себя».
        Controls.Add(new Label { Text = "Кликов", Location = new Point(14, 77), AutoSize = true });

        _count = NewNumber(1, 1_000_000, Math.Max(1, defaults.Count));
        _count.Location = new Point(270, 74);
        _count.Width = 80;
        Controls.Add(_count);

        _limit = new CheckBox
        {
            Text = "лимит",
            Location = new Point(200, 74),
            AutoSize = true,
            Checked = defaults.Count > 0,
        };
        _limit.CheckedChanged += (_, _) => _count.Enabled = _limit.Checked;
        Controls.Add(_limit);
        _count.Enabled = _limit.Checked;

        _fixedPoint = new CheckBox
        {
            Text = "Фиксированная точка",
            Location = new Point(14, 106),
            AutoSize = true,
            Checked = defaults.HasTarget,
        };
        _fixedPoint.CheckedChanged += (_, _) => UpdateTargetEnabled();
        Controls.Add(_fixedPoint);

        Controls.Add(new Label { Text = "X", Location = new Point(14, 138), AutoSize = true });
        _targetX = NewNumber(-32_768, 32_768, defaults.X);
        _targetX.Location = new Point(36, 135);
        _targetX.Size = new Size(80, 28);
        Controls.Add(_targetX);

        Controls.Add(new Label { Text = "Y", Location = new Point(126, 138), AutoSize = true });
        _targetY = NewNumber(-32_768, 32_768, defaults.Y);
        _targetY.Location = new Point(148, 135);
        _targetY.Size = new Size(80, 28);
        Controls.Add(_targetY);

        var pick = new Button
        {
            Text = "Взять позицию курсора",
            Location = new Point(240, 133),
            Size = new Size(155, 30),
        };
        pick.Click += (_, _) => CaptureCursorPoint();
        Controls.Add(pick);

        _toggle = new Button { Text = "▶ Старт (Ctrl+Alt+F3)", Location = new Point(14, 176), Size = new Size(381, 42) };
        _toggle.Click += (_, _) => OnToggleClick();
        Controls.Add(_toggle);

        _lamp = new Panel { Location = new Point(14, 226), Size = new Size(20, 20) };
        Controls.Add(_lamp);

        _stateLabel = new Label
        {
            Location = new Point(44, 226),
            Size = new Size(351, 22),
            AutoSize = false,
            Font = new Font(Font, FontStyle.Bold),
        };
        Controls.Add(_stateLabel);

        _status = new Label { Location = new Point(14, 254), Size = new Size(381, 22), AutoSize = false };
        Controls.Add(_status);

        _note = new Label
        {
            Location = new Point(14, 278),
            Size = new Size(381, 22),
            AutoSize = false,
            ForeColor = Color.Firebrick,
        };
        Controls.Add(_note);

        _hints = new Label
        {
            Text = DefaultHints,
            Location = new Point(14, 300),
            Size = new Size(381, 20),
            AutoSize = false,
        };
        Controls.Add(_hints);

        _iconOn = MakeStateIcon(Color.FromArgb(0, 175, 90));
        _iconOff = MakeStateIcon(Color.FromArgb(150, 150, 150));
        _tray = new NotifyIcon
        {
            Icon = _iconOff,
            Visible = true,
            Text = "Автокликер: выключен",
        };
        _tray.DoubleClick += (_, _) =>
        {
            WindowState = FormWindowState.Normal;
            Activate();
        };

        _refresh = new System.Windows.Forms.Timer { Interval = 100 };
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();

        UpdateTargetEnabled();
        RefreshStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!_selfElevated)
        {
            _note.Text = "Кликер не от администратора: над окнами с правами выше (игра из-под admin) "
                + "ни Ctrl+Alt+F3, ни клики не работают.";
        }

        var failures = new List<int>();
        foreach (var (id, vk) in new (int, uint)[]
                 {
                     (Program.HotToggle, Program.VkF3),
                     (Program.HotCoords, Program.VkF8),
                     (Program.HotQuit, Program.VkF10),
                 })
        {
            if (!Native.RegisterHotKey(Handle, id, Program.ModCtrlAlt | Program.ModNoRepeat, vk))
            {
                failures.Add(Marshal.GetLastPInvokeError());
            }
        }

        _hotkeysRegistered = failures.Count == 0;
        if (failures.Count > 0)
        {
            // Строка подсказок внизу краснеет: видно, что F3 мёртв не из-за поломки кликера.
            _hints.ForeColor = Color.Firebrick;
            _hints.Text = $"Горячие клавиши заняты: {Program.Win32Message(failures[0])}";
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, Program.HotToggle);
        Native.UnregisterHotKey(Handle, Program.HotCoords);
        Native.UnregisterHotKey(Handle, Program.HotQuit);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Native.WmHotkey)
        {
            switch ((int)message.WParam)
            {
                case Program.HotToggle:
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

        if (Native.IsMouseButtonMessage((uint)message.Msg))
        {
            // Метку ставит сам клик кликера (Program.OwnClickTag в dwExtraInfo).
            // Живое нажатие приходит без неё, поэтому «Стоп» по кнопке всегда настоящий.
            _ownClickSeen = Native.GetMessageExtraInfo() == Program.OwnClickTag;
        }

        base.WndProc(ref message);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
        _engine = null;
        ShutdownEngine();
        base.OnFormClosed(e);
    }

    private static Icon MakeStateIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 1, 1, 13, 13);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// Останавливает и запускает без оговорок: хоткей исходит от живого нажатия,
    /// и глушить его нельзя ни при каких интервалах.
    private void ToggleRunning()
    {
        if (_engine is null)
        {
            Start();
        }
        else
        {
            Stop();
            // После автосворачивания окно обычно не видно: без всплывашки остановку
            // выдаётся заметить только по потухшему значку в трее.
            ShowBalloon("Выключен.");
        }

        RefreshStatus();
    }

    /// Кликер бьёт туда, где курсор, а нажатие «Старт» оставляет курсор ровно на кнопке:
    /// её же первый синтетический клик и приходился по «Стоп». Такой клик узнаётся по
    /// метке в dwExtraInfo и игнорируется; живой клик по кнопке останавливает всегда.
    private void OnToggleClick()
    {
        if (_engine is not null && _ownClickSeen)
        {
            _note.Text = "Кликер кликнул по своей кнопке — убери курсор с окна.";
            RefreshStatus();
            return;
        }

        ToggleRunning();
    }

    private void Start()
    {
        // Убираем окно с дороги до первого клика: иначе первый же синтетический клик
        // приходится в собственные контролы. Возврат — из трея двойным кликом или из панели.
        WindowState = FormWindowState.Minimized;

        _current = BuildSettings();
        _lastClicks = 0;
        _cts = new CancellationTokenSource();
        _engine = new Engine(_current, _cts)
        {
            Report = ShowNote,
            LimitReached = msg => OnUi(() =>
            {
                // Кнопка гаснет следом на ближайшем обновлении статуса — пишем причину крупно,
                // чтобы это не выглядело самоотключением.
                _lastClicks = _engine?.Clicks ?? _lastClicks;
                _note.Text = msg;
                ShowBalloon(msg);
            }),
        };

        _worker = new Thread(_engine.Run) { IsBackground = true, Name = "clicks" };
        _engine.Toggle();
        _worker.Start();

        _toggle.Text = "⏹ Стоп (Ctrl+Alt+F3)";
        _note.Text = _current.Count > 0
            ? $"Лимит: {_current.Count} — после него кликер сам погаснет, это не поломка."
            : "Клики идут без лимита — Ctrl+Alt+F3 или кнопка остановят.";

        if (!_current.HasTarget)
        {
            _note.Text = "Кликер бьёт туда, где курсор: держи его вне этого окна.";
        }

        ShowBalloon("Включён и свёрнут. Ctrl+Alt+F3 — стоп, двойной клик по значку — окно.");
    }

    private void ShowBalloon(string message)
    {
        // Работничает, только когда окно свёрнуто: иначе сообщение видно в окне.
        if (WindowState != FormWindowState.Minimized)
        {
            return;
        }

        _tray.ShowBalloonTip(3000, "Автокликер", message, ToolTipIcon.Info);
    }

    private void Stop()
    {
        _lastClicks = _engine?.Clicks ?? _lastClicks;
        _engine = null;
        ShutdownEngine();
        _toggle.Text = "▶ Старт (Ctrl+Alt+F3)";
    }

    /// Поток кликов может в этот момент звать _shutdown.Cancel() на исчерпанном лимите,
    /// а Cancel() на уже освобождённом источнике бросает ObjectDisposedException вне UI-потока.
    /// Поэтому сначала ждём выхода потока, и только потом освобождаем источник.
    private void ShutdownEngine()
    {
        var cts = _cts;
        var worker = _worker;
        _cts = null;
        _worker = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (worker is null || worker.Join(250))
        {
            cts.Dispose();
        }
    }

    private Settings BuildSettings()
    {
        var (down, up, name) = _button.SelectedIndex switch
        {
            1 => (Native.RightDown, Native.RightUp, "right"),
            2 => (Native.MiddleDown, Native.MiddleUp, "middle"),
            _ => (Native.LeftDown, Native.LeftUp, "left"),
        };

        return new Settings
        {
            IntervalMs = (int)_interval.Value,
            DownFlag = down,
            UpFlag = up,
            ButtonName = name,
            Count = _limit.Checked ? (int)_count.Value : 0,
            HasTarget = _fixedPoint.Checked,
            X = (int)_targetX.Value,
            Y = (int)_targetY.Value,
        };
    }

    private void CaptureCursorPoint()
    {
        if (!Native.GetCursorPos(out var point))
        {
            _note.Text = "Не удалось прочитать позицию курсора.";
            return;
        }

        _targetX.Value = Clamp(point.X, _targetX);
        _targetY.Value = Clamp(point.Y, _targetY);
        _fixedPoint.Checked = true;
        _note.Text = $"Запомлена точка {point.X},{point.Y}.";
    }

    private void UpdateTargetEnabled()
    {
        _targetX.Enabled = _fixedPoint.Checked;
        _targetY.Enabled = _fixedPoint.Checked;
    }

    private void RefreshStatus()
    {
        // Движок сам завершается, когда выбран лимит кликов: приводим кнопку в согласие.
        if (_engine is not null && _cts is not null && _cts.IsCancellationRequested)
        {
            Stop();
        }

        var running = _engine is not null;
        var where = _current.HasTarget ? $"точка {_current.X},{_current.Y}" : "где курсор";
        var limit = _current.Count > 0 ? $"лимит {_current.Count}" : "без лимита";
        var clicks = running ? _engine!.Clicks : _lastClicks;

        _lamp.BackColor = running ? Color.FromArgb(0, 175, 90) : Color.FromArgb(190, 190, 190);
        _stateLabel.Text = running ? "ВКЛЮЧЕН — кликает" : "ВЫКЛЮЧЕН";
        Text = running ? "▶ Автокликер — включён" : "⏸ Автокликер — выключен";
        _tray.Icon = running ? _iconOn : _iconOff;
        _tray.Text = $"Автокликер: {(running ? "включён" : "выключен")} · кликов {clicks}";

        _status.Text = $"интервал {_current.IntervalMs} мс · {where} · {limit} · кликов за запуск: {clicks}";

        UpdateTrustWarning();
    }

    /// Клики через SendInput не доходят до активного окна с правами выше (UIPI) — об этом окно
    /// пишет в строку подсказок, пока фокус не вернётся к обычному окну. Кликер от
    /// администратора такие окна видит на равных, поэтому там предупреждение было бы ложным.
    private void UpdateTrustWarning()
    {
        if (!_hotkeysRegistered || _selfElevated)
        {
            return;
        }

        var window = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(window, out var pid);

        if (pid == 0)
        {
            return;
        }

        // Права процесса фиксированы при создании, так что токен смотрим один раз на pid,
        // а не каждые 100 мс.
        if (pid != _foregroundPid)
        {
            _foregroundPid = pid;
            _foregroundElevated = Native.IsForegroundWindowElevated(window);
        }

        if (_foregroundElevated == _deliveryBlocked)
        {
            return;
        }

        _deliveryBlocked = _foregroundElevated;
        _hints.ForeColor = _foregroundElevated ? Color.Firebrick : SystemColors.ControlText;
        _hints.Text = _foregroundElevated
            ? "Окно от администратора активно: клики не доходят"
            : DefaultHints;
    }

    private void ShowNote(string message) => OnUi(() => _note.Text = message);

    /// Всё, что рабочий поток хочет показать, проходит отсюда: напрямую трогать
    /// контролы из `clicks` нельзя, а окно к этому моменту может уже закрываться.
    private void OnUi(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(action);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Окно закрывается ровно в момент отчёта — показывать некуда.
        }
    }

    private static decimal Clamp(int value, NumericUpDown box) =>
        Math.Max(box.Minimum, Math.Min(box.Maximum, value));

    private static NumericUpDown NewNumber(int minimum, int maximum, int value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = Math.Max(minimum, Math.Min(maximum, value)),
        Size = new Size(110, 28),
    };
}
