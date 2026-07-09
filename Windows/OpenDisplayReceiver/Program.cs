using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.Initialize(args);
        RegisterGlobalExceptionHandlers();

        try
        {
            AppLog.Info("Application startup entered");
            var options = ReceiverOptions.Parse(args);
            if (options.ShowHelp)
            {
                MessageBox.Show(ReceiverOptions.HelpText, "OpenDisplay Receiver", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            AppLog.Info($"Parsed options: width={options.PixelsWide}, height={options.PixelsHigh}, scale={options.Scale:0.###}, port={options.Port}, renderer={options.Renderer}, mdns={options.EnableMdns}, fullscreen={options.Fullscreen}");

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ReceiverForm(options));
            AppLog.Info("Application.Run returned");
        }
        catch (Exception ex)
        {
            AppLog.Exception("Fatal startup/application exception", ex);
            ShowFatalError(ex);
        }
        finally
        {
            AppLog.Info("Application exiting");
        }
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            AppLog.Exception("Unhandled WinForms UI thread exception", e.Exception);
            ShowFatalError(e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.Exception("Unhandled AppDomain exception", ex);
            }
            else
            {
                AppLog.Error("Unhandled AppDomain exception object: " + e.ExceptionObject);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Exception("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static void ShowFatalError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "OpenDisplay Receiver hit an error.\r\n\r\n" +
                ex.Message + "\r\n\r\n" +
                "Log file:\r\n" + AppLog.CurrentFilePath,
                "OpenDisplay Receiver",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // If even the error dialog fails, the file log still contains the exception.
        }
    }
}
