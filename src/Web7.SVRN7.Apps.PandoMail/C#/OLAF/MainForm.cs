using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using System.Net.NetworkInformation;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace Web7.SVRN7.Apps
{
	public partial class MainForm : Form
	{
		private const string BaseTitle = "Pando Mail";

		private readonly ILogger<MainForm> _log = AppLog.CreateLogger<MainForm>();

		// Message Server
		private MessageStore		_store;
		private	Bitmap				_onlineImage;
		private Bitmap				_offlineImage;
		private TdaMailClient		_tdaClient;

		public MainForm()
		{
			// Use system fonts
			this.Font = SystemFonts.IconTitleFont;

			// Designer Generated Code
			InitializeComponent();
		}

		#region Event Handlers
		private void Form1_Load(object sender, EventArgs e)
		{
			_store = MessageStore.GetMessageStore();

			this.Text = BaseTitle + $" - ws://localhost:{Program.TdaPort}/localcomm-ws" + " - Not connected";

			// Show "0 Items" immediately; RefreshInboxAsync updates it after TDA connects.
			this.itemCountLabel.Text = String.Format(this.itemCountLabel.Text, 0);

			_onlineImage = Web7.SVRN7.Apps.Properties.Resources.PandoMail;
			_offlineImage = Web7.SVRN7.Apps.Properties.Resources.Error;

			NetworkChange.NetworkAvailabilityChanged += new NetworkAvailabilityChangedEventHandler(NetworkChange_NetworkAvailabilityChanged);
			UpdateStatusBar();

			using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("Web7.SVRN7.Apps.Images.Web7Pinwheel.ico"))
			{
				this.Icon = new Icon(iconStream);
			}

			Microsoft.Win32.SystemEvents.UserPreferenceChanged += new UserPreferenceChangedEventHandler(Form1_UserPreferenceChanged);

			toolStripSplitButton3.Click += async (s, ev) => await RefreshInboxAsync();

			this.FormClosing += MainForm_FormClosing;

			// Defer TDA connection until after first paint so the window appears immediately.
			this.Shown += MainForm_Shown;
		}

		private async void MainForm_FormClosing(object sender, FormClosingEventArgs e)
		{
			if (_tdaClient is not null)
			{
				try { await _tdaClient.DisconnectAsync(); }
				catch { /* best-effort — closing anyway */ }
			}
		}

		private async void MainForm_Shown(object sender, EventArgs e)
		{
			this.Shown -= MainForm_Shown;

			_tdaClient = new TdaMailClient(Program.TdaPort);
			try
			{
				await _tdaClient.ConnectAsync();
				_tdaClient.EmailNotifyReceived  += OnEmailNotifyReceived;
				_tdaClient.FolderCountsReceived += OnFolderCountsReceived;
				_tdaClient.Disconnected += OnTdaDisconnected;
				rightSpine1.SetTdaClient(_tdaClient);
			}
			catch
			{
				// TDA not available — PandoMail starts in offline mode with empty inbox.
				return;
			}

			leftSpine1.FolderSelected += async folder => await BeginInvokeLoadFolderAsync(folder);

			await UpdateTitleAsync();

			await _tdaClient.RequestFolderCountsAsync();
			await LoadFolderAsync("Inbox");
		}

		private async Task BeginInvokeLoadFolderAsync(string folder)
		{
			if (this.IsHandleCreated)
				await Task.Run(() => this.BeginInvoke(new MethodInvoker(async () => await LoadFolderAsync(folder))));
		}

		private void Form1_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
		{
			if (this.Font != SystemFonts.IconTitleFont)
			{
				// Only respond at RT
				this.Font = SystemFonts.IconTitleFont;
				this.PerformAutoScale();
			}
		}
		#endregion

		#region New Mail
		private void OpenNewMailForm_Click(object sender, EventArgs e)
		{
			using NewMailMessageForm form = new NewMailMessageForm(_tdaClient);
			form.ShowDialog(this);
		}
		#endregion

		#region Send/Receive
		private async Task RefreshInboxAsync() => await LoadFolderAsync("Inbox");

		private async Task LoadFolderAsync(string folderName)
		{
			if (_tdaClient == null || !_tdaClient.IsConnected)
			{
				_tdaClient?.Dispose();
				_tdaClient = new TdaMailClient(Program.TdaPort);
				try
				{
					await _tdaClient.ConnectAsync();
					_tdaClient.EmailNotifyReceived  += OnEmailNotifyReceived;
					_tdaClient.FolderCountsReceived += OnFolderCountsReceived;
					_tdaClient.Disconnected += OnTdaDisconnected;
					rightSpine1.SetTdaClient(_tdaClient);
					leftSpine1.FolderSelected += async folder => await BeginInvokeLoadFolderAsync(folder);
					await UpdateTitleAsync();
				}
				catch
				{
					MessageBox.Show(
						$"Unable to connect to the TDA on port {Program.TdaPort}.",
						"Not Connected",
						MessageBoxButtons.OK,
						MessageBoxIcon.Warning);
					return;
				}
			}

			try
			{
				_store.ClearMessages();

				List<EmailSummary> summaries;
				if (folderName.Equals("Inbox", StringComparison.OrdinalIgnoreCase))
					summaries = await _tdaClient.ListEmailsAsync();
				else if (folderName.Equals("Sent Items", StringComparison.OrdinalIgnoreCase))
					summaries = await _tdaClient.ListOutboundEmailsAsync();
				else if (folderName.Equals("Dead Letters", StringComparison.OrdinalIgnoreCase))
					summaries = await _tdaClient.ListDeadLettersAsync();
				else
					return; // folder not wired — leave current view unchanged

				List<MailMessage> messages = MapToMailMessages(summaries);
				_store.ReplaceAll(messages, folderName);
				this.itemCountLabel.Text = messages.Count + " Items";
				leftSpine1.SelectFolder(folderName);
				await UpdateTitleAsync();
			}
			catch (Exception ex)
			{
				MessageBox.Show($"TDA error: {ex.GetType().Name}\n{ex.Message}", "TDA Error",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private async Task UpdateTitleAsync()
		{
			string wsSuffix = _tdaClient is not null ? $" - {_tdaClient.WsUri}" : string.Empty;
			if (_tdaClient == null || !_tdaClient.IsConnected)
			{
				this.Text = BaseTitle + wsSuffix + " - Not connected";
				return;
			}
			string did = _tdaClient.TdaDid;
			if (string.IsNullOrEmpty(did))
			{
				try { did = await _tdaClient.GetTdaDidAsync(); }
				catch { }
			}
			this.Text = BaseTitle + wsSuffix + " - " + (string.IsNullOrEmpty(did) ? "Not connected" : did);
		}

		private void OnEmailNotifyReceived(string json)
		{
			// Marshal to UI thread and refresh the inbox when a new email arrives.
			// The lambda passed to MethodInvoker is effectively async void once BeginInvoke
			// adapts it to a void delegate — an exception here has no Task to surface through
			// and no caller to observe it, so it must be caught locally rather than relying
			// solely on the global handler (Program.InstallGlobalExceptionHandlers), which
			// would otherwise pop a dialog for what should be a quiet, self-correcting retry.
			if (this.IsHandleCreated)
				BeginInvoke(new MethodInvoker(async () =>
				{
					try { await RefreshInboxAsync(); }
					catch (Exception ex) { _log.LogError(ex, "OnEmailNotifyReceived: failed to refresh inbox."); }
				}));
		}

		private void OnFolderCountsReceived(int inbox, int sent, int deadLetters)
		{
			if (this.IsHandleCreated)
				BeginInvoke(new MethodInvoker(() =>
				{
					try { _store.UpdateFolderCounts(inbox, sent, deadLetters); }
					catch (Exception ex) { _log.LogError(ex, "OnFolderCountsReceived: failed to update folder counts."); }
				}));
		}

		private void OnTdaDisconnected()
		{
			if (this.IsHandleCreated)
				BeginInvoke(new MethodInvoker(async () =>
				{
					try { await UpdateTitleAsync(); }
					catch (Exception ex) { _log.LogError(ex, "OnTdaDisconnected: failed to update title."); }
				}));
		}

		private static List<MailMessage> MapToMailMessages(List<EmailSummary> summaries)
		{
			var result = new List<MailMessage>();
			foreach (EmailSummary s in summaries)
			{
				result.Add(new MailMessage
				{
					From     = s.FromHeader ?? s.SenderDid,
					To       = s.ToHeader ?? string.Empty,
					Cc       = s.CcHeader ?? string.Empty,
					Subject  = s.Subject ?? "(no subject)",
					SentDate = s.ReceivedAt,
					Path     = s.MessageDid,
					Read     = false
				});
			}
			return result;
		}
		#endregion

		#region Online Handling
		private void UpdateStatusBar()
		{
			if (NetworkInterface.GetIsNetworkAvailable())
			{
				this.connectedStatusLabel.Text = "All Folders are up to date.";
				this.connectedImageLabel.Text = " Connected";
				this.connectedImageLabel.Image = _onlineImage;
			}
			else
			{
				this.connectedStatusLabel.Text = "This folder was last updated on " + DateTime.Now.ToShortDateString() + ".";
				this.connectedImageLabel.Text = " Disconnected";
				this.connectedImageLabel.Image = _offlineImage;
			}
		}

		void NetworkChange_NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
		{
			this.Invoke(new MethodInvoker(this.UpdateStatusBar));
		}
		#endregion
	}
}