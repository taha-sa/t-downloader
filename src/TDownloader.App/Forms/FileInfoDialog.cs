using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App.Forms;

/// <summary>"Dosya bilgisi / İndirme başlat" diyaloğu (IDM Download File Info: 380x128 DLU).</summary>
public sealed class FileInfoDialog : DluForm
{
    private readonly DownloadManager _mgr;
    private readonly TextBox _url, _save, _desc;
    private readonly ComboBox _cat;
    private readonly CheckBox _remember;
    private readonly Label _size;
    private bool _pathEdited;
    private bool _updating;
    private long _fileSize;

    public bool StartNow { get; private set; }
    public string Url => _url.Text.Trim();
    public string? Description => string.IsNullOrWhiteSpace(_desc.Text) ? null : _desc.Text.Trim();
    public string SaveFolder => Path.GetDirectoryName(_save.Text.Trim()) ?? "";
    public string FileName => Path.GetFileName(_save.Text.Trim());
    public long FileSize => _fileSize;

    public FileInfoDialog(DownloadManager mgr, string url, string? fileName = null, long fileSize = -1)
    {
        _mgr = mgr;
        _fileSize = fileSize;
        Text = "Dosya bilgisi";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        SetClientDlu(316, 132);

        MakeLabel("&URL:", 7, 5, 55, 10);
        _url = Add(new TextBox { Text = url }, 64, 3, 244, 14);
        MakeLabel("&Kategori:", 7, 24, 55, 10);
        _cat = Add(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 64, 22, 116, 64);
        _cat.Items.AddRange(Categories.All);
        MakeLabel("Kayıt &yeri:", 7, 41, 55, 10);
        _save = Add(new TextBox(), 64, 39, 223, 14);
        var browse = MakeButton("...", 292, 39, 16, 14);
        _remember = Add(new CheckBox { AutoEllipsis = true }, 64, 57, 244, 11);
        MakeLabel("Boyut:", 7, 74, 55, 10);
        _size = MakeLabel("", 64, 74, 244, 10);
        MakeLabel("&Açıklama:", 7, 93, 55, 10);
        _desc = Add(new TextBox(), 64, 91, 244, 14);

        var later = MakeButton("&Sonra İndir", 64, 112, 74, 14, DialogResult.No);
        var start = MakeButton("&Şimdi Başlat", 149, 112, 74, 14, DialogResult.Yes);
        var cancel = MakeButton("İ&ptal", 234, 112, 74, 14, DialogResult.Cancel);
        AcceptButton = start; CancelButton = cancel;

        var name = string.IsNullOrWhiteSpace(fileName) ? NameFromUrl(url) : DownloadManager.SanitizeFileName(fileName!);
        SetCategoryAndPath(Categories.FromFileName(name), name);
        UpdateSizeLabel();

        _cat.SelectedIndexChanged += (_, _) => { if (!_updating) SetCategoryAndPath((string)_cat.SelectedItem!, FileName); };
        _save.TextChanged += (_, _) => { if (!_updating) _pathEdited = true; };
        _url.TextChanged += (_, _) =>
        {
            if (_pathEdited) return;
            SetCategoryAndPath(Categories.FromFileName(NameFromUrl(_url.Text)), NameFromUrl(_url.Text));
        };
        browse.Click += (_, _) =>
        {
            using var sfd = new SaveFileDialog { FileName = FileName, InitialDirectory = Directory.Exists(SaveFolder) ? SaveFolder : null, Title = "Kayıt yeri" };
            if (sfd.ShowDialog(this) == DialogResult.OK) { _pathEdited = true; _save.Text = sfd.FileName; }
        };
        FormClosing += OnClosing;
        Shown += async (_, _) => await ProbeSizeAsync();
    }

    private static string NameFromUrl(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? DownloadManager.NameFromUri(u) : "indirme";

    private void SetCategoryAndPath(string cat, string name)
    {
        _updating = true;
        _cat.SelectedItem = cat;
        _save.Text = Path.Combine(_mgr.FolderForCategory(cat), name);
        _remember.Text = $"Bu yolu \"{cat}\" kategorisi için hatırla";
        _updating = false;
        _pathEdited = false;
    }

    private void UpdateSizeLabel() => _size.Text = _fileSize > 0 ? Fmt.Size(_fileSize) : "Bilinmiyor";

    private async Task ProbeSizeAsync()
    {
        if (_fileSize > 0 || !Uri.TryCreate(Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var req = new HttpRequestMessage(HttpMethod.Head, u);
            using var resp = await Core.Downloads.EngineHttp.Shared.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.Content.Headers.ContentLength is long len && len > 0 && !IsDisposed) { _fileSize = len; UpdateSizeLabel(); }
        }
        catch { }
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
            try { _mgr.Settings.Save(_mgr.Paths.SettingsFile); } catch { }
        }
    }
}
