using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLogger.Initialize(args);
        InstallExceptionHandlers();

        try
        {
            var options = ReceiverOptions.Parse(args);
            if (options.ShowHelp)
            {
                AppLogger.WriteLine("Showing help dialog");
                MessageBox.Show(ReceiverOptions.HelpText, "OpenDisplay Receiver", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            AppLogger.WriteLine("Starting WinForms application");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ReceiverForm(options));
            AppLogger.WriteLine("WinForms application exited normally");
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Fatal startup/application exception", ex);
            ShowCrashDialog(ex);
        }
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            AppLogger.WriteException("Unhandled WinForms UI thread exception", e.Exception);
            ShowCrashDialog(e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLogger.WriteException($"Unhandled AppDomain exception; terminating={e.IsTerminating}", ex);
            }
            else
            {
                AppLogger.WriteLine($"Unhandled AppDomain exception object; terminating={e.IsTerminating}; value={e.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.WriteException("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static void ShowCrashDialog(Exception exception)
    {
        try
        {
            MessageBox.Show(
                "OpenDisplay Receiver hit an error and wrote a log file.\r\n\r\n" +
                exception.Message + "\r\n\r\n" +
                "Log file:\r\n" + AppLogger.CurrentLogPath,
                "OpenDisplay Receiver",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // The process may be too broken to show UI. The file log is the source of truth.
        }
    }
}
