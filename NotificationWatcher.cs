using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Microsoft.Win32;

namespace WinNotch
{
    public class NotificationItem
    {
        public string AppId, AppName, Title, Body;
        public ImageSource Icon;

        readonly object _lock = new object();
        bool _resolved;
        Action<string, ImageSource> _cb;

        /// <summary>Вызовет cb, когда будут известны настоящие имя и иконка приложения (или сразу, если уже известны).</summary>
        public void WhenResolved(Action<string, ImageSource> cb)
        {
            string n; ImageSource i;
            lock (_lock)
            {
                if (!_resolved) { _cb = cb; return; }
                n = AppName; i = Icon;
            }
            cb(n, i);
        }

        internal void SetResolved(string name, ImageSource icon)
        {
            Action<string, ImageSource> cb;
            lock (_lock)
            {
                AppName = name; Icon = icon; _resolved = true;
                cb = _cb; _cb = null;
            }
            if (cb != null) cb(name, icon);
        }
    }

    class AppVisual
    {
        public string Name;
        public ImageSource Icon;
    }

    /// <summary>
    /// Следит за новыми тостами Windows, читая КОПИЮ системной базы уведомлений (только чтение, оригинал не трогается).
    /// Официальный API (UserNotificationListener) требует пакетной идентичности (MSIX) и в обычном exe не работает.
    /// О новых записях узнаём мгновенно через FileSystemWatcher; опрос раз в 0,6 с — страховка.
    /// </summary>
    public sealed class NotificationWatcher : IDisposable
    {
        public event Action<NotificationItem> Received;

        static readonly string Src = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications", "wpndatabase.db");
        static readonly string Tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinNotch");
        static readonly HashSet<string> Generic = new HashSet<string> { "microsoft", "windows", "desktop", "store", "app" };

        readonly Dictionary<string, AppVisual> _visuals = new Dictionary<string, AppVisual>();
        Timer _timer;
        Timer _debounce;
        FileSystemWatcher _fsw;
        HashSet<long> _seen;     // какие уведомления уже были (не зависит от нумерации Id)
        DateTime _stamp;
        int _busy;
        int _failures;
        DateTime _nextTry = DateTime.MinValue;

        public void Start()
        {
            _debounce = new Timer(_ => Poll(true), null, Timeout.Infinite, Timeout.Infinite);
            _timer = new Timer(_ => Poll(false), null, 600, 600);
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Src);
                _fsw = new FileSystemWatcher(dir, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                FileSystemEventHandler h = (s, e) => Kick();
                _fsw.Changed += h;
                _fsw.Created += h;
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception ex) { App.Log(ex); } // без watcher'а работает обычный опрос
        }

        void Kick()
        {
            var d = _debounce;
            if (d != null) { try { d.Change(40, Timeout.Infinite); } catch { } }
        }

