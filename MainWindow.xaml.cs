using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

// FIX 3: namespace змінено з ClickerApp на ClickerApp (узгоджено з x:Class у XAML)
namespace ClickerApp
{
    public partial class MainWindow : Window
    {
        // ─── Win32 API для симуляції миші та глобальних хоткеїв ──────────────────
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Структури для SendInput
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        const uint INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        // ─── Змінні клікера ──────────────────────────────────────────────────
        private bool isRunning = false;
        private Thread? clickThread;
        private Random rng = new Random();

        // Хоткей за замовчуванням: Ctrl (0x0002) + Z (0x5A)
        private uint currentModifier = 0x0002;
        private uint currentKey = 0x5A;
        private const int HOTKEY_ID = 9000;
        private bool isRecordingHotkey = false;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                LoadConfig();
                ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "clicker_error.txt"),
                    ex.ToString());
                throw;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            RegisterGlobalHotkey();
        }

        // ─── Високоточний таймер та Цикл Клікера ───────────────────────────────
        private void ClickLoop()
        {
            Stopwatch sw = new Stopwatch();

            // Зчитуємо параметри один раз перед циклом для оптимізації швидкості
            bool isLmb = false;
            int cps = 100;
            bool antiOn = false;
            double jitter = 0, burstChance = 0, holdMs = 0;

            Dispatcher.Invoke(() => {
                isLmb = BtnLmb.IsChecked == true;
                int.TryParse(TxtCps.Text, out cps);
                if (cps < 1) cps = 1;
                antiOn = ChkAntiDetect.IsChecked == true;
                jitter = SldJitter.Value / 100.0;
                burstChance = SldBurstChance.Value;
                holdMs = SldHold.Value;
            });

            uint downFlag = isLmb ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_RIGHTDOWN;
            uint upFlag = isLmb ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_RIGHTUP;

            while (isRunning)
            {
                sw.Restart();

                // Базовий інтервал між кліками (сек)
                double baseInterval = 1.0 / cps;

                if (antiOn)
                {
                    // 1. Jitter (Розкид інтервалу)
                    double offset = (rng.NextDouble() * 2 - 1) * jitter * baseInterval;
                    baseInterval = Math.Max(0.0001, baseInterval + offset);
                }

                // Клік вниз
                SendMouseInput(downFlag);

                // 2. Hold Time (Час утримання кнопки)
                if (antiOn && holdMs > 0)
                {
                    double currentHold = rng.NextDouble() * (holdMs / 1000.0);
                    PreciseSleep(currentHold);
                }

                // Клік вгору
                SendMouseInput(upFlag);

                // 3. Мікро-паузи (Burst Chance)
                if (antiOn && rng.Next(1, 101) <= burstChance)
                {
                    PreciseSleep(rng.Next(50, 501) / 1000.0);
                }

                // Очікування залишку часу до наступного кліку
                double elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed < baseInterval)
                {
                    PreciseSleep(baseInterval - elapsed);
                }
            }
        }

        private void PreciseSleep(double seconds)
        {
            if (seconds <= 0) return;
            long ticks = (long)(seconds * Stopwatch.Frequency);
            long start = Stopwatch.GetTimestamp();

            // Якщо чекати довго, віддаємо квант часу ОС, щоб не навантажувати процесор
            if (seconds > 0.002)
                Thread.Sleep((int)((seconds - 0.002) * 1000));

            while (Stopwatch.GetTimestamp() - start < ticks) { /* Спін-лок для нано-точності */ }
        }

        private void SendMouseInput(uint flags)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = flags;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // ─── Керування станом (Старт / Стоп) ──────────────────────────────────
        private void StartClicker()
        {
            if (isRunning) return;
            isRunning = true;
            UpdateUiState(true);

            clickThread = new Thread(ClickLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
            clickThread.Start();
        }

        private void StopClicker()
        {
            if (!isRunning) return;
            isRunning = false;
            UpdateUiState(false);
        }

        private void ToggleClicker()
        {
            if (isRunning) StopClicker(); else StartClicker();
        }

        private void UpdateUiState(bool active)
        {
            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = active
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FF88"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555"));
                StatusText.Text = active ? "ACTIVE" : "IDLE";
                StatusText.Foreground = StatusDot.Fill;
                BtnStart.Background = active
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FF88"));
            });
        }

        // ─── Реєстрація гарячих клавіш (Global Hotkeys) ───────────────────────
        private void RegisterGlobalHotkey()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
            RegisterHotKey(helper.Handle, HOTKEY_ID, currentModifier, currentKey);
        }

        private void ComponentDispatcher_ThreadFilterMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID && !isRecordingHotkey)
            {
                ToggleClicker();
                handled = true;
            }
        }

        // ─── Запис нової хоткеї програми ──────────────────────────────────────
        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (isRecordingHotkey) return;
            isRecordingHotkey = true;
            BtnRecord.Content = "...";
            TxtHotkey.Text = "Нажмите клавишу";
            this.KeyDown += MainWindow_KeyDown;
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            this.KeyDown -= MainWindow_KeyDown;

            // Отримуємо модифікатори
            uint mod = 0;
            string modStr = "";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { mod |= 0x0002; modStr += "Ctrl + "; }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))   { mod |= 0x0004; modStr += "Shift + "; }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))     { mod |= 0x0001; modStr += "Alt + "; }

            // Перехоплюємо звичайну клавішу
            Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt  || key == Key.RightAlt)
            {
                // Якщо натиснули тільки модифікатор, залишаємо дефолт
                key = Key.Z;
            }

            currentModifier = mod;
            currentKey = (uint)KeyInterop.VirtualKeyFromKey(key);

            TxtHotkey.Text = modStr + key.ToString();
            BtnRecord.Content = "ЗАПИСЬ";
            isRecordingHotkey = false;

            RegisterGlobalHotkey();
            SaveConfig();
            e.Handled = true;
        }

        // ─── Збереження конфігу через змінні середовища (без файлів) ─────────────
        private void SaveConfig()
        {
            Environment.SetEnvironmentVariable("KILLMEY_CPS",  TxtCps.Text,                          EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_MOD",  currentModifier.ToString(),            EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_KEY",  currentKey.ToString(),                 EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_LMB",  (BtnLmb.IsChecked == true).ToString(), EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_ANTI", (ChkAntiDetect.IsChecked == true).ToString(), EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_JIT",  SldJitter.Value.ToString(),            EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_BRST", SldBurstChance.Value.ToString(),       EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("KILLMEY_HOLD", SldHold.Value.ToString(),              EnvironmentVariableTarget.User);
        }

        private void LoadConfig()
        {
            try
            {
                var c = Environment.GetEnvironmentVariable("KILLMEY_CPS", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(c)) TxtCps.Text = c;

                var m = Environment.GetEnvironmentVariable("KILLMEY_MOD", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(m)) currentModifier = uint.Parse(m);

                var k = Environment.GetEnvironmentVariable("KILLMEY_KEY", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(k)) currentKey = uint.Parse(k);

                var l = Environment.GetEnvironmentVariable("KILLMEY_LMB", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(l))
                {
                    BtnLmb.IsChecked = bool.Parse(l);
                    BtnRmb.IsChecked = !BtnLmb.IsChecked;
                }

                var anti = Environment.GetEnvironmentVariable("KILLMEY_ANTI", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(anti)) ChkAntiDetect.IsChecked = bool.Parse(anti);

                var jit = Environment.GetEnvironmentVariable("KILLMEY_JIT", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(jit) && double.TryParse(jit, out double jitVal)) SldJitter.Value = jitVal;

                var brst = Environment.GetEnvironmentVariable("KILLMEY_BRST", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(brst) && double.TryParse(brst, out double brstVal)) SldBurstChance.Value = brstVal;

                var hold = Environment.GetEnvironmentVariable("KILLMEY_HOLD", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(hold) && double.TryParse(hold, out double holdVal)) SldHold.Value = holdVal;

                // Відновлюємо текст хоткею
                if (currentModifier != 0 || currentKey != 0)
                {
                    string modStr = "";
                    if ((currentModifier & 0x0002) != 0) modStr += "Ctrl + ";
                    if ((currentModifier & 0x0004) != 0) modStr += "Shift + ";
                    if ((currentModifier & 0x0001) != 0) modStr += "Alt + ";
                    Key key = KeyInterop.KeyFromVirtualKey((int)currentKey);
                    TxtHotkey.Text = modStr + key.ToString();
                }
            }
            catch { /* перший запуск — залишаємо дефолтні налаштування */ }
        }

        // ─── UI Події ───────────────────────────────────────────────────────────
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void MouseMode_Changed(object sender, RoutedEventArgs e)
        {
            if (BtnLmb == null || BtnRmb == null) return;
            var activeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FF88"));
            var idleBrush   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A1A"));

            BtnLmb.Background = BtnLmb.IsChecked == true ? activeBrush : idleBrush;
            BtnLmb.Foreground = BtnLmb.IsChecked == true ? Brushes.Black : Brushes.Gray;
            BtnRmb.Background = BtnRmb.IsChecked == true ? activeBrush : idleBrush;
            BtnRmb.Foreground = BtnRmb.IsChecked == true ? Brushes.Black : Brushes.Gray;
            SaveConfig();
        }

        private void TxtCps_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCps.Text, out int cps) || cps < 1) TxtCps.Text = "100";
            SaveConfig();
        }

        private void AntiDetect_Toggle(object sender, RoutedEventArgs e) => SaveConfig();
        private void BtnStart_Click(object sender, RoutedEventArgs e)    => StartClicker();
        private void BtnStop_Click(object sender, RoutedEventArgs e)     => StopClicker();
        private void Minimize_Click(object sender, RoutedEventArgs e)    => this.WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e)       { StopClicker(); SaveConfig(); this.Close(); }
    }
}
