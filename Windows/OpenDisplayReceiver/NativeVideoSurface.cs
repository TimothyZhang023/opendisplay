using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class NativeVideoSurface : Control
{
    private readonly object _gate = new();
    private byte[]? _bgra;
    private int _width;
    private int _height;
    private int _stride;

    public NativeVideoSurface()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    public void ShowFrame(byte[] bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride <= 0 || bgra.Length < stride * height) return;

        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowFrame(bgra, width, height, stride)));
            return;
        }

        lock (_gate)
        {
            _bgra = bgra;
            _width = width;
            _height = height;
            _stride = stride;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Black);

        byte[]? data;
        int width;
        int height;
        int stride;
        lock (_gate)
        {
            data = _bgra;
            width = _width;
            height = _height;
            stride = _stride;
        }

        if (data is null || width <= 0 || height <= 0 || stride <= 0)
        {
            DrawPlaceholder(e.Graphics);
            return;
        }

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            var destination = Fit(ClientRectangle, width, height);
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(bitmap, destination);
        }
        finally
        {
            handle.Free();
        }
    }

    private void DrawPlaceholder(Graphics graphics)
    {
        using var brush = new SolidBrush(Color.FromArgb(180, 220, 220, 220));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("Waiting for native H.264 frames…", Font, brush, ClientRectangle, format);
    }

    private static Rectangle Fit(Rectangle bounds, int sourceWidth, int sourceHeight)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return bounds;
        }

        var scale = Math.Min(bounds.Width / (double)sourceWidth, bounds.Height / (double)sourceHeight);
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }
}
