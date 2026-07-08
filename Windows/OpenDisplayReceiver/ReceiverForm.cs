using System.Drawing;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class ReceiverForm : Form
{
    private readonly ReceiverOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Panel _videoPanel;
    private readonly Label _statusLabel;
    private readonly TextBox _logBox;
    private readonly TableLayoutPanel _layout;
    private readonly Panel _header;
    private readonly Button _fullscreenButton;

    private Receiver? _receiver;
    private bool _fullscreen;
    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private Rectangle _savedBounds;
    private bool _savedTopMost;

    public ReceiverForm(ReceiverOptions options)
    {
        _options = options;
        Text = "OpenDisplay Receiver";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 450);
        ClientSize = new Size(1120, 720);
        KeyPreview = true;
        BackColor = Color.Black;

        _videoPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Starting…",
        };

        _fullscreenButton = new Button
        {
            Text = "Fullscreen",
            AutoSize = true,
            Dock = DockStyle.Right,
        };
        _fullscreenButton.Click += (_, _) => SetFullscreen(!_fullscreen);

        _header = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = SystemColors.Control,
        };
        _header.Controls.Add(_statusLabel);
        _header.Controls.Add(_fullscreenButton);

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, 9.0f),
        };

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Color.Black,
        };
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        _layout.Controls.Add(_header, 0, 0);
        _layout.Controls.Add(_videoPanel, 0, 1);
        _layout.Controls.Add(_logBox, 0, 2);
        Controls.Add(_layout);

        AppendStartupInstructions();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartReceiver();
        if (_options.Fullscreen)
        {
            SetFullscreen(true);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.F11 or Keys.F)
        {
            SetFullscreen(!_fullscreen);
            return true;
        }

        if (keyData == Keys.Escape && _fullscreen)
        {
            SetFullscreen(false);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void StartReceiver()
    {
        _receiver = new Receiver(_options, _videoPanel, Log, SetStatus);
        _ = Task.Run(async () =>
        {
            try
            {
                await _receiver.RunAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                SetStatus("Receiver stopped: " + ex.Message);
            }
        });
    }

    private void AppendStartupInstructions()
    {
        Log("OpenDisplay Receiver for Windows");
        Log($"Receiver name : {_options.DeviceName}");
        Log($"Resolution    : {_options.PixelsWide}x{_options.PixelsHigh} @ {_options.Scale:0.#}x");
        Log($"Port          : {_options.Port}");
        Log($"ffplay        : {_options.FfplayPath}");
        Log("Toggle fullscreen with F11 or F; exit fullscreen with Esc.");
        Log(string.Empty);
        Log("Connect from the Mac with:");

        var addresses = NetworkAddresses.GetLocalIPv4Addresses().ToArray();
        if (addresses.Length == 0)
        {
            Log("  No active IPv4 address found. Connect both machines to the same LAN/Wi-Fi first.");
        }
        else
        {
            foreach (var ip in addresses)
            {
                Log($"  defaults write com.peetzweg.opensidecar.mac host {ip}");
                Log($"  defaults write com.peetzweg.opensidecar.mac port {_options.Port}");
                Log("  open -a OpenDisplay");
                Log(string.Empty);
            }
        }

        Log("Debug Mac build bundle id: com.peetzweg.opensidecar.mac.debug");
        Log(string.Empty);
    }

    private void SetFullscreen(bool enabled)
    {
        if (_fullscreen == enabled) return;
        _fullscreen = enabled;

        if (enabled)
        {
            _savedBorderStyle = FormBorderStyle;
            _savedWindowState = WindowState;
            _savedBounds = Bounds;
            _savedTopMost = TopMost;

            _layout.RowStyles[0].Height = 0;
            _layout.RowStyles[2].Height = 0;
            _header.Visible = false;
            _logBox.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            _fullscreenButton.Text = "Windowed";
        }
        else
        {
            TopMost = _savedTopMost;
            FormBorderStyle = _savedBorderStyle == 0 ? FormBorderStyle.Sizable : _savedBorderStyle;
            WindowState = _savedWindowState == 0 ? FormWindowState.Normal : _savedWindowState;
            Bounds = _savedBounds == Rectangle.Empty ? new Rectangle(100, 100, 1120, 720) : _savedBounds;
            _layout.RowStyles[0].Height = 48;
            _layout.RowStyles[2].Height = 130;
            _header.Visible = true;
            _logBox.Visible = true;
            _fullscreenButton.Text = "Fullscreen";
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

    private void Log(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(text));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }
}
