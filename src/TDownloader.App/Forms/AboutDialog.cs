using TSuite.Diagnostics;
using System.Diagnostics;
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
        SetClientDlu(240, 190);

        // Logo / Simge
        var iconPic = new PictureBox
        {
            Image = Icons.Get("app", 32) ?? Icons.Get("about", 32),
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.Transparent
        };
        Add(iconPic, 12, 12, 24, 24);

        // Başlık ve sürüm
        Add(new Label
        {
            Text = $"T-Downloader {AppServices.Version}",
            Font = Theme.UiFont(11f, FontStyle.Bold),
            ForeColor = Theme.Yellow,
            AutoEllipsis = true
        }, 42, 12, 186, 14);

        // Açıklama
        Add(new Label
        {
            Text = "Çok bağlantılı, devam ettirilebilir indirme yöneticisi.",
            ForeColor = Theme.Text,
            Font = Theme.UiFont(8f),
            AutoEllipsis = true
        }, 42, 28, 186, 11);

        // Veri klasörü
        Add(new Label
        {
            Text = $"Veri klasörü: {dataFolder}",
            ForeColor = Theme.TextDim,
            Font = Theme.UiFont(7.5f),
            AutoEllipsis = true
        }, 42, 42, 186, 10);

        // Ayırıcı çizgi
        Add(new Panel
        {
            BackColor = Theme.Border,
            Height = 1
        }, 12, 58, 216, 1);

        // Instagram
        Add(new Label
        {
            Text = "Instagram:",
            ForeColor = Theme.TextDim,
            Font = Theme.UiFont(8f)
        }, 12, 68, 50, 12);

        var instaLink = Add(new LinkLabel
        {
            Text = "tdesk.studio",
            Font = Theme.UiFont(8f, FontStyle.Regular),
            LinkColor = Theme.Yellow,
            ActiveLinkColor = Theme.YellowLight,
            VisitedLinkColor = Theme.Yellow,
            AutoEllipsis = true
        }, 62, 68, 166, 12);

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
            ForeColor = Theme.YellowLight,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        }, 62, 88, 166, 18);

        // Tamam düğmesi
        var automatic = Add(new CheckBox
        {
            Text = "Hata raporlarını otomatik hazırla", ForeColor = Theme.Text,
            Font = Theme.UiFont(8f), Checked = CrashReporter.AutomaticallyPrepareReports
        }, 12, 110, 216, 14);
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
            Text = "Hata raporu gönder…", Font = Theme.UiFont(8f), LinkColor = Theme.Yellow,
            ActiveLinkColor = Theme.YellowLight, VisitedLinkColor = Theme.Yellow
        }, 12, 130, 216, 14);
        reportLink.LinkClicked += (_, _) =>
        {
            using var dialog = new CrashReportDialog(ManualReport.Create(), UpdateService.Theme);
            dialog.ShowDialog(this);
            automatic.Checked = CrashReporter.AutomaticallyPrepareReports;
        };
        var ok = MakeButton("Tamam", 88, 160, 64, 16, DialogResult.OK);
        AcceptButton = ok;
        CancelButton = ok;
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
