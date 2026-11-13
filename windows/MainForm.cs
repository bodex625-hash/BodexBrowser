using System.Drawing;
using System.Net;
using System.Windows.Forms;

namespace HBProxy.Windows;

public sealed class MainForm : Form
{
    private readonly TextBox _server = new();
    private readonly TextBox _port = new();
    private readonly TextBox _username = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _udp = new();
    private readonly Label _status = new();
    private readonly Button _connect = new();
    private readonly CredentialStore _credentials = new();
    private readonly ProfileSettingsStore _settings = new();
    private readonly HevProcessHost _host = new();
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 1000 };
    private bool _connectionEstablished;
    private bool _allowClose;

    public MainForm()
    {
        Text = "H.B Proxy";
        MinimumSize = new Size(560, 650);
        Size = new Size(620, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 18, 34);
        ForeColor = Color.FromArgb(225, 239, 255);
        Font = new Font("Segoe UI", 10F);
        BuildInterface();
        LoadSavedProfile();

        _connect.Click += async (_, _) => await ToggleConnectionAsync();
        _healthTimer.Tick += (_, _) => CheckTunnelHealth();
        _healthTimer.Start();

        FormClosing += async (_, e) =>
        {
            if (_allowClose)
                return;

            e.Cancel = true;
            _connect.Enabled = false;
            _healthTimer.Stop();
            try
            {
                await _host.DisposeAsync();
                _allowClose = true;
                BeginInvoke(Close);
            }
            catch (Exception ex)
            {
                _status.Text = "NETWORK CLEANUP NEEDS ATTENTION";
                _status.ForeColor = Color.FromArgb(255, 110, 110);
                MessageBox.Show(this,
                    "The tunnel could not be safely stopped. Keep this window open and retry disconnect. " +
                    "If the application is forcibly closed, startup recovery will remove the previous session.\n\n" +
                    ex.Message,
                    "H.B Proxy cleanup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _connect.Enabled = true;
                _healthTimer.Start();
            }
        };
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "H.B PROXY",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 23F, FontStyle.Bold),
            ForeColor = Color.FromArgb(58, 190, 255),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = "SECURE SOCKS5 TUNNEL  •  WINDOWS x64",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(142, 177, 207),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(17, 31, 53),
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 7
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        for (var i = 0; i < 4; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(panel, 0, 2);

        var section = new Label
        {
            Text = "SOCKS5 SERVER SETTINGS",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(section, 0, 0);
        panel.SetColumnSpan(section, 2);

        AddField(panel, "Server IP", _server, 1, "e.g. 203.0.113.10");
        AddField(panel, "Port", _port, 2, "1080");
        AddField(panel, "Username", _username, 3, "Optional");
        AddField(panel, "Password", _password, 4, "Optional");
        _password.UseSystemPasswordChar = true;

        _udp.Text = "Enable UDP support";
        _udp.Checked = true;
        _udp.Dock = DockStyle.Fill;
        _udp.ForeColor = Color.FromArgb(210, 228, 245);
        panel.Controls.Add(_udp, 1, 5);

        var save = new Button
        {
            Text = "SAVE SETTINGS",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 112, 158),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => SaveProfile();
        panel.Controls.Add(save, 0, 6);
        panel.SetColumnSpan(save, 2);

        var statusPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(17, 31, 53),
            Padding = new Padding(16, 8, 16, 8)
        };
        _status.Text = "DISCONNECTED  •  READY";
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Color.FromArgb(255, 194, 92);
        _status.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusPanel.Controls.Add(_status);
        root.Controls.Add(statusPanel, 0, 3);

        _connect.Text = "CONNECT";
        _connect.Dock = DockStyle.Fill;
        _connect.Enabled = true;
        _connect.BackColor = Color.FromArgb(30, 112, 165);
        _connect.ForeColor = Color.White;
        _connect.FlatStyle = FlatStyle.Flat;
        _connect.FlatAppearance.BorderSize = 0;
        _connect.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        root.Controls.Add(_connect, 0, 4);
    }

    private static void AddField(TableLayoutPanel panel, string label, TextBox input, int row, string placeholder)
    {
        panel.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(177, 198, 220),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(4, 10, 4, 10);
        input.BackColor = Color.FromArgb(8, 19, 35);
        input.ForeColor = Color.White;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.PlaceholderText = placeholder;
        panel.Controls.Add(input, 1, row);
    }

    private void LoadSavedProfile()
    {
        var saved = _settings.Load();
        _server.Text = saved.ServerAddress;
        _port.Text = saved.Port.ToString();
        _udp.Checked = saved.EnableUdp;

        try
        {
            var credentials = _credentials.Load();
            if (credentials is not null)
            {
                _username.Text = credentials.Value.Username;
                _password.Text = credentials.Value.Password;
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _status.Text = "SAVED CREDENTIALS COULD NOT BE DECRYPTED";
            _status.ForeColor = Color.FromArgb(255, 110, 110);
        }
    }

    private bool TryBuildProfile(out Socks5Profile? profile)
    {
        profile = null;
        if (!IPAddress.TryParse(_server.Text.Trim(), out _))
        {
            MessageBox.Show(this, "Enter the SOCKS5 server as a valid IP address.", "Invalid server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _server.Focus();
            return false;
        }

        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be a number between 1 and 65535.", "Invalid port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _port.Focus();
            return false;
        }

        var noCredentials = string.IsNullOrEmpty(_username.Text) && string.IsNullOrEmpty(_password.Text);
        profile = new Socks5Profile(_server.Text.Trim(), port,
            noCredentials ? null : _username.Text,
            noCredentials ? null : _password.Text,
            _udp.Checked);

        try
        {
            profile.Validate();
            return true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid SOCKS5 profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void SaveProfile()
    {
        if (!TryBuildProfile(out var profile) || profile is null)
            return;

        try
        {
            PersistProfile(profile);
            _status.Text = "SETTINGS SAVED  •  DISCONNECTED";
            _status.ForeColor = Color.FromArgb(80, 210, 160);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PersistProfile(Socks5Profile profile)
    {
        _settings.Save(profile.ServerAddress, profile.Port, profile.EnableUdp);
        _credentials.Save(profile.Username, profile.Password);
    }

    private async Task ToggleConnectionAsync()
    {
        _connect.Enabled = false;
        try
        {
            if (_connectionEstablished)
            {
                _status.Text = "DISCONNECTING  •  RESTORING NETWORK";
                _status.ForeColor = Color.FromArgb(255, 194, 92);
                await _host.StopAsync();
                _connectionEstablished = false;
                _connect.Text = "CONNECT";
                _status.Text = "DISCONNECTED  •  ORIGINAL ROUTES RESTORED";
                _status.ForeColor = Color.FromArgb(142, 177, 207);
                return;
            }

            if (!TryBuildProfile(out var profile) || profile is null)
                return;

            PersistProfile(profile);
            _status.Text = "CONNECTING  •  APPLYING NETWORK PROTECTION";
            _status.ForeColor = Color.FromArgb(255, 194, 92);
            await _host.StartAsync(profile);

            _connectionEstablished = true;
            _connect.Text = "DISCONNECT";
            _status.Text = "CONNECTED  •  HTTPS VERIFIED  •  " + _host.VerifiedEgressAddress;
            _status.ForeColor = Color.FromArgb(80, 210, 160);
        }
        catch (Exception ex)
        {
            var cleanupSucceeded = false;
            try
            {
                await _host.StopAsync();
                cleanupSucceeded = true;
            }
            catch (Exception cleanupError)
            {
                ex = new AggregateException(ex, cleanupError);
            }

            _connectionEstablished = false;
            _connect.Text = "CONNECT";
            _status.Text = cleanupSucceeded
                ? "CONNECTION FAILED  •  NETWORK RESTORED"
                : "CLEANUP FAILED  •  NETWORK PROTECTION RETAINED";
            _status.ForeColor = Color.FromArgb(255, 110, 110);
            MessageBox.Show(this, ex.Message, "H.B Proxy connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    private void CheckTunnelHealth()
    {
        if (!_connectionEstablished || _host.IsRunning)
            return;

        _connectionEstablished = false;
        _connect.Text = "CONNECT";
        _status.Text = "TUNNEL STOPPED  •  CHECK NETWORK CLEANUP";
        _status.ForeColor = Color.FromArgb(255, 110, 110);
    }
}
