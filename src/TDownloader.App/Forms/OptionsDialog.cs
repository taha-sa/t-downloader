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

    private readonly CheckBox _tray, _clip, _showInfo;
    private readonly NumericUpDown _conn, _concurrent, _limit;
    private readonly TextBox _root, _catFolder;
    private readonly ListView _cats;
    private readonly CheckBox _capture;
    private readonly NumericUpDown _minKb;
    private readonly TextBox _types, _excluded;

    public AppSettings Result => _s;

    public OptionsDialog(AppSettings current, string dataFolder, Action<AppSettings> apply)
    {
        _apply = apply;
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, JsonDefaults.Options), JsonDefaults.Options) ?? new();
        Text = "Seçenekler";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(268, 257);

        var tabs = Add(new TabControl(), 5, 5, 258, 229);
        TabPage Page(string t) { var p = new TabPage(t) { UseVisualStyleBackColor = true }; tabs.TabPages.Add(p); return p; }
        T Put<T>(Control parent, T c, double x, double y, double w, double h) where T : Control { c.Bounds = Dlu.R(x, y, w, h); parent.Controls.Add(c); return c; }

        // --- Genel
        var g = Page("Genel");
        _tray = Put(g, new CheckBox { Text = "Pencereyi kapatınca sistem tepsisine küçült", AutoEllipsis = true }, 10, 12, 230, 11);
        _clip = Put(g, new CheckBox { Text = "Panodaki indirilebilir bağlantıları izle ve sor", AutoEllipsis = true }, 10, 28, 230, 11);
        _showInfo = Put(g, new CheckBox { Text = "Tarayıcıdan gelen indirmelerde \"Dosya bilgisi\" penceresini göster", AutoEllipsis = true }, 10, 44, 238, 11);
        Put(g, new Label { Text = "Veri klasörü:" }, 10, 70, 80, 10);
        Put(g, new TextBox { Text = dataFolder, ReadOnly = true }, 10, 82, 232, 14);

        // --- Bağlantı
        var c = Page("Bağlantı");
        Put(c, new Label { Text = "İndirme başına bağlantı sayısı (1-32):" }, 10, 14, 150, 10);
        _conn = Put(c, new NumericUpDown { Minimum = 1, Maximum = 32 }, 170, 12, 50, 14);
        Put(c, new Label { Text = "Aynı anda en çok indirme sayısı:" }, 10, 34, 150, 10);
        _concurrent = Put(c, new NumericUpDown { Minimum = 1, Maximum = 20 }, 170, 32, 50, 14);
        Put(c, new Label { Text = "Hız sınırı (KB/sn, 0 = sınırsız):" }, 10, 54, 150, 10);
        _limit = Put(c, new NumericUpDown { Minimum = 0, Maximum = 10_000_000, Increment = 100 }, 170, 52, 70, 14);
        Put(c, new Label { Text = "Sunucu Range desteklemiyorsa indirme otomatik olarak tek bağlantıyla yapılır.", AutoSize = false }, 10, 80, 232, 24);

        // --- Kayıt Yeri
        var k = Page("Kayıt Yeri");
        Put(k, new Label { Text = "Ana kayıt klasörü:" }, 10, 8, 120, 10);
        _root = Put(k, new TextBox(), 10, 20, 206, 14);
        var rb = Put(k, new Button { Text = "...", FlatStyle = FlatStyle.System }, 221, 20, 20, 14);
        Put(k, new Label { Text = "Kategori klasörleri (boşsa ana klasör altında kategori adı):" }, 10, 42, 232, 10);
        _cats = Put(k, new BufferedListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false }, 10, 54, 232, 80);
        _cats.Columns.Add("Kategori", Dlu.X(60));
        _cats.Columns.Add("Klasör", Dlu.X(165));
        _catFolder = Put(k, new TextBox(), 10, 140, 156, 14);
        var cb = Put(k, new Button { Text = "...", FlatStyle = FlatStyle.System }, 170, 140, 20, 14);
        var cr = Put(k, new Button { Text = "Varsayılan", FlatStyle = FlatStyle.System }, 194, 140, 48, 14);

        // --- Tarayıcı yakalama
        var b = Page("Tarayıcı Yakalama");
        _capture = Put(b, new CheckBox { Text = "Tarayıcı indirmelerini yakala", AutoEllipsis = true }, 10, 10, 230, 11);
        Put(b, new Label { Text = "En küçük dosya boyutu (KB):" }, 10, 30, 130, 10);
        _minKb = Put(b, new NumericUpDown { Minimum = 0, Maximum = 10_000_000 }, 150, 28, 70, 14);
        Put(b, new Label { Text = "Yakalanacak dosya türleri (boşlukla ayırın):" }, 10, 48, 232, 10);
        _types = Put(b, new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical }, 10, 60, 232, 50);
        Put(b, new Label { Text = "Yakalanmayacak siteler (her satıra bir tane):" }, 10, 116, 232, 10);
        _excluded = Put(b, new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true }, 10, 128, 232, 60);

        var ok = MakeButton("Tamam", 105, 238, 50, 14, DialogResult.OK);
        var cancel = MakeButton("İptal", 159, 238, 50, 14, DialogResult.Cancel);
        var apply2 = MakeButton("Uygula", 213, 238, 50, 14);
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

    private string? PickFolder(string start)
    {
        using var fb = new FolderBrowserDialog { SelectedPath = Directory.Exists(start) ? start : "", Description = "Klasör seçin" };
        return fb.ShowDialog(this) == DialogResult.OK ? fb.SelectedPath : null;
    }

    private void Load2()
    {
        _tray.Checked = _s.MinimizeToTrayOnClose;
        _clip.Checked = _s.ClipboardMonitor;
        _showInfo.Checked = _s.ShowFileInfoOnCapture;
        _conn.Value = Math.Clamp(_s.Connections, 1, 32);
        _concurrent.Value = Math.Clamp(_s.MaxConcurrentDownloads, 1, 20);
        _limit.Value = Math.Clamp(_s.SpeedLimitKBps, 0, 10_000_000);
        _root.Text = _s.SaveFolder;
        foreach (var cat in Categories.All)
            _cats.Items.Add(new ListViewItem(new[] { cat, _s.CategoryFolders.TryGetValue(cat, out var f) ? f : "" }));
        _capture.Checked = _s.BrowserCaptureEnabled;
        _minKb.Value = Math.Clamp(_s.MinFileSizeKb, 0, 10_000_000);
        _types.Text = string.Join(" ", _s.FileTypes);
        _excluded.Text = string.Join(Environment.NewLine, _s.ExcludedSites);
    }

    private void Collect()
    {
        _s.MinimizeToTrayOnClose = _tray.Checked;
        _s.ClipboardMonitor = _clip.Checked;
        _s.ShowFileInfoOnCapture = _showInfo.Checked;
        _s.Connections = (int)_conn.Value;
        _s.MaxConcurrentDownloads = (int)_concurrent.Value;
        _s.SpeedLimitKBps = (int)_limit.Value;
        if (!string.IsNullOrWhiteSpace(_root.Text)) _s.SaveFolder = _root.Text.Trim();
        _s.CategoryFolders = new();
        foreach (ListViewItem li in _cats.Items)
            if (!string.IsNullOrWhiteSpace(li.SubItems[1].Text)) _s.CategoryFolders[li.Text] = li.SubItems[1].Text;
        _s.BrowserCaptureEnabled = _capture.Checked;
        _s.MinFileSizeKb = (int)_minKb.Value;
        _s.FileTypes = _types.Text.Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimStart('.').ToLowerInvariant()).Distinct().ToList();
        _s.ExcludedSites = _excluded.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }
}
