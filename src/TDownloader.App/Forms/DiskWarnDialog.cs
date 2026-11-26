using TDownloader.Core.Localization;
using TDownloader.App.Ui;

namespace TDownloader.App.Forms;

/// <summary>"Diskte yeterli yer yok": Yine de indir (Yes) / İptal. Tema ortak DluForm'dan gelir.</summary>
public sealed class DiskWarnDialog : DluForm
{
    public DiskWarnDialog(long size, long free)
    {
        Text = Strings.NotEnoughDiskSpace;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        SetClientDlu(220, 76);
        MakeLabel(Strings.TheTargetDriveHasFreeThisDownloadNeeds(Fmt.Size(free), Fmt.Size(size)), 10, 10, 200, 40);
        MakeButton(Strings.DownloadAnyway, 100, 54, 62, 14, DialogResult.Yes);
        var cancel = MakeButton(Strings.Cancel, 168, 54, 42, 14, DialogResult.Cancel);
        AcceptButton = cancel; CancelButton = cancel;
    }
}
