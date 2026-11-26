using TDownloader.Core.Localization;
using System.Text.Json;
using TDownloader.App.Ui;
using TDownloader.Core;
using TDownloader.Core.Downloads;
using TDownloader.Core.Settings;

namespace TDownloader.App.Forms;

/// <summary>"Seçenekler": Genel, Bağlantı, Kayıt Yeri, Tarayıcı Yakalama (IDM Options ~268x257 DLU).</summary>
public sealed class OptionsDialog : DluForm
{
    private readonly AppSettings _s;
    private readonly Action<AppSettings> _apply;

    private readonly CheckBox _tray, _clip, _showInfo, _showCompleted, _completionSound, _startWithWindows;
    private readonly NumericUpDown _conn, _concurrent, _limit;
    private readonly TextBox _root, _catFolder;
    private readonly ListView _cats;
    private readonly CheckBox _capture;
    private readonly NumericUpDown _minKb;
    private readonly TextBox _types, _excluded;
    private readonly CheckBox _showVideoBtn;
    private readonly TextBox _ytPath, _ffPath;
    private readonly ComboBox _quality;
    private readonly ComboBox _language;
    private readonly Label _ytState, _ffState;
    private static readonly (string Key, string Text)[] Qualities = { ("best", Strings.Highest), ("1080", "1080p"), ("720", "720p"), ("480", "480p"), ("360", "360p") };

    public AppSettings Result => _s;

