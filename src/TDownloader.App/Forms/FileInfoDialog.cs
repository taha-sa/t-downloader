using System.Globalization;
using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App.Forms;

/// <summary>
/// "Dosya İndirme Bilgisi" (IDM Download File Info, 380x128 DLU). İndirme, kullanıcı "İndirmeyi Başlat"
/// veya "Daha Sonra İndir" demeden eklenmez. DialogResult: Yes = başlat, No = daha sonra, Cancel = iptal.
/// </summary>
public sealed class FileInfoDialog : DluForm
{
    private static readonly CultureInfo Tr = new("tr-TR");

    private readonly DownloadManager _mgr;
    private readonly TextBox _url, _save, _folder, _desc;
    private readonly DarkCombo _cat;
    private readonly DarkCheck _remember;
    private readonly Label _size;
    private readonly PictureBox _icon;
    private readonly bool _probe;
    private readonly string? _referrer, _cookies, _userAgent;
    private bool _pathEdited;
    private bool _updating;
    private long _fileSize;
    private string _iconExt = "\0";

    public bool StartNow { get; private set; }
    public string Url => _url.Text.Trim();
    public string? Description => string.IsNullOrWhiteSpace(_desc.Text) ? null : _desc.Text.Trim();
    public string SaveFolder => Path.GetDirectoryName(_save.Text.Trim()) ?? "";
    public string FileName => Path.GetFileName(_save.Text.Trim());
    public long FileSize => _fileSize;

    public FileInfoDialog(DownloadManager mgr, string url, string? fileName = null, long fileSize = -1,
        bool probeSize = true, string? referrer = null, string? cookies = null, string? userAgent = null, string? description = null)
    {
        _mgr = mgr;
        _fileSize = fileSize;
        _probe = probeSize; _referrer = referrer; _cookies = cookies; _userAgent = userAgent;
        Text = "Dosya İndirme Bilgisi";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetClientDlu(380, 128);

        MakeLabel("URL", 7, 5, 55, 10);
        _url = Add(new TextBox { Text = url }, 64, 3, 244, 14);
        MakeLabel("Kategori", 7, 24, 55, 10);
        _cat = Add(new DarkCombo { DropDownStyle = ComboBoxStyle.DropDownList }, 64, 22, 116, 64);
        var plus = MakeButton("+", 183, 22, 16, 14);
        MakeLabel("Kaydet", 7, 41, 55, 10);
        _save = Add(new TextBox(), 64, 39, 223, 14);
        var browse = MakeButton("...", 292, 39, 16, 14);

        var grp = Add(new DarkGroup { Text = "" }, 64, 55, 244, 34);
        _remember = new DarkCheck { AutoEllipsis = true, Bounds = Dlu.R(6, 8, 230, 11) };
        _folder = new TextBox { Bounds = Dlu.R(6, 19, 230, 13) };
        grp.Controls.Add(_remember);
        grp.Controls.Add(_folder);

        MakeLabel("Açıklama", 7, 93, 55, 10);
        _desc = Add(new TextBox { Text = description ?? "" }, 64, 91, 244, 14);

        _icon = Add(new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Theme.Bg }, 330, 6, 32, 32);
        _size = MakeLabel("", 316, 42, 58, 10, ContentAlignment.TopCenter);
        _size.ForeColor = Theme.TextDim;

        MakeButton("&Daha Sonra İndir", 64, 110, 74, 14, DialogResult.No);
        var start = MakeButton("İndirmeyi &Başlat", 149, 110, 74, 14, DialogResult.Yes);
        var cancel = MakeButton("İptal", 234, 110, 74, 14, DialogResult.Cancel);
        AcceptButton = start; CancelButton = cancel;

        _cat.Items.AddRange(Categories.All);
        foreach (var c in _mgr.Settings.CustomCategories) if (!_cat.Items.Contains(c)) _cat.Items.Add(c);

        var name = string.IsNullOrWhiteSpace(fileName) ? NameFromUrl(url) : DownloadManager.SanitizeFileName(fileName!);
        SetCategoryAndPath(Categories.FromFileName(name), name);
        UpdateSizeAndIcon();

