using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace UsageWidget;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run(args);
        if (args.Contains("--probe"))
        {
            try { var s = new UsageClient().ReadAsync(Settings.Load().CodexPath, CancellationToken.None).GetAwaiter().GetResult(); File.WriteAllText(args.Last(), $"Plan={s.Plan}; 5h remaining={s.FiveHour?.Remaining}; weekly remaining={s.Weekly?.Remaining}; blocked={s.Blocked}"); return 0; }
            catch (Exception e) { File.WriteAllText(args.Last(), e.GetType().Name + ": " + e.Message); return 1; }
        }
        bool preview = args.Contains("--preview") || args.Contains("--ui-check");
        using var mutex = new Mutex(true, "Local\\CustomChatGPTUsageWidget", out var first);
        if (!first && !preview) return 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Widget(preview);
        if (args.Contains("--weekly")) window.SetPreviewWindow(true);
        if (args.Contains("--animation-check")) window.Loaded += (_, _) => {
            window.RotateForTest();
            var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            check.Tick += (_, _) => { check.Stop(); bool passed = window.ShowingWeekly && window.RotationOpacity > .99; File.WriteAllText(args.Last(), passed ? "PASS: WPF fade-out, window switch, fade-in completed" : "FAIL: WPF rotation"); app.Shutdown(passed ? 0 : 1); }; check.Start();
        };
        if (args.Contains("--ui-check")) window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() => {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(args.Last()); encoder.Save(file);
            app.Shutdown();
        }), DispatcherPriority.ApplicationIdle);
        if (preview || args.Contains("--animation-check")) app.Run(window);
        else { app.MainWindow = window; window.StartMonitoring(); app.Run(); }
        return 0;
    }
}

