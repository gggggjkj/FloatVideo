using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FloatVideo;

internal sealed class PlayerForm : Form
{
    private const int WmHotkey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13;
    private const int HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    private static readonly string[] Actions = { "toggle", "back", "forward", "volumeDown", "volumeUp" };
    private static readonly HttpClient ShortLinkClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly string? _initialUrl;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly TextBox _url = new();
    private readonly Label _status = new();
    private readonly Button _pin = new();
    private readonly HashSet<int> _registered = new();
    private IntPtr _hotkeyWindow;
    private readonly HashSet<CoreWebView2Frame> _frames = new();
    private string _statusMessage = "貼上連結，按「播放」開始";
    private string _hotkeyStatus = "快捷鍵啟動中";
    private string _mediaKind = "";
    private bool _pageReady;
    private int _volumeValue;
    private int _loadVersion;
    private readonly ComboBox _parts = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 420, AccessibleName = "Bilibili 分 P" };
    private readonly ColumnStyle _partColumn = new(SizeType.Absolute, 0);
    private Uri? _partUrl;
    private string? _partsKey;
    private VideoPart[] _knownParts = Array.Empty<VideoPart>();
    private double _speed = 1;

    private sealed record VideoPart(int Number, string Title, long Cid)
    {
        public override string ToString() => $"P{Number} · {Title}";
    }

    public PlayerForm(string? initialUrl = null)
    {
        _initialUrl = initialUrl;
        _volumeValue = Math.Clamp(_settings.Volume, 0, 100);
        Text = "懸浮影片播放器 v9";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(42, 45, 53);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Microsoft JhengHei UI", 9F);
        MinimumSize = new Size(500, 260);
        Size = new Size(Math.Max(500, _settings.Width), Math.Max(260, _settings.Height));
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen!.WorkingArea;
        var saved = new Rectangle(_settings.X, _settings.Y, Width, Height);
        Location = _settings.X >= 0 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved))
            ? new Point(_settings.X, _settings.Y)
            : new Point(screen.Right - Width - 30, screen.Top + 30);
        TopMost = _settings.TopMost;
        KeyPreview = true;
        Padding = new Padding(5);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 26, 31), ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
        Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 9, Margin = Padding.Empty, Padding = new Padding(6, 4, 4, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        header.ColumnStyles.Add(_partColumn);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        layout.Controls.Add(header, 0, 0);

        var title = new Label { Text = "影片", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.SizeAll };
        title.MouseDown += DragWindow;
        header.Controls.Add(title, 0, 0);
        _url.Dock = DockStyle.Fill;
        _url.BorderStyle = BorderStyle.FixedSingle;
        _url.BackColor = Color.FromArgb(39, 42, 49);
        _url.ForeColor = Color.White;
        _url.Text = _settings.LastUrl;
        _url.PlaceholderText = "貼上 MP4、YouTube 或 Bilibili 網址";
        _url.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { LoadLink(); e.SuppressKeyPress = true; } };
        header.Controls.Add(_url, 1, 0);
        header.Controls.Add(MakeButton("▶", (_, _) => LoadLink()), 2, 0);
        _pin = MakeButton(TopMost ? "◆" : "◇", (_, _) => { TopMost = !TopMost; _pin.Text = TopMost ? "◆" : "◇"; SetStatus(TopMost ? "已固定在最上層" : "已取消置頂"); });
        header.Controls.Add(_pin, 3, 0);
        header.Controls.Add(MakeButton("⚙", (_, _) => OpenHotkeySettings()), 4, 0);
        var speed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "播放倍速" };
        double[] rates = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };
        foreach (var rate in rates) speed.Items.Add($"{rate:0.##}×");
        speed.SelectedIndex = 3;
        speed.SelectionChangeCommitted += (_, _) =>
        {
            _speed = rates[speed.SelectedIndex];
            if (_mediaKind == "bilibili") _ = ControlBilibili("speed", quiet: true);
            else if (_pageReady) _ = CallJs($"setSpeed({JsonSerializer.Serialize(_speed)})");
        };
        header.Controls.Add(speed, 5, 0);
        _parts.Visible = false;
        _parts.SelectionChangeCommitted += (_, _) => SwitchPart();
        header.Controls.Add(_parts, 6, 0);
        header.Controls.Add(MakeButton("─", (_, _) => WindowState = FormWindowState.Minimized), 7, 0);
        header.Controls.Add(MakeButton("×", (_, _) => Close()), 8, 0);
        var toolTip = new ToolTip();
        toolTip.SetToolTip(header.GetControlFromPosition(2, 0), "載入網址並播放");
        toolTip.SetToolTip(_pin, "切換置頂");
        toolTip.SetToolTip(header.GetControlFromPosition(4, 0), "設定全域快捷鍵");

        var videoPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Margin = Padding.Empty };
        videoPanel.Controls.Add(_web);
        layout.Controls.Add(videoPanel, 0, 1);

        _status.Text = _statusMessage;
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Color.FromArgb(170, 181, 194);
        _status.AutoEllipsis = true;
        _status.Padding = new Padding(7, 0, 7, 0);
        layout.Controls.Add(_status, 0, 2);

        Shown += async (_, _) => { InstallGlobalHotkeys(); await InitializeBrowser(); };
        FormClosing += (_, _) => { RemoveGlobalHotkeys(); SaveSettings(); };
        HandleCreated += (_, _) => { if (Visible) InstallGlobalHotkeys(); };
        HandleDestroyed += (_, _) => RemoveGlobalHotkeys();
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text };
        StyleButton(button);
        button.Click += click;
        return button;
    }

    private static void StyleButton(Button button)
    {
        button.Dock = DockStyle.Fill;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(57, 63, 75);
        button.BackColor = Color.FromArgb(35, 38, 45);
        button.ForeColor = Color.White;
        button.Margin = new Padding(1);
        button.Cursor = Cursors.Hand;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            var index = m.WParam.ToInt32() - 100;
            if (index >= 0 && index < Actions.Length) RunAction(Actions[index]);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != WmNcHitTest || m.Result.ToInt32() != HtClient || WindowState != FormWindowState.Normal) return;
        var p = PointToClient(Cursor.Position);
        const int edge = 7;
        var left = p.X < edge; var right = p.X >= Width - edge;
        var top = p.Y < edge; var bottom = p.Y >= Height - edge;
        m.Result = (IntPtr)(left && top ? HtTopLeft : right && top ? HtTopRight :
            left && bottom ? HtBottomLeft : right && bottom ? HtBottomRight :
            left ? HtLeft : right ? HtRight : top ? HtTop : bottom ? HtBottom : HtClient);
    }

    private void RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();
        _hotkeyWindow = Handle;
        var failures = new List<string>();
        var configured = 0;
        for (var i = 0; i < Actions.Length; i++)
        {
            if (!_settings.Hotkeys.TryGetValue(Actions[i], out var binding) || !binding.IsValid) continue;
            configured++;
            var modifiers = ModNoRepeat | (binding.Ctrl ? ModControl : 0u) |
                (binding.Alt ? ModAlt : 0u) | (binding.Shift ? ModShift : 0u);
            var id = 100 + i;
            if (RegisterHotKey(_hotkeyWindow, id, modifiers, (uint)binding.Key)) _registered.Add(id);
            else failures.Add(binding.ToString());
        }
        _hotkeyStatus = configured == 0 ? "未設定快捷鍵" : $"全域快捷鍵 {_registered.Count}/{configured} 就緒";
        if (failures.Count > 0) SetStatus("無法註冊快捷鍵，請換一組：" + string.Join("、", failures));
        else RefreshStatus();
    }

    private void InstallGlobalHotkeys() => RegisterAllHotkeys();
    private void RemoveGlobalHotkeys() => UnregisterAllHotkeys();

    private void UnregisterAllHotkeys()
    {
        foreach (var id in _registered) UnregisterHotKey(_hotkeyWindow, id);
        _registered.Clear();
        _hotkeyWindow = IntPtr.Zero;
    }

    private void RunAction(string action)
    {
        if (_mediaKind == "bilibili")
        {
            if (action == "volumeDown") _volumeValue = Math.Max(0, _volumeValue - 5);
            if (action == "volumeUp") _volumeValue = Math.Min(100, _volumeValue + 5);
            _ = ControlBilibili(action);
            return;
        }
        switch (action)
        {
            case "toggle": _ = CallJs("togglePlay()"); break;
            case "back": _ = CallJs("seekRelative(-10)"); SetStatus("倒轉 10 秒"); break;
            case "forward": _ = CallJs("seekRelative(10)"); SetStatus("快轉 10 秒"); break;
            case "volumeDown": _volumeValue = Math.Max(0, _volumeValue - 5); _ = CallJs($"setVolume({_volumeValue})"); SetStatus($"音量 {_volumeValue}%"); break;
            case "volumeUp": _volumeValue = Math.Min(100, _volumeValue + 5); _ = CallJs($"setVolume({_volumeValue})"); SetStatus($"音量 {_volumeValue}%"); break;
        }
    }

    private async Task<bool> ControlBilibili(string action, bool quiet = false)
    {
        var operation = action switch
        {
            "toggle" => "if (v.paused) v.play().catch(() => {}); else v.pause();",
            "back" => "v.currentTime = Math.max(0, v.currentTime - 10);",
            "forward" => "v.currentTime = Math.min(Number.isFinite(v.duration) ? v.duration : Infinity, v.currentTime + 10);",
            "volumeDown" or "volumeUp" => $"v.muted = false; v.volume = {JsonSerializer.Serialize(_volumeValue / 100.0)};",
            "speed" => $"v.playbackRate = {JsonSerializer.Serialize(_speed)};",
            "syncPlayback" => $"v.muted = false; v.volume = {JsonSerializer.Serialize(_volumeValue / 100.0)}; v.playbackRate = {JsonSerializer.Serialize(_speed)};",
            _ => ""
        };
        var script = "(() => { const videos = [...document.querySelectorAll('video')]; " +
            "const v = videos.find(x => x.clientWidth > 0 && x.clientHeight > 0) || videos[0]; " +
            "if (!v) return {found:false}; " + operation + " return {found:true, paused:v.paused}; })()";
        foreach (var frame in _frames.ToArray())
        {
            if (frame.IsDestroyed() != 0) continue;
            try
            {
                using var result = JsonDocument.Parse(await frame.ExecuteScriptAsync(script));
                if (!result.RootElement.TryGetProperty("found", out var found) || !found.GetBoolean()) continue;
                if (!quiet)
                    SetStatus(action switch
                    {
                        "toggle" => result.RootElement.GetProperty("paused").GetBoolean() ? "已暫停" : "播放中",
                        "back" => "倒轉 10 秒",
                        "forward" => "快轉 10 秒",
                        _ => $"音量 {_volumeValue}%"
                    });
                return true;
            }
            catch { /* The frame may be navigating or may not contain the video. */ }
        }
        if (!quiet) SetStatus("Bilibili 影片尚未就緒，請稍候再按快捷鍵");
        return false;
    }

    private async Task SyncBilibiliPlayback(int loadVersion)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_mediaKind != "bilibili" || loadVersion != _loadVersion) return;
            if (await ControlBilibili("syncPlayback", quiet: true)) return;
            await Task.Delay(500);
        }
    }

    private void OpenHotkeySettings()
    {
        using var dialog = new HotkeyForm(_settings.Hotkeys);
        var wasTopMost = TopMost;
        RemoveGlobalHotkeys();
        try
        {
            TopMost = false;
            dialog.TopMost = wasTopMost;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _settings.Hotkeys = dialog.Bindings;
                _settings.Save();
            }
        }
        finally
        {
            TopMost = wasTopMost;
            InstallGlobalHotkeys();
            Activate();
        }
    }

    private async Task InitializeBrowser()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatVideo", "WebView2");
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
            await _web.EnsureCoreWebView2Async(environment);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://floatvideo.local/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            _web.CoreWebView2.WebMessageReceived += (_, e) => HandleWebMessage(e.WebMessageAsJson);
            _web.CoreWebView2.FrameCreated += (_, e) => TrackFrame(e.Frame);
            const string playerUrl = "https://floatvideo.local/player.html";
            _web.CoreWebView2.AddWebResourceRequestedFilter(playerUrl, CoreWebView2WebResourceContext.Document);
            _web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                var html = typeof(PlayerForm).Assembly.GetManifestResourceStream("FloatVideo.Assets.player.html")
                    ?? throw new InvalidOperationException("找不到內建播放器頁面");
                e.Response = environment.CreateWebResourceResponse(html, 200, "OK", "Content-Type: text/html; charset=utf-8");
            };
            _web.CoreWebView2.Navigate(playerUrl);
        }
        catch (Exception ex) { SetStatus("播放元件啟動失敗：" + ex.Message); }
    }

    private void TrackFrame(CoreWebView2Frame frame)
    {
        _frames.Add(frame);
        frame.FrameCreated += (_, e) => TrackFrame(e.Frame);
        frame.Destroyed += (_, _) => _frames.Remove(frame);
    }

    private void LoadLink() => _ = LoadLinkAsync();

    private async Task LoadLinkAsync()
    {
        var loadVersion = ++_loadVersion;
        _partUrl = null;
        _parts.Visible = false;
        _partColumn.Width = 0;
        var input = _url.Text.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out _) || input.EndsWith(']') || input.EndsWith('】'))
        {
            var sharedLink = Regex.Match(input, @"https?://[^\s<>\[\]（）()【】「」『』]+", RegexOptions.IgnoreCase);
            if (sharedLink.Success) input = sharedLink.Value.TrimEnd('.', ',', '，', '。', ';', '；', '!', '！', '?', '？');
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("請輸入 http 或 https 影片網址");
            return;
        }
        if (_url.Text != input) _url.Text = input;
        string kind = "direct", id = "", bvid = "", aid = "", episodeId = "";
        var page = 1;
        long cid = 0;
        var host = uri.Host.ToLowerInvariant();
        var wasShortLink = host == "b23.tv";
        if (host == "b23.tv")
        {
            SetStatus("正在解析 Bilibili 短網址…");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var response = await ShortLinkClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                uri = response.RequestMessage?.RequestUri ?? uri;
                host = uri.Host.ToLowerInvariant();
                if (loadVersion != _loadVersion) return;
            }
            catch (Exception ex) { SetStatus("Bilibili 短網址解析失敗：" + ex.Message); return; }
        }
        if (wasShortLink && host != "bilibili.com" && !host.EndsWith(".bilibili.com", StringComparison.Ordinal))
        {
            SetStatus("短網址沒有導向 Bilibili 影片頁");
            return;
        }
        if (host == "bilibili.com" || host.EndsWith(".bilibili.com", StringComparison.Ordinal))
        {
            kind = "bilibili";
            var video = Regex.Match(uri.AbsolutePath, @"^/video/(BV[A-Za-z0-9]{10}|av\d+)", RegexOptions.IgnoreCase);
            var episode = Regex.Match(uri.AbsolutePath, @"^/bangumi/play/ep(\d+)", RegexOptions.IgnoreCase);
            if (video.Success)
            {
                var value = video.Groups[1].Value;
                if (value.StartsWith("BV", StringComparison.OrdinalIgnoreCase)) bvid = value;
                else aid = value[2..];
            }
            else if (episode.Success) episodeId = episode.Groups[1].Value;
            else if (host == "player.bilibili.com" && uri.AbsolutePath == "/player.html")
            {
                bvid = GetQueryValue(uri, "bvid");
                aid = GetQueryValue(uri, "aid");
                episodeId = GetQueryValue(uri, "episodeId");
            }
            if (bvid == "" && aid == "" && episodeId == "")
            {
                SetStatus("請使用 Bilibili 影片頁、番劇單集或官方播放器網址");
                return;
            }
            if (bvid != "" && !Regex.IsMatch(bvid, @"^BV[A-Za-z0-9]{10}$", RegexOptions.IgnoreCase) ||
                aid != "" && !Regex.IsMatch(aid, @"^\d+$") ||
                episodeId != "" && !Regex.IsMatch(episodeId, @"^\d+$"))
            {
                SetStatus("無法辨識這個 Bilibili 影片網址");
                return;
            }
            if (int.TryParse(GetQueryValue(uri, "p"), out var requestedPage) && requestedPage > 0 && requestedPage <= 10000)
                page = requestedPage;
            if (episodeId == "" && (bvid != "" || aid != ""))
            {
                var parts = await GetVideoPartsAsync(bvid, aid, loadVersion);
                if (loadVersion != _loadVersion || IsDisposed) return;
                if (parts.Length > 0)
                {
                    var selected = parts.FirstOrDefault(part => part.Number == page) ?? parts[0];
                    page = selected.Number;
                    cid = selected.Cid;
                    if (parts.Length > 1)
                    {
                        _partUrl = uri;
                        _parts.Items.Clear();
                        _parts.Items.AddRange(parts);
                        _parts.SelectedItem = selected;
                        _partColumn.Width = 105;
                        _parts.Visible = true;
                    }
                }
            }
        }
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be")
        {
            kind = "youtube";
            if (host == "youtu.be") id = uri.AbsolutePath.Trim('/').Split('/')[0];
            else if (uri.AbsolutePath == "/watch")
                id = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && x[0] == "v")?.ElementAt(1) ?? "";
            else
            {
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 2 && (segments[0] == "shorts" || segments[0] == "embed" || segments[0] == "live")) id = segments[1];
            }
            id = Uri.UnescapeDataString(id);
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) { SetStatus("無法辨識這個 YouTube 影片網址"); return; }
        }
        _settings.LastUrl = input;
        _settings.Save();
        _mediaKind = kind;
        _frames.Clear();
        SetStatus("正在載入影片…");
        _ = CallJs($"loadMedia({JsonSerializer.Serialize(new { kind, url = input, id, bvid, aid, episodeId, page, cid, volume = _volumeValue, speed = _speed })})");
    }

    private async Task<VideoPart[]> GetVideoPartsAsync(string bvid, string aid, int loadVersion)
    {
        var key = bvid != "" ? $"bvid={bvid}" : $"aid={aid}";
        if (_partsKey == key) return _knownParts;
        SetStatus("正在讀取 Bilibili 分 P…");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/player/pagelist?{key}");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            using var response = await ShortLinkClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (root.GetProperty("code").GetInt32() != 0) return Array.Empty<VideoPart>();
            var parts = root.GetProperty("data").EnumerateArray()
                .Select(part => new VideoPart(part.GetProperty("page").GetInt32(),
                    part.GetProperty("part").GetString() ?? "", part.GetProperty("cid").GetInt64()))
                .OrderBy(part => part.Number).ToArray();
            if (loadVersion == _loadVersion && parts.Length > 0)
            {
                _partsKey = key;
                _knownParts = parts;
            }
            return parts;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Array.Empty<VideoPart>();
        }
    }

    private void SwitchPart()
    {
        if (_partUrl is null || _parts.SelectedItem is not VideoPart part) return;
        var query = _partUrl.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => !Uri.UnescapeDataString(value.Split('=', 2)[0]).Equals("p", StringComparison.OrdinalIgnoreCase));
        var next = new UriBuilder(_partUrl) { Query = string.Join("&", query.Append($"p={part.Number}")) };
        _url.Text = next.Uri.AbsoluteUri;
        LoadLink();
    }

    private static string GetQueryValue(Uri uri, string name) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .Where(part => part.Length == 2 && part[0].Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(part => Uri.UnescapeDataString(part[1]))
        .FirstOrDefault() ?? "";

    private async Task CallJs(string script)
    {
        if (!_pageReady) { SetStatus("播放元件仍在啟動，請稍候"); return; }
        try { await _web.ExecuteScriptAsync(script); }
        catch (Exception ex) { SetStatus("控制播放失敗：" + ex.Message); }
    }

    private void HandleWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement;
            var type = data.GetProperty("type").GetString();
            if (type == "ready")
            {
                _pageReady = true;
                SetStatus("已就緒，貼上連結後按播放");
                if (!string.IsNullOrWhiteSpace(_initialUrl)) { _url.Text = _initialUrl; LoadLink(); }
                return;
            }
            if (type == "error") { SetStatus(data.GetProperty("message").GetString() ?? "播放失敗"); return; }
            if (type == "status")
            {
                var message = data.GetProperty("message").GetString() ?? "";
                SetStatus(message);
                if (_mediaKind == "bilibili" && message.StartsWith("Bilibili 播放器已開啟", StringComparison.Ordinal))
                    _ = SyncBilibiliPlayback(_loadVersion);
            }
        }
        catch { /* Ignore incomplete messages from a page being replaced. */ }
    }

    private void SetStatus(string message) { _statusMessage = message; RefreshStatus(); }
    private void RefreshStatus() => _status.Text = $"{_statusMessage}  ·  {_hotkeyStatus}";

    private void SaveSettings()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.X = Left; _settings.Y = Top;
            _settings.Width = Width; _settings.Height = Height;
        }
        _settings.LastUrl = _url.Text.Trim();
        _settings.Volume = _volumeValue;
        _settings.TopMost = TopMost;
        _settings.Save();
    }
}
