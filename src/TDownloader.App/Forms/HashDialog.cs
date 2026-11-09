using System.Globalization;
using System.Security.Cryptography;
using TDownloader.App.Ui;

namespace TDownloader.App.Forms;

/// <summary>
/// İndirilen dosyanın MD5 ve SHA-256 sağlama toplamını (hash) tek geçişte hesaplar ve doğrular.
/// </summary>
public sealed class HashDialog : DluForm
{
    private readonly string _filePath;
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBox _txtMd5;
    private readonly TextBox _txtSha256;
    private readonly TextBox _txtVerify;
    private readonly Label _lblStatus;
    private readonly Label _lblMatch;
    private readonly Button _btnCopyMd5;
    private readonly Button _btnCopySha256;

    private string _calcMd5 = "";
    private string _calcSha256 = "";

    public HashDialog(string filePath, string fileName)
    {
        _filePath = filePath;

        Text = "Sağlama Toplamı (Hash) - " + fileName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(290, 185);

        // Dosya bilgisi
        MakeLabel("Dosya:", 10, 10, 45, 10);
        Add(new Label
        {
            Text = fileName,
            Font = Theme.UiFont(8f, FontStyle.Bold),
            ForeColor = Theme.Yellow,
            AutoEllipsis = true
        }, 55, 10, 225, 10);

        long size = 0;
        try { if (File.Exists(_filePath)) size = new FileInfo(_filePath).Length; } catch { }

        MakeLabel("Boyut:", 10, 23, 45, 10);
        Add(new Label
        {
            Text = FormatSize(size),
            ForeColor = Theme.TextDim,
            Font = Theme.UiFont(7.5f)
        }, 55, 23, 225, 10);

        // Durum
        _lblStatus = Add(new Label
        {
            Text = "Hesaplanıyor, lütfen bekleyin...",
            ForeColor = Theme.YellowLight,
            Font = Theme.UiFont(7.5f, FontStyle.Italic),
            AutoEllipsis = true
        }, 10, 36, 270, 10);

        // Ayırıcı
        Add(new Panel { BackColor = Theme.Border, Height = 1 }, 10, 48, 270, 1);

        // MD5
        MakeLabel("MD5:", 10, 56, 40, 12);
        _txtMd5 = Add(new TextBox { ReadOnly = true, Font = Theme.UiFont(8f) }, 55, 54, 175, 13);
        _btnCopyMd5 = MakeButton("Kopyala", 235, 54, 45, 13);
        _btnCopyMd5.Click += (_, _) => CopyToClipboard(_calcMd5, _btnCopyMd5);

        // SHA-256
        MakeLabel("SHA-256:", 10, 73, 40, 12);
        _txtSha256 = Add(new TextBox { ReadOnly = true, Font = Theme.UiFont(8f) }, 55, 71, 175, 13);
        _btnCopySha256 = MakeButton("Kopyala", 235, 71, 45, 13);
        _btnCopySha256.Click += (_, _) => CopyToClipboard(_calcSha256, _btnCopySha256);

        // Ayırıcı
        Add(new Panel { BackColor = Theme.Border, Height = 1 }, 10, 92, 270, 1);

        // Doğrulama / Karşılaştırma
        MakeLabel("Doğrula (Beklenen hash değerini yapıştırın):", 10, 98, 270, 10);
        _txtVerify = Add(new TextBox { Font = Theme.UiFont(8f) }, 10, 110, 270, 13);
        _txtVerify.TextChanged += (_, _) => UpdateMatchStatus();

        _lblMatch = Add(new Label
        {
            Text = "",
            Font = Theme.UiFont(8f, FontStyle.Bold),
            AutoEllipsis = true
        }, 10, 128, 270, 14);

        // Kapat
        var close = MakeButton("Kapat", 115, 155, 60, 15, DialogResult.OK);
        AcceptButton = close;
        CancelButton = close;

        FormClosing += (_, _) => _cts.Cancel();
        Shown += (_, _) => StartCalculate();
    }

    private void StartCalculate()
    {
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    InvokeSafe(() => _lblStatus.Text = "Hata: Dosya bulunamadı.");
                    return;
                }

                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    sha.AppendData(buffer, 0, read);
                }

                var md5Bytes = md5.GetHashAndReset();
                var shaBytes = sha.GetHashAndReset();

                string md5Hex = Convert.ToHexString(md5Bytes).ToLowerInvariant();
                string shaHex = Convert.ToHexString(shaBytes).ToLowerInvariant();

                InvokeSafe(() =>
                {
                    _calcMd5 = md5Hex;
                    _calcSha256 = shaHex;
                    _txtMd5.Text = md5Hex;
                    _txtSha256.Text = shaHex;
                    _lblStatus.Text = "Tamamlandı.";
                    _lblStatus.ForeColor = Theme.Ok;
                    UpdateMatchStatus();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                InvokeSafe(() =>
                {
                    _lblStatus.Text = "Hesaplama hatası: " + ex.Message;
                    _lblStatus.ForeColor = Theme.Danger;
                });
            }
        }, ct);
    }

    private void UpdateMatchStatus()
    {
        string input = _txtVerify.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            _lblMatch.Text = "";
            return;
        }

        if (string.IsNullOrEmpty(_calcMd5) && string.IsNullOrEmpty(_calcSha256))
        {
            _lblMatch.Text = "Hesaplama devam ediyor...";
            _lblMatch.ForeColor = Theme.TextDim;
            return;
        }

        if (string.Equals(input, _calcMd5, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, _calcSha256, StringComparison.OrdinalIgnoreCase))
        {
            _lblMatch.Text = "✓ Hash eşleşti! (Dosya sağlam ve orijinal)";
            _lblMatch.ForeColor = Theme.Ok;
        }
        else
        {
            _lblMatch.Text = "✗ Eşleşmedi! (Beklenen değer ile uyuşmuyor)";
            _lblMatch.ForeColor = Theme.Danger;
        }
    }

    private void CopyToClipboard(string text, Button btn)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            string old = btn.Text;
            btn.Text = "Kopyalandı!";
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (!btn.IsDisposed) btn.Text = old; };
            t.Start();
        }
        catch { }
    }

    private void InvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes;
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {units[i]} ({bytes.ToString("N0", CultureInfo.CurrentCulture)} bayt)";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
