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

        // Modules: global on/off switch (default ON). When OFF, no module runs
        // regardless of its individual state.
        bool modulesEnabled = true;

        // Modules: Anti-Detect
        bool antiIgnoreStartStop;

        // Modules: Auto-Rejoin
        bool modulesAutoRejoinOn;
        bool rejoinIgnoreStartStop;
        uint rejoinHotMod;
        uint rejoinHotVk = 0x4C; // L
        string rejoinHotDisplay = "L";
        string rejoinLeaveCmd = "/leave";
        string rejoinJoinCmd = "/rejoin";
        // stored in milliseconds; slider shows seconds, textbox shows ms
        int rejoinDelayMs = 3000;

        // Live refs into the open "MODULES" popup (null when closed)
        ToggleButton? modAntiToggle;
        TextBlock? modAntiSummary;
        ToggleButton? modRejoinToggle;
        TextBlock? modRejoinSummary;

        ThemeConfig theme = new();

        // ── Cached no-focus-border ControlTemplate for popup TextBoxes ──────────
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
                antiIgnoreStartStop = cfg.AntiDetect.IgnoreStartStop;
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
                modulesEnabled = cfg.Modules?.Enabled ?? true;
                modulesAutoRejoinOn = rejoin.On;
                rejoinIgnoreStartStop = rejoin.IgnoreStartStop;
                rejoinHotMod = rejoin.Modifiers;
                rejoinHotVk = rejoin.VirtualKey == 0 ? 0x4C : rejoin.VirtualKey;
                rejoinHotDisplay = string.IsNullOrWhiteSpace(rejoin.Display) ? KeyName(rejoinHotVk) : rejoin.Display;
                rejoinLeaveCmd = string.IsNullOrWhiteSpace(rejoin.LeaveCommand) ? "/leave" : rejoin.LeaveCommand;
                rejoinJoinCmd = string.IsNullOrWhiteSpace(rejoin.RejoinCommand) ? "/rejoin" : rejoin.RejoinCommand;
                // DelayMs takes priority; fall back to DelaySeconds * 1000 for backward compat
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
                        IgnoreStartStop = antiIgnoreStartStop,
                        Jitter = jitter,
                        BurstChance = burstChance,
                        BurstMs = burstMs,
                        HoldMs = holdMs
                    },
                    Theme = theme,
                    FontSize = fontSize,
                    Modules = new ModulesConfig
                    {
                        Enabled = modulesEnabled,
                        AutoRejoin = new AutoRejoinConfig
                        {
                            On = modulesAutoRejoinOn,
                            IgnoreStartStop = rejoinIgnoreStartStop,
                            Modifiers = rejoinHotMod,
                            VirtualKey = rejoinHotVk,
                            Display = rejoinHotDisplay,
                            LeaveCommand = rejoinLeaveCmd,
                            RejoinCommand = rejoinJoinCmd,
                            DelayMs = rejoinDelayMs,
                            DelaySeconds = rejoinDelayMs / 1000 // keep for compat
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
                    localAnti = antiOn && modulesEnabled;
                    localHoldMode = holdMode;
                    localJitter = jitter;
                    localBurstChance = burstChance;
                    localBurstMs = burstMs;
                    localHoldMs = holdMs;
                }

                if (!localLeft && !localRight)
                    localRight = true;

                // Pause synthetic clicks while our own window is focused, so the user
                // can reliably click UI buttons (e.g. STOP) without the autoclicker
                // interfering with the real mouse click.
                if (GetForegroundWindow() == windowHandle)
                {
                    nextTick = 0;
                    Thread.Sleep(1);
                    continue;
                }

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

        // ── Hover animations: minimal opacity-based (no brightening) ─────────────
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
            if (modulesEnabled && modulesAutoRejoinOn && rejoinHotVk != 0)
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

        void TriggerAutoRejoin()
        {
            bool modsEnabled, on, ignoreStartStop;
            string leave, rejoin;
            int delayMs;
            lock (stateLock)
            {
                modsEnabled = modulesEnabled;
                on = modulesAutoRejoinOn;
                ignoreStartStop = rejoinIgnoreStartStop;
                leave = rejoinLeaveCmd;
                rejoin = rejoinJoinCmd;
                delayMs = rejoinDelayMs;
            }

            // Global modules switch always wins. Otherwise the module must be on,
            // and the clicker must be running unless this module ignores that rule.
            if (!modsEnabled || !on) return;
            if (!running && !ignoreStartStop) return;

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
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
                or Key.System;

        string HotkeyText()
        {
            var text = "";
            if ((hotMod & ModCtrl) != 0) text += "Ctrl + ";
            if ((hotMod & ModShift) != 0) text += "Shift + ";
            if ((hotMod & ModAlt) != 0) text += "Alt + ";
            return text + hotDisplay;
        }

        static string KeyName(uint vk) =>
            KeyToDisplay(KeyInterop.KeyFromVirtualKey((int)vk), vk);

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

        void SetModulesEnabled(bool on)
        {
            lock (stateLock) modulesEnabled = on;
            RegisterRejoinHotkey();
            UpdateModulesEnabledButton();
            RefreshModulesUi();
            if (initialized) SaveConfig();
        }

        void UpdateModulesEnabledButton()
        {
            if (BtnModulesEnabled == null) return;
            var on = modulesEnabled;
            BtnModulesEnabled.IsChecked = on;
            BtnModulesEnabled.Background = Brushes.Transparent;
            BtnModulesEnabled.Foreground = on ? AccentBrush : DangerBrush;
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

        string RejoinSummaryText() => modulesAutoRejoinOn
            ? $"{RejoinHotkeyText()} · {FormatSeconds(rejoinDelayMs)} с"
            : "Вимкнено";

        /// <summary>Formats milliseconds as seconds with up to 3 decimal digits (trailing zeros trimmed).</summary>
        static string FormatSeconds(int ms) => (ms / 1000.0).ToString("0.###", Invariant);

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
            UpdateModulesEnabledButton();

            if (modAntiToggle != null)
            {
                var on = antiOn;
                modAntiToggle.IsChecked = on;
                modAntiToggle.Content = on ? "ВКЛ" : "ВИКЛ";
                modAntiToggle.Background = on ? AccentBrush : LineBrush;
                modAntiToggle.Foreground = on ? BgBrush : MutedBrush;
            }
            if (modAntiSummary != null)
                modAntiSummary.Text = AntiSummaryText();

            if (modRejoinToggle != null)
            {
                var on = modulesAutoRejoinOn;
                modRejoinToggle.IsChecked = on;
                modRejoinToggle.Content = on ? "ВКЛ" : "ВИКЛ";
                modRejoinToggle.Background = on ? AccentBrush : LineBrush;
                modRejoinToggle.Foreground = on ? BgBrush : MutedBrush;
            }
            if (modRejoinSummary != null)
                modRejoinSummary.Text = RejoinSummaryText();

            UpdateActiveModulesBadges();
        }

        // ── Active modules badges in main window header ───────────────────────────
        void UpdateActiveModulesBadges()
        {
            if (ActiveModulesWrap == null) return;
            ActiveModulesWrap.Children.Clear();

            if (modulesEnabled)
            {
                if (antiOn)
                    ActiveModulesWrap.Children.Add(MakeModuleBadge("ANTI-DETECT"));
                if (modulesAutoRejoinOn)
                    ActiveModulesWrap.Children.Add(MakeModuleBadge("AUTO-REJOIN"));
            }

            ActiveModulesWrap.Visibility = ActiveModulesWrap.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
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
            MiniMode.Foreground = AccentBrush;
            MiniCps.Foreground = TextBrush;
            MiniHotkey.Foreground = MutedBrush;
        }

        // ── Popup window factory ──────────────────────────────────────────────────
        Window CreatePopup(string title, double width)
        {
            var win = new Window
            {
                Owner = this,
                Width = width,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                // Manual placement: to the left (or right) of main window, bottom-edge aligned
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Left,  // temporary; corrected in Loaded
                Top = Top,
                ShowInTaskbar = false
            };

            // Position: to the LEFT of main window if space, else RIGHT.
            // Bottom edges of both windows are aligned.
            win.Loaded += (_, _) =>
            {
                const double gap = 6;
                var screen = SystemParameters.WorkArea;
                double popupW = win.ActualWidth;
                double popupH = win.ActualHeight;

                // Bottom-edge alignment
                double newTop = Top + ActualHeight - popupH;
                if (newTop < screen.Top) newTop = screen.Top;

                // Try left side first
                double newLeft = Left - popupW - gap;
                if (newLeft < screen.Left)
                    newLeft = Left + ActualWidth + gap; // fall back to right

                win.Left = newLeft;
                win.Top = newTop;
            };

            var border = new Border
            {
                Tag = "PopupRoot",
                Background = BgBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18)
            };

            var root = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Tag = "AccentText",
                Text = title,
                Foreground = AccentBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = fontSize + 7,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            // Close button: uses ChromeButton style – no background fill on hover
            var close = new Button
            {
                Tag = "ChromeButton",
                Content = "x",
                Width = 30,
                Height = 30,
                Style = (Style)FindResource("ChromeButton"),
                Foreground = MutedBrush,
                Cursor = Cursors.Hand
            };
            AttachHoverAnimation(close);
            close.Click += (_, _) => win.Close();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);

            root.Children.Add(header);
            border.Child = root;
            win.Content = border;
            win.PreviewMouseDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && !IsInside<TextBox>(source))
                    Keyboard.ClearFocus();
            };
            win.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source &&
                    (IsInside<TextBox>(source) || IsInside<ButtonBase>(source) || IsInside<Slider>(source)))
                    return;
                try { win.DragMove(); } catch { }
            };
            return win;
        }

        void RefreshPopupTheme(DependencyObject? root)
        {
            if (root == null) return;

            if (root is FrameworkElement element && element.Tag is string tag)
            {
                switch (tag)
                {
                    case "PopupRoot" when root is Border popup:
                        popup.Background = BgBrush;
                        popup.BorderBrush = LineBrush;
                        break;
                    case "Surface" when root is Border surface:
                        surface.Background = SurfaceBrush;
                        surface.BorderBrush = LineBrush;
                        break;
                    case "AccentText" when root is TextBlock accent:
                        accent.Foreground = AccentBrush;
                        accent.FontSize = fontSize + 7;
                        break;
                    case "Text" when root is TextBlock text:
                        text.Foreground = TextBrush;
                        text.FontSize = fontSize;
                        break;
                    case "Muted" when root is TextBlock muted:
                        muted.Foreground = MutedBrush;
                        muted.FontSize = Math.Max(9, fontSize - 2);
                        break;
                    case "Line" when root is Border line:
                        line.Background = LineBrush;
                        break;
                    case "Input" when root is TextBox input:
                        input.Background = SurfaceBrush;
                        input.Foreground = AccentBrush;
                        input.BorderBrush = LineBrush;
                        input.CaretBrush = Brushes.Transparent;
                        break;
                    case "ChromeButton" when root is Button chrome:
                        chrome.Foreground = MutedBrush;
                        break;
                    case "DialogPrimary" when root is Button primary:
                        primary.Background = SurfaceBrush;
                        primary.Foreground = TextBrush;
                        break;
                    case "DialogSecondary" when root is Button secondary:
                        secondary.Background = LineBrush;
                        secondary.Foreground = MutedBrush;
                        break;
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                RefreshPopupTheme(VisualTreeHelper.GetChild(root, i));
        }

        StackPanel PopupRoot(Window win) =>
            (StackPanel)((Border)win.Content).Child;

        void OpenModulesSettings()
        {
            var win = CreatePopup("МОДУЛІ", 430);
            var root = PopupRoot(win);

            if (!modulesEnabled)
            {
                root.Children.Add(new TextBlock
                {
                    Tag = "Muted",
                    Text = "Модулі вимкнено загальним перемикачем у головному вікні. " +
                           "Поки він вимкнений, жоден модуль не працюватиме — " +
                           "навіть якщо увімкнений нижче.",
                    Foreground = MutedBrush,
                    FontSize = Math.Max(9, fontSize - 2),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14)
                });
            }

            AddModuleRow(root, "Рандомізація кліків", AntiSummaryText(), antiOn,
                SetAntiOn, OpenAntiDetectSettings,
                (toggle, summary) => { modAntiToggle = toggle; modAntiSummary = summary; });

            AddDivider(root);

            // Display name: "Авто-перезахід". Config key remains "AutoRejoin"/"auto-rejoin".
            AddModuleRow(root, "Авто-перезахід", RejoinSummaryText(), modulesAutoRejoinOn,
                SetAutoRejoinOn, OpenAutoRejoinSettings,
                (toggle, summary) => { modRejoinToggle = toggle; modRejoinSummary = summary; });

            win.Closed += (_, _) =>
            {
                modAntiToggle = null;
                modAntiSummary = null;
                modRejoinToggle = null;
                modRejoinSummary = null;
            };

            win.ShowDialog();
        }

        void AddModuleRow(Panel root, string title, string summary, bool on,
            Action<bool> onToggle, Action onSettings, Action<ToggleButton, TextBlock> bind)
        {
            var border = new Border
            {
                Tag = "Surface",
                Background = SurfaceBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Tag = "Text",
                Text = title,
                Foreground = TextBrush,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold
            });

            var summaryBlock = new TextBlock
            {
                Tag = "Muted",
                Text = summary,
                Foreground = MutedBrush,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0)
            };
            stack.Children.Add(summaryBlock);
            grid.Children.Add(stack);

            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("SegmentButton"),
                IsChecked = on,
                Content = on ? "ВКЛ" : "ВИКЛ",
                Background = on ? AccentBrush : LineBrush,
                Foreground = on ? BgBrush : MutedBrush,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(toggle, 1);
            AttachHoverAnimation(toggle);
            toggle.Click += (_, _) =>
            {
                var state = toggle.IsChecked == true;
                toggle.Content = state ? "ВКЛ" : "ВИКЛ";
                toggle.Background = state ? AccentBrush : LineBrush;
                toggle.Foreground = state ? BgBrush : MutedBrush;
                onToggle(state);
            };
            grid.Children.Add(toggle);

            var gear = new Button
            {
                Style = (Style)FindResource("FlatButton"),
                Content = "⚙",
                Background = LineBrush,
                Foreground = TextBrush,
                FontSize = 16,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(gear, 2);
            AttachHoverAnimation(gear);
            gear.Click += (_, _) => onSettings();
            grid.Children.Add(gear);

            border.Child = grid;
            root.Children.Add(border);

            bind(toggle, summaryBlock);
        }

        /// <summary>Simple title + subtitle + single ON/OFF toggle row (no gear button).</summary>
        void AddToggleRow(Panel root, string title, string subtitle, bool on, Action<bool> apply)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            stack.Children.Add(new TextBlock
            {
                Tag = "Text",
                Text = title,
                Foreground = TextBrush,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold
            });
            stack.Children.Add(new TextBlock
            {
                Tag = "Muted",
                Text = subtitle,
                Foreground = MutedBrush,
                FontSize = Math.Max(9, fontSize - 2),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            row.Children.Add(stack);

            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("SegmentButton"),
                IsChecked = on,
                Content = on ? "ВКЛ" : "ВИКЛ",
                Width = 70,
                Background = on ? AccentBrush : LineBrush,
                Foreground = on ? BgBrush : MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(toggle, 1);
            AttachHoverAnimation(toggle);
            toggle.Click += (_, _) =>
            {
                var state = toggle.IsChecked == true;
                toggle.Content = state ? "ВКЛ" : "ВИКЛ";
                toggle.Background = state ? AccentBrush : LineBrush;
                toggle.Foreground = state ? BgBrush : MutedBrush;
                apply(state);
            };
            row.Children.Add(toggle);

            root.Children.Add(row);
        }

        void OpenAutoRejoinSettings()
        {
            // Window title renamed to AUTO-REJOIN
            var win = CreatePopup("AUTO-REJOIN", 430);
            var root = PopupRoot(win);

            root.Children.Add(new TextBlock
            {
                Tag = "Muted",
                Text = "При натисканні гарячої клавіші відправляє команду виходу, " +
                       "чекає вказаний час і відправляє команду повернення.",
                Foreground = MutedBrush,
                FontSize = Math.Max(9, fontSize - 2),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var bindRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            bindRow.ColumnDefinitions.Add(new ColumnDefinition());
            bindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });

            var bindBox = new Border
            {
                Tag = "Surface",
                Background = SurfaceBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Height = 42,
                Padding = new Thickness(13, 0, 13, 0)
            };
            var bindText = new TextBlock
            {
                Tag = "AccentText",
                Text = RejoinHotkeyText(),
                Foreground = AccentBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            bindBox.Child = bindText;
            bindRow.Children.Add(bindBox);

            var recordBtn = new Button
            {
                Style = (Style)FindResource("FlatButton"),
                Content = "ЗАПИС",
                Background = LineBrush,
                Foreground = TextBrush,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(recordBtn, 1);
            AttachHoverAnimation(recordBtn);
            recordBtn.Click += (_, _) => StartRejoinHotkeyCapture(win, recordBtn, bindText);
            bindRow.Children.Add(recordBtn);
            root.Children.Add(bindRow);

            AddDivider(root);

            AddTextRow(root, "Команда виходу в лобі", rejoinLeaveCmd,
                v => { rejoinLeaveCmd = v; RefreshModulesUi(); SaveConfig(); });
            AddTextRow(root, "Команда повернення", rejoinJoinCmd,
                v => { rejoinJoinCmd = v; RefreshModulesUi(); SaveConfig(); });

            AddDivider(root);

            // Slider in whole seconds (0-60), textbox shows seconds and accepts
            // a fractional part (milliseconds) via '.' or ','
            AddSliderSecRow(root,
                "Затримка перед поверненням",
                "Слайдер: секунди · Поле: секунди, мс через «.» або «,» (напр. 1.5)",
                0, 60000, rejoinDelayMs,
                value => { rejoinDelayMs = value; RefreshModulesUi(); SaveConfig(); });

            AddDivider(root);

            AddToggleRow(root,
                "Працювати без СТАРТ клікера",
                "Якщо увімкнено — модуль діє за гарячою клавішею незалежно від СТАРТ/СТОП клікера " +
                "(загальний перемикач модулів все одно має бути увімкнений).",
                rejoinIgnoreStartStop,
                v => { rejoinIgnoreStartStop = v; SaveConfig(); });

            win.ShowDialog();
        }

        void AddTextRow(Panel root, string title, string value, Action<string> apply)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            wrap.Children.Add(new TextBlock
            {
                Tag = "Text",
                Text = title,
                Foreground = TextBrush,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 7)
            });

            var box = new TextBox
            {
                Tag = "Input",
                Text = value,
                Height = 38,
                Background = SurfaceBrush,
                Foreground = AccentBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CaretBrush = AccentBrush,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 0, 10, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            FixTextBoxFocus(box);
            box.LostFocus += (_, _) =>
                apply(string.IsNullOrWhiteSpace(box.Text) ? value : box.Text.Trim());
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Keyboard.ClearFocus();
            };

            wrap.Children.Add(box);
            root.Children.Add(wrap);
        }

        void StartRejoinHotkeyCapture(Window owner, Button recordButton, TextBlock display)
        {
            recordButton.Content = "СКАСУВАТИ";
            recordButton.Background = DangerBrush;
            recordButton.Foreground = BgBrush;
            display.Text = "Натисни комбінацію...";
            display.Foreground = DangerBrush;

            void Finish()
            {
                owner.PreviewKeyDown -= Handler;
                recordButton.Content = "ЗАПИС";
                recordButton.Background = LineBrush;
                recordButton.Foreground = TextBrush;
            }

            void Handler(object sender, KeyEventArgs e)
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;

                if (key == Key.Escape)
                {
                    Finish();
                    display.Text = RejoinHotkeyText();
                    display.Foreground = AccentBrush;
                    return;
                }

                if (IsModifierOnly(key)) return;

                var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (vk == 0) return;

                uint mods = 0;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= ModCtrl;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= ModShift;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;

                rejoinHotMod = mods;
                rejoinHotVk = vk;
                rejoinHotDisplay = KeyToDisplay(key, vk);

                Finish();
                display.Text = RejoinHotkeyText();
                display.Foreground = AccentBrush;

                RegisterRejoinHotkey();
                RefreshModulesUi();
                SaveConfig();
            }

            owner.PreviewKeyDown += Handler;
            owner.Focus();
            Keyboard.Focus(recordButton);
        }

        void OpenAntiDetectSettings()
        {
            var win = CreatePopup("ANTI-DETECT", 430);
            var root = PopupRoot(win);

            AddSliderRow(root, "Розкид інтервалу", "Випадкове відхилення від базового CPS", 0, 50, jitter, "%",
                value => { lock (stateLock) jitter = value; this.jitter = value; RefreshModulesUi(); SaveConfig(); });
            AddDivider(root);
            AddSliderRow(root, "Шанс мікропаузи", "Ймовірність паузи між кліками", 0, 30, burstChance, "%",
                value => { lock (stateLock) burstChance = value; this.burstChance = value; RefreshModulesUi(); SaveConfig(); });
            AddSliderRow(root, "Тривалість мікропаузи", "Окремий параметр, який загубився у C# версії", 50, 500, burstMs, "мс",
                value => { lock (stateLock) burstMs = value; this.burstMs = value; RefreshModulesUi(); SaveConfig(); });
            AddDivider(root);
            AddSliderRow(root, "Розкид утримання", "Випадковий hold-time перед відпусканням кнопки", 0, 40, holdMs, "мс",
                value => { lock (stateLock) holdMs = value; this.holdMs = value; RefreshModulesUi(); SaveConfig(); });

            AddDivider(root);

            AddToggleRow(root,
                "Працювати без СТАРТ клікера",
                "Поки модуль діє лише під час кліків, тому ця опція наразі не змінює поведінку — " +
                "залишена для єдиного вигляду з іншими модулями.",
                antiIgnoreStartStop,
                v => { antiIgnoreStartStop = v; SaveConfig(); });

            win.ShowDialog();
        }

        void OpenAppearanceSettings()
        {
            var win = CreatePopup("ВИГЛЯД", 410);
            var root = PopupRoot(win);

            AddColorRow(root, "Фон", theme.Background, v => theme.Background = v);
            AddColorRow(root, "Панелі", theme.Surface, v => theme.Surface = v);
            AddColorRow(root, "Акцент", theme.Accent, v => theme.Accent = v);
            AddColorRow(root, "Стоп / попередження", theme.Danger, v => theme.Danger = v);
            AddColorRow(root, "Текст", theme.Text, v => theme.Text = v);
            AddColorRow(root, "Приглушений", theme.Muted, v => theme.Muted = v);
            AddColorRow(root, "Лінії", theme.Line, v => theme.Line = v);

            AddDivider(root);
            AddSliderRow(root, "Розмір шрифту", "Застосовується одразу до елементів керування", 11, 18, fontSize, "",
                value => { fontSize = value; ApplyTheme(); RefreshPopupTheme(win); SaveConfig(); });

            var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition());

            var reset = DialogButton("СКИНУТИ", LineBrush, MutedBrush);
            reset.Click += (_, _) =>
            {
                theme = new ThemeConfig();
                fontSize = 14;
                ApplyTheme();
                RefreshPopupTheme(win);
                SaveConfig();
                win.Close();
            };
            reset.Tag = "DialogSecondary";
            buttons.Children.Add(reset);

            var close = DialogButton("ГОТОВО", SurfaceBrush, TextBrush);
            close.Click += (_, _) => win.Close();
            close.Tag = "DialogPrimary";
            Grid.SetColumn(close, 1);
            close.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            win.ShowDialog();
        }

        Button DialogButton(string text, Brush bg, Brush fg)
        {
            var button = new Button
            {
                Content = text,
                Height = 40,
                Background = bg,
                Foreground = fg,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            AttachHoverAnimation(button);
            return button;
        }

        void AddDivider(Panel root) =>
            root.Children.Add(new Border { Tag = "Line", Height = 1, Background = LineBrush, Margin = new Thickness(0, 10, 0, 10) });

        void AddColorRow(Panel root, string label, string color, Action<string> apply)
        {
            var current = color;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Tag = "Text",
                Text = label,
                Foreground = TextBrush,
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center
            });

            var swatch = new Button
            {
                Width = 58,
                Height = 28,
                Background = BrushOf(color),
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            AttachHoverAnimation(swatch);

            swatch.Click += (_, _) =>
            {
                using var dlg = new Forms.ColorDialog
                {
                    Color = System.Drawing.ColorTranslator.FromHtml(current),
                    FullOpen = true
                };
                if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
                var next = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                current = next;
                apply(next);
                swatch.Background = BrushOf(next);
                ApplyTheme();
                RefreshPopupTheme(Window.GetWindow(swatch));
                SaveConfig();
            };

            Grid.SetColumn(swatch, 1);
            row.Children.Add(swatch);
            root.Children.Add(row);
        }

        void AddSliderRow(Panel root, string title, string subtitle, int min, int max, int value, string suffix, Action<int> apply)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            wrap.Children.Add(new TextBlock
            {
                Tag = "Text",
                Text = title,
                Foreground = TextBrush,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold
            });
            wrap.Children.Add(new TextBlock
            {
                Tag = "Muted",
                Text = subtitle,
                Foreground = MutedBrush,
                FontSize = Math.Max(9, fontSize - 2),
                Margin = new Thickness(0, 2, 0, 7)
            });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(slider);

            var box = new TextBox
            {
                Tag = "Input",
                Text = value.ToString(Invariant),
                Width = 58,
                Height = 28,
                Background = SurfaceBrush,
                Foreground = AccentBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CaretBrush = Brushes.Transparent,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0)
            };
            FixTextBoxFocus(box);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var unit = new TextBlock
            {
                Tag = "Muted",
                Text = suffix,
                Foreground = MutedBrush,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(unit, 2);
            row.Children.Add(unit);

            bool syncing = false;
            void Commit(int next)
            {
                next = Clamp(next, min, max);
                syncing = true;
                slider.Value = next;
                box.Text = next.ToString(Invariant);
                syncing = false;
                apply(next);
            }

            slider.ValueChanged += (_, _) =>
            {
                if (syncing) return;
                Commit((int)Math.Round(slider.Value));
            };

            void CommitFromBox()
            {
                if (int.TryParse(box.Text, out var parsed)) Commit(parsed);
                else Commit((int)Math.Round(slider.Value));
            }

            box.LostFocus += (_, _) => CommitFromBox();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                CommitFromBox();
                Keyboard.ClearFocus();
            };

            wrap.Children.Add(row);
            root.Children.Add(wrap);
        }

        /// <summary>
        /// Slider shows/sets whole seconds (0..maxMs/1000). TextBox shows the value in seconds
        /// (e.g. "1.5") and accepts an optional fractional (millisecond) part using either
        /// '.' or ',' as the decimal separator (e.g. "1.5" or "1,501" → 1500/1501 ms).
        /// The underlying value is always stored/applied in milliseconds.
        /// </summary>
        void AddSliderSecRow(Panel root, string title, string subtitle,
            int minMs, int maxMs, int valueMs, Action<int> apply)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            wrap.Children.Add(new TextBlock
            {
                Tag = "Text",
                Text = title,
                Foreground = TextBrush,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold
            });
            wrap.Children.Add(new TextBlock
            {
                Tag = "Muted",
                Text = subtitle,
                Foreground = MutedBrush,
                FontSize = Math.Max(9, fontSize - 2),
                Margin = new Thickness(0, 2, 0, 7)
            });

            int maxSec = maxMs / 1000;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = maxSec,
                Value = Math.Round(valueMs / 1000.0),
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(slider);

            var box = new TextBox
            {
                Tag = "Input",
                Text = FormatSeconds(valueMs),
                Width = 66,
                Height = 28,
                Background = SurfaceBrush,
                Foreground = AccentBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CaretBrush = Brushes.Transparent,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0)
            };
            FixTextBoxFocus(box);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var unit = new TextBlock
            {
                Tag = "Muted",
                Text = "сек",
                Foreground = MutedBrush,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(unit, 2);
            row.Children.Add(unit);

            bool syncing = false;
            void Commit(int nextMs)
            {
                nextMs = Clamp(nextMs, minMs, maxMs);
                syncing = true;
                slider.Value = Math.Round(nextMs / 1000.0);
                box.Text = FormatSeconds(nextMs);
                syncing = false;
                apply(nextMs);
            }

            // Slider always moves in whole-second steps → seconds * 1000 ms
            slider.ValueChanged += (_, _) =>
            {
                if (syncing) return;
                Commit((int)Math.Round(slider.Value) * 1000);
            };
            // TextBox accepts seconds, optionally with '.' or ',' for the ms part
            void CommitFromBox()
            {
                var text = box.Text.Trim().Replace(',', '.');
                if (double.TryParse(text, NumberStyles.Float, Invariant, out var seconds))
                    Commit((int)Math.Round(seconds * 1000));
                else
                    Commit(valueMs);
            }

            box.LostFocus += (_, _) => CommitFromBox();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                CommitFromBox();
                Keyboard.ClearFocus();
            };

            wrap.Children.Add(row);
            root.Children.Add(wrap);
        }

        static int Clamp(int value, int min, int max) =>
            Math.Min(max, Math.Max(min, value));

        void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && !IsInside<TextBox>(source))
                Keyboard.ClearFocus();
        }

        void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source &&
                (IsInside<TextBox>(source) || IsInside<ButtonBase>(source) || IsInside<Slider>(source)))
                return;
            try { DragMove(); } catch { }
        }

        static bool IsInside<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T) return true;
                DependencyObject? parent = null;
                try { parent = VisualTreeHelper.GetParent(source); } catch { }
                source = parent ??
                    (source as FrameworkElement)?.Parent ??
                    (source as FrameworkContentElement)?.Parent;
            }
            return false;
        }

        void MouseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!holdMode)
            {
                SetMouseButtons(sender == BtnLmb, sender == BtnRmb);
                return;
            }

            var left = BtnLmb.IsChecked == true;
            var right = BtnRmb.IsChecked == true;
            if (!left && !right)
            {
                left = sender == BtnLmb;
                right = sender == BtnRmb;
            }
            SetMouseButtons(left, right);
        }

        void RunMode_Click(object sender, RoutedEventArgs e) =>
            SetRunMode(sender == BtnHoldMode);

        void TxtCps_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCps.Text, out var value) || value < 1)
                value = 100;
            TxtCps.Text = value.ToString(Invariant);
            lock (stateLock) cps = value;
            UpdateMiniUi();
            if (initialized) SaveConfig();
        }

        void TxtCps_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            TxtCps_LostFocus(sender, new RoutedEventArgs());
            Keyboard.ClearFocus();
        }

        void BtnModules_Click(object sender, RoutedEventArgs e) =>
            OpenModulesSettings();

        void BtnModulesEnabled_Click(object sender, RoutedEventArgs e) =>
            SetModulesEnabled(BtnModulesEnabled.IsChecked == true);

        void BtnAppearance_Click(object sender, RoutedEventArgs e) =>
            OpenAppearanceSettings();

        void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (recording) CancelRecording();
            else BeginRecording();
        }

        void BtnStart_Click(object sender, RoutedEventArgs e) => Start();
        void BtnStop_Click(object sender, RoutedEventArgs e) => Stop();
        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        sealed class AppConfig
        {
            public int Cps { get; set; } = 100;
            public string Mode { get; set; } = "RMB";
            public bool? ClickLeft { get; set; }
            public bool? ClickRight { get; set; }
            public string RunMode { get; set; } = "Stay";
            public HotkeyConfig Hotkey { get; set; } = new();
            public AntiDetectConfig AntiDetect { get; set; } = new();
            public ThemeConfig Theme { get; set; } = new();
            public int FontSize { get; set; } = 14;
            public ModulesConfig Modules { get; set; } = new();
        }

        sealed class ModulesConfig
        {
            /// <summary>Global on/off switch for all modules. Default ON.</summary>
            public bool Enabled { get; set; } = true;
            public AutoRejoinConfig AutoRejoin { get; set; } = new();
        }

        sealed class AutoRejoinConfig
        {
            public bool On { get; set; }
            /// <summary>If true, this module ignores the clicker's START/STOP state
            /// (still requires the global "Enabled" modules switch to be on).</summary>
            public bool IgnoreStartStop { get; set; }
            public uint Modifiers { get; set; }
            public uint VirtualKey { get; set; } = 0x4C;
            public string Display { get; set; } = "L";
            public string LeaveCommand { get; set; } = "/leave";
            public string RejoinCommand { get; set; } = "/rejoin";
            /// <summary>Delay in milliseconds (preferred). Overrides DelaySeconds.</summary>
            public int DelayMs { get; set; } = 3000;
            /// <summary>Legacy: delay in seconds. Used only when DelayMs == 0.</summary>
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
            /// <summary>If true, this module ignores the clicker's START/STOP state
            /// (still requires the global "Enabled" modules switch to be on).</summary>
            public bool IgnoreStartStop { get; set; }
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
