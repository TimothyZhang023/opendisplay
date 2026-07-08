namespace OpenDisplayReceiver;

internal interface IVideoSink : IAsyncDisposable
{
    string Name { get; }
    Task StartAsync(CancellationToken token);
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token);
}
