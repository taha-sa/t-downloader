using TDownloader.Core.Localization;
using TDownloader.App.Ui;

namespace TDownloader.App.Forms;

/// <summary>"URL Ekle": yeni indirme adresi girilir, ardından Dosya bilgisi açılır.</summary>
public sealed class AddUrlDialog : DluForm
{
    private readonly TextBox _url;
    public string Url => _url.Text.Trim();

    public AddUrlDialog(string? initial = null)
    {
        Text = Strings.EnterANewDownloadURL;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(250, 62);
        MakeLabel(Strings.EnterTheFileURLToDownload, 7, 7, 236, 10);
        _url = Add(new TextBox { Text = initial ?? "" }, 7, 21, 236, 14);
        var ok = MakeButton(Strings.OK, 85, 42, 74, 14, DialogResult.OK);
        var cancel = MakeButton(Strings.Cancel, 169, 42, 74, 14, DialogResult.Cancel);
        AcceptButton = ok; CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            {
                MessageBox.Show(this, Strings.TheURLIsInvalidEnterAValidHTTP, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        };
    }
}
