using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Web7.SVRN7.Apps
{
    /// <summary>
    /// Startup dialog: scans for locally running TDAs (via <see cref="TdaDiscovery"/>),
    /// lets the user pick one by name/port/DID, then requires the TDA's wallet password
    /// before PandoMail will proceed to <see cref="MainForm"/>. Password verification is
    /// real (Svrn7.Signin LOBE re-checks the wallet server-side) but this dialog itself is
    /// the only enforcement point — PandoMail never receives key material either way.
    /// </summary>
    public sealed class TdaPickerForm : Form
    {
        private readonly int? _forcePort;

        private ListView    _list;
        private Button      _btnRescan;
        private Label       _lblPassword;
        private TextBox     _txtPassword;
        private Button      _btnConnect;
        private Button      _btnCancel;
        private Label       _lblStatus;

        /// <summary>Set on a successful login — the port MainForm should use.</summary>
        public int SelectedPort { get; private set; }

        private TdaPickerForm(int? forcePort)
        {
            _forcePort = forcePort;
            Text            = "Login to TDA";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterScreen;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ClientSize      = new Size(460, 360);

            BuildLayout();
            Load += async (_, __) => await RunScanAsync();
        }

        /// <summary>
        /// Shows the picker modally. Returns the chosen (and authenticated) port, or
        /// null if the user cancelled. <paramref name="forcePort"/> skips the scan and
        /// shows a single pre-identified entry for that port (the --port CLI override).
        /// </summary>
        public static int? PickTda(int? forcePort = null)
        {
            using var form = new TdaPickerForm(forcePort);
            return form.ShowDialog() == DialogResult.OK ? form.SelectedPort : (int?)null;
        }

        private void BuildLayout()
        {
            var lblHeader = new Label
            {
                Text     = "Select a TDA:",
                Location = new Point(12, 12),
                AutoSize = true
            };

            _list = new ListView
            {
                Location      = new Point(12, 34),
                Size          = new Size(436, 180),
                View          = View.Details,
                FullRowSelect = true,
                MultiSelect   = false,
                HideSelection = false
            };
            _list.Columns.Add("Name", 140);
            _list.Columns.Add("Port", 70);
            _list.Columns.Add("DID", 220);
            _list.SelectedIndexChanged += (_, __) => UpdateConnectEnabled();

            _btnRescan = new Button
            {
                Text     = "Rescan",
                Location = new Point(12, 222),
                Size     = new Size(90, 26)
            };
            _btnRescan.Click += async (_, __) => await RunScanAsync();

            _lblPassword = new Label
            {
                Text     = "Wallet password:",
                Location = new Point(12, 258),
                AutoSize = true
            };
            _txtPassword = new TextBox
            {
                Location     = new Point(12, 278),
                Size         = new Size(436, 23),
                UseSystemPasswordChar = true
            };
            _txtPassword.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _btnConnect.Enabled)
                {
                    e.SuppressKeyPress = true;
                    await ConnectClickedAsync();
                }
            };

            _lblStatus = new Label
            {
                Location  = new Point(12, 306),
                Size      = new Size(436, 20),
                ForeColor = Color.DarkRed,
                Text      = ""
            };

            _btnConnect = new Button
            {
                Text     = "Login",
                Location = new Point(292, 328),
                Size     = new Size(78, 26),
                Enabled  = false
            };
            _btnConnect.Click += async (_, __) => await ConnectClickedAsync();

            _btnCancel = new Button
            {
                Text     = "Cancel",
                Location = new Point(372, 328),
                Size     = new Size(76, 26),
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[]
            {
                lblHeader, _list, _btnRescan, _lblPassword, _txtPassword,
                _lblStatus, _btnConnect, _btnCancel
            });

            AcceptButton = _btnConnect;
            CancelButton = _btnCancel;
        }

        private void UpdateConnectEnabled() =>
            _btnConnect.Enabled = _list.SelectedItems.Count > 0;

        private async Task RunScanAsync()
        {
            _btnRescan.Enabled  = false;
            _btnConnect.Enabled = false;
            _list.Items.Clear();
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "Scanning...";

            try
            {
                if (_forcePort is int p)
                {
                    var found = await TdaDiscovery.ProbeAsync(p);
                    if (found is not null) AddRow(found);
                    else _lblStatus.Text = $"No TDA responding on port {p}.";
                }
                else
                {
                    var found = await TdaDiscovery.ScanAsync();
                    foreach (var tda in found) AddRow(tda);
                    _lblStatus.Text = found.Count == 0
                        ? "No running TDAs found."
                        : $"Found {found.Count} TDA(s).";
                }

                if (_list.Items.Count == 1) _list.Items[0].Selected = true;
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Color.DarkRed;
                _lblStatus.Text = $"Scan failed: {ex.Message}";
            }
            finally
            {
                _btnRescan.Enabled = true;
                UpdateConnectEnabled();
            }
        }

        private void AddRow(DiscoveredTda tda)
        {
            var item = new ListViewItem(string.IsNullOrEmpty(tda.Name) ? "(unnamed)" : tda.Name);
            item.SubItems.Add(tda.Port.ToString());
            item.SubItems.Add(tda.Did);
            item.Tag = tda;
            _list.Items.Add(item);
        }

        private async Task ConnectClickedAsync()
        {
            if (_list.SelectedItems.Count == 0) return;
            var tda = (DiscoveredTda)_list.SelectedItems[0].Tag;
            var password = _txtPassword.Text;

            SetBusy(true, "Logging in...");

            TdaMailClient client = null;
            try
            {
                client = new TdaMailClient(tda.Port);
                await client.ConnectAsync();

                var result = await client.AuthenticateAsync(password);
                if (!result.Authenticated)
                {
                    _lblStatus.ForeColor = Color.DarkRed;
                    _lblStatus.Text = result.Reason ?? "Authentication failed.";
                    return;
                }

                SelectedPort = tda.Port;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Color.DarkRed;
                _lblStatus.Text = $"Could not connect: {ex.Message}";
            }
            finally
            {
                // This picker's own connection was only for verification — MainForm makes
                // its own TdaMailClient against the chosen port. Never leave this one open.
                try { await (client?.DisconnectAsync() ?? Task.CompletedTask); } catch { }
                client?.Dispose();
                SetBusy(false, null);
            }
        }

        private void SetBusy(bool busy, string status)
        {
            _list.Enabled       = !busy;
            _btnRescan.Enabled  = !busy && true;
            _btnConnect.Enabled = !busy && _list.SelectedItems.Count > 0;
            _txtPassword.Enabled = !busy;
            if (status is not null)
            {
                _lblStatus.ForeColor = Color.DimGray;
                _lblStatus.Text = status;
            }
        }
    }
}
