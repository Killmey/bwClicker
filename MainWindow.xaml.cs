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
        struct UNION { [FieldOffset(0)] public MOUSEINPUT mouse; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        const uint InputMouse = 0;
        const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
        const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004;
        const int HotkeyId = 9000;
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
        volatile bool holdActive;
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

        ThemeConfig theme = new();

        public MainWindow()
        {
            try
            {
                mouseProc = MouseHookCallback;
                InitializeComponent();
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
            InstallMouseHook();
        }

        protected override void OnClosed(EventArgs e)
        {
            Stop();
            SaveConfig();
            UnregisterCurrentHotkey();
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
                    FontSize = fontSize
                };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOptions));
            }
            catch { }
        }

        void ApplyConfigToUi()
        {
            TxtCps.Text = cps.ToString(Invariant);
            BtnAntiToggle.IsChecked = antiOn;
            BtnStayMode.IsChecked = !holdMode;
            BtnHoldMode.IsChecked = holdMode;
            SetMouseButtons(clickLeft, clickRight, save: false);
            SetRunMode(holdMode, save: false);
            TxtHotkey.Text = HotkeyText();
            UpdateAntiUi();
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
                antiOn = BtnAntiToggle.IsChecked == true;
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
            UpdateAntiUi();
            UpdateMiniUi();
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

                if (localHoldMode && !holdActive)
                {
                    nextTick = 0;
                    Thread.Sleep(1);
                    continue;
                }

                var interval = 1.0 / localCps;
                if (localAnti)
                {
                    var spread = localJitter / 100.0;
                    interval = Math.Max(0.0001, interval + (rng.NextDouble() * 2 - 1) * spread * interval);
                }

                var intervalTicks = Math.Max(1, (long)Math.Round(interval * Stopwatch.Frequency));
                var now = Stopwatch.GetTimestamp();
                if (nextTick == 0 || nextTick < now)
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

                nextTick = Stopwatch.GetTimestamp() + intervalTicks;
            }
        }

        static void Click(uint flag)
        {
            var inputs = new INPUT[1];
            inputs[0].type = InputMouse;
            inputs[0].input.mouse.dwFlags = flag;
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
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
            holdActive = false;
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
            holdActive = false;
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
            StatusText.Text = on ? (holdMode && !holdActive ? "ARMED" : "ACTIVE") : "IDLE";
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
            bool localHoldMode, localLeft, localRight;
            lock (stateLock)
            {
                localHoldMode = holdMode;
                localLeft = clickLeft;
                localRight = clickRight;
            }

            if (!running || !localHoldMode)
                return;

            if (msg == WmLButtonDown) physicalLeftDown = true;
            if (msg == WmLButtonUp) physicalLeftDown = false;
            if (msg == WmRButtonDown) physicalRightDown = true;
            if (msg == WmRButtonUp) physicalRightDown = false;

            SetHoldActive((localLeft && physicalLeftDown) || (localRight && physicalRightDown));
        }

        void SetHoldActive(bool active)
        {
            if (holdActive == active) return;
            holdActive = active;
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

        void AttachHoverAnimation(ButtonBase button)
        {
            if (button.RenderTransform is not ScaleTransform)
                button.RenderTransform = new ScaleTransform(1, 1);
            button.RenderTransformOrigin = new Point(0.5, 0.5);

            button.MouseEnter += (_, _) => AnimateScale(button, 1.018);
            button.MouseLeave += (_, _) => AnimateScale(button, 1.0);
            button.PreviewMouseDown += (_, _) => AnimateScale(button, 0.992);
            button.PreviewMouseUp += (_, _) => AnimateScale(button, button.IsMouseOver ? 1.018 : 1.0);
        }

        static void AnimateScale(UIElement element, double scale)
        {
            var duration = TimeSpan.FromMilliseconds(95);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var x = new DoubleAnimation(scale, duration) { EasingFunction = easing };
            var y = new DoubleAnimation(scale, duration) { EasingFunction = easing };
            element.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            element.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, y);
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

        void OnHotkeyMessage(ref MSG msg, ref bool handled)
        {
            if (msg.message != 0x0312 || msg.wParam.ToInt32() != HotkeyId) return;
            handled = true;
            if (!recording) Toggle();
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
            holdActive = false;
            physicalLeftDown = false;
            physicalRightDown = false;
            SetUi(running);
            if (save && initialized) SaveConfig();
        }

        void UpdateAntiUi()
        {
            var on = BtnAntiToggle.IsChecked == true;
            BtnAntiToggle.Content = on ? "ВКЛ" : "ВИКЛ";
            BtnAntiToggle.Background = on ? AccentBrush : LineBrush;
            BtnAntiToggle.Foreground = on ? BgBrush : MutedBrush;
            TxtAntiSummary.Text = on ? "Активна рандомізація" : "Вимкнено";
            lock (stateLock) antiOn = on;
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
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
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

            var close = new Button
            {
                Tag = "ChromeButton",
                Content = "x",
                Width = 30,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = MutedBrush,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            AttachHoverAnimation(close);
            close.Click += (_, _) => win.Close();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);

            root.Children.Add(header);
            border.Child = root;
            win.Content = border;
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

        void OpenAntiDetectSettings()
        {
            var win = CreatePopup("ANTI-DETECT", 430);
            var root = PopupRoot(win);

            AddSliderRow(root, "Розкид інтервалу", "Випадкове відхилення від базового CPS", 0, 50, jitter, "%",
                value => { lock (stateLock) jitter = value; this.jitter = value; UpdateAntiUi(); SaveConfig(); });
            AddDivider(root);
            AddSliderRow(root, "Шанс мікропаузи", "Ймовірність паузи між кліками", 0, 30, burstChance, "%",
                value => { lock (stateLock) burstChance = value; this.burstChance = value; UpdateAntiUi(); SaveConfig(); });
            AddSliderRow(root, "Тривалість мікропаузи", "Окремий параметр, який загубився у C# версії", 50, 500, burstMs, "мс",
                value => { lock (stateLock) burstMs = value; this.burstMs = value; UpdateAntiUi(); SaveConfig(); });
            AddDivider(root);
            AddSliderRow(root, "Розкид утримання", "Випадковий hold-time перед відпусканням кнопки", 0, 40, holdMs, "мс",
                value => { lock (stateLock) holdMs = value; this.holdMs = value; UpdateAntiUi(); SaveConfig(); });

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
                CaretBrush = Brushes.Transparent,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center
            };
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
            box.LostFocus += (_, _) =>
            {
                if (int.TryParse(box.Text, out var parsed)) Commit(parsed);
                else Commit((int)Math.Round(slider.Value));
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
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

        void BtnAntiToggle_Click(object sender, RoutedEventArgs e)
        {
            UpdateAntiUi();
            if (initialized) SaveConfig();
        }

        void BtnAntiSettings_Click(object sender, RoutedEventArgs e) =>
            OpenAntiDetectSettings();

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
