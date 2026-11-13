using System.Drawing;
using System.Windows.Forms;

namespace HBProxy.Windows;

public sealed class MainForm : Form
{
    private readonly Label _status = new();
    private readonly Button _connect = new();

    public MainForm()
    {
        Text = "H.B Proxy";
        MinimumSize = new Size(560, 560);
        Size = new Size(620, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 18, 34);
        ForeColor = Color.FromArgb(225, 239, 255);
        Font = new Font("Segoe UI", 10F);
        BuildInterface();
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(28),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        Controls.Add(root);

        var heading = new Label
        {
            Text = "H.B PROXY",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 23F, FontStyle.Bold),
            ForeColor = Color.FromArgb(58, 190, 255),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(heading, 0, 0);

        var subtitle = new Label
        {
            Text = "SECURE SOCKS5 TUNNEL  •  WINDOWS x64",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(142, 177, 207),
            TextAlign = ContentAlignment.TopLeft
        };
        root.Controls.Add(subtitle, 0, 1);

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(17, 31, 53),
            Padding = new Padding(20)
        };
        root.Controls.Add(panel, 0, 2);

        var title = new Label
        {
            Text = "Connection status",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White
        };
        panel.Controls.Add(title);

        _status.Text = "ENGINE BUILT  •  WINDOWS TUNNEL INTEGRATION IN PROGRESS";
        _status.Dock = DockStyle.Top;
        _status.Height = 64;
        _status.ForeColor = Color.FromArgb(255, 194, 92);
        _status.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_status);

        var note = new Label
        {
            Text = "The desktop interface is being prepared. Connection controls will be enabled only after Wintun, route protection, DNS handling and recovery checks are implemented.",
            Dock = DockStyle.Top,
            Height = 90,
            ForeColor = Color.FromArgb(177, 198, 220),
            Font = new Font("Segoe UI", 9.5F),
            Padding = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(note);

        _connect.Text = "CONNECT";
        _connect.Dock = DockStyle.Fill;
        _connect.Enabled = false;
        _connect.BackColor = Color.FromArgb(30, 86, 125);
        _connect.ForeColor = Color.White;
        _connect.FlatStyle = FlatStyle.Flat;
        _connect.FlatAppearance.BorderSize = 0;
        _connect.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        root.Controls.Add(_connect, 0, 3);

        var footer = new Label
        {
            Text = "H.B Proxy for Windows  •  Independent desktop edition",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(112, 145, 176),
            Font = new Font("Segoe UI", 8.5F),
            TextAlign = ContentAlignment.MiddleCenter
        };
        root.Controls.Add(footer, 0, 4);
    }
}
