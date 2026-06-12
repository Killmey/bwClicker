// .NET 8 (net8.0-windows), C# 12, Nullable enabled, WPF + WinForms (UseWindowsForms=true)
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ClickerApp
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string? moduleName);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public UNION input; }

        [StructLayout(LayoutKind.Explicit)]
        struct UNION
        {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        const uint InputMouse = 0;
        const uint InputKeyboard = 1;
        const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
        const uint KeyEventFUnicode = 0x0004, KeyEventFKeyUp = 0x0002;
        const ushort VkReturn = 0x0D, VkChatOpen = 0x54; // T
        const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004;
        const int HotkeyId = 9000;
        const int RejoinHotkeyId = 9001;
        const int MouseHookId = 14;
        const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
        const int WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
        const uint MouseInjected = 0x00000001;

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "clicker_config.json");

        readonly object stateLock = new();
        readonly Random rng = new();

        volatile bool running;
        Thread? clickThread;
        IntPtr windowHandle;
        IntPtr mouseHook;
        readonly LowLevelMouseProc mouseProc;
        bool initialized;
        bool recording;
        bool highResolutionTimer;
        volatile bool holdActiveLeft;
        volatile bool holdActiveRight;
        volatile bool physicalLeftDown;
        volatile bool physicalRightDown;

        int cps = 100;
        bool clickLeft;
        bool clickRight = true;
        bool holdMode;
        bool antiOn;
        int jitter = 10;
        int burstChance = 2;
        int burstMs = 70;
        int holdMs = 8;

        uint hotMod = ModCtrl;
        uint hotVk = 0x5A;
        string hotDisplay = "Z";
        int fontSize = 14;

        // Modules: Auto-Rejoin
        bool modulesAutoRejoinOn;
        uint rejoinHotMod;
        uint rejoinHotVk = 0x4C; // L
        string rejoinHotDisplay = "L";
        string rejoinLeaveCmd = "/leave";
        string rejoinJoinCmd = "/rejoin";
        int rejoinDelayMs = 3000; // Зберігаємо в мс

        // Елементи динамічного інтерфейсу модулів
        ToggleButton? modAntiToggle;
        TextBlock? modAntiSummary;
        ToggleButton? modRejoinToggle;
        TextBlock? modRejoinSummary;

        ThemeConfig theme = new();

        static ControlTemplate? _noFocusBorderTemplate;
        static ControlTemplate NoFocusBorderTemplate
        {
            get
            {
                if (_noFocusBorderTemplate != null) return _noFocusBorderTemplate;
                _noFocusBorderTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate " +
                    "  xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "  xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
                    "  TargetType='{x:Type TextBox}'>" +
                    "<Border Background='{TemplateBinding Background}' " +
                    "        BorderBrush='{TemplateBinding BorderBrush}' " +
                    "        BorderThickness='{TemplateBinding BorderThickness}' " +
                    "        CornerRadius='4'>" +
                    "  <ScrollViewer x:Name='PART_ContentHost' " +
                    "                Margin='{TemplateBinding Padding}' " +
                    "                VerticalAlignment='{TemplateBinding VerticalContentAlignment}' " +
                    "                HorizontalScrollBarVisibility='Hidden' " +
                    "                VerticalScrollBarVisibility='Hidden'/>" +
                    "</Border>" +
                    "</ControlTemplate>");
                return _noFocusBorderTemplate;
            }
        }

        static void FixTextBoxFocus(TextBox box)
        {
            box.FocusVisualStyle = null;
            box.Template = NoFocusBorderTemplate;
        }

        public MainWindow()
        {
            try
            {
                mouseProc = MouseHookCallback;
                InitializeComponent();
                TrySetWindowIcon();
                LoadConfig();
                ApplyConfigToUi();
                ApplyTheme();
                Loaded += (_, _) => AttachHoverAnimations(this);
                initialized = true;
                ComponentDispatcher.ThreadFilterMessage += OnHotkeyMessage;
            }
            catch (Exception ex) { Log(ex); throw; }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            windowHandle = new WindowInteropHelper(this).Handle;
            RegisterCurrentHotkey();
            RegisterRejoinHotkey();
            InstallMouseHook();
        }

        protected override void OnClosed(EventArgs e)
        {
            Stop();
            SaveConfig();
            UnregisterCurrentHotkey();
            UnregisterRejoinHotkey();
            UninstallMouseHook();
            ComponentDispatcher.ThreadFilterMessage -= OnHotkeyMessage;
            base.OnClosed(e);
        }

        static void Log(Exception ex)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "clicker_error.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch { }
        }

        void TrySetWindowIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                    Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
            }
            catch { }
        }

        void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg == null) return;

                cps = Math.Max(1, cfg.Cps);
                if (cfg.ClickLeft.HasValue || cfg.ClickRight.HasValue)
                {
                    clickLeft = cfg.ClickLeft == true;
                    clickRight = cfg.ClickRight != false;
                }
                else
                {
                    clickLeft = string.Equals(cfg.Mode, "LMB", StringComparison.OrdinalIgnoreCase);
                    clickRight = !clickLeft;
                }

                if (!clickLeft && !clickRight)
                    clickRight = true;

                holdMode = string.Equals(cfg.RunMode, "Hold", StringComparison.OrdinalIgnoreCase);
                antiOn = cfg.AntiDetect.On;
                jitter = Clamp(cfg.AntiDetect.Jitter, 0, 50);
                burstChance = Clamp(cfg.AntiDetect.BurstChance, 0, 30);
                burstMs = Clamp(cfg.AntiDetect.BurstMs, 50, 500);
                holdMs = Clamp(cfg.AntiDetect.HoldMs, 0, 40);
                hotMod = cfg.Hotkey.Modifiers;
                hotVk = cfg.Hotkey.VirtualKey == 0 ? 0x5A : cfg.Hotkey.VirtualKey;
                hotDisplay = string.IsNullOrWhiteSpace(cfg.Hotkey.Display) ? KeyName(hotVk) : cfg.Hotkey.Display;
                theme = cfg.Theme ?? new ThemeConfig();
                fontSize = Clamp(cfg.FontSize, 11, 18);

                var rejoin = cfg.Modules?.AutoRejoin ?? new AutoRejoinConfig();
                modulesAutoRejoinOn = rejoin.On;
                rejoinHotMod = rejoin.Modifiers;
                rejoinHotVk = rejoin.VirtualKey == 0 ? 0x4C : rejoin.VirtualKey;
                rejoinHotDisplay = string.IsNullOrWhiteSpace(rejoin.Display) ? KeyName(rejoinHotVk) : rejoin.Display;
                rejoinLeaveCmd = string.IsNullOrWhiteSpace(rejoin.LeaveCommand) ? "/leave" : rejoin.LeaveCommand;
                rejoinJoinCmd = string.IsNullOrWhiteSpace(rejoin.RejoinCommand) ? "/rejoin" : rejoin.RejoinCommand;
                
                rejoinDelayMs = rejoin.DelayMs > 0
                    ? Clamp(rejoin.DelayMs, 0, 60000)
                    : Clamp(rejoin.DelaySeconds, 0, 60) * 1000;
            }
            catch { }
        }

        void SaveConfig()
        {
            try
            {
                RefreshRuntimeState();
                var cfg = new AppConfig
                {
                    Cps = cps,
                    Mode = clickLeft && !clickRight ? "LMB" : "RMB",
                    ClickLeft = clickLeft,
                    ClickRight = clickRight,
                    RunMode = holdMode ? "Hold" : "Stay",
                    Hotkey = new HotkeyConfig { Modifiers = hotMod, VirtualKey = hotVk, Display = hotDisplay },
                    AntiDetect = new AntiDetectConfig
                    {
                        On = antiOn,
                        Jitter = jitter,
                        BurstChance = burstChance,
                        BurstMs = burstMs,
                        HoldMs = holdMs
                    },
                    Theme = theme,
                    FontSize = fontSize,
                    Modules = new ModulesConfig
                    {
                        AutoRejoin = new AutoRejoinConfig
                        {
                            On = modulesAutoRejoinOn,
                            Modifiers = rejoinHotMod,
                            VirtualKey = rejoinHotVk,
                            Display = rejoinHotDisplay,
                            LeaveCommand = rejoinLeaveCmd,
                            RejoinCommand = rejoinJoinCmd,
                            DelayMs = rejoinDelayMs,
                            DelaySeconds = rejoinDelayMs / 1000
                        }
                    }
                };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOptions));
            }
            catch { }
        }

        void ApplyConfigToUi()
        {
            TxtCps.Text = cps.ToString(Invariant);
            BtnStayMode.IsChecked = !holdMode;
            BtnHoldMode.IsChecked = holdMode;
            SetMouseButtons(clickLeft, clickRight, save: false);
            SetRunMode(holdMode, save: false);
            TxtHotkey.Text = HotkeyText();
            RefreshModulesUi();
            UpdateMiniUi();
            SetUi(running);
        }

        void RefreshRuntimeState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshRuntimeState);
                return;
            }

            if (!int.TryParse(TxtCps.Text, out var parsed) || parsed < 1)
                parsed = 100;

            lock (stateLock)
            {
                cps = parsed;
                clickLeft = BtnLmb.IsChecked == true;
                clickRight = BtnRmb.IsChecked == true;
                holdMode = BtnHoldMode.IsChecked == true;
            }
        }

        void ApplyTheme()
        {
            Resources["BgBrush"] = BrushOf(theme.Background);
            Resources["SurfaceBrush"] = BrushOf(theme.Surface);
            Resources["AccentBrush"] = BrushOf(theme.Accent);
            Resources["DangerBrush"] = BrushOf(theme.Danger);
            Resources["TextBrush"] = BrushOf(theme.Text);
            Resources["MutedBrush"] = BrushOf(theme.Muted);
            Resources["LineBrush"] = BrushOf(theme.Line);
            Resources["ControlFontSize"] = (double)fontSize;
            SetMouseButtons(BtnLmb.IsChecked == true, BtnRmb.IsChecked == true, save: false);
            SetRunMode(BtnHoldMode.IsChecked == true, save: false);
            RefreshModulesUi();
            UpdateMiniUi();
            UpdateActiveModulesBadges();
            SetUi(running);
        }

        static SolidColorBrush BrushOf(string hex)
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch
            {
                return new SolidColorBrush(Color.FromRgb(0, 255, 136));
            }
        }

        Brush AccentBrush => (Brush)Resources["AccentBrush"];
        Brush SurfaceBrush => (Brush)Resources["SurfaceBrush"];
        Brush BgBrush => (Brush)Resources["BgBrush"];
        Brush TextBrush => (Brush)Resources["TextBrush"];
        Brush MutedBrush => (Brush)Resources["MutedBrush"];
        Brush LineBrush => (Brush)Resources["LineBrush"];
        Brush DangerBrush => (Brush)Resources["DangerBrush"];

        void Loop()
        {
            long nextTick = 0;

            while (running)
            {
                int localCps, localJitter, localBurstChance, localBurstMs, localHoldMs;
                bool localLeft, localRight, localAnti, localHoldMode;
                lock (stateLock)
                {
                    localCps = Math.Max(1, cps);
                    localLeft = clickLeft;
                    localRight = clickRight;
                    localAnti = antiOn;
                    localHoldMode = holdMode;
                    localJitter = jitter;
                    localBurstChance = burstChance;
                    localBurstMs = burstMs;
                    localHoldMs = holdMs;
                }

                if (!localLeft && !localRight)
                    localRight = true;

                if (localHoldMode)
                {
                    var leftActive = localLeft && holdActiveLeft;
                    var rightActive = localRight && holdActiveRight;
                    if (!leftActive && !rightActive)
                    {
                        nextTick = 0;
                        Thread.Sleep(1);
                        continue;
                    }
                    localLeft = leftActive;
                    localRight = rightActive;
                }

                var interval = 1.0 / localCps;
                if (localAnti)
                {
                    var spread = localJitter / 100.0;
                    interval = Math.Max(0.0001, interval + (rng.NextDouble() * 2 - 1) * spread * interval);
                }

                var intervalTicks = Math.Max(1, (long)Math.Round(interval * Stopwatch.Frequency));
                var now = Stopwatch.GetTimestamp();
                if (nextTick == 0)
                    nextTick = now + intervalTicks;
                else if (nextTick < now - intervalTicks)
                    nextTick = now + intervalTicks;

                PreciseSleepUntil(nextTick);

                if (localLeft) Click(LeftDown);
                if (localRight) Click(RightDown);
                if (localAnti && localHoldMs > 0)
                    PreciseSleep(rng.NextDouble() * localHoldMs / 1000.0);
                if (localRight) Click(RightUp);
                if (localLeft) Click(LeftUp);

                if (localAnti && rng.Next(1, 101) <= localBurstChance)
                    PreciseSleep(localBurstMs / 1000.0);

                nextTick += intervalTicks;
            }
        }

        static void Click(uint flag)
        {
            var inputs = new INPUT[1];
            inputs[0].type = InputMouse;
            inputs[0].input.mouse.dwFlags = flag;
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }

        static void SendUnicodeChar(char c)
        {
            var inputs = new INPUT[2];
            inputs[0].type = InputKeyboard;
            inputs[0].input.keyboard = new KEYBDINPUT { wScan = c, dwFlags = KeyEventFUnicode };
            inputs[1].type = InputKeyboard;
            inputs[1].input.keyboard = new KEYBDINPUT { wScan = c, dwFlags = KeyEventFUnicode | KeyEventFKeyUp };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        static void SendVirtualKey(ushort vk)
        {
            var inputs = new INPUT[2];
            inputs[0].type = InputKeyboard;
            inputs[0].input.keyboard = new KEYBDINPUT { wVk = vk };
            inputs[1].type = InputKeyboard;
            inputs[1].input.keyboard = new KEYBDINPUT { wVk = vk, dwFlags = KeyEventFKeyUp };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        static void SendChatCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            SendVirtualKey(VkChatOpen);
            Thread.Sleep(60);

            foreach (var c in command)
            {
                SendUnicodeChar(c);
                Thread.Sleep(8);
            }

            Thread.Sleep(40);
            SendVirtualKey(VkReturn);
        }

        static void PreciseSleep(double seconds)
        {
            if (seconds <= 0) return;
            PreciseSleepUntil(Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency));
        }

        static void PreciseSleepUntil(long targetTick)
        {
            while (true)
            {
                var remaining = (targetTick - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                if (remaining <= 0) return;

                if (remaining > 0.006)
                    Thread.Sleep(Math.Max(1, (int)(remaining * 1000) - 2));
                else if (remaining > 0.0015)
                    Thread.Sleep(0);
                else
                    Thread.SpinWait(80);
            }
        }

        void Start()
        {
            if (running) return;
            TxtCps_LostFocus(this, new RoutedEventArgs());
            RefreshRuntimeState();
            holdActiveLeft = false;
            holdActiveRight = false;
            physicalLeftDown = false;
            physicalRightDown = false;
            EnableHighResolutionTimer();
            running = true;
            SetUi(true);
            clickThread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
            clickThread.Start();
        }

        void Stop()
        {
            if (!running) return;
            running = false;
            holdActiveLeft = false;
            holdActiveRight = false;
            physicalLeftDown = false;
            physicalRightDown = false;
            DisableHighResolutionTimer();
            SetUi(false);
        }

        void Toggle()
        {
            if (running) Stop();
            else Start();
        }

        void SetUi(bool on)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetUi(on));
                return;
            }

            StatusDot.Fill = on ? AccentBrush : MutedBrush;
            StatusText.Text = on ? (holdMode && !(holdActiveLeft || holdActiveRight) ? "ARMED" : "ACTIVE") : "IDLE";
            StatusText.Foreground = on ? AccentBrush : MutedBrush;
            BtnStart.Background = on ? MutedBrush : AccentBrush;
            BtnStart.Foreground = BgBrush;
            BtnStop.Background = on ? DangerBrush : LineBrush;
            BtnStop.Foreground = on ? BgBrush : DangerBrush;
        }

        void EnableHighResolutionTimer()
        {
            if (highResolutionTimer) return;
            if (timeBeginPeriod(1) == 0)
                highResolutionTimer = true;
        }

        void DisableHighResolutionTimer()
        {
            if (!highResolutionTimer) return;
            timeEndPeriod(1);
            highResolutionTimer = false;
        }

        void InstallMouseHook()
        {
            if (mouseHook != IntPtr.Zero) return;
            var moduleHandle = IntPtr.Zero;
            try
            {
                using var process = Process.GetCurrentProcess();
                moduleHandle = GetModuleHandle(process.MainModule?.ModuleName);
            }
            catch { }
            mouseHook = SetWindowsHookEx(MouseHookId, mouseProc, moduleHandle, 0);
        }

        void UninstallMouseHook()
        {
            if (mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }

        IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((data.flags & MouseInjected) == 0 && GetForegroundWindow() != windowHandle)
                {
                    var msg = wParam.ToInt32();
                    if (msg is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp)
                        HandlePhysicalMouse(msg);
                }
            }

            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        void HandlePhysicalMouse(int msg)
        {
            bool localHoldMode;
            lock (stateLock) localHoldMode = holdMode;

            if (!running || !localHoldMode)
                return;

            if (msg == WmLButtonDown) physicalLeftDown = true;
            if (msg == WmLButtonUp) physicalLeftDown = false;
            if (msg == WmRButtonDown) physicalRightDown = true;
            if (msg == WmRButtonUp) physicalRightDown = false;

            SetHoldActive(physicalLeftDown, physicalRightDown);
        }

        void SetHoldActive(bool left, bool right)
        {
            if (holdActiveLeft == left && holdActiveRight == right) return;
            holdActiveLeft = left;
            holdActiveRight = right;
            Dispatcher.BeginInvoke(new Action(() => SetUi(running)));
        }

        void AttachHoverAnimations(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ButtonBase button)
                    AttachHoverAnimation(button);
                AttachHoverAnimations(child);
            }
        }

        static void AttachHoverAnimation(ButtonBase button)
        {
            button.MouseEnter += (_, _) => AnimateButtonHover(button, hover: true, pressed: false);
            button.MouseLeave += (_, _) => AnimateButtonHover(button, hover: false, pressed: false);
            button.PreviewMouseDown += (_, _) => AnimateButtonHover(button, hover: false, pressed: true);
            button.PreviewMouseUp += (_, _) => AnimateButtonHover(button, hover: button.IsMouseOver, pressed: false);
        }

        static void AnimateButtonHover(UIElement element, bool hover, bool pressed)
        {
            double target = pressed ? 0.55 : hover ? 0.80 : 1.0;
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        void RegisterCurrentHotkey()
        {
            if (windowHandle == IntPtr.Zero || recording) return;
            UnregisterHotKey(windowHandle, HotkeyId);
            RegisterHotKey(windowHandle, HotkeyId, hotMod, hotVk);
        }

        void UnregisterCurrentHotkey()
        {
            if (windowHandle != IntPtr.Zero)
                UnregisterHotKey(windowHandle, HotkeyId);
        }

        void RegisterRejoinHotkey()
        {
            if (windowHandle == IntPtr.Zero) return;
            UnregisterHotKey(windowHandle, RejoinHotkeyId);
            if (modulesAutoRejoinOn && rejoinHotVk != 0)
                RegisterHotKey(windowHandle, RejoinHotkeyId, rejoinHotMod, rejoinHotVk);
        }

        void UnregisterRejoinHotkey()
        {
            if (windowHandle != IntPtr.Zero)
                UnregisterHotKey(windowHandle, RejoinHotkeyId);
        }

        void OnHotkeyMessage(ref MSG msg, ref bool handled)
        {
            if (msg.message != 0x0312) return;

            var id = msg.wParam.ToInt32();
            if (id == HotkeyId)
            {
                handled = true;
                if (!recording) Toggle();
            }
            else if (id == RejoinHotkeyId)
            {
                handled = true;
                TriggerAutoRejoin();
            }
        }

        // Фікс: Працює ТІЛЬКИ коли клікер активний (running == true)
        void TriggerAutoRejoin()
        {
            bool on;
            string leave, rejoin;
            int delayMs;
            lock (stateLock)
            {
                on = modulesAutoRejoinOn;
                leave = rejoinLeaveCmd;
                rejoin = rejoinJoinCmd;
                delayMs = rejoinDelayMs;
            }

            if (!on || !running) return;

            new Thread(() =>
            {
                SendChatCommand(leave);
                Thread.Sleep(Math.Max(0, delayMs));
                SendChatCommand(rejoin);
            })
            { IsBackground = true }.Start();
        }

        void BeginRecording()
        {
            recording = true;
            UnregisterCurrentHotkey();
            BtnRecord.Content = "СКАСУВАТИ";
            BtnRecord.Background = DangerBrush;
            BtnRecord.Foreground = BgBrush;
            TxtHotkey.Text = "Натисни комбінацію...";
            TxtHotkey.Foreground = DangerBrush;
            PreviewKeyDown += CaptureHotkey;
            Focus();
            Keyboard.Focus(BtnRecord);
        }

        void CancelRecording()
        {
            recording = false;
            PreviewKeyDown -= CaptureHotkey;
            BtnRecord.Content = "ЗАПИС";
            BtnRecord.Background = LineBrush;
            BtnRecord.Foreground = TextBrush;
            TxtHotkey.Text = HotkeyText();
            TxtHotkey.Foreground = AccentBrush;
            UpdateMiniUi();
            RegisterCurrentHotkey();
        }

        void CaptureHotkey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                CancelRecording();
                return;
            }

            if (IsModifierOnly(key)) return;

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            uint mods = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= ModCtrl;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= ModShift;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;

            hotMod = mods;
            hotVk = vk;
            hotDisplay = KeyToDisplay(key, vk);
            recording = false;
            PreviewKeyDown -= CaptureHotkey;
            BtnRecord.Content = "ЗАПИС";
            BtnRecord.Background = LineBrush;
            BtnRecord.Foreground = TextBrush;
            TxtHotkey.Text = HotkeyText();
            TxtHotkey.Foreground = AccentBrush;
            UpdateMiniUi();
            RegisterCurrentHotkey();
            SaveConfig();
        }

        static bool IsModifierOnly(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
                   Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

        string HotkeyText()
        {
            var text = "";
            if ((hotMod & ModCtrl) != 0) text += "Ctrl + ";
            if ((hotMod & ModShift) != 0) text += "Shift + ";
            if ((hotMod & ModAlt) != 0) text += "Alt + ";
            return text + hotDisplay;
        }

        static string KeyName(uint vk) => KeyToDisplay(KeyInterop.KeyFromVirtualKey((int)vk), vk);

        static string KeyToDisplay(Key key, uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            return key switch
            {
                Key.Space => "Space",
                Key.Return => "Enter",
                Key.Back => "Bksp",
                Key.Delete => "Del",
                Key.Insert => "Ins",
                Key.PageUp => "PgUp",
                Key.PageDown => "PgDn",
                Key.OemPlus => "=",
                Key.OemMinus => "-",
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                _ => key.ToString()
            };
        }

        void SetMouseButtons(bool left, bool right, bool save = true)
        {
            if (!left && !right) right = true;
            BtnLmb.IsChecked = left;
            BtnRmb.IsChecked = right;
            BtnLmb.Background = left ? AccentBrush : SurfaceBrush;
            BtnLmb.Foreground = left ? BgBrush : MutedBrush;
            BtnRmb.Background = right ? AccentBrush : SurfaceBrush;
            BtnRmb.Foreground = right ? BgBrush : MutedBrush;
            lock (stateLock)
            {
                clickLeft = left;
                clickRight = right;
            }
            UpdateMiniUi();
            if (save && initialized) SaveConfig();
        }

        void SetRunMode(bool hold, bool save = true)
        {
            if (!hold && BtnLmb != null && BtnRmb != null && BtnLmb.IsChecked == true && BtnRmb.IsChecked == true)
                SetMouseButtons(false, true, save: false);

            BtnStayMode.IsChecked = !hold;
            BtnHoldMode.IsChecked = hold;
            BtnStayMode.Foreground = !hold ? AccentBrush : MutedBrush;
            BtnHoldMode.Foreground = hold ? AccentBrush : MutedBrush;
            RunModeShell.Background = LineBrush;
            RunModeThumb.Background = SurfaceBrush;

            if (RunModeThumb.RenderTransform is TranslateTransform translate)
            {
                var target = hold ? 54.0 : 0.0;
                translate.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }

            lock (stateLock) holdMode = hold;
            holdActiveLeft = false;
            holdActiveRight = false;
            physicalLeftDown = false;
            physicalRightDown = false;
            SetUi(running);
            if (save && initialized) SaveConfig();
        }

        void SetAntiOn(bool on)
        {
            lock (stateLock) antiOn = on;
            RefreshModulesUi();
            if (initialized) SaveConfig();
        }

        void SetAutoRejoinOn(bool on)
        {
            modulesAutoRejoinOn = on;
            RegisterRejoinHotkey();
            RefreshModulesUi();
            if (initialized) SaveConfig();
        }

        string AntiSummaryText() => antiOn ? "Активна рандомізація" : "Вимкнено";
        string RejoinSummaryText() => modulesAutoRejoinOn ? $"{RejoinHotkeyText()} · {rejoinDelayMs} мс" : "Вимкнено";

        string RejoinHotkeyText()
        {
            var text = "";
            if ((rejoinHotMod & ModCtrl) != 0) text += "Ctrl + ";
            if ((rejoinHotMod & ModShift) != 0) text += "Shift + ";
            if ((rejoinHotMod & ModAlt) != 0) text += "Alt + ";
            return text + rejoinHotDisplay;
        }

        void RefreshModulesUi()
        {
            if (modAntiToggle != null)
            {
                var on = antiOn;
                modAntiToggle.IsChecked = on;
                modAntiToggle.Content = on ? "ВКЛ" : "ВИКЛ";
                modAntiToggle.Background = on ? AccentBrush : LineBrush;
                modAntiToggle.Foreground = on ? BgBrush : MutedBrush;
            }
            if (modAntiSummary != null) modAntiSummary.Text = AntiSummaryText();

            if (modRejoinToggle != null)
            {
                var on = modulesAutoRejoinOn;
                modRejoinToggle.IsChecked = on;
                modRejoinToggle.Content = on ? "ВКЛ" : "ВИКЛ";
                modRejoinToggle.Background = on ? AccentBrush : LineBrush;
                modRejoinToggle.Foreground = on ? BgBrush : MutedBrush;
            }
            if (modRejoinSummary != null) modRejoinSummary.Text = RejoinSummaryText();

            UpdateActiveModulesBadges();
        }

        void UpdateActiveModulesBadges()
        {
            if (ActiveModulesWrap == null) return;
            ActiveModulesWrap.Children.Clear();

            if (antiOn) ActiveModulesWrap.Children.Add(MakeModuleBadge("ANTI-DETECT"));
            if (modulesAutoRejoinOn) ActiveModulesWrap.Children.Add(MakeModuleBadge("AUTO-REJOIN"));

            ActiveModulesWrap.Visibility = ActiveModulesWrap.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        Border MakeModuleBadge(string text)
        {
            var border = new Border
            {
                Background = LineBrush,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 5, 3)
            };
            border.Child = new TextBlock
            {
                Text = text,
                Foreground = AccentBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                FontWeight = FontWeights.Bold
            };
            return border;
        }

        void UpdateMiniUi()
        {
            if (MiniMode == null || MiniCps == null || MiniHotkey == null) return;
            var left = BtnLmb?.IsChecked == true;
            var right = BtnRmb?.IsChecked == true;
            MiniMode.Text = left && right ? "ЛКМ+ПКМ" : left ? "ЛКМ" : "ПКМ";
            MiniCps.Text = $"{Math.Max(1, cps)} CPS";
            MiniHotkey.Text = HotkeyText();
            MiniMode.Foreground = running ? AccentBrush : MutedBrush;
        }

        static int Clamp(int val, int min, int max) => val < min ? min : (val > max ? max : val);

        // Фікс кнопки СТОП: Дозволяємо клікати по контролах без безумовного блокування
        void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (recording) return;

            var hit = VisualTreeHelper.HitTest(this, e.GetPosition(this))?.VisualHit;
            bool overControl = false;
            while (hit != null && hit != this)
            {
                if (hit is Button || hit is ToggleButton || hit is TextBox || hit is Slider)
                {
                    overControl = true;
                    break;
                }
                hit = VisualTreeHelper.GetParent(hit);
            }

            if (!overControl)
            {
                if (TxtCps != null && TxtCps.IsFocused)
                {
                    Focus();
                    e.Handled = true;
                }
            }
        }

        void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!recording) DragMove();
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void SldCps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!initialized) return;
            TxtCps.Text = ((int)SldCps.Value).ToString(Invariant);
            RefreshRuntimeState();
            UpdateMiniUi();
        }

        void TxtCps_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!initialized || !TxtCps.IsFocused) return;
            if (int.TryParse(TxtCps.Text, out var parsed) && parsed >= 1 && parsed <= 200)
            {
                SldCps.Value = parsed;
                lock (stateLock) cps = parsed;
                UpdateMiniUi();
            }
        }

        void TxtCps_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCps.Text, out var parsed) || parsed < 1)
                parsed = 100;
            parsed = Clamp(parsed, 1, 200);
            TxtCps.Text = parsed.ToString(Invariant);
            SldCps.Value = parsed;
            lock (stateLock) cps = parsed;
            UpdateMiniUi();
            SaveConfig();
        }

        void BtnLmb_Click(object sender, RoutedEventArgs e) => SetMouseButtons(true, false);
        void BtnRmb_Click(object sender, RoutedEventArgs e) => SetMouseButtons(false, true);

        void RunMode_Click(object sender, RoutedEventArgs e)
        {
            var hold = sender == BtnHoldMode;
            SetRunMode(hold);
        }

        void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (recording) CancelRecording();
            else BeginRecording();
        }

        void BtnStart_Click(object sender, RoutedEventArgs e) => Start();
        void BtnStop_Click(object sender, RoutedEventArgs e) => Stop();

        // ═══ ПОП-АП МЕНЮ МОДУЛІВ ═══
        void BtnModules_Click(object sender, RoutedEventArgs e)
        {
            var pop = new Popup
            {
                PlacementTarget = BtnModules,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = -250,
                VerticalOffset = 8,
                AllowsTransparency = true,
                StaysOpen = false
            };

            var border = new Border
            {
                Background = BgBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Width = 280,
                Padding = new Thickness(16)
            };

            var mainStack = new StackPanel();

            // --- Заголовок ---
            mainStack.Children.Add(new TextBlock
            {
                Text = "МОДУЛІ МОДИФІКАЦІЇ",
                Foreground = MutedBrush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // 1. Модуль ANTI-DETECT
            var rowAnti = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            rowAnti.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowAnti.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var stackAntiText = new StackPanel();
            stackAntiText.Children.Add(new TextBlock { Text = "ANTI-DETECT", Style = (Style)Resources["PanelTitle"] });
            modAntiSummary = new TextBlock { Text = AntiSummaryText(), Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            stackAntiText.Children.Add(modAntiSummary);
            rowAnti.Children.Add(stackAntiText);

            modAntiToggle = new ToggleButton { Height = 26, Style = (Style)Resources["SegmentButton"] };
            modAntiToggle.Click += (_, _) => SetAntiOn(modAntiToggle.IsChecked == true);
            Grid.SetColumn(modAntiToggle, 1);
            rowAnti.Children.Add(modAntiToggle);
            mainStack.Children.Add(rowAnti);

            mainStack.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 4, 0, 14) });

            // 2. Модуль АВТО-ПЕРЕЗАХІД (Змінено назву на «Авто-перезахід»)
            var rowRejoin = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            rowRejoin.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowRejoin.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var stackRejoinText = new StackPanel();
            stackRejoinText.Children.Add(new TextBlock { Text = "Авто-перезахід", Style = (Style)Resources["PanelTitle"] });
            modRejoinSummary = new TextBlock { Text = RejoinSummaryText(), Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            stackRejoinText.Children.Add(modRejoinSummary);
            rowRejoin.Children.Add(stackRejoinText);

            modRejoinToggle = new ToggleButton { Height = 26, Style = (Style)Resources["SegmentButton"] };
            modRejoinToggle.Click += (_, _) => SetAutoRejoinOn(modRejoinToggle.IsChecked == true);
            Grid.SetColumn(modRejoinToggle, 1);
            rowRejoin.Children.Add(modRejoinToggle);
            mainStack.Children.Add(rowRejoin);

            // --- Блок розширених налаштувань автоперезаходу ---
            var rejoinSettings = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            rejoinSettings.Children.Add(new TextBlock { Text = "ЗАТРИМКА (СЕКУНДИ)", Style = (Style)Resources["SectionLabel"], Margin = new Thickness(0, 4, 0, 5) });
            var delayGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            delayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            delayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            // Повзунок керує секундами (цілі значення)
            var sldDelay = new Slider { Minimum = 0, Maximum = 10, Value = Math.Round(rejoinDelayMs / 1000.0), IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = Center, Margin = new Thickness(0, 0, 10, 0) };
            var txtDelay = new TextBox { Text = (rejoinDelayMs / 1000.0).ToString("0.000", Invariant), Background = SurfaceBrush, Foreground = TextBrush, BorderThickness = new Thickness(1), BorderBrush = LineBrush, Height = 28, FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 4, 0), TextAlignment = TextAlignment.Center };
            FixTextBoxFocus(txtDelay);

            sldDelay.ValueChanged += (_, _) =>
            {
                if (txtDelay.IsFocused) return;
                int secs = (int)sldDelay.Value;
                rejoinDelayMs = secs * 1000;
                txtDelay.Text = sldDelay.Value.ToString("0.0", Invariant);
                if (modRejoinSummary != null) modRejoinSummary.Text = RejoinSummaryText();
                SaveConfig();
            };

            // Фікс: Ручне введення підтримує дробові числа через крапку (.) або кому (,)
            txtDelay.LostFocus += (_, _) =>
            {
                string raw = txtDelay.Text.Replace(',', '.').Trim();
                if (double.TryParse(raw, NumberStyles.Any, Invariant, out var parsedSecs) && parsedSecs >= 0 && parsedSecs <= 60)
                {
                    rejoinDelayMs = (int)Math.Round(parsedSecs * 1000.0);
                    int clampedSlider = Clamp((int)Math.Floor(parsedSecs), (int)sldDelay.Minimum, (int)sldDelay.Maximum);
                    sldDelay.Value = clampedSlider;
                }
                txtDelay.Text = (rejoinDelayMs / 1000.0).ToString(Invariant);
                if (modRejoinSummary != null) modRejoinSummary.Text = RejoinSummaryText();
                SaveConfig();
            };

            txtDelay.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    Forms.SendKeys.SendWait("{TAB}");
                    ke.Handled = true;
                }
            };

            delayGrid.Children.Add(sldDelay);
            Grid.SetColumn(txtDelay, 1);
            delayGrid.Children.Add(txtDelay);
            rejoinSettings.Children.Add(delayGrid);

            // Гаряча клавіша модуля
            rejoinSettings.Children.Add(new TextBlock { Text = "БІНД ДЛЯ ПЕРЕЗАХОДУ", Style = (Style)Resources["SectionLabel"], Margin = new Thickness(0, 4, 0, 5) });
            var hkGrid = new Grid();
            hkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var borderHk = new Border { Background = SurfaceBrush, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = LineBrush, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var txtHk = new TextBlock { Text = RejoinHotkeyText(), Foreground = AccentBrush, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            borderHk.Child = txtHk;
            hkGrid.Children.Add(borderHk);

            var btnHkRecord = new Button { Content = "ЗАПИС", Style = (Style)Resources["FlatButton"], Height = 30, Background = LineBrush, Foreground = TextBrush, FontSize = 11 };
            
            bool rejoinRecording = false;
            KeyEventHandler? rejoinCaptureHandler = null;

            rejoinCaptureHandler = (senderHk, khArgs) =>
            {
                khArgs.Handled = true;
                var k = khArgs.Key == Key.System ? khArgs.SystemKey : khArgs.Key;
                if (k == Key.Escape)
                {
                    rejoinRecording = false;
                    pop.PreviewKeyDown -= rejoinCaptureHandler;
                    btnHkRecord.Content = "ЗАПИС";
                    btnHkRecord.Background = LineBrush;
                    btnHkRecord.Foreground = TextBrush;
                    txtHk.Text = RejoinHotkeyText();
                    txtHk.Foreground = AccentBrush;
                    RegisterRejoinHotkey();
                    return;
                }
                if (IsModifierOnly(k)) return;
                var vkey = (uint)KeyInterop.VirtualKeyFromKey(k);
                if (vkey == 0) return;

                uint mds = 0;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mds |= ModCtrl;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mds |= ModShift;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mds |= ModAlt;

                rejoinHotMod = mds;
                rejoinHotVk = vkey;
                rejoinHotDisplay = KeyToDisplay(k, vkey);

                rejoinRecording = false;
                pop.PreviewKeyDown -= rejoinCaptureHandler;
                btnHkRecord.Content = "ЗАПИС";
                btnHkRecord.Background = LineBrush;
                btnHkRecord.Foreground = TextBrush;
                txtHk.Text = RejoinHotkeyText();
                txtHk.Foreground = AccentBrush;
                if (modRejoinSummary != null) modRejoinSummary.Text = RejoinSummaryText();
                RegisterRejoinHotkey();
                SaveConfig();
            };

            btnHkRecord.Click += (_, _) =>
            {
                if (rejoinRecording)
                {
                    rejoinRecording = false;
                    pop.PreviewKeyDown -= rejoinCaptureHandler;
                    btnHkRecord.Content = "ЗАПИС";
                    btnHkRecord.Background = LineBrush;
                    btnHkRecord.Foreground = TextBrush;
                    txtHk.Text = RejoinHotkeyText();
                    txtHk.Foreground = AccentBrush;
                    RegisterRejoinHotkey();
                }
                else
                {
                    rejoinRecording = true;
                    UnregisterRejoinHotkey();
                    btnHkRecord.Content = "СКАСУВАТИ";
                    btnHkRecord.Background = DangerBrush;
                    btnHkRecord.Foreground = BgBrush;
                    txtHk.Text = "Клавіша...";
                    txtHk.Foreground = DangerBrush;
                    pop.PreviewKeyDown += rejoinCaptureHandler;
                    pop.Focus();
                }
            };

            Grid.SetColumn(btnHkRecord, 1);
            hkGrid.Children.Add(btnHkRecord);
            rejoinSettings.Children.Add(hkGrid);

            mainStack.Children.Add(rejoinSettings);

            border.Child = mainStack;
            pop.Child = border;

            RefreshModulesUi();
            AttachHoverAnimations(border);

            pop.IsOpen = true;
        }

        // Внутрішні класи конфігурації
        sealed class AppConfig
        {
            public int Cps { get; set; } = 100;
            public string Mode { get; set; } = "RMB";
            public bool? ClickLeft { get; set; }
            public bool? ClickRight { get; set; }
            public string RunMode { get; set; } = "Stay";
            public int FontSize { get; set; } = 14;
            public HotkeyConfig Hotkey { get; set; } = new();
            public AntiDetectConfig AntiDetect { get; set; } = new();
            public ModulesConfig? Modules { get; set; }
            public ThemeConfig? Theme { get; set; }
        }

        sealed class ModulesConfig
        {
            public AutoRejoinConfig AutoRejoin { get; set; } = new();
        }

        sealed class AutoRejoinConfig
        {
            public bool On { get; set; }
            public uint Modifiers { get; set; }
            public uint VirtualKey { get; set; } = 0x4C;
            public string Display { get; set; } = "L";
            public string LeaveCommand { get; set; } = "/leave";
            public string RejoinCommand { get; set; } = "/rejoin";
            public int DelayMs { get; set; } = 3000;
            public int DelaySeconds { get; set; } = 3;
        }

        sealed class HotkeyConfig
        {
            public uint Modifiers { get; set; } = ModCtrl;
            public uint VirtualKey { get; set; } = 0x5A;
            public string Display { get; set; } = "Z";
        }

        sealed class AntiDetectConfig
        {
            public bool On { get; set; }
            public int Jitter { get; set; } = 10;
            public int BurstChance { get; set; } = 2;
            public int BurstMs { get; set; } = 70;
            public int HoldMs { get; set; } = 8;
        }

        sealed class ThemeConfig
        {
            public string Background { get; set; } = "#0D0D0D";
            public string Surface { get; set; } = "#1A1A1A";
            public string Accent { get; set; } = "#00FF88";
            public string Danger { get; set; } = "#FF3355";
            public string Text { get; set; } = "#E0E0E0";
            public string Muted { get; set; } = "#676767";
            public string Line { get; set; } = "#2A2A2A";
        }
    }
}
