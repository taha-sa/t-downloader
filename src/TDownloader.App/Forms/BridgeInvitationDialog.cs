using TDownloader.App.Ui;
using TDownloader.Core.Localization;

namespace TDownloader.App.Forms;

/// <summary>Closing this invitation never changes preferences.</summary>
internal sealed class BridgeInvitationDialog : DluForm
{
    public BridgeInvitationDialog()
    {
        Text = AccessibleName = Strings.BridgeInvitationTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        SetClientDlu(310, 118);
        Add(new PictureBox { Image = Icons.Family("t-bridge", 64), SizeMode = PictureBoxSizeMode.Zoom,
            AccessibleName = "T-Bridge" }, 12, 12, 36, 36);
        MakeLabel(Strings.BridgeInvitationTitle, 58, 12, 238, 18);
        MakeLabel(Strings.BridgeInvitationBody, 58, 34, 238, 42);
        var get = MakeButton(Strings.GetBridge, 130, 91, 112, 18);
        var close = MakeButton("×", 250, 91, 46, 18, DialogResult.Cancel);
        close.AccessibleName = Strings.Close;
        CancelButton = close;
        AcceptButton = get;
        get.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/taha-sa/t-bridge-releases/releases/latest") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { MessageBox.Show(this, Strings.CouldNotCompleteTheActionTryAgain, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}
