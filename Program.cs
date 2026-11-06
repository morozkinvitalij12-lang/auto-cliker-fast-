using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AutoClicker;

internal static class Program
{
    internal const uint ModNoRepeat = 0x4000;

    /// Голый F3 игра перехватывает сама и его же держат другие окна кликера.
    /// Ctrl+Alt+F3 не конфликтует ни с чем и не крадёт клавишу у игры.
    internal const uint ModCtrlAlt = 0x0002 | 0x0008;
    internal const uint VkF3 = 0x72;
    internal const uint VkF8 = 0x77;
    internal const uint VkF10 = 0x79;
    internal const int HotToggle = 1;
    internal const int HotCoords = 2;
    internal const int HotQuit = 3;

    /// Метка в dwExtraInfo каждого собственного клика: по ней окно отличает клик кликера
    /// от клика живой мыши и не путает их при остановке.
    internal static readonly IntPtr OwnClickTag = new(0x514B434C);

    private const string SingleInstanceName = "Local\\autoclicker-single-instance";

    private static Mutex? _singleInstance;

    /// Второе окно кликера перехватить Ctrl+Alt+F3 не может — оно занято первым, и пользователь
    /// видит «клавиша не работает». Поэтому дубликат не запускаем, а объясняем, что закрыть.
    private static bool AlreadyRunning()
    {
        _singleInstance = new Mutex(initiallyOwned: false, SingleInstanceName, out var createdNew);
        return !createdNew;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Dictionary<string, string> options;
        Settings settings;
        try
        {
            options = ParseArgs(args);
            if (options.ContainsKey("help"))
            {
                PrintUsage();
                return 0;
            }

            settings = Settings.From(options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        // Физические пиксели вместо масштабированных: иначе на экране с масштабом 125–150%
        // координаты из --x/--y и вывод F8 не совпадают с реальным положением курсора.
        if (!Native.SetProcessDpiAwarenessContext(Native.DpiAwarePerMonitorV2))
        {
            Native.SetProcessDPIAware();
        }

        if (AlreadyRunning())
        {
            const string alreadyRunningText = "Автокликер уже запущен.\n\n"
                + "Второе окно не сможет перехватить Ctrl+Alt+F3 — клавиша занята первым запуском, "
                + "и будет казаться, что она не работает.\n\n"
                + "Найди первое окно (значок в трее) и закрой его, либо нажми в нём Ctrl+Alt+F10.";

            if (args.Length == 0)
            {
                MessageBox.Show(alreadyRunningText, "Автокликер", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                Console.Error.WriteLine(alreadyRunningText.Replace("\n\n", "\n"));
            }

            return 3;
        }

        if (args.Length == 0)
        {
            Native.ShowWindow(Native.GetConsoleWindow(), Native.SwHide);
            Application.Run(new MainForm(settings));
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        var engine = new Engine(settings, shutdown);

        // Marshal.GetLastWin32Error здесь отдаёт 0 — маршаллинг P/Invoke затирает код ошибки,
        // поэтому читаем его напрямую и расшифровыв сами.
        var hotkeyFailures = new List<int>();
        foreach (var (id, vk) in new (int, uint)[] { (HotToggle, VkF3), (HotCoords, VkF8), (HotQuit, VkF10) })
        {
            if (!Native.RegisterHotKey(IntPtr.Zero, id, ModCtrlAlt | ModNoRepeat, vk))
            {
                hotkeyFailures.Add(Marshal.GetLastPInvokeError());
            }
        }

        if (hotkeyFailures.Count > 0)
        {
            Console.Error.WriteLine($"Не удалось зарегистрировать горячие клавиши: {Win32Message(hotkeyFailures[0])}.");
            return 2;
        }

        Console.WriteLine(settings.Describe());
        Console.WriteLine("Ctrl+Alt+F3 — включить/выключить   Ctrl+Alt+F8 — показать курсор   Ctrl+Alt+F10 — выход");

        if (settings.Count > 0)
        {
            engine.Toggle();
            Console.WriteLine($"▶ {Plural(settings.Count, "клик", "клика", "кликов")} с интервалом {settings.IntervalMs} мс");
        }

        var worker = new Thread(engine.Run) { IsBackground = true, Name = "clicks" };
        worker.Start();

        var message = new Msg();
        while (!shutdown.IsCancellationRequested)
        {
            while (Native.PeekMessageW(out message, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                if (message.Message == Native.WmQuit)
                {
                    shutdown.Cancel();
                    break;
                }

                if (message.Message == Native.WmHotkey)
                {
                    HandleHotkey((int)message.WParam, settings, engine);
                }
            }

            Thread.Sleep(20);
        }

        Native.UnregisterHotKey(IntPtr.Zero, HotToggle);
        Native.UnregisterHotKey(IntPtr.Zero, HotCoords);
        Native.UnregisterHotKey(IntPtr.Zero, HotQuit);

        Console.WriteLine($"\nОстановлено. Кликов сделано: {engine.Clicks}.");
        return 0;
    }

    private static void HandleHotkey(int id, Settings settings, Engine engine)
    {
        switch (id)
        {
            case HotToggle:
                engine.Toggle();
                Console.WriteLine(engine.Running
                    ? $"▶ клик каждые {settings.IntervalMs} мс"
                    : $"⏸ пауза (сделано {engine.Clicks})");
                break;

            case HotCoords:
                if (Native.GetCursorPos(out var point))
                {
                    Console.WriteLine($"Курсор: X={point.X} Y={point.Y} — например: --x {point.X} --y {point.Y}");
                }
                else
                {
                    Console.Error.WriteLine("Не удалось прочитать позицию курсора.");
                }
                break;

            case HotQuit:
                engine.Shutdown();
                break;
        }
    }

    internal static string Win32Message(int code) => code switch
    {
        1409 => "клавиша уже занята другим приложением или вторым запуском кликера",
        1407 => "такое сочетание клавиш не найдено",
        _ => $"Win32 error {code}",
    };

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Неожиданный аргумент: {args[i]}");
            }

            var name = args[i][2..];
            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                result[name[..separator]] = name[(separator + 1)..];
                continue;
            }

            if (name.Equals("help", StringComparison.Ordinal) || name.Equals("h", StringComparison.Ordinal))
            {
                result["help"] = string.Empty;
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Аргументу --{name} не хватает значения");
            }

            result[name] = args[++i];
        }

        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            autoclicker — шлёт клики мышью через Win32 SendInput

            Использование:
              autoclicker [--interval 500] [--button left|right|middle] [--count 0]
                          [--x <пикс>] [--y <пикс>]

            Параметры:
              --interval <мс>  пауза между кликами, по умолчанию 500, минимум 10
              --button <имя>   left (по умолчанию) | right | middle
              --count <n>      сколько кликов сделать; 0 = без лимита (по умолчанию).
                               С --count старт сразу, без него — по Ctrl+Alt+F3, процесс ждёт хоткеи.
              --x/--y <пикс>   фиксированная точка: курсор устанавливается туда перед каждым
                               кликом. Без --x/--y клик идёт туда, где сейчас курсор.

            Горячие клавиши (работают, когда активно любое окно):
              Ctrl+Alt+F3    включить / выключить клики
              Ctrl+Alt+F8    вывести текущие координаты курсора
              Ctrl+Alt+F10   выйти

            Замечание: сочетания с Ctrl+Alt выбраны специально — голый F3 перехватывает сама
            игра, и тогда казалось, что клавиша «не работает».

            Примеры:
              autoclicker --interval 250
              autoclicker --button right --count 10 --x 820 --y 540
            """);
    }

    private static int GetInt(Dictionary<string, string> options, string name, int fallback)
    {
        if (!options.TryGetValue(name, out var raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"--{name} ожидал целое число, получено: {raw}");
        }

        return value;
    }

    private static string Plural(long value, string one, string few, string many)
    {
        var mod10 = value % 10;
        var mod100 = value % 100;
        var form = mod10 == 1 && mod100 != 11
            ? one
            : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? few : many;
        return $"{value} {form}";
    }

    internal sealed class Settings
    {
        public required int IntervalMs { get; init; }
        public required uint DownFlag { get; init; }
        public required uint UpFlag { get; init; }
        public required string ButtonName { get; init; }
        public required int Count { get; init; }
        public required bool HasTarget { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }

        public static Settings From(Dictionary<string, string> options)
        {
            var interval = GetInt(options, "interval", 500);
            if (interval < 10)
            {
                throw new ArgumentException("--interval не может быть меньше 10 мс");
            }

            var (down, up, label) = (options.TryGetValue("button", out var button) ? button : "left").ToLowerInvariant() switch
            {
                "left" => (Native.LeftDown, Native.LeftUp, "left"),
                "right" => (Native.RightDown, Native.RightUp, "right"),
                "middle" => (Native.MiddleDown, Native.MiddleUp, "middle"),
                _ => throw new ArgumentException($"Неизвестная кнопка: {button} (left|right|middle)"),
            };

            var count = GetInt(options, "count", 0);
            if (count < 0)
            {
                throw new ArgumentException("--count не может быть отрицательным");
            }

            var x = GetInt(options, "x", 0);
            var y = GetInt(options, "y", 0);
            var hasTarget = options.ContainsKey("x") || options.ContainsKey("y");

            return new Settings
            {
                IntervalMs = interval,
                DownFlag = down,
                UpFlag = up,
                ButtonName = label,
                Count = count,
                HasTarget = hasTarget,
                X = x,
                Y = y,
            };
        }

        public string Describe()
        {
            var target = HasTarget ? $"в точку {X},{Y}" : "там, где курсор";
            var limit = Count > 0 ? Plural(Count, "клик", "клика", "кликов") : "без лимита";
            return $"Кнопка: {ButtonName}, интервал: {IntervalMs} мс, {limit}, клик {target}.";
        }
    }

    internal sealed class Engine
    {
        private readonly Settings _settings;
        private readonly CancellationTokenSource _shutdown;
        private readonly Input[] _click = new Input[2];
        private bool _blockedReported;

        public Engine(Settings settings, CancellationTokenSource shutdown)
        {
            _settings = settings;
            _shutdown = shutdown;

            _click[0] = ClickInput(settings.DownFlag);
            _click[1] = ClickInput(settings.UpFlag);
        }

        private long _clicks;
        private volatile bool _running;

        public long Clicks => Interlocked.Read(ref _clicks);

        public bool Running
        {
            get => _running;
            private set => _running = value;
        }

        public void Toggle() => _running = !_running;

        public void Shutdown() => _shutdown.Cancel();

        public Action<string>? Report { get; set; }

        /// Движок сам выключается на лимите — без этого сообщения выглядит, будто он «умер сам».
        public Action<string>? LimitReached { get; set; }

        private void Notify(string message) => (Report ?? Console.Error.WriteLine)(message);

        public void Run()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (!Running)
                {
                    Thread.Sleep(25);
                    continue;
                }

                if (_settings.HasTarget)
                {
                    Native.SetCursorPos(_settings.X, _settings.Y);
                }

                var sent = Native.SendInput((uint)_click.Length, _click, Marshal.SizeOf<Input>());
                if (sent != (uint)_click.Length && !_blockedReported)
                {
                    _blockedReported = true;
                    Notify("Windows отклонил SendInput — обычно это значит, что активное окно запущено с правами выше (admin) или защищено античитом.");
                }

                var clicks = Interlocked.Increment(ref _clicks);

                if (_settings.Count > 0 && clicks >= _settings.Count)
                {
                    Running = false;
                    _shutdown.Cancel();
                    (LimitReached ?? Notify)(
                        $"Лимит исчерпан: {Plural(_settings.Count, "клик", "клика", "кликов")}."
                        + " Нажми Ctrl+Alt+F3 или Старт — снова включится.");
                    continue;
                }

                SleepBetweenClicks(_settings.IntervalMs);
            }
        }

        private void SleepBetweenClicks(int ms)
        {
            var remaining = ms;
            while (remaining > 0 && Running && !_shutdown.IsCancellationRequested)
            {
                var slice = Math.Min(20, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        private static Input ClickInput(uint flags) => new()
        {
            Type = Native.InputMouse,
            Mouse = new MouseInput { DwFlags = flags, DwExtraInfo = OwnClickTag },
        };
    }

    internal static class Native
    {
        public const uint InputMouse = 0;
        public const uint LeftDown = 0x0002;
        public const uint LeftUp = 0x0004;
        public const uint RightDown = 0x0008;
        public const uint RightUp = 0x0010;
        public const uint MiddleDown = 0x0020;
        public const uint MiddleUp = 0x0040;

        public const uint PmRemove = 0x0001;
        public const uint WmQuit = 0x0012;
        public const uint WmHotkey = 0x0400;

        private const uint WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
        private const uint WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
        private const uint WmMButtonDown = 0x0207, WmMButtonUp = 0x0208;

        /// Сообщения от любой кнопки мыши — именно на них смотрим метку собственного клика.
        public static bool IsMouseButtonMessage(uint message) =>
            message is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp;

        [DllImport("user32.dll")]
        public static extern IntPtr GetMessageExtraInfo();

        public static readonly IntPtr DpiAwarePerMonitorV2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool PeekMessageW(out Msg msg, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("shell32.dll")]
        public static extern bool IsUserAnAdmin();

        public const int SwHide = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }
}
