using TDownloader.App.Ui;

namespace TDownloader.App.Forms;

/// <summary>"Diskte yeterli yer yok": Yine de indir (Yes) / İptal. Tema ortak DluForm'dan gelir.</summary>
public sealed class DiskWarnDialog : DluForm
{
    public DiskWarnDialog(long size, long free)
    {
        Text = "Diskte yeterli yer yok";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        SetClientDlu(220, 76);
        MakeLabel($"Hedef sürücüde {Fmt.Size(free)} boş alan var; bu indirme {Fmt.Size(size)} + 1 GB pay gerektiriyor.\n\nDisk dolarsa indirme yarıda kalabilir.", 10, 10, 200, 40);
        MakeButton("Yine de indir", 100, 54, 62, 14, DialogResult.Yes);
        var cancel = MakeButton("İptal", 168, 54, 42, 14, DialogResult.Cancel);
        AcceptButton = cancel; CancelButton = cancel;
    }
}
