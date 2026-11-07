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
    private readonly System.Windows.Forms.Timer _capture;
    private readonly Label _hints;
    private readonly ComboBox _hotkey;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _trayToggle;

    private CancellationTokenSource? _cts;
    private Engine? _engine;
    private Thread? _worker;
    private Settings _current;
    private long _lastClicks;
    private bool _ownClickSeen;

    // Ошибка регистрации не должна скрываться предупреждением о правах активного окна.
    private int _hotkeyFailure;
    private int _otherHotkeyFailure;
    private int _toggleHotkeyId = Program.HotToggle;
    private int _registeredToggleIndex = -1;
    private bool? _deliveryBlocked;
    private uint _foregroundPid;
    private bool _foregroundElevated;
    private readonly bool _selfElevated = Native.IsUserAnAdmin();

    public MainForm(Settings defaults)
    {
        _current = defaults;

        Text = "Автокликер";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(410, 372);

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
        pick.Click += (_, _) => BeginCapture();
        Controls.Add(pick);

        _toggle = new Button { Text = "▶ Старт", Location = new Point(14, 176), Size = new Size(381, 42) };
        _toggle.Click += (_, _) => OnToggleClick();
        Controls.Add(_toggle);

        Controls.Add(new Label { Text = "Клавиша старт/стоп", Location = new Point(14, 227), AutoSize = true });
        _hotkey = new ComboBox
        {
            Location = new Point(200, 224),
            Size = new Size(195, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var preset in Program.ToggleHotkeys)
        {
            _hotkey.Items.Add(preset.Label);
        }

        // Выбор ставим до подписки: иначе инициализация сама себя просит пересохранять настройку.
        _hotkey.SelectedIndex = Program.SavedToggleIndex;
        _hotkey.SelectedIndexChanged += (_, _) => ApplyToggleHotkey();
        Controls.Add(_hotkey);

        _lamp = new Panel { Location = new Point(14, 256), Size = new Size(20, 20) };
        Controls.Add(_lamp);

        _stateLabel = new Label
        {
            Location = new Point(44, 256),
            Size = new Size(351, 22),
            AutoSize = false,
            Font = new Font(Font, FontStyle.Bold),
        };
        Controls.Add(_stateLabel);

        _status = new Label { Location = new Point(14, 284), Size = new Size(381, 22), AutoSize = false };
        Controls.Add(_status);

        _note = new Label
        {
            Location = new Point(14, 308),
            Size = new Size(381, 22),
            AutoSize = false,
            ForeColor = Color.Firebrick,
        };
        Controls.Add(_note);

        // Две строки: список комбинаций в 381 пиксель по ширине не помещается, а обрезать
        // подсказку про клавиши хуже, чем отвезти ей место.
        _hints = new Label
        {
            Location = new Point(14, 330),
            Size = new Size(381, 34),
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
        // Меню в трее — путь без клавиатуры: он работает и над админ-окном, и когда клавиши
        // заняты чужим приложением. Одиночный клик по значку просто возвращает окно.
        _trayToggle = new ToolStripMenuItem();
        _trayToggle.Click += (_, _) => ToggleRunning();
        var trayCapture = new ToolStripMenuItem("Взять позицию курсора");
        trayCapture.Click += (_, _) => BeginCapture();
        var trayExit = new ToolStripMenuItem("Выход");
        trayExit.Click += (_, _) => Close();
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.AddRange(new ToolStripItem[] { _trayToggle, trayCapture, new ToolStripSeparator(), trayExit });

        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                RestoreFromTray();
            }
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _refresh = new System.Windows.Forms.Timer { Interval = 100 };
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();

        _capture = new System.Windows.Forms.Timer { Interval = 3000 };
        _capture.Tick += (_, _) =>
        {
            _capture.Stop();
            CaptureCursorPoint();
        };

        UpdateTargetEnabled();
        UpdateKeyLabels();
        RefreshStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!_selfElevated)
        {
            _note.Text = "Клики в окно от администратора требуют таких же прав у кликера.";
        }

        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        var toggle = Program.ToggleHotkeys[_hotkey.SelectedIndex];
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
                if (id == _toggleHotkeyId)
                {
                    _registeredToggleIndex = _hotkey.SelectedIndex;
                }
            }
            else
            {
                var error = Marshal.GetLastPInvokeError();
                if (failure == 0)
                {
                    failure = error;
                }

                if (id != _toggleHotkeyId && _otherHotkeyFailure == 0)
                {
                    _otherHotkeyFailure = error;
                }
            }
        }

        _hotkeyFailure = failure;
        _deliveryBlocked = null;
        ReportHotkeys(failure);
    }

    /// Нижняя строка всегда отвечает за клавиши: либо список комбинаций, либо почему они молчат.
    private void ReportHotkeys(int failureCode)
    {
        _hints.ForeColor = failureCode == 0 ? SystemColors.ControlText : Color.Firebrick;
        _hints.Text = failureCode == 0 ? HintsText() : $"Горячие клавиши заняты: {Program.Win32Message(failureCode)}";
    }

    private void ApplyToggleHotkey()
    {
        if (!IsHandleCreated || _hotkey.SelectedIndex == _registeredToggleIndex)
        {
            return;
        }

        var toggle = Program.ToggleHotkeys[_hotkey.SelectedIndex];
        var nextId = _toggleHotkeyId == Program.HotToggle ? Program.HotToggleAlternate : Program.HotToggle;

        // Второй id позволяет проверить новое сочетание, не теряя рабочую клавишу остановки.
        if (!Native.RegisterHotKey(Handle, nextId, toggle.Mods | Program.ModNoRepeat, toggle.Vk))
        {
            var error = Marshal.GetLastPInvokeError();
            if (_registeredToggleIndex >= 0)
            {
                _hotkey.SelectedIndex = _registeredToggleIndex;
                _note.Text = $"Смена отклонена ({error}); работает {ToggleKeyLabel}.";
            }
            else
            {
                _hotkeyFailure = error;
                ReportHotkeys(error);
                UpdateKeyLabels();
            }

            return;
        }

        Native.UnregisterHotKey(Handle, _toggleHotkeyId);
        _toggleHotkeyId = nextId;
        _registeredToggleIndex = _hotkey.SelectedIndex;
        _hotkeyFailure = _otherHotkeyFailure;
        _deliveryBlocked = null;
        Program.SaveToggleIndex(_registeredToggleIndex);

        if (_engine is not null)
        {
            _engine.ToggleKeyName = toggle.Label;
        }

        ReportHotkeys(_hotkeyFailure);
        UpdateKeyLabels();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, _toggleHotkeyId);
        Native.UnregisterHotKey(Handle, Program.HotCoords);
        Native.UnregisterHotKey(Handle, Program.HotQuit);
        _registeredToggleIndex = -1;
        base.OnHandleDestroyed(e);
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
        _capture.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayMenu.Dispose();
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
        if (_capture.Enabled)
        {
            _note.Text = "Сначала дождись захвата точки.";
            ShowBalloon(_note.Text);
            return;
        }

        // Убираем окно с дороги до первого клика: иначе первый же синтетический клик
        // приходится в собственные контролы. Возврат — клик по значку в трее или панель.
        WindowState = FormWindowState.Minimized;

        _current = BuildSettings();
        _lastClicks = 0;
        _cts = new CancellationTokenSource();
        _engine = new Engine(_current, _cts)
        {
            ToggleKeyName = ToggleKeyLabel,
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

        UpdateKeyLabels();
        _note.Text = _current.Count > 0
            ? $"Лимит: {_current.Count} — после него кликер сам погаснет, это не поломка."
            : $"Клики идут без лимита — {ToggleKeyLabel} или кнопка остановят.";

        if (!_current.HasTarget)
        {
            _note.Text = "Кликер бьёт туда, где курсор: держи его вне этого окна.";
        }

        ShowBalloon($"Включён и свёрнут. {ToggleKeyLabel} — стоп, клик по значку в трее — окно.");
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
        UpdateKeyLabels();
    }

    private string ToggleKeyLabel => Program.ToggleHotkeys[_hotkey.SelectedIndex].Label;

    private string StartLabel(bool running) => (running ? "⏹ Стоп (" : "▶ Старт (") + ToggleKeyLabel + ")";

    /// Кнопка и пункт меню в трее всегда показывают ту клавишу, которая сейчас зарегистрирована.
    private void UpdateKeyLabels()
    {
        var label = StartLabel(_engine is not null);
        _toggle.Text = label;
        _trayToggle.Text = label;
    }

    private string HintsText() =>
        $"{ToggleKeyLabel} — старт/стоп    Ctrl+Alt+F8 — координаты    Ctrl+Alt+F10 — выход";

    /// Курсор в момент клика по кнопке стоит на самой кнопке, поэтому точку запоминаем не сразу,
    /// а через паузу: за три секунды курсор успевает уйти в нужное место.
    private void BeginCapture()
    {
        _capture.Stop();
        _note.Text = "Три секунды: поставь курсор в точку клика.";

        // Из трея окно свёрнуто и _note невидим — поэтому то же самое уходит во всплывашку.
        ShowBalloon("Три секунды: поставь курсор в точку клика.");
        _capture.Start();
    }

    private void RestoreFromTray()
    {
        WindowState = FormWindowState.Normal;
        Activate();
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
        _capture.Stop();
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
        if (_hotkeyFailure != 0 || _selfElevated)
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
            : HintsText();
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
