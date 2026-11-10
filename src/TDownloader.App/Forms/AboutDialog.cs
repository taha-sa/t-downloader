using TSuite.Diagnostics;
using System.Diagnostics;
using TSuite.Link;
using System.Runtime.InteropServices;
using TDownloader.App.Services;
using TDownloader.App.Ui;
using TDownloader.Core;

namespace TDownloader.App.Forms;

/// <summary>
/// "T-Downloader hakkında" iletişim kutusu: sürüm bilgisi, Instagram ve imza.
/// </summary>
public sealed class AboutDialog : DluForm
{
    private readonly Font _signatureFont;

    public AboutDialog(string dataFolder)
    {
        Text = "T-Downloader hakkında";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(294, 320);
        AutoScroll = true;

        // Logo / Simge
        var iconPic = new PictureBox
        {
            Image = Icons.Family("t-downloader", 64),
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.Transparent
        };
        Add(iconPic, 125, 8, 44, 44);

        // Başlık ve sürüm
        Add(new Label
        {
            Text = $"T-Downloader {AppServices.Version} · {RuntimeInformation.ProcessArchitecture}",
            Font = Theme.UiFont(11f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleCenter
        }, 12, 56, 270, 14);

        // Açıklama
        Add(new Label
        {
            Text = "Çok bağlantılı, devam ettirilebilir indirme yöneticisi.",
            ForeColor = Theme.Text,
            Font = Theme.UiFont(8f),
            AutoEllipsis = true
        }, 12, 74, 270, 11);

        // Veri klasörü
        Add(new Label
        {
            Text = $"Veri klasörü: {dataFolder}",
            ForeColor = Theme.TextDim,
            Font = Theme.UiFont(7.5f),
            AutoEllipsis = true
        }, 12, 88, 270, 10);

        // Ayırıcı çizgi
        Add(new Panel
        {
            BackColor = Theme.Border,
            Height = 1
        }, 12, 102, 270, 1);

        Add(new Label { Text = "T-Suite ailesinin parçası", ForeColor = Theme.Accent,
            Font = Theme.UiFont(10f, FontStyle.Bold) }, 12, 108, 270, 16);
        var registry = new AppRegistry();
        (string Id, string Name)[] family = [
            ("t-bridge", "T-Bridge"), ("t-downloader", "T-Downloader"),
            ("t-ramcash", "T-RamCash"), ("t-desktree", "T-DeskTree"),
            ("t-desk-viewer", "T-Desk Viewer")
        ];
        int rowY = 130;
        foreach (var app in family)
        {
            var manifest = registry.Read(app.Id);
            bool current = app.Id == "t-downloader";
            bool installed = current || (manifest != null && File.Exists(manifest.ExePath));
            string? version = current ? AppServices.Version : null;
            if (installed && !current && manifest != null)
            {
                try { version = FileVersionInfo.GetVersionInfo(manifest.ExePath).ProductVersion; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
                if (string.IsNullOrWhiteSpace(version)) version = manifest.Version;
            }
            // ProductVersion can contain a full source revision; keep the roster compact.
            version = version?.Split('+')[0];
            Add(new PictureBox { Image = Icons.Family(app.Id), SizeMode = PictureBoxSizeMode.Zoom,
                AccessibleName = app.Name }, 12, rowY, 20, 20);
            Add(new Label { Text = app.Name + (installed ? $"  {version}" : ""), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft }, 38, rowY, 162, 20);
            if (installed)
                Add(new Label { Text = current ? "Bu uygulama" : "Yüklü", ForeColor = Theme.TextDim,
                    TextAlign = ContentAlignment.MiddleRight }, 202, rowY, 80, 20);
            else
            {
                var get = Add(new LinkLabel { Text = "Edin…", TextAlign = ContentAlignment.MiddleRight,
                    AccessibleName = app.Name + " edin", LinkColor = Theme.Accent }, 202, rowY, 80, 20);
                get.LinkClicked += (_, _) => Open($"https://github.com/taha-sa/{app.Id}-releases/releases/latest");
            }
            rowY += 26;
        }

        // Instagram
        Add(new Label
        {
            Text = "Instagram:",
            ForeColor = Theme.TextDim,
            Font = Theme.UiFont(8f)
        }, 12, 264, 50, 12);

        var instaLink = Add(new LinkLabel
        {
            Text = "tdesk.studio",
            Font = Theme.UiFont(8f, FontStyle.Regular),
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            VisitedLinkColor = Theme.Accent,
            AutoEllipsis = true
        }, 62, 264, 110, 12);

        instaLink.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.instagram.com/tdesk.studio") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        };

        // İmza: "Taha Şaprak " (küçük boyutlu signature fontu)
        _signatureFont = CreateSignatureFont(9.5f);
        Add(new Label
        {
            Text = "Taha Şaprak ",
            Font = _signatureFont,
            ForeColor = Theme.AccentHover,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        }, 176, 260, 106, 18);

        // Tamam düğmesi
        var automatic = Add(new CheckBox
        {
            Text = "Hata raporlarını otomatik hazırla", ForeColor = Theme.Text,
            Font = Theme.UiFont(8f), Checked = CrashReporter.AutomaticallyPrepareReports
        }, 12, 282, 270, 14);
        automatic.CheckedChanged += (_, _) =>
        {
            try { CrashReporter.AutomaticallyPrepareReports = automatic.Checked; }
            catch (Exception ex)
            {
                Log.Error("Could not save crash report preference: " + ex);
                MessageBox.Show(this, "Tercih kaydedilemedi.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var reportLink = Add(new LinkLabel
        {
            Text = "Hata raporu gönder…", Font = Theme.UiFont(8f), LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover, VisitedLinkColor = Theme.Accent
        }, 12, 302, 270, 14);
        reportLink.LinkClicked += (_, _) =>
        {
            using var dialog = new CrashReportDialog(ManualReport.Create(), UpdateService.Theme);
            dialog.ShowDialog(this);
            automatic.Checked = CrashReporter.AutomaticallyPrepareReports;
        };
        Add(new Label { Text = "Hata raporunu göndermeden önce inceleyebilirsiniz.",
            ForeColor = Theme.TextDim }, 12, 322, 270, 14);
        var source = Add(new LinkLabel { Text = "Kaynak ve lisans bilgileri", LinkColor = Theme.Accent }, 12, 342, 270, 14);
        source.LinkClicked += (_, _) => Open("https://github.com/taha-sa/T-Downloader");
        var diagnostics = MakeButton("Sistem tanısını kopyala", 12, 364, 132, 22);
        diagnostics.Click += (_, _) =>
        {
            try { Clipboard.SetText($"T-Downloader {AppServices.Version}\r\n{RuntimeInformation.OSDescription}\r\nOS: {RuntimeInformation.OSArchitecture}; App: {RuntimeInformation.ProcessArchitecture}\r\n.NET: {RuntimeInformation.FrameworkDescription}"); }
            catch (ExternalException) { }
        };
        var issue = MakeButton("Sorun bildir", 150, 364, 132, 22);
        issue.Click += (_, _) => Open("https://github.com/taha-sa/t-downloader-releases/issues");
        var ok = MakeButton("Kapat", 115, 398, 64, 22, DialogResult.OK);
        ok.FlatStyle = FlatStyle.Flat;
        ok.FlatAppearance.BorderSize = 0;
        ok.BackColor = Theme.Accent;
        ok.ForeColor = Theme.OnAccent;
        ok.UseVisualStyleBackColor = false;
        ok.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
        ok.FlatAppearance.MouseDownBackColor = Theme.AccentPressed;
        AcceptButton = ok;
        CancelButton = ok;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static Font CreateSignatureFont(float pt)
    {
        string[] candidates = ["Segoe Script", "Brush Script MT", "Lucida Handwriting", "Freestyle Script", "Segoe Print"];
        foreach (var name in candidates)
        {
            try
            {
                var f = new Font(name, pt, FontStyle.Italic);
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return f;
                f.Dispose();
            }
            catch { }
        }
        return new Font(FontFamily.GenericSansSerif, pt, FontStyle.Italic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _signatureFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
