using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;

namespace WinNotch
{
    public partial class NotchWindow : Window
    {
        enum Mode { Idle, Media, Info, Notif }

        /// <summary>Пружина как в iOS: сохраняет скорость при смене цели, поэтому любое движение выглядит «живым».</summary>
        sealed class Spring
        {
            public double X, V, T, K = 385, C = 28;

            public bool Settled
            {
                get { return Math.Abs(X - T) < 0.12 && Math.Abs(V) < 1.0; }
            }

            public void Step(double dt)
            {
                int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
                double h = dt / n;
                for (int i = 0; i < n; i++)
                {
                    V += (-K * (X - T) - C * V) * h;
                    X += V * h;
                }
            }

            public void Snap(double t) { X = T = t; V = 0; }
        }

        static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        static readonly Color[] Palette =
        {
            Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58),
            Color.FromRgb(0x40, 0xC8, 0xE0), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x5E, 0x5C, 0xE6),
            Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x37, 0x5F)
        };
        static readonly Brush BrushWhite = Freeze(new SolidColorBrush(Colors.White));
        static readonly Brush BrushGreen = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
        static readonly Brush BrushRed = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));

        static Brush Freeze(Brush b) { b.Freeze(); return b; }
        static DispatcherTimer MakeTimer(double ms)
        {
            return new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
        }

        GlobalSystemMediaTransportControlsSessionManager _mgr;
        GlobalSystemMediaTransportControlsSession _session;
        readonly NotificationWatcher _watcher = new NotificationWatcher();

        readonly DispatcherTimer _tick = MakeTimer(250);
        readonly DispatcherTimer _expand = MakeTimer(70);      // почти мгновенная реакция на наведение
        readonly DispatcherTimer _collapse = MakeTimer(260);
        readonly DispatcherTimer _notifTimer = MakeTimer(5000);

        readonly Spring _sw = new Spring();
        readonly Spring _sh = new Spring();
        bool _springOn;
        long _lastTs;
        TimeSpan _lastRender = TimeSpan.MinValue;

        bool _hover, _playing, _hasMedia, _notifActive, _fullscreen, _eqOn, _hideIdle, _scrubbing, _island;
        bool? _hasBattery;
        Mode _mode = (Mode)(-1);
        double _w = -1, _h = -1;
        double _total, _startSec;
        int _gen;
        string _trackKey;
        BitmapImage _art;
        NotificationItem _currentNotif;
        DateTime _progressHold = DateTime.MinValue;

        ScaleTransform[] _bars;
        int[] _barMs;

        public bool ShowNotifications { get; set; }

        public bool HideIdle
        {
            get { return _hideIdle; }
            set { _hideIdle = value; Refresh(); }
        }

        /// <summary>true — «остров» как на iPhone, false — челка как на MacBook.</summary>
        public bool IslandStyle
        {
            get { return _island; }
            set { if (_island == value) return; _island = value; ApplyStyle(); }
        }

        // размеры зависят от стиля
        double BarH { get { return _island ? 34 : 32; } }
        double PadTop { get { return _island ? 18 : 40; } }
        double PadX { get { return _island ? 22 : 24; } }
        double PadBottom { get { return 20; } }

        public NotchWindow()
        {
            InitializeComponent();
            ShowNotifications = true;
            _hideIdle = Settings.Get("HideIdle", false);
            _island = Settings.Get("Island", true);
            _bars = new[] { Sc1, Sc2, Sc3, Sb1, Sb2, Sb3, Sb4 };
            _barMs = new[] { 420, 300, 520, 400, 280, 480, 340 };
            Reposition();
            ApplyStyle();

            _tick.Tick += (s, e) => OnTick();
            _expand.Tick += (s, e) => { _expand.Stop(); _hover = true; Refresh(); };
            _collapse.Tick += (s, e) =>
            {
                if (_scrubbing) return;
                _collapse.Stop();
                _hover = false;
                Refresh();
            };
            _notifTimer.Tick += (s, e) =>
            {
                if (_hover) return; // пока курсор на челке — уведомление не исчезает
                _notifTimer.Stop();
                _notifActive = false;
                Refresh();
            };

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SourceInitialized += (s, e) => MakeToolWindow();
            Loaded += async (s, e) =>
            {
                _tick.Start();
                _watcher.Received += OnNotification;
                _watcher.Start();
                WarmUp();

                // На старте системы медиа-служба может быть ещё не готова — пробуем несколько раз.
                for (int i = 0; i < 8 && _mgr == null; i++)
                {
                    try { await InitMediaAsync(); }
                    catch (Exception ex) { App.Log(ex); await Task.Delay(5000); }
                }
            };
            Closed += (s, e) =>
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                _watcher.Dispose();
            };
        }

        // Невидимый «прогон» раскрытия при запуске: прогревает JIT и отрисовку,
        // чтобы самое первое наведение не тормозило. Потом челка плавно появляется.
        async void WarmUp()
        {
            try
            {
                Notch.Opacity = 0;
                _hover = true; Refresh();
                await Task.Delay(450);
                _hover = false; Refresh();
                await Task.Delay(450);
            }
            catch { }
            Notch.Opacity = 1;
            Notch.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { FillBehavior = FillBehavior.Stop });
        }

        // ───────────── Окно: не берёт фокус, нет в Alt+Tab ─────────────

        void MakeToolWindow()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, -20);
            SetWindowLong(h, -20, ex | 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/);
        }

        void Reposition()
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(Reposition));
        }

        void ApplyStyle()
        {
            Notch.Margin = new Thickness(0, _island ? 8 : 0, 0, 0);
            EarL.Visibility = EarR.Visibility = _island ? Visibility.Collapsed : Visibility.Visible;
            TopBar.Height = BarH;
            _mode = (Mode)(-1);
            Refresh(true);
        }

        // ───────────── Размеры, пружина, переходы ─────────────

        bool HasBattery()
        {
            if (_hasBattery == null)
            {
                try
                {
                    var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                    _hasBattery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0;
                }
                catch { _hasBattery = false; }
            }
            return _hasBattery.Value;
        }

        double TargetWidth(Mode m)
        {
            switch (m)
            {
                case Mode.Media: return _island ? 380 : 400;
                case Mode.Info: return _island ? 340 : 330;
                case Mode.Notif: return _island ? 372 : 384;
                default: return _hasMedia ? (_island ? 172 : 232) : (_island ? 122 : 180);
            }
        }

        void SetPanels(Mode m)
        {
            MediaPanel.Visibility = m == Mode.Media ? Visibility.Visible : Visibility.Collapsed;
            InfoPanel.Visibility = m == Mode.Info ? Visibility.Visible : Visibility.Collapsed;
            NotifPanel.Visibility = m == Mode.Notif ? Visibility.Visible : Visibility.Collapsed;
        }

        // Высота корпуса = реальная высота содержимого + отступы. Именно это убирает «обрезание» кнопок.
        double MeasureHeight(Mode m, double w)
        {
            SetPanels(m);
            double bw = w - 2 * PadX;
            Body.Width = bw;
            Body.Margin = new Thickness(0, PadTop, 0, 0);
            Body.Measure(new Size(bw, double.PositiveInfinity));
            return Body.DesiredSize.Height + PadBottom;
        }

        void Refresh(bool force = false)
        {
            Mode m = _notifActive ? Mode.Notif : _hover ? (_hasMedia ? Mode.Media : Mode.Info) : Mode.Idle;

            bool visible = !_fullscreen && !(m == Mode.Idle && _hideIdle && !_hasMedia);
            Notch.Visibility = visible ? Visibility.Visible : Visibility.Hidden;

            UpdateMini(m);

            double w = TargetWidth(m);
            double h;
            if (m == Mode.Idle) h = BarH;
            else
            {
                if (m == Mode.Info) UpdateInfo();
                h = MeasureHeight(m, w);
            }

            if (!force && m == _mode && Math.Abs(w - _w) < 0.5 && Math.Abs(h - _h) < 0.5) return;

            bool wasOpen = (int)_mode >= 0 && _mode != Mode.Idle;
            bool open = m != Mode.Idle;
            bool grow = open && !wasOpen;
            _mode = m; _w = w; _h = h;

            if (m == Mode.Media) UpdateProgress();

            if (force) { _sw.Snap(w); _sh.Snap(h); ApplySize(); }
            else
            {
                // раскрытие — упругое, с лёгким перелётом; смена панелей — мягче; сворачивание — без отскока
                double zeta = grow ? 0.72 : open ? 0.84 : 0.95;
                double response = grow ? 0.34 : 0.30;
                double om = 2 * Math.PI / response;
                _sw.K = _sh.K = om * om;
                _sw.C = _sh.C = 2 * zeta * om;
                _sw.T = w; _sh.T = h;
                StartSpring();
            }

            Body.IsHitTestVisible = open;
            var op = new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 170 : 80));
            if (open) { if (wasOpen) op.From = 0; else op.BeginTime = TimeSpan.FromMilliseconds(70); }
            else op.Completed += (s, e) => { if (_mode == Mode.Idle) SetPanels(Mode.Idle); };
            Body.BeginAnimation(UIElement.OpacityProperty, op);

            if (open)
            {
                double fromScale = wasOpen ? 0.96 : 0.88;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var sx = new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
                var sy = new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
                BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
                BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
            }
        }

        void StartSpring()
        {
            if (_springOn) return;
            _springOn = true;
            _lastTs = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRender;
        }

        void OnRender(object sender, EventArgs e)
        {
            var re = e as RenderingEventArgs;
            if (re != null)
            {
                if (re.RenderingTime == _lastRender) return; // одно событие на кадр
                _lastRender = re.RenderingTime;
            }
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastTs) / (double)Stopwatch.Frequency;
            _lastTs = now;
            if (dt <= 0) return;
            if (dt > 0.05) dt = 0.05;

            _sw.Step(dt);
            _sh.Step(dt);
            if (_sw.Settled && _sh.Settled)
            {
                _sw.Snap(_sw.T); _sh.Snap(_sh.T);
                ApplySize();
                CompositionTarget.Rendering -= OnRender;
                _springOn = false;
                return;
            }
            ApplySize();
        }

        void ApplySize()
        {
            double w = Math.Max(24, _sw.X), h = Math.Max(24, _sh.X);
            Notch.Width = w;
            Notch.Height = h;
            // радиус зависит от текущей высоты: свёрнутый — «таблетка», раскрытый — крупные скругления
            double r = _island ? Math.Min(h / 2, 38) : Math.Min(h * 0.4, 26);
            double top = _island ? r : 0;
            Shell.CornerRadius = new CornerRadius(top, top, r, r);
            Shell.Clip = RoundRect(w, h, top, r);
        }

        static Geometry RoundRect(double w, double h, double top, double bottom)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(top, 0), true, true);
                c.LineTo(new Point(w - top, 0), true, false);
                if (top > 0) c.ArcTo(new Point(w, top), new Size(top, top), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(w, h - bottom), true, false);
                if (bottom > 0) c.ArcTo(new Point(w - bottom, h), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(bottom, h), true, false);
                if (bottom > 0) c.ArcTo(new Point(0, h - bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(0, top), true, false);
                if (top > 0) c.ArcTo(new Point(top, 0), new Size(top, top), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        void UpdateMini(Mode m)
        {
            bool idle = m == Mode.Idle;
            MiniArt.Visibility = idle && _hasMedia ? Visibility.Visible : Visibility.Collapsed;
            Eq.Visibility = idle && _hasMedia && _playing ? Visibility.Visible : Visibility.Collapsed;
            SetEq(_hasMedia && _playing && (idle || m == Mode.Media));
        }

        void SetEq(bool on)
        {
            if (on == _eqOn) return;
            _eqOn = on;
            for (int i = 0; i < _bars.Length; i++)
            {
                if (on)
                {
                    var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(_barMs[i]))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(a, 30);
                    _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }

        // Раскрытие почти сразу (70 мс), но не мгновенно — чтобы случайный пролёт курсора не срабатывал
        void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapse.Stop();
            if (_hover || _notifActive) { _hover = true; Refresh(); return; }
            _expand.Stop();
            _expand.Start();
        }

        void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            _expand.Stop();
            _collapse.Stop();
            _collapse.Start();
        }

        // Колесо мыши над РАСКРЫТОЙ челкой = громкость. В свёрнутом виде не трогаем — не мешаем прокрутке вкладок.
        void Notch_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_mode != Mode.Media && _mode != Mode.Info) return;
            byte vk = e.Delta > 0 ? (byte)0xAF : (byte)0xAE;
            for (int i = 0; i < 2; i++)
            {
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, 2, UIntPtr.Zero);
            }
            e.Handled = true;
        }

        void OnTick()
        {
            bool fs = IsFullscreenAppActive();
            if (fs != _fullscreen) { _fullscreen = fs; Refresh(); }
            if (_mode == Mode.Info) UpdateInfo();
            if (_mode == Mode.Media) UpdateProgress();
        }

        static string Cap(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Ru) + s.Substring(1);
        }

        void UpdateInfo()
        {
            var now = DateTime.Now;
            ClockText.Text = now.ToString("HH:mm");
            DayText.Text = Cap(now.ToString("dddd", Ru));
            DateText.Text = now.ToString("d MMMM", Ru);

            if (!HasBattery()) { BatteryPanel.Visibility = Visibility.Collapsed; return; }
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                float p = ps.BatteryLifePercent;
                if (p < 0 || p > 1) { BatteryPanel.Visibility = Visibility.Collapsed; return; }
                BatteryPanel.Visibility = Visibility.Visible;
                int pct = (int)Math.Round(p * 100);
                bool plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                BatteryText.Text = pct + "%";
                BatFill.Width = Math.Max(3, 25.5 * pct / 100.0);
                BatFill.Background = plugged ? BrushGreen : pct <= 20 ? BrushRed : BrushWhite;
            }
            catch { BatteryPanel.Visibility = Visibility.Collapsed; }
        }

        // ───────────── Музыка (системные медиа-сессии Windows) ─────────────

        async Task InitMediaAsync()
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.CurrentSessionChanged += (s, a) => Dispatcher.BeginInvoke(new Action(AttachSession));
            _mgr = mgr;
            AttachSession();
        }

        void AttachSession()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnMediaChanged;
            }
            _session = _mgr.GetCurrentSession();
            _trackKey = null;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnMediaChanged;
            }
            var ignore = RefreshMediaAsync();
        }

        void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object a)
        {
            Dispatcher.BeginInvoke(new Action(() => { var ignore = RefreshMediaAsync(); }));
        }

        void ShowPlayState()
        {
            PlayShape.Visibility = _playing ? Visibility.Collapsed : Visibility.Visible;
            PauseShape.Visibility = _playing ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task RefreshMediaAsync()
        {
            int gen = ++_gen; // защита от «гонки»: устаревший ответ не перезапишет свежий
            var s = _session;
            try
            {
                if (s == null)
                {
                    _hasMedia = false; _playing = false; _trackKey = null;
                    ShowPlayState();
                    Refresh();
                    return;
                }

                var status = s.GetPlaybackInfo().PlaybackStatus;
                _playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                ShowPlayState();
                var props = await s.TryGetMediaPropertiesAsync();
                if (gen != _gen) return;

                _hasMedia = !string.IsNullOrEmpty(props.Title)
                            && status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                TitleText.Text = props.Title ?? "";
                ArtistText.Text = props.Artist ?? "";

                // Обложку перезагружаем только при смене трека (или если она ещё не успела прийти)
                string key = props.Title + "\n" + props.Artist + "\n" + props.AlbumTitle;
                if (key != _trackKey || _art == null)
                {
                    _trackKey = key;
                    BitmapImage art = null;
                    if (props.Thumbnail != null)
                    {
                        try
                        {
                            using (var st = await props.Thumbnail.OpenReadAsync())
                            using (var ms = new MemoryStream())
                            {
                                await st.AsStreamForRead().CopyToAsync(ms);
                                ms.Position = 0;
                                art = new BitmapImage();
                                art.BeginInit();
                                art.CacheOption = BitmapCacheOption.OnLoad;
                                art.StreamSource = ms;
                                art.EndInit();
                                art.Freeze();
                            }
                        }
                        catch { art = null; }
                    }
                    if (gen != _gen) return;
                    _art = art;
                    ArtBrush.ImageSource = art;
                    MiniArtBrush.ImageSource = art;
                    ApplyAccent(art);
                }

                Refresh();
                UpdateProgress();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Цвет эквалайзера берём из обложки — маленький живой акцент в монохромном интерфейсе
        void ApplyAccent(BitmapSource art)
        {
            Color c = Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
            var a = art == null ? null : AccentFrom(art);
            if (a.HasValue) c = a.Value;
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            foreach (var r in Eq.Children.OfType<Rectangle>()) r.Fill = brush;
            foreach (var r in EqBig.Children.OfType<Rectangle>()) r.Fill = brush;
        }

        static Color? AccentFrom(BitmapSource src)
        {
            try
            {
                if (src.PixelWidth < 1 || src.PixelHeight < 1) return null;
                var small = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
                var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);

                double r = 0, g = 0, b = 0, wt = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue;
                    double B = px[i], G = px[i + 1], R = px[i + 2];
                    double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                    double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                    double weight = 0.1 + sat; // насыщенные пиксели важнее серых
                    r += R * weight; g += G * weight; b += B * weight; wt += weight;
                }
                if (wt <= 0) return null;
                r /= wt; g /= wt; b /= wt;
                double max = Math.Max(r, Math.Max(g, b));
                if (max < 190) // на чёрном фоне цвет должен быть заметным
                {
                    double k = 210.0 / Math.Max(max, 1);
                    r = Math.Min(255, r * k); g = Math.Min(255, g * k); b = Math.Min(255, b * k);
                }
                return Color.FromRgb((byte)r, (byte)g, (byte)b);
            }
            catch { return null; }
        }

        void UpdateProgress()
        {
            var s = _session;
            if (s == null || _scrubbing || DateTime.UtcNow < _progressHold) return;
            try
            {
                var tl = s.GetTimelineProperties();
                double start = tl.StartTime.TotalSeconds;
                double total = (tl.EndTime - tl.StartTime).TotalSeconds;
                double pos = tl.Position.TotalSeconds - start;
                if (_playing) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                _total = total; _startSec = start;

                if (total <= 0) { Fill.Width = 0; TimeCur.Text = ""; TimeTotal.Text = ""; return; }
                pos = Math.Max(0, Math.Min(pos, total));
                TimeCur.Text = Fmt(pos);
                TimeTotal.Text = Fmt(total);
                double tw = TrackLine.ActualWidth > 0 ? TrackLine.ActualWidth : Body.Width;
                Fill.Width = Math.Max(0, tw * pos / total);
            }
            catch { }
        }

        static string Fmt(double sec)
        {
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        // Полоска прогресса: клик или перетаскивание = перемотка. При наведении становится толще.
        double Ratio(MouseEventArgs e)
        {
            if (Track.ActualWidth <= 0) return 0;
            return Math.Max(0, Math.Min(1, e.GetPosition(Track).X / Track.ActualWidth));
        }

        void AnimTrack(double height)
        {
            TrackLine.BeginAnimation(FrameworkElement.HeightProperty,
                new DoubleAnimation(height, TimeSpan.FromMilliseconds(110)));
        }

        void Track_Enter(object sender, MouseEventArgs e) { AnimTrack(8); }
        void Track_Leave(object sender, MouseEventArgs e) { if (!_scrubbing) AnimTrack(5); }

        void Track_Down(object sender, MouseButtonEventArgs e)
        {
            if (_session == null || _total <= 0) return;
            _scrubbing = true;
            Track.CaptureMouse();
            Scrub(e);
            e.Handled = true;
        }

        void Track_Move(object sender, MouseEventArgs e) { if (_scrubbing) Scrub(e); }

        void Scrub(MouseEventArgs e)
        {
            double r = Ratio(e);
            Fill.Width = TrackLine.ActualWidth * r;
            TimeCur.Text = Fmt(r * _total);
        }

        async void Track_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_scrubbing) return;
            double r = Ratio(e);
            _scrubbing = false;
            Track.ReleaseMouseCapture();
            AnimTrack(Track.IsMouseOver ? 8 : 5);
            if (!Notch.IsMouseOver) { _collapse.Stop(); _collapse.Start(); }

            var s = _session;
            if (s == null || _total <= 0) return;
            _progressHold = DateTime.UtcNow.AddMilliseconds(900); // пока плеер «догонит» новую позицию
            try { await s.TryChangePlaybackPositionAsync((long)((_startSec + r * _total) * TimeSpan.TicksPerSecond)); }
            catch { }
        }

        void Track_Lost(object sender, MouseEventArgs e) { _scrubbing = false; }

        async void Prev_Click(object sender, RoutedEventArgs e)
        {
            var s = _session; if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch { }
        }

        // Значок меняется сразу, не дожидаясь ответа плеера — кнопка «отзывчивая»
        async void Play_Click(object sender, RoutedEventArgs e)
        {
            var s = _session; if (s == null) return;
            _playing = !_playing;
            ShowPlayState();
            UpdateMini(_mode);
            try
            {
                await s.TryTogglePlayPauseAsync();
                await Task.Delay(350);
                await RefreshMediaAsync(); // сверяем с реальным состоянием плеера
            }
            catch { }
        }

        async void Next_Click(object sender, RoutedEventArgs e)
        {
            var s = _session; if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch { }
        }

        // ───────────── Уведомления ─────────────

        void ApplyNotifVisual(string name, ImageSource icon)
        {
            name = name ?? "";
            NotifApp.Text = name;
            NotifIcon.Source = icon;
            NotifInitial.Text = icon == null && name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "";
            var bg = icon != null ? Color.FromRgb(0x2C, 0x2C, 0x2E) : BadgeColor(name);
            NotifBadge.Background = new SolidColorBrush(bg);
        }

        void OnNotification(NotificationItem n)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ShowNotifications) return;
                _currentNotif = n;
                ApplyNotifVisual(n.AppName, n.Icon);
                NotifTitle.Text = n.Title ?? "";
                NotifBody.Text = n.Body ?? "";

                _notifActive = true;
                _notifTimer.Stop();
                _notifTimer.Start();
                Refresh();

                // Уведомление показывается мгновенно; настоящая иконка и имя подгружаются фоном и «встают на место»
                n.WhenResolved((name, icon) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentNotif == n) ApplyNotifVisual(name, icon);
                })));
            }));
        }

        void Notif_Click(object sender, MouseButtonEventArgs e)
        {
            _notifTimer.Stop();
            _notifActive = false;
            Refresh();
        }

        static Color BadgeColor(string name)
        {
            int h = 0;
            foreach (char c in name) h = (h * 31 + c) & 0x7FFFFFFF;
            return Palette[h % Palette.Length];
        }

        // ───────────── Полноэкранные игры/видео: челка прячется ─────────────

        static bool IsFullscreenAppActive()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;

                var cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;

                // нас интересует только основной монитор — там висит челка
                IntPtr mon = MonitorFromWindow(h, 2 /*NEAREST*/);
                IntPtr primary = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /*PRIMARY*/);
                if (mon != primary) return false;

                RECT r;
                if (!GetWindowRect(h, out r)) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(mon, ref mi)) return false;

                bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                              r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
                if (!covers) return false;

                // обычное окно с заголовком (например, при автоскрытии панели задач) — не полноэкранное
                const int WS_CAPTION = 0x00C00000;
                return (GetWindowLong(h, -16) & WS_CAPTION) != WS_CAPTION;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }
}