        _cat.SelectedIndexChanged += (_, _) => { if (!_updating && _cat.SelectedItem is string c) SetCategoryAndPath(c, FileName); };
        _save.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _pathEdited = true;
            _updating = true; _folder.Text = SaveFolder; _updating = false;
            UpdateSizeAndIcon();
        };
        _folder.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true; _save.Text = Path.Combine(_folder.Text.Trim(), FileName); _updating = false;
            _pathEdited = true;
        };
        _url.TextChanged += (_, _) =>
        {
            if (_pathEdited) return;
            var n = NameFromUrl(_url.Text);
            SetCategoryAndPath(Categories.FromFileName(n), n);
            UpdateSizeAndIcon();
        };
        plus.Click += (_, _) => AddCategory();
        browse.Click += (_, _) =>
        {
            using var sfd = new SaveFileDialog { FileName = FileName, InitialDirectory = Directory.Exists(SaveFolder) ? SaveFolder : null, Title = "Kaydet", OverwritePrompt = false };
            if (sfd.ShowDialog(this) == DialogResult.OK) _save.Text = sfd.FileName;
        };
        FormClosing += OnClosing;
        Load += (_, _) => CenterOnCursorScreen();
        Shown += async (_, _) => { Foreground.Force(this); await ProbeSizeAsync(); };
    }

    /// <summary>Fare hangi monitördeyse onun çalışma alanının ortası.</summary>
    private void CenterOnCursorScreen()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
    }

    private static string NameFromUrl(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? DownloadManager.NameFromUri(u) : "indirme";

    private void SetCategoryAndPath(string cat, string name)
    {
        _updating = true;
        if (!_cat.Items.Contains(cat)) _cat.Items.Add(cat);
        _cat.SelectedItem = cat;
        var folder = _mgr.FolderForCategory(cat);
        _save.Text = Path.Combine(folder, name);
        _folder.Text = folder;
        _remember.Text = $"Bu yolu \"{cat}\" kategorisi için hatırla";
        _updating = false;
        _pathEdited = false;
    }

    private void AddCategory()
    {
        using var d = new AddCategoryDialog(_cat.Items.Cast<string>());
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var name = d.CategoryName;
        _mgr.Settings.CustomCategories.Add(name);
        if (!_mgr.Settings.CategoryFolders.ContainsKey(name))
            _mgr.Settings.CategoryFolders[name] = Path.Combine(_mgr.Settings.SaveFolder, name);
        try { _mgr.Settings.Save(_mgr.Paths.SettingsFile); } catch (Exception) { }
        _cat.Items.Add(name);
        _cat.SelectedItem = name;   // SelectedIndexChanged yolu günceller
    }

    private static string SizeText(long b)
    {
        if (b <= 0) return "Bilinmiyor";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : v.ToString("0.00", Tr) + " " + u[i];
    }

    private void UpdateSizeAndIcon()
    {
        _size.Text = SizeText(_fileSize);
        var ext = Path.GetExtension(FileName);
        if (ext == _iconExt) return;
        _iconExt = ext;
        var old = _icon.Image;
        _icon.Image = ShellIcons.Large(FileName);
        old?.Dispose();
    }

    private async Task ProbeSizeAsync()
    {
        if (!_probe || _fileSize > 0 || !Uri.TryCreate(Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
        long len = await TryProbe(u, HttpMethod.Head);
        if (len <= 0) len = await TryProbe(u, HttpMethod.Get);
        if (len > 0 && !IsDisposed && _fileSize <= 0) { _fileSize = len; UpdateSizeAndIcon(); }
        // Sunucu Content-Disposition ile gerçek adı veriyorsa (ve kullanıcı yolu elle değiştirmediyse) onu öner.
        if (_probedName != null && !IsDisposed && !_pathEdited &&
            string.Equals(FileName, NameFromUrl(Url), StringComparison.OrdinalIgnoreCase))
        {
            var n = DownloadManager.SanitizeFileName(_probedName);
            SetCategoryAndPath(Categories.FromFileName(n), n);
            UpdateSizeAndIcon();
        }
    }

    private string? _probedName;

    private async Task<long> TryProbe(Uri u, HttpMethod method)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(method, u);
            if (method == HttpMethod.Get) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            if (!string.IsNullOrEmpty(_referrer)) req.Headers.TryAddWithoutValidation("Referer", _referrer);
            if (!string.IsNullOrEmpty(_cookies)) req.Headers.TryAddWithoutValidation("Cookie", _cookies);
            if (!string.IsNullOrEmpty(_userAgent)) req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            using var resp = await Core.Downloads.EngineHttp.Shared.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (_probedName == null && resp.Content.Headers.TryGetValues("Content-Disposition", out var cd))
                _probedName = Core.Util.ContentDispositionParser.Parse(string.Join("; ", cd));
            if (method == HttpMethod.Get && resp.Content.Headers.ContentRange?.Length is long total) return total;
            if (method == HttpMethod.Head || resp.StatusCode == System.Net.HttpStatusCode.OK)
                return resp.Content.Headers.ContentLength ?? -1;
        }
        catch (Exception) { }
        return -1;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult is not (DialogResult.Yes or DialogResult.No)) return;
        string err = "";
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            err = "Geçerli bir http/https adresi girin.";
        else if (string.IsNullOrWhiteSpace(FileName) || string.IsNullOrWhiteSpace(SaveFolder))
            err = "Geçerli bir kayıt yeri girin.";
        if (err.Length > 0)
        {
            MessageBox.Show(this, err, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true; DialogResult = DialogResult.None; return;
        }
        StartNow = DialogResult == DialogResult.Yes;
        if (_remember.Checked && _cat.SelectedItem is string cat)
        {
            _mgr.Settings.CategoryFolders[cat] = SaveFolder;
            try { _mgr.Settings.Save(_mgr.Paths.SettingsFile); } catch (Exception) { }
        }
    }
}

/// <summary>"+" düğmesi: yeni kategori adı için basit giriş kutusu.</summary>
public sealed class AddCategoryDialog : DluForm
{
    private readonly TextBox _name;
    private readonly HashSet<string> _existing;
    public string CategoryName => _name.Text.Trim();

    public AddCategoryDialog(IEnumerable<string> existing)
    {
        _existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        Text = "Yeni kategori";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(200, 56);
        MakeLabel("Kategori adı:", 7, 7, 186, 10);
        _name = Add(new TextBox(), 7, 20, 186, 14);
        var ok = MakeButton("Tamam", 63, 38, 60, 14, DialogResult.OK);
        var cancel = MakeButton("İptal", 133, 38, 60, 14, DialogResult.Cancel);
        AcceptButton = ok; CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            string err = "";
            if (CategoryName.Length == 0) err = "Bir kategori adı girin.";
            else if (CategoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) err = "Kategori adında geçersiz karakter var.";
            else if (_existing.Contains(CategoryName)) err = "Bu adda bir kategori zaten var.";
            if (err.Length == 0) return;
            MessageBox.Show(this, err, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
        };
    }
}
