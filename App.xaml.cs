using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace WpfTrayKeyboardSwitcher
{
    public partial class App : Application
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static IntPtr hookId = IntPtr.Zero;
        private HookProc proc;

        private MenuItem enableItem;
        private MenuItem disableItem;
        private bool ctrlPressed = false;
        private bool otherKeyPressed = false;

        private TaskbarIcon trayIcon;

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_INPUTLANGCHANGEREQUEST = 0x0050;
        private const int WH_KEYBOARD_LL = 13;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        // ↓↓↓ Single-instance + IPC ↓↓↓
        private static Mutex singletonMutex;
        private const string MutexName = "WpfTrayKeyboardSwitcher_Mutex";
        private const string PipeName = "WpfTrayKeyboardSwitcher_Pipe";
        private Thread pipeServerThread;
        private CancellationTokenSource pipeCts;
        // ↑↑↑ Single-instance + IPC ↑↑↑

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            singletonMutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                // Второй запуск → отправляем команду и завершаем
                if (e.Args.Length > 0)
                    TrySendCommandToPrimary(e.Args[0]);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // ← создаём иконку только в первом экземпляре
            trayIcon = new TaskbarIcon
            {
                IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/ru.ico")),
                ToolTipText = "Keyboard Switcher",
                ContextMenu = new ContextMenu()
            };

            // меню
            enableItem = new MenuItem { Header = "Включить", IsCheckable = true, IsChecked = true };
            disableItem = new MenuItem { Header = "Выключить", IsCheckable = true };
            var exitItem = new MenuItem { Header = "Выход" };

            enableItem.Click += Enable_Click;
            disableItem.Click += Disable_Click;
            exitItem.Click += Exit_Click;

            trayIcon.ContextMenu.Items.Add(enableItem);
            trayIcon.ContextMenu.Items.Add(disableItem);
            trayIcon.ContextMenu.Items.Add(new Separator());
            trayIcon.ContextMenu.Items.Add(exitItem);

            proc = HookCallback;
            EnableHook();

            StartPipeServer();

            if (e.Args.Length > 0)
                ProcessCommand(e.Args[0]);
        }


        private void EnableHook()
        {
            if (hookId == IntPtr.Zero)
            {
                hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
            }
        }

        private void DisableHook()
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
        }

        private void SwitchLayout(string klid, string iconPath)
        {
            IntPtr hkl = LoadKeyboardLayout(klid, 1);
            if (hkl == IntPtr.Zero) return;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, (IntPtr)1, hkl);
            trayIcon.IconSource = new BitmapImage(new Uri(iconPath));
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if (wParam == (IntPtr)WM_KEYDOWN)
                {
                    if (kb.vkCode == VK_LCONTROL || kb.vkCode == VK_RCONTROL)
                    {
                        ctrlPressed = true;
                        otherKeyPressed = false;
                    }
                    else if (ctrlPressed)
                    {
                        otherKeyPressed = true;
                    }
                }
                else if (wParam == (IntPtr)WM_KEYUP)
                {
                    if (kb.vkCode == VK_LCONTROL && ctrlPressed)
                    {
                        if (!otherKeyPressed)
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                 SwitchLayout("00000419", "pack://application:,,,/Resources/ru.ico")));
                           // SwitchLayout("00000419", "pack://application:,,,/Resources/ru.ico");
                        }
                        ctrlPressed = false;
                    }
                    else if (kb.vkCode == VK_RCONTROL && ctrlPressed)
                    {
                        if (!otherKeyPressed)
                        {
                           Application.Current.Dispatcher.BeginInvoke(new Action(() =>                            //     SwitchLayout("00000409", "pack://application:,,,/Resources/en.ico")));
                            SwitchLayout("00000409", "pack://application:,,,/Resources/en.ico")));
                        }
                        ctrlPressed = false;
                    }
                }
            }

            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        // Меню
        private void Enable_Click(object sender, RoutedEventArgs e)
        {
            EnableHook(); // ← включаем хук
            enableItem.IsChecked = true;
            disableItem.IsChecked = false;
        }

        private void Disable_Click(object sender, RoutedEventArgs e)
        {
            DisableHook(); // ← снимаем хук
            enableItem.IsChecked = false;
            disableItem.IsChecked = true;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            // корректно завершить Pipe Server
            StopPipeServer();

            DisableHook(); // ← снимаем хук при выходе
            trayIcon.Dispose();

            try { singletonMutex?.ReleaseMutex(); } catch { /* игнор */ }
            singletonMutex?.Dispose();

            Current.Shutdown();
        }

        // ==== IPC: приём и отправка команд "r" / "e" ====

        private void StartPipeServer()
        {
            pipeCts = new CancellationTokenSource();
            var token = pipeCts.Token;

            pipeServerThread = new Thread(() =>
            {
                // Постоянно создаём сервер и ждём подключений
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Message))
                        using (var reader = new StreamReader(server))
                        {
                            server.WaitForConnection();
                            string command = reader.ReadLine();
                            if (!string.IsNullOrWhiteSpace(command))
                            {
                                // Команда обрабатывается независимо от Enable/Disable — это важно
                                Application.Current.Dispatcher.BeginInvoke(new Action(() => ProcessCommand(command)));
                            }
                        }
                    }
                    catch
                    {
                        // Можно добавить лог, если нужно
                        Thread.Sleep(100);
                    }
                }
            });

            pipeServerThread.IsBackground = true;
            pipeServerThread.Start();
        }

        private void StopPipeServer()
        {
            try
            {
                pipeCts?.Cancel();
            }
            catch { /* игнор */ }

            try
            {
                // Пингуем пайп, чтобы выйти из WaitForConnection
                TrySendCommandToPrimary("ping");
            }
            catch { /* игнор */ }

            try
            {
                pipeServerThread?.Join(500);
            }
            catch { /* игнор */ }

            pipeCts?.Dispose();
            pipeCts = null;
        }

        private void TrySendCommandToPrimary(string arg)
        {
            using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                // Подключаемся к уже запущенному экземпляру (до 1 сек)
                client.Connect(1000);
                using (var writer = new StreamWriter(client))
                {
                    writer.WriteLine(arg);
                    writer.Flush();
                }
            }
        }

        private void ProcessCommand(string command)
        {
            // Нормализуем команду
            command = command?.Trim().ToLowerInvariant();

            if (command == "r")
            {
                SwitchLayout("00000419", "pack://application:,,,/Resources/ru.ico");
            }
            else if (command == "e")
            {
                SwitchLayout("00000409", "pack://application:,,,/Resources/en.ico");
            }
            // Любые другие команды можно игнорировать или логировать
        }
    }
}