public sealed class Widget : Window
{
    readonly Settings settings = Settings.Load();
    readonly UsageClient client = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Forms.NotifyIcon tray = new();
    readonly DispatcherTimer refreshTimer = new();
    readonly DispatcherTimer countdownTimer = new();
    readonly DispatcherTimer presenceTimer = new();
    readonly DispatcherTimer rotateTimer = new();
    readonly AppPresenceState presence = new();
    readonly DesktopAppMonitor appMonitor = new();
    readonly Grid rotating = new();
    readonly TextBlock plan = new(), status = new(), scope = new();
    readonly StackPanel root = new();
    readonly Row five = new("5-hour"), week = new("Weekly");
    readonly bool preview;
    bool busy, exiting;
    bool weekly;
    bool dragging;
    bool? appliedLightTheme;
    DateTimeOffset? updated;
    UsageSnapshot? snapshot;
    Drawing.Icon? trayIcon;
    const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public Widget(bool preview)
    {
        this.preview = preview;
        Title = "ChatGPT Usage"; Width = 344; Height = 44;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        var border = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(7, 3, 7, 3), Child = root };
        Content = border;
        var header = new DockPanel { Height = 16 };
        var hide = Button("−", HideWidget, "Hide until restored from tray"); DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
        var gear = Button("⚙", ShowSettings, "Settings"); DockPanel.SetDock(gear, Dock.Right); header.Children.Add(gear);
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        plan.FontSize = 12; plan.FontWeight = FontWeights.SemiBold; plan.Text = "ChatGPT"; heading.Children.Add(plan);
        scope.Text = "Work / Codex"; scope.FontSize = 9; scope.VerticalAlignment = VerticalAlignment.Center; scope.Margin = new Thickness(7, 0, 0, 0); heading.Children.Add(scope); header.Children.Add(heading); root.Children.Add(header);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock) DragStrip(); };
        rotating.Children.Add(five.Panel); rotating.Children.Add(week.Panel); week.Panel.Visibility = Visibility.Collapsed; root.Children.Add(rotating);
        border.ToolTip = status;
        rotating.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) OpenUsage(); };
        border.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == border) DragStrip(); };
        ApplyTheme();
        Left = settings.Left ?? SystemParameters.WorkArea.Left + 180;
        Top = settings.Top ?? SystemParameters.WorkArea.Bottom - Height - 6;
        Loaded += (_, _) => {
            PositionStrip();
            if (preview) { snapshot = new("plus", "demo", new(71, DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds(), 300), new(95, DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds(), 10080), false); updated = DateTimeOffset.UtcNow; Render(); return; }
            _ = Refresh();
        };
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; HideWidget(); } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { HideWidget(); WindowState = WindowState.Normal; } };
        rotateTimer.Interval = TimeSpan.FromSeconds(settings.RotateSeconds); rotateTimer.Tick += (_, _) => Rotate(); rotateTimer.Start();
        if (!preview)
        {
            tray.Text = "ChatGPT usage: connecting"; tray.Icon = Drawing.SystemIcons.Application; tray.Visible = false;
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(ShowWidget));
            menu.Items.Add("Refresh", null, (_, _) => Dispatcher.Invoke(() => _ = Refresh()));
            menu.Items.Add("Open ChatGPT Usage", null, (_, _) => OpenUsage());
            menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(ShowSettings));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Exit)); tray.ContextMenuStrip = menu;
            tray.BalloonTipClicked += (_, _) => OpenUsage();
            refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.RefreshSeconds, 60, 600)); refreshTimer.Tick += (_, _) => _ = Refresh();
            countdownTimer.Interval = TimeSpan.FromSeconds(15); countdownTimer.Tick += (_, _) => Render();
            presenceTimer.Interval = TimeSpan.FromSeconds(2); presenceTimer.Tick += (_, _) => SyncPresence();
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        }
    }
    static Button Button(string text, Action action, string tip) { var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand }; b.Click += (_, _) => action(); return b; }
    public void StartMonitoring()
    {
        // Retire the previous version's optional startup registration.
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true); key?.DeleteValue("CustomUsageWidget", false);
        presenceTimer.Start(); SyncPresence();
    }
    void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) { if (!exiting && settings.Theme == "System") Dispatcher.BeginInvoke(new Action(ApplyTheme)); }
    void SyncPresence()
    {
        bool changed = presence.Update(appMonitor.IsOpen());
        tray.Visible = presence.Running;
        if (!presence.Running) { Hide(); refreshTimer.Stop(); rotateTimer.Stop(); countdownTimer.Stop(); return; }
        if (presence.ShouldShow && !IsVisible) { Show(); PositionStrip(); }
        if (IsVisible) { if (!rotateTimer.IsEnabled) rotateTimer.Start(); if (!countdownTimer.IsEnabled) countdownTimer.Start(); }
        else { rotateTimer.Stop(); countdownTimer.Stop(); }
        if (changed) { refreshTimer.Start(); _ = Refresh(); }
        if (settings.DockToTaskbar && IsVisible) PositionStrip();
    }
    void HideWidget() { presence.Hide(); Hide(); rotateTimer.Stop(); countdownTimer.Stop(); if (!preview) SavePosition(); }
    void ShowWidget() { if (!presence.Running && !preview) return; presence.Restore(); Show(); Render(); rotateTimer.Start(); if (!preview) countdownTimer.Start(); PositionStrip(); Activate(); }
    public void SetPreviewWindow(bool showWeekly) { weekly = showWeekly; five.Panel.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible; week.Panel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed; if (snapshot != null) { if (weekly) week.Update(snapshot.Weekly); else five.Update(snapshot.FiveHour); } }
    public bool ShowingWeekly => weekly;
    public double RotationOpacity => rotating.Opacity;
    public void RotateForTest() => Rotate();
    void Rotate()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => {
            SetPreviewWindow(!weekly);
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
            fadeIn.Completed += (_, _) => { rotating.BeginAnimation(OpacityProperty, null); rotating.Opacity = 1; };
            rotating.BeginAnimation(OpacityProperty, fadeIn);
        };
        rotating.BeginAnimation(OpacityProperty, fade);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    void PositionStrip()
    {
        if (preview || dragging) return;
        if (settings.DockToTaskbar && GetWindowRect(FindWindow("Shell_TrayWnd", null), out var r) && r.Right > r.Left && r.Bottom - r.Top < r.Right - r.Left)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var left = r.Left / dpi.DpiScaleX; var right = r.Right / dpi.DpiScaleX;
            Left = Math.Clamp(settings.Left ?? left + 180, left + 2, Math.Max(left + 2, right - Width - 2));
            Top = r.Top / dpi.DpiScaleY + Math.Max(0, (r.Bottom - r.Top) / dpi.DpiScaleY - Height) / 2;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); // topmost, no move/size/activation
        }
        else if (Left + Width < SystemParameters.VirtualScreenLeft || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth || Top + Height < SystemParameters.VirtualScreenTop || Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { Left = SystemParameters.WorkArea.Left + 180; Top = SystemParameters.WorkArea.Bottom - Height - 6; }
    }
    void DragStrip() { dragging = true; try { DragMove(); } finally { dragging = false; } SavePosition(); PositionStrip(); }
    void SavePosition() { settings.Left = Left; settings.Top = Top; Save(); }
    void Save() { try { settings.Save(); } catch { status.Text = "Could not save local settings."; } }
    async Task Refresh()
    {
        if (busy || preview || !presence.Running) return;
        busy = true; status.Text = "Refreshing…";
        try {
            snapshot = await client.ReadAsync(settings.CodexPath, lifetime.Token); updated = DateTimeOffset.UtcNow; failure = null;
            var alertCount = settings.SentAlerts.Count;
            foreach (var pair in new[] { ("5-hour", snapshot.FiveHour), ("Weekly", snapshot.Weekly) })
            {
                var crossed = Alerts.Evaluate(settings, snapshot, pair.Item2, pair.Item1).ToArray();
                if (crossed.Length > 0 && presence.Running) tray.ShowBalloonTip(7000, "ChatGPT usage", $"{pair.Item1}: {pair.Item2!.Remaining:0}% remaining (≤{crossed.Min()}%).", Forms.ToolTipIcon.Warning);
            }
            if (settings.SentAlerts.Count > 200) settings.SentAlerts = settings.SentAlerts.TakeLast(100).ToList();
            if (settings.SentAlerts.Count != alertCount) Save();
        }
        catch (OperationCanceledException) { if (!exiting) Failed("Connection timed out. Retry or check sign-in."); }
        catch (Exception e) { if (!exiting) Failed(e is FileNotFoundException ? e.Message : "Usage unavailable. Check Codex sign-in / connection."); }
        finally { busy = false; if (!exiting) Render(); }
    }
    string? failure;
    void Failed(string message) { failure = message; Render(); }
    void Render()
    {
        if (snapshot != null)
        {
            plan.Text = string.IsNullOrEmpty(snapshot.Plan) || snapshot.Plan == "unknown" ? "ChatGPT" : "ChatGPT " + System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(snapshot.Plan.Replace('_', ' '));
            if (IsVisible) { if (weekly) week.Update(snapshot.Weekly); else five.Update(snapshot.FiveHour); }
        }
        bool stale = updated == null || DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(Math.Clamp(settings.RefreshSeconds, 60, 600) + 35) || failure != null;
        if (busy) return;
        status.Text = failure != null ? failure + (updated != null ? " Showing last read." : "") : snapshot?.Blocked == true ? "Account usage restricted — open Usage for details." : updated == null ? "Waiting for usage…" : $"Updated {updated.Value.ToLocalTime():h:mm tt}" + (stale ? " • stale" : "");
        if (preview) { status.Text = "Preview • sample values"; return; }
        double? percentage = null;
        if (snapshot?.FiveHour is { Expired: false } fiveHour) percentage = fiveHour.Remaining;
        if (snapshot?.Weekly is { Expired: false } weeklyQuota) percentage = percentage == null ? weeklyQuota.Remaining : Math.Min(percentage.Value, weeklyQuota.Remaining);
        string label = snapshot?.Blocked == true ? "!" : stale || percentage == null ? "?" : $"{Math.Floor(percentage.Value):0}";
        var tooltip = snapshot?.Blocked == true ? "ChatGPT • account usage restricted" : "ChatGPT • " + (stale ? "stale • " : "") + (percentage == null ? "usage unavailable" : $"{percentage:0}% remaining (lower window)");
        tooltip = tooltip[..Math.Min(63, tooltip.Length)]; if (tray.Text != tooltip) tray.Text = tooltip;
        UpdateIcon(label, snapshot?.Blocked == true ? "#F87171" : stale || percentage == null ? "#94A3B8" : Row.Color(percentage.Value));
    }
    string iconKey = "";
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    void UpdateIcon(string label, string color)
    {
        if (iconKey == label + color) return; iconKey = label + color;
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bitmap); g.Clear(Drawing.Color.FromArgb(24, 30, 40));
        using var brush = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(color));
        using var font = new Drawing.Font("Segoe UI", label.Length > 2 ? 11 : 14, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        g.DrawString(label, font, brush, new Drawing.RectangleF(0, 0, 32, 32), format);
        var handle = bitmap.GetHicon();
        try { var next = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); tray.Icon = next; trayIcon?.Dispose(); trayIcon = next; } finally { DestroyIcon(handle); }
    }
    void ApplyTheme()
    {
        bool light = settings.Theme == "Light";
        if (settings.Theme == "System") { using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"); light = key?.GetValue("AppsUseLightTheme") is int n && n == 1; }
        if (appliedLightTheme == light) return; appliedLightTheme = light;
        var foreground = light ? Palette.LightForeground : Palette.DarkForeground;
        var border = (Border)Content;
        border.Background = light ? Palette.LightBackground : Palette.DarkBackground;
        border.BorderBrush = light ? Palette.LightBorder : Palette.DarkBorder;
        void Paint(DependencyObject d) { if (d is TextBlock t) t.Foreground = foreground; if (d is Button b) b.Foreground = foreground; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Paint(VisualTreeHelper.GetChild(d, i)); }
        Paint(border); scope.Opacity = .65; status.Opacity = .65;
    }
    static void OpenUsage() { try { Process.Start(new ProcessStartInfo("https://chatgpt.com/#settings/Usage") { UseShellExecute = true }); } catch { } }
    void ShowSettings()
    {
        var dialog = new Window { Title = "Usage Widget settings", Width = 410, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        var p = new StackPanel { Margin = new Thickness(20) }; dialog.Content = p;
        p.Children.Add(new TextBlock { Text = "Appearance", Margin = new Thickness(0, 0, 0, 5) });
        var theme = new ComboBox { ItemsSource = new[] { "System", "Dark", "Light" }, SelectedItem = settings.Theme }; p.Children.Add(theme);
        var notify = new CheckBox { Content = "Notify at 25%, 10%, and 5% remaining", IsChecked = settings.Notifications, Margin = new Thickness(0, 14, 0, 8) }; p.Children.Add(notify);
        p.Children.Add(new TextBlock { Text = "Appears only while the ChatGPT desktop window is open.\nWindows autostart is disabled.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var dock = new CheckBox { Content = "Place strip over taskbar (drag to a blank area)", IsChecked = settings.DockToTaskbar, Margin = new Thickness(0, 0, 0, 12) }; p.Children.Add(dock);
        p.Children.Add(new TextBlock { Text = "Alternate 5-hour / weekly every (seconds)" });
        var rotation = new ComboBox { ItemsSource = new[] { 3, 5, 8, 10, 15, 30, 60 }, SelectedItem = settings.RotateSeconds }; if (rotation.SelectedItem == null) rotation.SelectedItem = 8; p.Children.Add(rotation);
        p.Children.Add(new TextBlock { Text = "Refresh interval (seconds)" });
        var interval = new ComboBox { ItemsSource = new[] { 60, 120, 300, 600 }, SelectedItem = settings.RefreshSeconds }; if (interval.SelectedItem == null) interval.SelectedItem = 120; p.Children.Add(interval);
        p.Children.Add(new TextBlock { Text = "Codex executable (blank = auto-detect)", Margin = new Thickness(0, 14, 0, 4) });
        var path = new TextBox { Text = settings.CodexPath }; p.Children.Add(path);
        p.Children.Add(Button("Browse…", () => { var chooser = new OpenFileDialog { Filter = "Codex executable|codex.exe" }; if (chooser.ShowDialog(dialog) == true) path.Text = chooser.FileName; }, "Choose Codex"));
        p.Children.Add(new TextBlock { Text = "Uses your local Codex ChatGPT sign-in. No API key.\nIf the usage link opens the home page, select Settings → Usage.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8) });
        p.Children.Add(Button("Sign in to Codex", () => { try { Process.Start(new ProcessStartInfo(UsageClient.FindCodex(path.Text)) { Arguments = "login", UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(dialog, e.Message); } }, "Opens the official interactive sign-in"));
        p.Children.Add(Button("Save", () => {
            try {
                if (!string.IsNullOrWhiteSpace(path.Text) && (!File.Exists(path.Text) || !path.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) throw new IOException("Choose an existing Codex .exe.");
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true); key?.DeleteValue("CustomUsageWidget", false);
                settings.Theme = (string)theme.SelectedItem; settings.Notifications = notify.IsChecked == true; settings.RefreshSeconds = (int)interval.SelectedItem; settings.CodexPath = path.Text.Trim();
                settings.DockToTaskbar = dock.IsChecked == true; settings.RotateSeconds = (int)rotation.SelectedItem;
                settings.Save(); refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds); rotateTimer.Interval = TimeSpan.FromSeconds(settings.RotateSeconds); ApplyTheme(); PositionStrip(); dialog.Close(); _ = Refresh();
            } catch (Exception e) { MessageBox.Show(dialog, "Settings could not be saved: " + e.Message); }
        }, "Save preferences"));
        dialog.ShowDialog();
    }
    void Exit() { exiting = true; lifetime.Cancel(); refreshTimer.Stop(); countdownTimer.Stop(); presenceTimer.Stop(); rotateTimer.Stop(); SystemEvents.UserPreferenceChanged -= OnPreferenceChanged; if (!preview) SavePosition(); tray.Dispose(); trayIcon?.Dispose(); Close(); Application.Current.Shutdown(); }
}

public sealed class Row
{
    public StackPanel Panel { get; } = new();
    readonly TextBlock value = new(), reset = new(); readonly ProgressBar bar = new();
    readonly string title;
    public Row(string title)
    {
        this.title = title;
        var top = new Grid { Height = 15 }; top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(113) }); top.ColumnDefinitions.Add(new ColumnDefinition());
        value.FontSize = 10; value.FontWeight = FontWeights.SemiBold; top.Children.Add(value);
        reset.FontSize = 9; reset.Opacity = .65; reset.TextAlignment = TextAlignment.Right; reset.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(reset, 1); top.Children.Add(reset); Panel.Children.Add(top);
        bar.Height = 3; bar.Maximum = 100; bar.Margin = new Thickness(0, 1, 0, 0); bar.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 72, 88)); bar.BorderThickness = new Thickness(0); Panel.Children.Add(bar); Update(null);
    }
    public static string Color(double value) => value <= 10 ? "#F87171" : value <= 25 ? "#FBBF24" : "#34D399";
    public void Update(UsageWindow? w) { value.Text = title + " · " + (w == null ? "—" : $"{w.Remaining:0}% left"); bar.Value = w?.Remaining ?? 0; bar.Foreground = w == null || w.Expired ? Palette.Neutral : Palette.Usage(w.Remaining); reset.Text = w == null ? "Window unavailable" : w.Countdown + (w.Expired ? "" : " • " + UsageWindow.State(w.Remaining)); }
}
