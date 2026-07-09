using System.Drawing;
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
    private readonly Button _copyLogPathButton = new();
    private readonly Button _openLogFolderButton = new();
    private readonly Button _toggleFullscreenButton = new();
    private readonly NativeVideoSurface _videoSurface = new();
    private FormBorderStyle _previousBorderStyle = FormBorderStyle.Sizable;
    private FormWindowState _previousWindowState = FormWindowState.Normal;
    private Rectangle _previousBounds = Rectangle.Empty;
    private bool _controlWindowFullscreen;

    public ReceiverForm(ReceiverOptions options)
    {
        _options = options;
        Text = "OpenDisplay Receiver";
        MinimumSize = new Size(900, 600);
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildLayout();

        Load += (_, _) =>
        {
            StartReceiver();
            if (_options.Fullscreen)
            {
                ToggleControlWindowFullscreen();
            }
        };
        FormClosing += (_, _) =>
        {
            AppLog.Info("Receiver window closing");
            _cts.Cancel();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11 || e.KeyCode == Keys.F || (e.Alt && e.KeyCode == Keys.Enter))
            {
                ToggleControlWindowFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && _controlWindowFullscreen)
            {
                ToggleControlWindowFullscreen();
                e.Handled = true;
            }
        };
        _videoSurface.DoubleClick += (_, _) => ToggleControlWindowFullscreen();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
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
            $"Renderer: {_options.Renderer}\r\n" +
            $"Bonjour/mDNS: {(_options.EnableMdns ? "advertising _opensidecar._tcp" : "disabled")}\r\n" +
            $"Log file: {AppLog.CurrentFilePath}\r\n" +
            $"Latest log: {AppLog.LatestFilePath}\r\n\r\n" +
            "Run this on the Mac sender if Bonjour discovery does not show it yet:\r\n" + commands;
        _instructionsLabel.AutoSize = true;
        _instructionsLabel.MaximumSize = new Size(1050, 0);
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

        _copyLogPathButton.Text = "Copy log path";
        _copyLogPathButton.AutoSize = true;
        _copyLogPathButton.Click += (_, _) => Clipboard.SetText(AppLog.CurrentFilePath);
        buttons.Controls.Add(_copyLogPathButton);

        _openLogFolderButton.Text = "Open logs folder";
        _openLogFolderButton.AutoSize = true;
        _openLogFolderButton.Click += (_, _) => AppLog.OpenLogFolder();
        buttons.Controls.Add(_openLogFolderButton);

        _toggleFullscreenButton.Text = "Toggle fullscreen (F11 / F / double-click video)";
        _toggleFullscreenButton.AutoSize = true;
        _toggleFullscreenButton.Click += (_, _) => ToggleControlWindowFullscreen();
        buttons.Controls.Add(_toggleFullscreenButton);
        root.Controls.Add(buttons, 0, 3);

        _videoSurface.Margin = new Padding(0, 0, 0, 8);
        root.Controls.Add(_videoSurface, 0, 4);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9);
        _logBox.Margin = new Padding(0, 8, 0, 0);
        root.Controls.Add(_logBox, 0, 5);
    }

    private void StartReceiver()
    {
        AppendLog("OpenDisplay Receiver starting");
        AppendLog($"log file: {AppLog.CurrentFilePath}");
        AppendLog($"latest log: {AppLog.LatestFilePath}");
        AppendLog($"renderer: {_options.Renderer}");
        AppendLog($"ffplay fallback: {_options.FfplayPath}");
        AppendLog($"mDNS/Bonjour: {(_options.EnableMdns ? "enabled" : "disabled")}");
        AppendLog("Plain USB-C Mac-to-PC is not a supported transport because both sides are USB hosts. Use TCP over LAN or another real network interface.");

        var receiver = new Receiver(_options, _videoSurface, AppendLog, SetStatus);
        _ = Task.Run(async () =>
        {
            try
            {
                await receiver.RunAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                AppLog.Info("Receiver task cancelled");
            }
            catch (Exception ex)
            {
                AppLog.Exception("Receiver task failed", ex);
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
            AppLog.Info("Entered fullscreen");
        }
        else
        {
            TopMost = false;
            FormBorderStyle = _previousBorderStyle;
            WindowState = FormWindowState.Normal;
            if (_previousBounds != Rectangle.Empty)
            {
                Bounds = _previousBounds;
            }
            WindowState = _previousWindowState;
            _controlWindowFullscreen = false;
            AppLog.Info("Exited fullscreen");
        }
    }

    private void SetStatus(string text)
    {
        AppLog.Info("Status: " + text);
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text)));
            return;
        }
        _statusLabel.Text = text;
    }

    private void AppendLog(string message)
    {
        AppLog.Info(message);
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppLog.Info("Disposing receiver form");
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