    public OptionsDialog(AppSettings current, string dataFolder, Action<AppSettings> apply)
    {
        _apply = apply;
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, JsonDefaults.Options), JsonDefaults.Options) ?? new();
        Text = Strings.Settings3;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(268, 257);

        var tabs = Add(new DarkTabs(), 5, 5, 258, 229);
        TabPage Page(string t) { var p = new TabPage(t) { UseVisualStyleBackColor = true }; tabs.TabPages.Add(p); return p; }
        T Put<T>(Control parent, T c, double x, double y, double w, double h) where T : Control { c.Bounds = Dlu.R(x, y, w, h); parent.Controls.Add(c); return c; }

        // --- Genel
        var g = Page(Strings.General);
        _tray = Put(g, new DarkCheck { Text = Strings.MinimizeToSystemTrayWhenClosingTheWindow, AutoEllipsis = true }, 10, 12, 230, 11);
        _clip = Put(g, new DarkCheck { Text = Strings.MonitorDownloadableClipboardURLsAndAsk, AutoEllipsis = true }, 10, 28, 230, 11);
        _showInfo = Put(g, new DarkCheck { Text = Strings.ShowFileInformationForBrowserDownloads, AutoEllipsis = true }, 10, 44, 238, 11);
        _showCompleted = Put(g, new DarkCheck { Text = Strings.ShowTheCompletedDialogAfterADownload, AutoEllipsis = true }, 10, 60, 238, 11);
        _completionSound = Put(g, new DarkCheck { Text = Strings.PlayASoundOnCompletion, AutoEllipsis = true }, 10, 76, 238, 11);
        _startWithWindows = Put(g, new DarkCheck { Text = Strings.StartWithWindows, AutoEllipsis = true }, 10, 92, 238, 11);
        Put(g, new Label { Text = Strings.DataFolder2 }, 10, 118, 80, 10);
        Put(g, new TextBox { Text = dataFolder, ReadOnly = true }, 10, 130, 232, 14);

        Put(g, new Label { Text = Strings.Language }, 10, 154, 70, 12);
        _language = Put(g, new DarkCombo { DropDownStyle = ComboBoxStyle.DropDownList }, 10, 170, 232, 70);
        _language.Items.AddRange(new object[] { Strings.SystemLanguage, Strings.Turkish, Strings.English, Strings.German });
        _language.SelectedIndex = Array.IndexOf(new[] { "", "tr", "en", "de" }, _s.Language);
        if (_language.SelectedIndex < 0) _language.SelectedIndex = 0;

        // --- Bağlantı
        var c = Page(Strings.Connection);
        Put(c, new Label { Text = Strings.ConnectionsPerDownload132 }, 10, 14, 150, 10);
        _conn = Put(c, new NumericUpDown { Minimum = 1, Maximum = 32 }, 170, 12, 50, 14);
        Put(c, new Label { Text = Strings.MaximumSimultaneousDownloads }, 10, 34, 150, 10);
        _concurrent = Put(c, new NumericUpDown { Minimum = 1, Maximum = 20 }, 170, 32, 50, 14);
        Put(c, new Label { Text = Strings.SpeedLimitKBS0Unlimited }, 10, 54, 150, 10);
        _limit = Put(c, new NumericUpDown { Minimum = 0, Maximum = 10_000_000, Increment = 100 }, 170, 52, 70, 14);
        Put(c, new Label { Text = Strings.DownloadsUseASingleConnectionIfTheServer, AutoSize = false }, 10, 80, 232, 24);

        // --- Kayıt Yeri
        var k = Page(Strings.SaveLocation);
        Put(k, new Label { Text = Strings.MainSaveFolder }, 10, 8, 120, 10);
        _root = Put(k, new TextBox(), 10, 20, 206, 14);
        var rb = Put(k, new Button { Text = "...", AccessibleName = Strings.ChooseTheMainSaveFolder, TabStop = true }, 221, 20, 20, 14);
        Put(k, new Label { Text = Strings.CategoryFoldersLeaveEmptyToUseTheCategory }, 10, 42, 232, 10);
        _cats = Put(k, new BufferedListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false }, 10, 54, 232, 80);
        _cats.Columns.Add(Strings.Category, Dlu.X(60));
        _cats.Columns.Add(Strings.Folder, Dlu.X(165));
        _catFolder = Put(k, new TextBox(), 10, 140, 156, 14);
        var cb = Put(k, new Button { Text = "...", AccessibleName = Strings.ChooseTheCategoryFolder, TabStop = true }, 170, 140, 20, 14);
        var cr = Put(k, new Button { Text = Strings.RestoreDefaults, }, 194, 140, 48, 14);

        // --- Tarayıcı yakalama
        var b = Page(Strings.BrowserCapture);
        _capture = Put(b, new DarkCheck { Text = Strings.CaptureBrowserDownloads, AutoEllipsis = true }, 10, 10, 230, 11);
        Put(b, new Label { Text = Strings.MinimumFileSizeKB }, 10, 30, 130, 10);
        _minKb = Put(b, new NumericUpDown { Minimum = 0, Maximum = 10_000_000 }, 150, 28, 70, 14);
        Put(b, new Label { Text = Strings.FileTypesToCaptureSeparateWithSpaces }, 10, 48, 232, 10);
        _types = Put(b, new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical }, 10, 60, 232, 50);
        Put(b, new Label { Text = Strings.ExcludedSitesOnePerLine }, 10, 116, 232, 10);
        _excluded = Put(b, new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true }, 10, 128, 232, 60);

        // --- Video
        var v = Page(Strings.Video);
        _showVideoBtn = Put(v, new DarkCheck { Text = Strings.ShowDownloadThisVideoOverVideos, AutoEllipsis = true }, 10, 8, 238, 11);
        Put(v, new Label { Text = Strings.YtDlpPathLeaveEmptyToSearchAutomatically }, 10, 26, 232, 10);
        _ytPath = Put(v, new TextBox(), 10, 37, 206, 14);
        var yb = Put(v, new Button { Text = "...", AccessibleName = Strings.ChooseTheYtDlpFile, TabStop = true }, 221, 37, 20, 14);
        _ytState = Put(v, new Label { AutoEllipsis = true }, 10, 53, 232, 10);
        Put(v, new Label { Text = Strings.FfmpegPathOrFolderLeaveEmptyToSearch }, 10, 68, 232, 10);
        _ffPath = Put(v, new TextBox(), 10, 79, 206, 14);
        var fb = Put(v, new Button { Text = "...", AccessibleName = Strings.ChooseTheFfmpegFile, TabStop = true }, 221, 79, 20, 14);
        _ffState = Put(v, new Label { AutoEllipsis = true }, 10, 95, 232, 10);
        Put(v, new Label { Text = Strings.DefaultQuality }, 10, 112, 80, 10);
        _quality = Put(v, new DarkCombo { DropDownStyle = ComboBoxStyle.DropDownList }, 90, 110, 80, 14);
        foreach (var q in Qualities) _quality.Items.Add(q.Text);
        var link = Put(v, new LinkLabel { Text = Strings.OpenYtDlpDownloadsGithubComYtDlp }, 10, 132, 232, 10);
        link.LinkClicked += (_, _) => OpenUrl("https://github.com/yt-dlp/yt-dlp/releases");
        var link2 = Put(v, new LinkLabel { Text = Strings.OpenFfmpegBuildsGyanDevFfmpegBuilds }, 10, 146, 232, 10);
        link2.LinkClicked += (_, _) => OpenUrl("https://www.gyan.dev/ffmpeg/builds/");
        Put(v, new Label { Text = Strings.SearchOrderThisPathAPPDATATDownloaderTools, AutoSize = false }, 10, 164, 232, 40);
        yb.Click += (_, _) => { var f = PickFile(_ytPath.Text, "yt-dlp|yt-dlp*.exe|" + Strings.AllExecutables + "|*.exe"); if (f != null) _ytPath.Text = f; };
        fb.Click += (_, _) => { var f = PickFile(_ffPath.Text, "ffmpeg|ffmpeg*.exe|" + Strings.AllExecutables + "|*.exe"); if (f != null) _ffPath.Text = f; };
        _ytPath.TextChanged += (_, _) => UpdateToolState();
        _ffPath.TextChanged += (_, _) => UpdateToolState();

        var ok = MakeButton(Strings.OK, 105, 238, 50, 14, DialogResult.OK);
        var cancel = MakeButton(Strings.Cancel, 159, 238, 50, 14, DialogResult.Cancel);
        var apply2 = MakeButton(Strings.Apply, 213, 238, 50, 14);
        AcceptButton = ok; CancelButton = cancel;

        Load2();

        rb.Click += (_, _) => { var d = PickFolder(_root.Text); if (d != null) _root.Text = d; };
        _cats.SelectedIndexChanged += (_, _) => { if (_cats.SelectedItems.Count > 0) _catFolder.Text = _cats.SelectedItems[0].SubItems[1].Text; };
        cb.Click += (_, _) => { var d = PickFolder(_catFolder.Text); if (d != null) SetCat(d); };
        _catFolder.Leave += (_, _) => SetCat(_catFolder.Text);
        cr.Click += (_, _) => SetCat("");
        apply2.Click += (_, _) => { Collect(); _apply(_s); };
        ok.Click += (_, _) => Collect();
    }

    private void SetCat(string folder)
    {
        if (_cats.SelectedItems.Count == 0) return;
        _cats.SelectedItems[0].SubItems[1].Text = folder.Trim();
        _catFolder.Text = folder.Trim();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private string? PickFile(string current, string filter)
    {
        using var ofd = new OpenFileDialog { Filter = filter, CheckFileExists = true, FileName = File.Exists(current) ? current : "" };
        return ofd.ShowDialog(this) == DialogResult.OK ? ofd.FileName : null;
    }

    private void UpdateToolState()
    {
        var paths = AppPaths.Default;
        var yt = Core.Video.ToolLocator.Find("yt-dlp", _ytPath.Text, paths.ToolsDir, AppContext.BaseDirectory);
        var ff = Core.Video.ToolLocator.Find("ffmpeg", _ffPath.Text, paths.ToolsDir, AppContext.BaseDirectory);
        _ytState.Text = yt != null ? Strings.Found + yt : Strings.YtDlpNotFoundUseTheInstallationLink;
        _ytState.ForeColor = yt != null ? Theme.Ok : Theme.Danger;
        _ffState.Text = ff != null ? Strings.Found + ff : Strings.FfmpegNotFoundInstallItToMergeVideo;
        _ffState.ForeColor = ff != null ? Theme.Ok : Theme.Danger;
    }

    private string? PickFolder(string start)
    {
        using var fb = new FolderBrowserDialog { SelectedPath = Directory.Exists(start) ? start : "", Description = Strings.ChooseAFolder };
        return fb.ShowDialog(this) == DialogResult.OK ? fb.SelectedPath : null;
    }

    private void Load2()
    {
        _tray.Checked = _s.MinimizeToTrayOnClose;
        _clip.Checked = _s.ClipboardMonitor;
        _showInfo.Checked = _s.ShowFileInfoOnCapture;
        _showCompleted.Checked = _s.ShowCompletedDialog;
        _completionSound.Checked = _s.PlayCompletionSound;
        _startWithWindows.Checked = _s.StartWithWindows;
        _conn.Value = Math.Clamp(_s.Connections, 1, 32);
        _concurrent.Value = Math.Clamp(_s.MaxConcurrentDownloads, 1, 20);
        _limit.Value = Math.Clamp(_s.SpeedLimitKBps, 0, 10_000_000);
        _root.Text = _s.SaveFolder;
        foreach (var cat in Categories.All)
            _cats.Items.Add(new ListViewItem(new[] { CategoryLabels.Display(cat), _s.CategoryFolders.TryGetValue(cat, out var f) ? f : "" }) { Tag = cat });
        _capture.Checked = _s.BrowserCaptureEnabled;
        _minKb.Value = Math.Clamp(_s.MinFileSizeKb, 0, 10_000_000);
        _types.Text = string.Join(" ", _s.FileTypes);
        _excluded.Text = string.Join(Environment.NewLine, _s.ExcludedSites);
        _showVideoBtn.Checked = _s.ShowVideoButton;
        _ytPath.Text = _s.YtDlpPath;
        _ffPath.Text = _s.FfmpegPath;
        int qi = Array.FindIndex(Qualities, q => q.Key == _s.DefaultVideoQuality);
        _quality.SelectedIndex = qi < 0 ? 0 : qi;
        UpdateToolState();
    }

    private void Collect()
    {
        _s.Language = new[] { "", "tr", "en", "de" }[Math.Max(0, _language.SelectedIndex)];
        _s.MinimizeToTrayOnClose = _tray.Checked;
        _s.ClipboardMonitor = _clip.Checked;
        _s.ShowFileInfoOnCapture = _showInfo.Checked;
        _s.ShowCompletedDialog = _showCompleted.Checked;
        _s.PlayCompletionSound = _completionSound.Checked;
        _s.StartWithWindows = _startWithWindows.Checked;
        _s.Connections = (int)_conn.Value;
        _s.MaxConcurrentDownloads = (int)_concurrent.Value;
        _s.SpeedLimitKBps = (int)_limit.Value;
        if (!string.IsNullOrWhiteSpace(_root.Text)) _s.SaveFolder = _root.Text.Trim();
        var catFolders = new Dictionary<string, string>();   // doldurulup sonra atanır: canlı ayar nesnesi yarım durumda görünmez (A26)
        foreach (ListViewItem li in _cats.Items)
            if (!string.IsNullOrWhiteSpace(li.SubItems[1].Text)) catFolders[(string)li.Tag!] = li.SubItems[1].Text;
        _s.CategoryFolders = catFolders;
        _s.BrowserCaptureEnabled = _capture.Checked;
        _s.MinFileSizeKb = (int)_minKb.Value;
        _s.FileTypes = _types.Text.Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimStart('.').ToLowerInvariant()).Distinct().ToList();
        _s.ShowVideoButton = _showVideoBtn.Checked;
        _s.YtDlpPath = _ytPath.Text.Trim();
        _s.FfmpegPath = _ffPath.Text.Trim();
        _s.DefaultVideoQuality = Qualities[Math.Max(0, _quality.SelectedIndex)].Key;
        _s.ExcludedSites = _excluded.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }
}