        public void Dispose()
        {
            if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); }
            if (_debounce != null) _debounce.Dispose();
            if (_timer != null) _timer.Dispose();
            CleanTmp();
        }

        static void CleanTmp()
        {
            try { if (Directory.Exists(Tmp)) Directory.Delete(Tmp, true); } catch { }
        }

        void Poll(bool force)
        {
            if (DateTime.UtcNow < _nextTry) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                if (!File.Exists(Src)) return;
                string wal = Src + "-wal";
                DateTime stamp = File.GetLastWriteTimeUtc(Src);
                if (File.Exists(wal))
                {
                    var w = File.GetLastWriteTimeUtc(wal);
                    if (w > stamp) stamp = w;
                }
                if (!force && stamp == _stamp) return;

                Directory.CreateDirectory(Tmp);
                string db = System.IO.Path.Combine(Tmp, "w.db");
                CopyShared(Src, db);
                if (File.Exists(wal)) CopyShared(wal, db + "-wal");
                else if (File.Exists(db + "-wal")) File.Delete(db + "-wal");
                if (File.Exists(db + "-shm")) File.Delete(db + "-shm");

                var fresh = new List<Tuple<object, string>>();
                var current = new HashSet<long>();
                using (var c = new SQLiteConnection("Data Source=" + db + ";Pooling=False"))
                {
                    c.Open();
                    using (var q = new SQLiteCommand(
                        "SELECT n.Id, n.Payload, h.PrimaryId FROM Notification n " +
                        "LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId ORDER BY n.Id", c))
                    using (var r = q.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            long id = r.GetInt64(0);
                            current.Add(id);
                            if (_seen != null && !_seen.Contains(id))
                                fresh.Add(Tuple.Create(r.GetValue(1), Convert.ToString(r.GetValue(2))));
                        }
                    }
                }
                _stamp = stamp;
                _seen = current;   // первый проход только запоминает уже имеющиеся уведомления
                _failures = 0;

                // Если пришла пачка (например, после сна) — показываем только последние три
                foreach (var row in fresh.Skip(Math.Max(0, fresh.Count - 3)))
                    Emit(row.Item1, row.Item2 ?? "");
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _failures++;
                // пауза до повторной попытки растёт: 2, 4, 8 … до 60 секунд
                _nextTry = DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(_failures, 6))));
            }
            finally
            {
                CleanTmp(); // не оставляем копию с текстами уведомлений на диске
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        static void CopyShared(string src, string dst)
        {
            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var o = File.Create(dst))
                fs.CopyTo(o);
        }

        void Emit(object payload, string appId)
        {
            string title, body;
            if (!TryParse(payload, out title, out body)) return;
            if (!AllowedByUser(appId)) return;
            var h = Received;
            if (h == null) return;

            // Уведомление уходит на экран сразу. Поиск настоящей иконки (он может занять сотни миллисекунд
            // при первом уведомлении от приложения) выполняется фоном и подставляется, когда готов.
            var cached = TryGetCached(appId);
            var item = new NotificationItem
            {
                AppId = appId,
                AppName = cached != null ? cached.Name : NiceName(appId),
                Title = title,
                Body = body,
                Icon = cached != null ? cached.Icon : null
            };
            if (cached != null) item.SetResolved(cached.Name, cached.Icon);
            h(item);

            if (cached == null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { var v = Resolve(appId); item.SetResolved(v.Name, v.Icon); }
                    catch { item.SetResolved(item.AppName, null); }
                });
            }
        }

        // Уважаем настройки самой Windows: уведомления выключены глобально или для этого приложения / баннеры отключены
        static bool AllowedByUser(string id)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
                {
                    var t = k == null ? null : k.GetValue("ToastEnabled");
                    if (t is int && (int)t == 0) return false;
                }
                if (string.IsNullOrEmpty(id)) return true;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + id))
                {
                    if (k == null) return true;
                    var en = k.GetValue("Enabled");
                    if (en is int && (int)en == 0) return false;
                    var sb = k.GetValue("ShowBanner");
                    if (sb is int && (int)sb == 0) return false;
                }
            }
            catch { }
            return true;
        }

        static bool TryParse(object blob, out string title, out string body)
        {
            title = body = "";
            try
            {
                string xml;
                var b = blob as byte[];
                if (b != null)
                    xml = (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b);
                else
                    xml = Convert.ToString(blob);
                xml = xml.Trim('\uFEFF', '\0', ' ', '\r', '\n');

                var doc = new XmlDocument { XmlResolver = null };
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var rd = XmlReader.Create(new StringReader(xml), settings))
                    doc.Load(rd);
                if (doc.DocumentElement == null || doc.DocumentElement.Name != "toast") return false;

                var nodes = doc.SelectNodes("//visual//text");
                var parts = new List<string>();
                if (nodes != null)
                    foreach (XmlNode n in nodes)
                        if (!string.IsNullOrWhiteSpace(n.InnerText)) parts.Add(n.InnerText.Trim());
                if (parts.Count == 0) return false;

                title = parts[0];
                body = string.Join(" ", parts.Skip(1));
                return true;
            }
            catch { return false; }
        }

        // ───────────── Имя и иконка приложения ─────────────

        static string NiceName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string s = id;
            int i = s.IndexOf('!'); if (i > 0) s = s.Substring(0, i);
            i = s.IndexOf('_'); if (i > 0) s = s.Substring(0, i);
            i = s.LastIndexOf('.'); if (i >= 0 && i < s.Length - 1) s = s.Substring(i + 1);
            return s;
        }

        AppVisual TryGetCached(string id)
        {
            lock (_visuals)
            {
                AppVisual v;
                return _visuals.TryGetValue(id ?? "", out v) ? v : null;
            }
        }

        AppVisual Resolve(string id)
        {
            id = id ?? "";
            var cached = TryGetCached(id);
            if (cached != null) return cached;

            var v = new AppVisual { Name = NiceName(id) };
            try { FromPackage(id, v); } catch { }
            if (v.Icon == null) { try { FromProcess(id, v); } catch { } }

            lock (_visuals) _visuals[id] = v;
            return v;
        }

        // Приложения из Microsoft Store (AUMID вида «Пакет!App»): настоящее имя и логотип из манифеста
        static void FromPackage(string id, AppVisual v)
        {
            if (id.IndexOf('!') < 0) return;
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(id);
            var di = info.DisplayInfo;
            if (!string.IsNullOrWhiteSpace(di.DisplayName)) v.Name = di.DisplayName;

            var logo = di.GetLogo(new Windows.Foundation.Size(64, 64));
            using (var s = logo.OpenReadAsync().AsTask().GetAwaiter().GetResult())
            using (var ms = new MemoryStream())
            {
                s.AsStreamForRead().CopyTo(ms);
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                v.Icon = bi;
            }
        }

        // Обычные программы: ищем запущенный процесс, похожий на ID приложения, и берём иконку и название из его exe
        static void FromProcess(string id, AppVisual v)
        {
            var tokens = id.Split(new[] { '.', '!', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(t => t.ToLowerInvariant())
                           .Where(t => t.Length >= 4 && !Generic.Contains(t))
                           .ToArray();
            if (tokens.Length == 0) return;

            string bestPath = null;
            int bestLen = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    if (n.Length < 4 || n.Length <= bestLen) continue;
                    if (!tokens.Any(t => t.Contains(n) || n.Contains(t))) continue;
                    string path = p.MainModule.FileName;
                    bestPath = path; bestLen = n.Length; // предпочитаем самое «длинное» (точное) совпадение
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (bestPath == null) return;

            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(bestPath);
                string pn = !string.IsNullOrWhiteSpace(fvi.ProductName) ? fvi.ProductName : fvi.FileDescription;
                if (!string.IsNullOrWhiteSpace(pn)) v.Name = pn.Trim();
            }
            catch { }

            v.Icon = LoadIcon(bestPath);
        }

        static ImageSource LoadIcon(string path)
        {
            // сначала пробуем крупную (64 px) иконку из ресурсов exe, иначе стандартную 32 px
            try
            {
                var h = new IntPtr[1];
                var ids = new int[1];
                uint n = PrivateExtractIcons(path, 0, 64, 64, h, ids, 1, 0);
                if (n > 0 && h[0] != IntPtr.Zero)
                {
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                    finally { DestroyIcon(h[0]); }
                }
            }
            catch { }
            try
            {
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
            }
            catch { return null; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
    }
}
