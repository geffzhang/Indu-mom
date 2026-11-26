namespace HoneyMOM.Infrastructure.Labeling;

public interface IPrintDeviceTransport
{
    Task SendPrinterAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> payload,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<string> SendMarkerAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
