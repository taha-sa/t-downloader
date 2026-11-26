using System.Reflection;
using TDownloader.App.Ui;
using TDownloader.Core.Localization;

namespace TDownloader.App.Forms;

internal sealed class LegalDialog : Form
{
    internal LegalDialog()
    {
        Text = AccessibleName = Strings.Legal;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg; ForeColor = Theme.Text;
        ShowInTaskbar = false;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var (title, file) in new[] { (Strings.Privacy, "PRIVACY.md"), (Strings.License, "LICENSE"), (Strings.Notices, "THIRD-PARTY-NOTICES.md") })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Legal." + file)
                ?? throw new InvalidOperationException("Missing embedded legal document: " + file);
            using var reader = new StreamReader(stream);
            var page = new TabPage(title) { BackColor = Theme.Bg, ForeColor = Theme.Text };
            page.Controls.Add(new TextBox { Text = reader.ReadToEnd(), ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill,
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10),
                AccessibleName = title, SelectionStart = 0 });
            tabs.TabPages.Add(page);
        }
        var close = new Button { Text = Strings.Close, AccessibleName = Strings.Close,
            Dock = DockStyle.Bottom, Height = 40, DialogResult = DialogResult.Cancel };
        Controls.Add(tabs); Controls.Add(close); CancelButton = close;
    }
}
