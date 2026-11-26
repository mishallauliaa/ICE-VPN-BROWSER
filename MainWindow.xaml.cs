using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ICEBrowser;

public partial class MainWindow : Window
{
    private sealed class BrowserTab
    {
        public required string Id { get; init; }
        public required string Title { get; set; }
        public required WebView2 View { get; init; }
        public required Button TabButton { get; set; }
        public required string ProfilePath { get; init; }
        public bool IsPrivate { get; init; }
    }

    private readonly List<BrowserTab> _tabs = new();
    private readonly List<string> _downloadLog = new();
    private readonly HashSet<string> _blockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "adservice.google.com", "adnxs.com", "taboola.com", "outbrain.com",
        "criteo.com", "popads.net", "popcash.net"
    };
    private bool _adBlockEnabled = true;
    private readonly Dictionary<WebView2, Stopwatch> _navigationTimers = new();
    private readonly string _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICEBrowser", "settings.json");
    private readonly string _filterFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICEBrowser", "adblock-hosts.txt");
    private BrowserSettings _settings = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private sealed class BrowserSettings
    {
        public bool AutoConnectVpn { get; set; }
        public bool AdBlockEnabled { get; set; } = true;
        public bool RestoreLastSession { get; set; } = true;
        public List<string> LastTabs { get; set; } = new();
    }
    private BrowserTab? _active;
    private readonly string _profileRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICEBrowser", "Profiles");
    private const string HomeUrl = "https://www.google.com";
    private bool _vpnBusy;
    private CoreWebView2Environment? _normalEnvironment;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadSettings();
            _adBlockEnabled = _settings.AdBlockEnabled;
            AdBlockButton.Content = _adBlockEnabled ? "AdBlock ON" : "AdBlock OFF";
            LoadAdBlockHosts();
            var restored = _settings.RestoreLastSession
                ? _settings.LastTabs.Where(IsSafeHttpUrl).Take(10).ToList()
                : new List<string>();
            if (restored.Count == 0) restored.Add(HomeUrl);
            foreach (var url in restored) await NewTabAsync(url);
            await RefreshVpnStatusAsync();
            if (_settings.AutoConnectVpn)
            {
                StatusText.Text = "Connecting VPN…";
                var connect = await RunWarpAsync("connect");
                await Task.Delay(1800);
                await RefreshVpnStatusAsync();
                StatusText.Text = connect.ExitCode == 0 ? "Auto VPN requested" : "Auto VPN unavailable";
            }
        };
        Closing += (_, _) =>
        {
            _settings.LastTabs = _tabs.Where(t => !t.IsPrivate && t.View.Source != null)
                .Select(t => t.View.Source!.ToString()).Where(IsSafeHttpUrl).TakeLast(10).ToList();
            SaveSettings();
            foreach (var tab in _tabs.Where(t => t.IsPrivate))
            {
                try { tab.View.Dispose(); } catch { }
                try { if (Directory.Exists(tab.ProfilePath)) Directory.Delete(tab.ProfilePath, true); } catch { }
            }
        };
    }

    private async Task NewTabAsync(string url, bool isPrivate = false)
    {
        var view = new WebView2();
        var profile = isPrivate
            ? Path.Combine(Path.GetTempPath(), "ICEBrowser-InPrivate-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(_profileRoot, "Default");
        Directory.CreateDirectory(profile);
        BrowserHost.Children.Add(view);
        view.Visibility = Visibility.Collapsed;
        CoreWebView2Environment environment;
        if (isPrivate)
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        else
            environment = _normalEnvironment ??= await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        await view.EnsureCoreWebView2Async(environment);
        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        view.CoreWebView2.Settings.IsStatusBarEnabled = false;
        view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        view.CoreWebView2.Settings.IsZoomControlEnabled = true;
        view.CoreWebView2.Settings.IsPasswordAutosaveEnabled = !isPrivate;
        view.CoreWebView2.Settings.IsGeneralAutofillEnabled = !isPrivate;
        view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        view.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            if (!_adBlockEnabled) return;
            try
            {
                var host = new Uri(args.Request.Uri).Host;
                if (_blockedHosts.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
                    args.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by ICE AdBlock", "Content-Type: text/plain");
            }
            catch { }
        };
        view.CoreWebView2.DownloadStarting += (_, args) =>
        {
            var downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadDir);
            var suggested = Path.GetFileName(args.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggested)) suggested = "download";
            args.ResultFilePath = Path.Combine(downloadDir, suggested);
            var entry = Path.GetFileName(args.ResultFilePath);
            _downloadLog.Add($"{DateTime.Now:HH:mm:ss}  {entry}  (started)");
            args.Handled = false;
            args.DownloadOperation.StateChanged += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var state = args.DownloadOperation.State.ToString();
                    var total = args.DownloadOperation.TotalBytesToReceive;
                    var received = args.DownloadOperation.BytesReceived;
                    var progress = total > 0 ? $" {received * 100.0 / total:0}%" : "";
                    var index = _downloadLog.FindLastIndex(x => x.Contains(entry, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) _downloadLog[index] = $"{DateTime.Now:HH:mm:ss}  {entry}  ({state}{progress})";
                });
            };
        };
        view.CoreWebView2.NavigationStarting += (_, _) =>
        {
            _navigationTimers[view] = Stopwatch.StartNew();
            StatusText.Text = "Loading…";
        };
        view.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            var elapsed = _navigationTimers.TryGetValue(view, out var timer) ? timer.ElapsedMilliseconds : 0;
            StatusText.Text = isPrivate ? "Private session" : "Ready";
            SpeedText.Text = $"Load: {elapsed} ms";
            if (_active?.View == view) AddressBox.Text = view.Source?.ToString() ?? "";
        };
        view.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            var tab = _tabs.FirstOrDefault(t => t.View == view);
            if (tab != null)
            {
                tab.Title = string.IsNullOrWhiteSpace(view.CoreWebView2.DocumentTitle)
                    ? "New Tab" : view.CoreWebView2.DocumentTitle;
                UpdateTabCaption(tab);
            }
        };
        var item = new BrowserTab { Id = Guid.NewGuid().ToString("N"), Title = isPrivate ? "Private Tab" : "New Tab", View = view, TabButton = new Button(), ProfilePath = profile, IsPrivate = isPrivate };
        item.TabButton.Click += (_, _) => ActivateTab(item);
        var tabMenu = new ContextMenu();
        var closeItem = new MenuItem { Header = "Close tab" };
        closeItem.Click += async (_, _) => await CloseTabAsync(item);
        tabMenu.Items.Add(closeItem);
        item.TabButton.ContextMenu = tabMenu;
        _tabs.Add(item);
        UpdateTabCaption(item);
        ActivateTab(item);
        Navigate(url);
    }

    private void ActivateTab(BrowserTab tab)
    {
        _active = tab;
        foreach (var t in _tabs) t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        AddressBox.Text = tab.View.Source?.ToString() ?? "";
        foreach (var t in _tabs)
        {
            t.TabButton.Background = t == tab ? new SolidColorBrush(Color.FromRgb(31, 65, 99)) : new SolidColorBrush(Color.FromRgb(23, 38, 58));
        }
    }

    private void UpdateTabCaption(BrowserTab tab)
    {
        tab.TabButton.Content = tab.Title.Length > 20 ? tab.Title[..20] + "…" : tab.Title;
        tab.TabButton.ToolTip = tab.Title;
        tab.TabButton.Padding = new Thickness(13, 6, 13, 6);
        if (!TabsPanel.Children.Contains(tab.TabButton)) TabsPanel.Children.Add(tab.TabButton);
    }

    private async Task CloseTabAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabsPanel.Children.Remove(tab.TabButton);
        BrowserHost.Children.Remove(tab.View);
        try { tab.View.Dispose(); } catch { }
        if (tab.IsPrivate)
        {
            try { if (Directory.Exists(tab.ProfilePath)) Directory.Delete(tab.ProfilePath, true); } catch { }
        }
        _navigationTimers.Remove(tab.View);
        if (_tabs.Count == 0)
        {
            await NewTabAsync(HomeUrl);
            return;
        }
        ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.T) { await NewTabAsync(HomeUrl); e.Handled = true; }
            else if (e.Key == Key.W && _active != null) { await CloseTabAsync(_active); e.Handled = true; }
            else if (e.Key == Key.L) { AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.R) { _active?.View.Reload(); e.Handled = true; }
            else if (e.Key == Key.D) { Bookmark_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }
        if (e.Key == Key.F5) { _active?.View.Reload(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void Navigate(string text)
    {
        if (_active == null) return;
        var value = text.Trim();
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            value = value.Contains(' ') || !value.Contains('.') 
                ? "https://www.google.com/search?q=" + Uri.EscapeDataString(value)
                : "https://" + value;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var target))
        {
            _active.View.Source = target;
            AddressBox.Text = target.ToString();
            StatusText.Text = "Navigating…";
        }
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await NewTabAsync(HomeUrl);
    private async void PrivateTab_Click(object sender, RoutedEventArgs e) => await NewTabAsync("https://www.google.com", true);

    private void AdBlock_Click(object sender, RoutedEventArgs e)
    {
        _adBlockEnabled = !_adBlockEnabled;
        AdBlockButton.Content = _adBlockEnabled ? "AdBlock ON" : "AdBlock OFF";
        StatusText.Text = _adBlockEnabled ? "AdBlock enabled (basic filter)" : "AdBlock disabled";
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        var text = _downloadLog.Count == 0 ? "Belum ada unduhan." : string.Join(Environment.NewLine, _downloadLog.TakeLast(30));
        MessageBox.Show(text, "ICE BROWSER — Downloads", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (_active?.View.CanGoBack == true) _active.View.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (_active?.View.CanGoForward == true) _active.View.GoForward(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _active?.View.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(HomeUrl);
    private void AddressBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Navigate(AddressBox.Text); }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.Source == null) return;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICEBrowser", "bookmarks.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, _active.View.Source + Environment.NewLine);
        StatusText.Text = "Bookmark saved";
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
                _settings = JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(_settingsFile)) ?? new BrowserSettings();
        }
        catch { _settings = new BrowserSettings(); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { StatusText.Text = "Settings save failed: " + ex.Message; }
    }

    private static bool IsSafeHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void LoadAdBlockHosts()
    {
        try
        {
            if (File.Exists(_filterFile))
                foreach (var line in File.ReadLines(_filterFile))
                {
                    var host = line.Trim().ToLowerInvariant();
                    if (host.Length > 2 && host.Length < 254 && !host.StartsWith("#") && host.Contains('.') && !host.Contains('/'))
                        _blockedHosts.Add(host);
                }
        }
        catch { }
    }

    private async Task UpdateAdBlockListAsync()
    {
        const string filterUrl = "https://raw.githubusercontent.com/easylist/easylist/master/easylist/easylist.txt";
        StatusText.Text = "Updating ad filter…";
        try
        {
            var data = await Http.GetStringAsync(filterUrl);
            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in data.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("||")) continue;
                var host = line[2..].Split(new[] { '^', '/', '$', '*' }, 2)[0].Trim('.').ToLowerInvariant();
                if (host.Contains('.') && host.Length < 254 && !host.Contains('@')) domains.Add(host);
            }
            if (domains.Count < 100) throw new InvalidDataException("Filter response did not contain enough valid domains.");
            Directory.CreateDirectory(Path.GetDirectoryName(_filterFile)!);
            File.WriteAllLines(_filterFile, domains.OrderBy(x => x));
            _blockedHosts.Clear();
            foreach (var d in domains) _blockedHosts.Add(d);
            StatusText.Text = $"Ad filter updated: {domains.Count:N0} domains";
        }
        catch (Exception ex) { StatusText.Text = "Ad filter update failed: " + ex.Message; }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(18), Width = 390 };
        var autoVpn = new CheckBox { Content = "Automatically connect Cloudflare WARP when ICE BROWSER starts", IsChecked = _settings.AutoConnectVpn, Margin = new Thickness(0, 8, 0, 12), Foreground = Brushes.White };
        var adblock = new CheckBox { Content = "Enable basic AdBlock", IsChecked = _adBlockEnabled, Margin = new Thickness(0, 8, 0, 12), Foreground = Brushes.White };
        var restore = new CheckBox { Content = "Restore last browsing session (up to 10 tabs)", IsChecked = _settings.RestoreLastSession, Margin = new Thickness(0, 8, 0, 12), Foreground = Brushes.White };
        var update = new Button { Content = "Update AdBlock filter list", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 8) };
        var clearData = new Button { Content = "Clear browsing data (cookies, cache, history)", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 8) };
        var save = new Button { Content = "Save settings", Background = new SolidColorBrush(Color.FromRgb(20, 123, 192)), HorizontalAlignment = HorizontalAlignment.Right };
        var note = new TextBlock { Text = "VPN uses the official Cloudflare WARP app and affects the Windows device, not only this browser. AdBlock is a basic domain filter and may not block every ad.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 14) };
        panel.Children.Add(autoVpn); panel.Children.Add(adblock); panel.Children.Add(restore); panel.Children.Add(note); panel.Children.Add(update); panel.Children.Add(clearData); panel.Children.Add(save);
        var window = new Window { Title = "ICE BROWSER Settings", Content = panel, Width = 460, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(17, 28, 46)) };
        update.Click += async (_, _) => await UpdateAdBlockListAsync();
        clearData.Click += async (_, _) =>
        {
            if (MessageBox.Show("Hapus cookies, cache, dan riwayat browser untuk profil normal?", "ICE BROWSER", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                if (_active?.View.CoreWebView2?.Profile != null)
                    await _active.View.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                StatusText.Text = "Browsing data cleared";
                MessageBox.Show("Data browsing profil normal sudah dibersihkan.", "ICE BROWSER");
            }
            catch (Exception ex) { MessageBox.Show("Gagal menghapus data: " + ex.Message, "ICE BROWSER"); }
        };
        save.Click += (_, _) =>
        {
            _settings.AutoConnectVpn = autoVpn.IsChecked == true;
            _settings.AdBlockEnabled = adblock.IsChecked == true;
            _settings.RestoreLastSession = restore.IsChecked == true;
            _adBlockEnabled = _settings.AdBlockEnabled;
            AdBlockButton.Content = _adBlockEnabled ? "AdBlock ON" : "AdBlock OFF";
            SaveSettings();
            window.Close();
        };
        window.ShowDialog();
    }

    private async void VpnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vpnBusy) return;
        _vpnBusy = true;
        VpnButton.IsEnabled = false;
        try
        {
            var status = await RunWarpAsync("status");
            bool connected = status.ExitCode == 0 &&
                status.Output.Contains("Connected", StringComparison.OrdinalIgnoreCase);
            if (connected)
            {
                var result = await RunWarpAsync("disconnect");
                StatusText.Text = result.ExitCode == 0 ? "VPN disconnected" : "Unable to disconnect VPN";
            }
            else
            {
                var result = await RunWarpAsync("connect");
                StatusText.Text = result.ExitCode == 0 ? "VPN connection requested" : "WARP command failed";
            }
            await Task.Delay(1200);
            await RefreshVpnStatusAsync();
        }
        catch (Exception ex) { StatusText.Text = "VPN error: " + ex.Message; }
        finally { _vpnBusy = false; VpnButton.IsEnabled = true; }
    }

    private async Task RefreshVpnStatusAsync()
    {
        var result = await RunWarpAsync("status");
        bool connected = result.ExitCode == 0 &&
            result.Output.Contains("Connected", StringComparison.OrdinalIgnoreCase);
        VpnDot.Fill = connected ? new SolidColorBrush(Color.FromRgb(49, 190, 120)) : new SolidColorBrush(Color.FromRgb(217, 154, 54));
        VpnLabel.Text = connected ? "VPN CONNECTED" : "VPN DISCONNECTED";
        VpnButton.Content = connected ? "DISCONNECT" : "CONNECT VPN";
    }

    private static async Task<(int ExitCode, string Output)> RunWarpAsync(string args)
    {
        string? exe = FindWarpCli();
        if (exe == null) return (-1, "Cloudflare WARP CLI not found. Install Cloudflare WARP first.");
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return (-1, "Could not start warp-cli.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string? FindWarpCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP", "warp-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "WARP", "warp-cli.exe")
        };
        foreach (var path in candidates) if (File.Exists(path)) return path;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "warp-cli.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
