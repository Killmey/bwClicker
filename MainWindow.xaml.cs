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

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int HOTKEY_REJOIN_ID = 9001;

        private const uint ModCtrl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModAlt = 0x0001;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelMouseProc? mouseHookProcedure;
        private IntPtr mouseHookHandle = IntPtr.Zero;

        private Thread? clickThread;
        private bool isClicking = false;
        private readonly object lockObj = new();

        private bool isConfigLoading = false;
        private bool isBindingHotkey = false;
        private readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private AppConfig config = new();

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            RegisterGlobalHotkey(helper.Handle);
            HookMouse();
        }

        protected override void OnClosed(EventArgs e)
        {
            UnHookMouse();
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_REJOIN_ID);
            StopClicking();
            base.OnClosed(e);
        }

        // --- WINDOW ACTIONS ---
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (isBindingHotkey) e.Handled = true;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // --- CONFIG MANAGEMENT ---
        private void LoadConfig()
        {
            isConfigLoading = true;
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                    if (loaded != null) config = loaded;
                }
            }
            catch { }

            SldCps.Value = config.Cps;
            TxtCps.Text = config.Cps.ToString();

            if (config.MouseButton == "R") RadRightClick.IsChecked = true;
            else RadLeftClick.IsChecked = true;

            TglModules.IsChecked = config.ModulesBlockOpen;
            PanelSubModules.Visibility = config.ModulesBlockOpen ? Visibility.Visible : Visibility.Collapsed;
            PanelSubModules.Height = config.ModulesBlockOpen ? double.NaN : 0;
            PanelSubModules.Opacity = config.ModulesBlockOpen ? 1 : 0;

            TglStayTarget.IsChecked = config.Modules.StayOnTarget;
            TglHoldSelect.IsChecked = config.Modules.HoldSelection;
            TglAutoRejoin.IsChecked = config.Modules.AutoRejoin;
            TglAntiDetect.IsChecked = config.Modules.AntiDetect;

            // Конвертація DelayMs назад у дробовий вигляд для відображення в текстбоксі (наприклад, 1500 -> 1.5)
            double delaySeconds = config.AutoRejoinConfig.DelayMs / 1000.0;
            TxtRejoinDelay.Text = delaySeconds.ToString("0.###", CultureInfo.InvariantCulture);

            int clampedSlider = (int)Math.Clamp(delaySeconds, SldRejoinDelay.Minimum, SldRejoinDelay.Maximum);
            SldRejoinDelay.Value = clampedSlider;

            BtnRejoinHotkey.Content = config.Hotkey.Display.ToUpper();

            isConfigLoading = false;
        }

        private void SaveConfig()
        {
            if (isConfigLoading) return;
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(configPath, json);
            }
            catch { }
        }

        // --- APP LOGIC / CLICKER CONTROLS ---
        private void BtnStart_Click(object sender, RoutedEventArgs e) => StartClicking();
        private void BtnStop_Click(object sender, RoutedEventArgs e) => StopClicking();

        private void StartClicking()
        {
            lock (lockObj)
            {
                if (isClicking) return;
                isClicking = true;
                BtnStart.IsEnabled = false;
                BtnStart.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x40, 0x28));
                BtnStop.Background = (SolidColorBrush)FindResource("DangerBrush");

                clickThread = new Thread(ClickLoop) { IsBackground = true };
                clickThread.Start();
            }
        }

        private void StopClicking()
        {
            lock (lockObj)
            {
                if (!isClicking) return;
                isClicking = false;
                BtnStart.IsEnabled = true;
                BtnStart.Background = (SolidColorBrush)FindResource("AccentBrush");
                BtnStop.Background = (SolidColorBrush)FindResource("LineBrush");
            }
        }

        private void ClickLoop()
        {
            var rand = new Random();
            while (true)
            {
                lock (lockObj) { if (!isClicking) break; }

                int currentCps = config.Cps;
                bool antiDetect = config.Modules.AntiDetect;
                var antiConfig = config.AntiDetectConfig;
                string btn = config.MouseButton;

                bool stayTarget = config.Modules.StayOnTarget;
                bool holdSelect = config.Modules.HoldSelection;

                if (stayTarget || holdSelect)
                {
                    // Логіка перевірки гри/цілі
                }

                int baseDelay = 1000 / currentCps;
                if (antiDetect && antiConfig.On)
                {
                    int jitter = rand.Next(-antiConfig.Jitter, antiConfig.Jitter + 1);
                    baseDelay += jitter;
                    if (baseDelay < 1) baseDelay = 1;
                }

                ushort flagDown = (ushort)(btn == "R" ? 0x0008 : 0x0002);
                ushort flagUp = (ushort)(btn == "R" ? 0x0010 : 0x0004);

                INPUT[] inputDown = [new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flagDown } }];
                SendInput(1, inputDown, Marshal.SizeOf(typeof(INPUT)));

                int holdTime = 4;
                if (antiDetect && antiConfig.On)
                {
                    holdTime = rand.Next(antiConfig.HoldMs - 2, antiConfig.HoldMs + 5);
                    if (holdTime < 1) holdTime = 1;
                }
                Thread.Sleep(holdTime);

                INPUT[] inputUp = [new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flagUp } }];
                SendInput(1, inputUp, Marshal.SizeOf(typeof(INPUT)));

                int finalSleep = baseDelay - holdTime;
                if (antiDetect && antiConfig.On && rand.Next(100) < antiConfig.BurstChance)
                {
                    finalSleep += rand.Next(10, antiConfig.BurstMs);
                }

                if (finalSleep > 0) Thread.Sleep(finalSleep);
            }
        }

        // --- UI CONTROLS & INTERACTION ---
        private void SldCps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtCps == null) return;
            int val = (int)e.NewValue;
            TxtCps.Text = val.ToString();
            config.Cps = val;
            SaveConfig();
        }

        private void TxtCps_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SldCps == null) return;
            if (int.TryParse(TxtCps.Text, out int val))
            {
                if (val < 1) val = 1;
                if (val > 100) val = 100;
                SldCps.Value = val;
                config.Cps = val;
                SaveConfig();
            }
        }

        private void RadMouse_Checked(object sender, RoutedEventArgs e)
        {
            if (isConfigLoading) return;
            config.MouseButton = RadRightClick.IsChecked == true ? "R" : "L";
            SaveConfig();
        }

        private void TglModules_Checked(object sender, RoutedEventArgs e)
        {
            config.ModulesBlockOpen = true;
            SaveConfig();
            PanelSubModules.Visibility = Visibility.Visible;
            DoubleAnimation heightAnim = new(0, 75, TimeSpan.FromSeconds((double)FindResource("AnimTime")));
            DoubleAnimation opacityAnim = new(0, 1, TimeSpan.FromSeconds((double)FindResource("AnimTime")));
            PanelSubModules.BeginAnimation(HeightProperty, heightAnim);
            PanelSubModules.BeginAnimation(OpacityProperty, opacityAnim);
        }

        private void TglModules_Unchecked(object sender, RoutedEventArgs e)
        {
            config.ModulesBlockOpen = false;
            SaveConfig();
            DoubleAnimation heightAnim = new(PanelSubModules.ActualHeight, 0, TimeSpan.FromSeconds((double)FindResource("AnimTime")));
            DoubleAnimation opacityAnim = new(PanelSubModules.ActualOpacity, 0, TimeSpan.FromSeconds((double)FindResource("AnimTime")));
            heightAnim.Completed += (s, a) => { PanelSubModules.Visibility = Visibility.Collapsed; };
            PanelSubModules.BeginAnimation(HeightProperty, heightAnim);
            PanelSubModules.BeginAnimation(OpacityProperty, opacityAnim);
        }

        private void BtnGoToModules_Click(object sender, RoutedEventArgs e)
        {
            MenuMain.Visibility = Visibility.Collapsed;
            MenuModules.Visibility = Visibility.Visible;
        }

        private void BtnBackToMain_Click(object sender, RoutedEventArgs e)
        {
            MenuModules.Visibility = Visibility.Collapsed;
            MenuMain.Visibility = Visibility.Visible;
        }

        private void Module_StateChanged(object sender, RoutedEventArgs e)
        {
            if (isConfigLoading) return;
            config.Modules.StayOnTarget = TglStayTarget.IsChecked == true;
            config.Modules.HoldSelection = TglHoldSelect.IsChecked == true;
            config.Modules.AutoRejoin = TglAutoRejoin.IsChecked == true;
            config.Modules.AntiDetect = TglAntiDetect.IsChecked == true;
            SaveConfig();
        }

        // ПАРСИНГ ЗАТРИМКИ REJOIN (Крапка/Кома + конвертація в Мілісекунди)
        private void TxtRejoinDelay_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isConfigLoading || SldRejoinDelay == null) return;

            string rawText = TxtRejoinDelay.Text.Replace(',', '.').Trim();
            if (double.TryParse(rawText, NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
            {
                if (seconds < 0.1) seconds = 0.1;
                if (seconds > 60.0) seconds = 60.0;

                int convertedMs = (int)Math.Round(seconds * 1000.0);
                config.AutoRejoinConfig.DelayMs = convertedMs;
                config.AutoRejoinConfig.DelaySeconds = (int)Math.Ceiling(seconds);
                SaveConfig();

                int clampedSec = (int)Math.Clamp(seconds, SldRejoinDelay.Minimum, SldRejoinDelay.Maximum);
                SldRejoinDelay.Value = clampedSec;
            }
        }

        private void SldRejoinDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isConfigLoading || TxtRejoinDelay == null) return;
            int sec = (int)e.NewValue;
            
            if (!TxtRejoinDelay.IsFocused)
            {
                TxtRejoinDelay.Text = sec.ToString("0.0", CultureInfo.InvariantCulture);
                config.AutoRejoinConfig.DelayMs = sec * 1000;
                config.AutoRejoinConfig.DelaySeconds = sec;
                SaveConfig();
            }
        }

        // --- HOTKEY SETUP MECHANICS ---
        private void BtnRejoinHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (isBindingHotkey) return;
            isBindingHotkey = true;
            BtnRejoinHotkey.Content = "[ НАДАТИ КЛАВІШУ ]";
            BtnRejoinHotkey.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x11, 0x11));
            KeyDown += MainWindow_KeyDown_Bind;
        }

        private void MainWindow_KeyDown_Bind(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftShift || k == Key.RightShift || k == Key.LeftAlt || k == Key.RightAlt) return;

            KeyDown -= MainWindow_KeyDown_Bind;
            isBindingHotkey = false;

            uint modifiers = 0;
            string modDisp = "";
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) { modifiers |= ModCtrl; modDisp += "CTRL + "; }
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) { modifiers |= ModShift; modDisp += "SHIFT + "; }
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) { modifiers |= ModAlt; modDisp += "ALT + "; }

            int vk = KeyInterop.VirtualKeyFromKey(k);
            string keyName = k.ToString().ToUpper();

            config.Hotkey.Modifiers = modifiers;
            config.Hotkey.VirtualKey = (uint)vk;
            config.Hotkey.Display = modDisp + keyName;
            SaveConfig();

            BtnRejoinHotkey.Content = config.Hotkey.Display;
            BtnRejoinHotkey.Background = (SolidColorBrush)FindResource("SurfaceBrush");

            var helper = new WindowInteropHelper(this);
            RegisterGlobalHotkey(helper.Handle);
        }

        private void RegisterGlobalHotkey(IntPtr hWnd)
        {
            UnregisterHotKey(hWnd, HOTKEY_REJOIN_ID);
            RegisterHotKey(hWnd, HOTKEY_REJOIN_ID, config.Hotkey.Modifiers, config.Hotkey.VirtualKey);
        }

        protected override void OnSourceInitialized(IntPtr hwnd, out bool handled)
        {
            handled = false;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source.AddHook(HwndHook);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_REJOIN_ID)
            {
                ExecuteAutoRejoinAction();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ВИКОНАННЯ МОДУЛЯ АВТО-ПЕРЕЗАХОДУ (Тільки коли Клікер запущений)
        private void ExecuteAutoRejoinAction()
        {
            // Перевірка: модуль має працювати ЛИШЕ коли клікер активний (isClicking == true)
            if (!config.Modules.AutoRejoin || !isClicking) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Надіслати /leave через буфер обміну чи емуляцію клавіатури
                    Thread.Sleep(config.AutoRejoinConfig.DelayMs);
                    // Надіслати /rejoin
                }
                catch { }
            });
        }

        // --- MOUSE HOOKS ---
        private void HookMouse()
        {
            mouseHookProcedure = MouseHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule != null) mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseHookProcedure, IntPtr.Zero, 0);
        }

        private void UnHookMouse()
        {
            if (mouseHookHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHookHandle);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                {
                    // Логіка перехоплення кліків у грі за потреби
                }
            }
            return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
        }

        // --- WIN32 STRUCTURES ---
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        // --- CONFIG CONFIGURATION SCHEMAS ---
        sealed class AppConfig
        {
            public int Cps { get; set; } = 10;
            public string MouseButton { get; set; } = "L";
            public bool ModulesBlockOpen { get; set; } = false;
            public ModulesConfig Modules { get; set; } = new();
            public AutoRejoinConfig AutoRejoinConfig { get; set; } = new();
            public HotkeyConfig Hotkey { get; set; } = new();
            public AntiDetectConfig AntiDetectConfig { get; set; } = new();
        }

        sealed class ModulesConfig
        {
            public bool StayOnTarget { get; set; }
            public bool HoldSelection { get; set; }
            public bool AutoRejoin { get; set; }
            public bool AntiDetect { get; set; }
        }

        sealed class AutoRejoinConfig
        {
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
    }
}
