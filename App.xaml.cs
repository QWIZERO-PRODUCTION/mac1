using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace WinNotch
{
    /// <summary>Мелкие настройки в HKCU\Software\WinNotch (без файлов и без админ-прав).</summary>
    static class Settings
    {
        const string Key = @"Software\WinNotch";

        public static bool Get(string name, bool def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    var v = k == null ? null : k.GetValue(name);
                    return v == null ? def : Convert.ToInt32(v) != 0;
                }
            }
            catch { return def; }
        }

        public static void Set(string name, bool val)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue(name, val ? 1 : 0);
            }
            catch { }
        }
    }

    public partial class App : Application
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "WinNotch";

        Mutex _mutex;
        bool _ownsMutex;
        WF.NotifyIcon _tray;
        NotchWindow _window;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, @"Local\WinNotch.SingleInstance", out _ownsMutex);
            if (!_ownsMutex) { Shutdown(); return; }

            base.OnStartup(e);
            DispatcherUnhandledException += (s, a) => { Log(a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log(a.ExceptionObject as Exception);

            // Первый запуск: сами прописываем автозапуск. Дальше про запуск можно забыть.
            if (!Settings.Get("Init", false)) { Settings.Set("Init", true); SetAutostart(true); }
            else if (IsAutostart()) SetAutostart(true); // обновить путь, если папку переместили

            BuildTray();

            // При старте Windows даём системе 4 секунды, чтобы поднялись проводник и медиа-службы.
            if (e.Args.Contains("--autostart"))
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (s, a) => { t.Stop(); ShowNotch(); };
                t.Start();
            }
            else ShowNotch();
        }

        void ShowNotch()
        {
            _window = new NotchWindow();
            _window.ShowNotifications = !Settings.Get("NoNotif", false);
            _window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_ownsMutex && _mutex != null) { try { _mutex.ReleaseMutex(); } catch { } }
            base.OnExit(e);
        }

        void BuildTray()
        {
            System.Drawing.Icon icon = null;
            try { icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { }
            if (icon == null) icon = System.Drawing.SystemIcons.Application;

            _tray = new WF.NotifyIcon { Icon = icon, Text = "WinNotch", Visible = true };
            var menu = new WF.ContextMenuStrip();

            var auto = new WF.ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true, Checked = IsAutostart() };
            auto.CheckedChanged += (s, a) => SetAutostart(auto.Checked);

            var notif = new WF.ToolStripMenuItem("Показывать уведомления") { CheckOnClick = true, Checked = !Settings.Get("NoNotif", false) };
            notif.CheckedChanged += (s, a) =>
            {
                Settings.Set("NoNotif", !notif.Checked);
                if (_window != null) _window.ShowNotifications = notif.Checked;
            };

            var island = new WF.ToolStripMenuItem("Стиль «остров» (как на iPhone)") { CheckOnClick = true, Checked = Settings.Get("Island", true) };
            island.CheckedChanged += (s, a) =>
            {
                Settings.Set("Island", island.Checked);
                if (_window != null) _window.IslandStyle = island.Checked;
            };

            var hide = new WF.ToolStripMenuItem("Прятать, когда ничего не происходит") { CheckOnClick = true, Checked = Settings.Get("HideIdle", false) };
            hide.CheckedChanged += (s, a) =>
            {
                Settings.Set("HideIdle", hide.Checked);
                if (_window != null) _window.HideIdle = hide.Checked;
            };

            var exit = new WF.ToolStripMenuItem("Выход");
            exit.Click += (s, a) => Shutdown();

            menu.Items.AddRange(new WF.ToolStripItem[] { auto, notif, island, hide, new WF.ToolStripSeparator(), exit });
            _tray.ContextMenuStrip = menu;
        }

        static bool IsAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(AppName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --autostart");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex) { Log(ex); }
        }

        static string _lastLog;

        /// <summary>Лог ошибок: без спама одинаковыми строками и не больше ~200 КБ.</summary>
        public static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string msg = ex.GetType().Name + ": " + ex.Message;
                if (msg == _lastLog) return;
                _lastLog = msg;

                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNotch");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "error.log");
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                File.AppendAllText(file, DateTime.Now + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
