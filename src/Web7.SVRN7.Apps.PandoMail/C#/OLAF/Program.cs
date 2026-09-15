using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace Web7.SVRN7.Apps
{
	static class Program
	{
		internal static int TdaPort = 8443;

		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main(string[] args)
		{
			for (int i = 0; i < args.Length - 1; i++)
				if (args[i] == "--port" && int.TryParse(args[i + 1], out int p))
					TdaPort = p;

			InstallGlobalExceptionHandlers();

			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.SetHighDpiMode(HighDpiMode.SystemAware);
			Application.Run(new MainForm());
		}

		/// <summary>
		/// Without these, any unhandled exception on the UI thread — including the
		/// BeginInvoke-marshaled push-notification callbacks MainForm subscribes for new
		/// mail, folder counts, and disconnects — crashes the whole application with no
		/// diagnostic trail. CatchException + ThreadException turns that into a logged,
		/// dismissible error dialog instead, so one bad notification degrades gracefully
		/// rather than taking the mail client down entirely.
		/// </summary>
		private static void InstallGlobalExceptionHandlers()
		{
			var log = AppLog.CreateLogger<AppLifecycle>();

			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			Application.ThreadException += (sender, e) =>
			{
				log.LogError(e.Exception, "Unhandled UI-thread exception.");
				MessageBox.Show(
					$"An unexpected error occurred:\n{e.Exception.Message}\n\nPandoMail will attempt to continue running.",
					"PandoMail Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			};

			// Exceptions on non-UI threads (background Tasks whose faults aren't observed,
			// finalizers, etc.) cannot be recovered from — IsTerminating is usually true —
			// but logging here at least leaves a diagnostic trail instead of a silent exit.
			AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
			{
				log.LogCritical(e.ExceptionObject as Exception,
					"Unhandled non-UI-thread exception (IsTerminating={IsTerminating}).", e.IsTerminating);
			};
		}
	}
}
