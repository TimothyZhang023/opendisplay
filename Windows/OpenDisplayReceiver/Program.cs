using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = ReceiverOptions.Parse(args);
        if (options.ShowHelp)
        {
            MessageBox.Show(ReceiverOptions.HelpText, "OpenDisplay Receiver", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ReceiverForm(options));
    }
}
