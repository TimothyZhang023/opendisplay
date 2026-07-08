using System.Net;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class ReceiverForm : Form
{
    private readonly ReceiverOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _logBox = new();
    private readonly Label _instructionsLabel = new();
    private readonly Button _copyMacCommandsButton = new();
    private readonly Button _toggleFullscreenButton = new();
    private FormBorderStyle _previousBorderStyle;
    private FormWindowState _previousWindowState;
    private Rectangle _previousBounds;
    private bool _controlWindowFullscreen;

    public ReceiverForm(ReceiverOptions options)
    {
        _options = options;
        Text = "OpenDisplay Receiver";
        MinimumSize = new Size(720, 480);
        Size = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildLayout();

        Load += (_, _) => StartReceiver();
        FormClosing += (_, _) => _cts.Cancel();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11 || (e.Alt && e.KeyCode == Keys.Enter))
            {
                ToggleControlWindowFullscreen();
                e.Handled = true;
            }
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "OpenDisplay Receiver for Windows",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        _statusLabel.Text = "Starting…";
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        _statusLabel.Margin = new Padding(0, 0, 0, 12);
        root.Controls.Add(_statusLabel, 0, 1);

        var commands = BuildMacCommands();
        _instructionsLabel.Text =
            $"Receiver: {_options.DeviceName}\r\n" +
            $"Resolution announced to Mac: {_options.PixelsWide}x{_options.PixelsHigh} @ {_options.Scale:0.#}x\r\n" +
            $"Listening port: {_options.Port}\r\n" +
            $"Video window: {(_options.Fullscreen ? "fullscreen by default" : "windowed by default")} — press f inside the video window to toggle fullscreen.\r\n\r\n" +
            "Run this on the Mac sender:\r\n" + commands;
        _instructionsLabel.AutoSize = true;
        _instructionsLabel.MaximumSize = new Size(790, 0);
        _instructionsLabel.Margin = new Padding(0, 0, 0, 12);
        root.Controls.Add(_instructionsLabel, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        _copyMacCommandsButton.Text = "Copy Mac commands";
        _copyMacCommandsButton.AutoSize = true;
        _copyMacCommandsButton.Click += (_, _) => Clipboard.SetText(commands);
        buttons.Controls.Add(_copyMacCommandsButton);

        _toggleFullscreenButton.Text = "Toggle control window fullscreen (F11)";
        _toggleFullscreenButton.AutoSize = true;
        _toggleFullscreenButton.Click += (_, _) => ToggleControlWindowFullscreen();
        buttons.Controls.Add(_toggleFullscreenButton);
        root.Controls.Add(buttons, 0, 3);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9);
        _logBox.Margin = new Padding(0, 8, 0, 0);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowCount = 5;
        root.Controls.Add(_logBox, 0, 4);
    }

    private void StartReceiver()
    {
        AppendLog("OpenDisplay Receiver starting");
        AppendLog($"ffplay: {_options.FfplayPath}");
        AppendLog("Plain USB-C Mac-to-PC is not a supported transport because both sides are USB hosts. Use TCP over LAN or another real network interface.");

        var receiver = new Receiver(_options, log: AppendLog, status: SetStatus);
        _ = Task.Run(async () =>
        {
            try
            {
                await receiver.RunAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                AppendLog(ex.ToString());
                SetStatus("Receiver failed: " + ex.Message);
            }
        });
    }

    private string BuildMacCommands()
    {
        var firstIp = NetworkAddresses.GetLocalIPv4Addresses().FirstOrDefault()?.ToString() ?? "WINDOWS_IP";
        return string.Join("\r\n",
            $"defaults write com.peetzweg.opensidecar.mac host \"{firstIp}\"",
            $"defaults write com.peetzweg.opensidecar.mac port \"{_options.Port}\"",
            "defaults write com.peetzweg.opensidecar.mac localCursor -bool false",
            "open -a OpenDisplay");
    }

    private void ToggleControlWindowFullscreen()
    {
        if (!_controlWindowFullscreen)
        {
            _previousBorderStyle = FormBorderStyle;
            _previousWindowState = WindowState;
            _previousBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;
            _controlWindowFullscreen = true;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = _previousBorderStyle;
            WindowState = _previousWindowState;
            Bounds = _previousBounds;
            _controlWindowFullscreen = false;
        }
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text));
            return;
        }
        _statusLabel.Text = text;
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
