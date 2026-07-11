using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class NativeH264Sink : IVideoSink
{
    private readonly ReceiverOptions _options;
    private readonly NativeVideoSurface? _surface;
    private readonly Action<string> _log;
    private readonly FfplaySink _fallback;
    private MediaFoundationH264Decoder? _decoder;
    private bool _usingFallback;
    private bool _reportedDecodeFailure;

    public NativeH264Sink(ReceiverOptions options, Control? videoHost = null, Action<string>? log = null)
    {
        _options = options;
        _surface = videoHost as NativeVideoSurface;
        _log = log ?? Console.WriteLine;
        _fallback = new FfplaySink(options, videoHost, _log);
    }

    public string Name => _usingFallback ? _fallback.Name : "Windows Media Foundation H.264";

    public async Task StartAsync(CancellationToken token)
    {
        if (_surface is null)
        {
            _log("Native renderer needs a NativeVideoSurface; falling back to ffplay.");
            await StartFallbackAsync(token).ConfigureAwait(false);
            return;
        }

        try
        {
            _decoder = new MediaFoundationH264Decoder(_options.PixelsWide, _options.PixelsHigh, RenderFrame);
            _log("Started native Windows H.264 renderer (Media Foundation).");
        }
        catch (Exception ex)
        {
            _log("Native Windows H.264 renderer unavailable; falling back to ffplay: " + ex);
            await StartFallbackAsync(token).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (_usingFallback)
        {
            await _fallback.WriteAsync(data, token).ConfigureAwait(false);
            return;
        }

        if (_decoder is null)
        {
            await StartFallbackAsync(token).ConfigureAwait(false);
            await _fallback.WriteAsync(data, token).ConfigureAwait(false);
            return;
        }

        try
        {
            _decoder.Process(data.Span);
        }
        catch (Exception ex)
        {
            if (!_reportedDecodeFailure)
            {
                _reportedDecodeFailure = true;
                _log("Native H.264 decode failed; switching to ffplay fallback: " + ex);
            }
            await StartFallbackAsync(token).ConfigureAwait(false);
            await _fallback.WriteAsync(data, token).ConfigureAwait(false);
        }
    }

    private void RenderFrame(byte[] bgra, int width, int height, int stride)
    {
        _surface?.ShowFrame(bgra, width, height, stride);
    }

    private async Task StartFallbackAsync(CancellationToken token)
    {
        if (_usingFallback) return;
        _usingFallback = true;
        _decoder?.Dispose();
        _decoder = null;
        await _fallback.StartAsync(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _decoder?.Dispose();
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class MediaFoundationH264Decoder : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;
        private readonly int _sampleDuration;
        private readonly Action<byte[], int, int, int> _onFrame;
        private IMFTransform? _decoder;
        private bool _mfStarted;
        private bool _comInitialized;
        private long _sampleTime;
        private int _outputBufferSize;
        private bool _disposed;

        public MediaFoundationH264Decoder(int width, int height, Action<byte[], int, int, int> onFrame)
        {
            _width = Math.Max(16, width);
            _height = Math.Max(16, height);
            _stride = _width * 4;
            _sampleDuration = 333_333; // 30 fps in Media Foundation 100 ns units.
            _onFrame = onFrame;

            StartMediaFoundation();
            CreateDecoder();
            ConfigureTypes();
        }

        public void Process(ReadOnlySpan<byte> annexBAccessUnit)
        {
            if (_disposed || annexBAccessUnit.Length == 0 || _decoder is null) return;

            using var sample = ComReleaser.Track(CreateInputSample(annexBAccessUnit));
            var hr = _decoder.ProcessInput(0, sample.Value, 0);
            if (hr == NativeMethods.MF_E_NOTACCEPTING)
            {
                DrainOutput();
                hr = _decoder.ProcessInput(0, sample.Value, 0);
            }
            NativeMethods.ThrowIfFailed(hr, "ProcessInput");
            DrainOutput();
        }

        private void StartMediaFoundation()
        {
            var hr = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
            if (hr == 0 || hr == 1)
            {
                _comInitialized = true;
            }
            else if (hr != NativeMethods.RPC_E_CHANGED_MODE)
            {
                NativeMethods.ThrowIfFailed(hr, "CoInitializeEx");
            }

            NativeMethods.ThrowIfFailed(NativeMethods.MFStartup(NativeMethods.MF_VERSION, 0), "MFStartup");
            _mfStarted = true;
        }

        private void CreateDecoder()
        {
            var clsid = NativeMethods.CLSID_CMSH264DecoderMFT;
            var iid = NativeMethods.IID_IMFTransform;
            NativeMethods.ThrowIfFailed(NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, ref iid, out var ptr), "CoCreateInstance(CMSH264DecoderMFT)");
            try
            {
                _decoder = (IMFTransform)Marshal.GetObjectForIUnknown(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }

        private void ConfigureTypes()
        {
            if (_decoder is null) throw new InvalidOperationException("H.264 decoder was not created");

            using var input = ComReleaser.Track(NativeMethods.CreateMediaType());
            NativeMethods.SetGuid(input.Value, NativeMethods.MF_MT_MAJOR_TYPE, NativeMethods.MFMediaType_Video);
            NativeMethods.SetGuid(input.Value, NativeMethods.MF_MT_SUBTYPE, NativeMethods.MFVideoFormat_H264);
            NativeMethods.SetSize(input.Value, NativeMethods.MF_MT_FRAME_SIZE, _width, _height);
            NativeMethods.ThrowIfFailed(_decoder.SetInputType(0, input.Value, 0), "SetInputType(H264)");

            SetRgb32OutputType();
            NativeMethods.ThrowIfFailed(_decoder.ProcessMessage(NativeMethods.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, UIntPtr.Zero), "MFT begin streaming");
            NativeMethods.ThrowIfFailed(_decoder.ProcessMessage(NativeMethods.MFT_MESSAGE_NOTIFY_START_OF_STREAM, UIntPtr.Zero), "MFT start of stream");
        }

        private void SetRgb32OutputType()
        {
            if (_decoder is null) throw new InvalidOperationException("H.264 decoder was not created");

            using var output = ComReleaser.Track(NativeMethods.CreateMediaType());
            NativeMethods.SetGuid(output.Value, NativeMethods.MF_MT_MAJOR_TYPE, NativeMethods.MFMediaType_Video);
            NativeMethods.SetGuid(output.Value, NativeMethods.MF_MT_SUBTYPE, NativeMethods.MFVideoFormat_RGB32);
            NativeMethods.SetSize(output.Value, NativeMethods.MF_MT_FRAME_SIZE, _width, _height);
            NativeMethods.SetUInt32(output.Value, NativeMethods.MF_MT_INTERLACE_MODE, NativeMethods.MFVideoInterlace_Progressive);

            NativeMethods.ThrowIfFailed(_decoder.SetOutputType(0, output.Value, 0), "SetOutputType(RGB32)");
            NativeMethods.ThrowIfFailed(_decoder.GetOutputStreamInfo(0, out var info), "GetOutputStreamInfo");
            _outputBufferSize = Math.Max(info.cbSize, _stride * _height);
        }

        private IMFSample CreateInputSample(ReadOnlySpan<byte> data)
        {
            var buffer = NativeMethods.CreateMemoryBuffer(data.Length);
            try
            {
                NativeMethods.ThrowIfFailed(buffer.Lock(out var ptr, out _, out _), "input buffer lock");
                try
                {
                    var managed = data.ToArray();
                    Marshal.Copy(managed, 0, ptr, managed.Length);
                }
                finally
                {
                    NativeMethods.ThrowIfFailed(buffer.Unlock(), "input buffer unlock");
                }

                NativeMethods.ThrowIfFailed(buffer.SetCurrentLength(data.Length), "input SetCurrentLength");
                var sample = NativeMethods.CreateSample();
                NativeMethods.ThrowIfFailed(sample.AddBuffer(buffer), "input sample AddBuffer");
                NativeMethods.ThrowIfFailed(sample.SetSampleTime(_sampleTime), "input SetSampleTime");
                NativeMethods.ThrowIfFailed(sample.SetSampleDuration(_sampleDuration), "input SetSampleDuration");
                _sampleTime += _sampleDuration;
                return sample;
            }
            finally
            {
                Marshal.FinalReleaseComObject(buffer);
            }
        }

        private IMFSample CreateOutputSample()
        {
            var sample = NativeMethods.CreateSample();
            var buffer = NativeMethods.CreateMemoryBuffer(_outputBufferSize > 0 ? _outputBufferSize : _stride * _height);
            try
            {
                NativeMethods.ThrowIfFailed(sample.AddBuffer(buffer), "output sample AddBuffer");
                return sample;
            }
            finally
            {
                Marshal.FinalReleaseComObject(buffer);
            }
        }

        private void DrainOutput()
        {
            if (_decoder is null) return;

            while (true)
            {
                using var sample = ComReleaser.Track(CreateOutputSample());
                var output = new[]
                {
                    new NativeMethods.MFT_OUTPUT_DATA_BUFFER
                    {
                        dwStreamID = 0,
                        pSample = sample.Value,
                    }
                };

                var hr = _decoder.ProcessOutput(0, output.Length, output, out _);
                if (output[0].pEvents != IntPtr.Zero)
                {
                    Marshal.Release(output[0].pEvents);
                    output[0].pEvents = IntPtr.Zero;
                }

                if (hr == NativeMethods.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    return;
                }

                if (hr == NativeMethods.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    SetRgb32OutputType();
                    continue;
                }

                NativeMethods.ThrowIfFailed(hr, "ProcessOutput");
                if (output[0].pSample is not null)
                {
                    RenderSample(output[0].pSample);
                }
            }
        }

        private void RenderSample(IMFSample sample)
        {
            NativeMethods.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer), "ConvertToContiguousBuffer");
            try
            {
                NativeMethods.ThrowIfFailed(buffer.Lock(out var ptr, out _, out var currentLength), "output buffer lock");
                try
                {
                    var expected = _stride * _height;
                    if (currentLength < expected) return;
                    var managed = new byte[expected];
                    Marshal.Copy(ptr, managed, 0, expected);
                    _onFrame(managed, _width, _height, _stride);
                }
                finally
                {
                    NativeMethods.ThrowIfFailed(buffer.Unlock(), "output buffer unlock");
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(buffer);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_decoder is not null)
            {
                try { _decoder.ProcessMessage(NativeMethods.MFT_MESSAGE_NOTIFY_END_OF_STREAM, UIntPtr.Zero); } catch { }
                try { _decoder.ProcessMessage(NativeMethods.MFT_MESSAGE_COMMAND_DRAIN, UIntPtr.Zero); } catch { }
                Marshal.FinalReleaseComObject(_decoder);
                _decoder = null;
            }

            if (_mfStarted)
            {
                NativeMethods.MFShutdown();
                _mfStarted = false;
            }

            if (_comInitialized)
            {
                NativeMethods.CoUninitialize();
                _comInitialized = false;
            }
        }
    }

    private sealed class ComReleaser<T> : IDisposable where T : class
    {
        public T Value { get; }

        private ComReleaser(T value) => Value = value;

        public static ComReleaser<T> Track(T value) => new(value);

        public void Dispose()
        {
            if (Marshal.IsComObject(Value))
            {
                Marshal.FinalReleaseComObject(Value);
            }
        }
    }

    private static class ComReleaser
    {
        public static ComReleaser<T> Track<T>(T value) where T : class => ComReleaser<T>.Track(value);
    }

    private static class NativeMethods
    {
        public const int MF_VERSION = 0x00020070;
        public const int COINIT_MULTITHREADED = 0x0;
        public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        public const uint CLSCTX_INPROC_SERVER = 0x1;
        public const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
        public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
        public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
        public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000004;
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B0);
        public const int MFVideoInterlace_Progressive = 2;

        public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        public static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
        public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
        public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
        public static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
        public static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
        public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateSample(out IMFSample ppIMFSample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern void CoUninitialize();

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        public static IMFMediaType CreateMediaType()
        {
            ThrowIfFailed(MFCreateMediaType(out var mediaType), "MFCreateMediaType");
            return mediaType;
        }

        public static IMFSample CreateSample()
        {
            ThrowIfFailed(MFCreateSample(out var sample), "MFCreateSample");
            return sample;
        }

        public static IMFMediaBuffer CreateMemoryBuffer(int size)
        {
            ThrowIfFailed(MFCreateMemoryBuffer(size, out var buffer), "MFCreateMemoryBuffer");
            return buffer;
        }

        public static void SetGuid(IMFAttributes attributes, Guid key, Guid value)
        {
            ThrowIfFailed(attributes.SetGUID(ref key, ref value), "SetGUID");
        }

        public static void SetUInt32(IMFAttributes attributes, Guid key, int value)
        {
            ThrowIfFailed(attributes.SetUINT32(ref key, value), "SetUINT32");
        }

        public static void SetSize(IMFAttributes attributes, Guid key, int width, int height)
        {
            var packed = ((long)width << 32) | (uint)height;
            ThrowIfFailed(attributes.SetUINT64(ref key, packed), "SetSize");
        }

        public static void ThrowIfFailed(int hr, string operation)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MFT_OUTPUT_STREAM_INFO
        {
            public int dwFlags;
            public int cbSize;
            public int cbAlignment;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MFT_OUTPUT_DATA_BUFFER
        {
            public int dwStreamID;
            [MarshalAs(UnmanagedType.Interface)] public IMFSample? pSample;
            public int dwStatus;
            public IntPtr pEvents;
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out long punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        [PreserveSig] int InitFromBlob(IntPtr pBuf, int cbBufSize);
        [PreserveSig] int GetCount(out int pcItems);
        [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, long unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetAllItems(IntPtr pProps);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
    private interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat(out bool pfCompressed);
        [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out int pdwFlags);
        [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(int cbCurrentLength);
        [PreserveSig] int GetMaxLength(out int pcbMaxLength);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
    private interface IMFSample : IMFAttributes
    {
        [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(int dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out int pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(int dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out int pdwInputMinimum, out int pdwInputMaximum, out int pdwOutputMinimum, out int pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out int pcInputStreams, out int pcOutputStreams);
        [PreserveSig] int GetStreamIDs(int dwInputIDArraySize, IntPtr pdwInputIDs, int dwOutputIDArraySize, IntPtr pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(int dwInputStreamID, IntPtr pStreamInfo);
        [PreserveSig] int GetOutputStreamInfo(int dwOutputStreamID, out NativeMethods.MFT_OUTPUT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
        [PreserveSig] int GetInputStreamAttributes(int dwInputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(int dwOutputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int DeleteInputStream(int dwStreamID);
        [PreserveSig] int AddInputStreams(int cStreams, IntPtr adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(int dwOutputStreamID, int dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(int dwInputStreamID, IMFMediaType pType, int dwFlags);
        [PreserveSig] int SetOutputType(int dwOutputStreamID, IMFMediaType pType, int dwFlags);
        [PreserveSig] int GetInputCurrentType(int dwInputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(int dwOutputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(int dwInputStreamID, out int pdwFlags);
        [PreserveSig] int GetOutputStatus(out int pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(int dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(int eMessage, UIntPtr ulParam);
        [PreserveSig] int ProcessInput(int dwInputStreamID, IMFSample pSample, int dwFlags);
        [PreserveSig] int ProcessOutput(int dwFlags, int cOutputBufferCount, [In, Out] NativeMethods.MFT_OUTPUT_DATA_BUFFER[] pOutputSamples, out int pdwStatus);
    }
}
