namespace OpenDisplayReceiver;

internal interface IVideoSink : IAsyncDisposable
{
    string Name { get; }
    Task StartAsync(CancellationToken token);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token);
}
