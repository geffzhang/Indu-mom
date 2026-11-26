using System.Net.Sockets;
using System.Text;

namespace HoneyMOM.Infrastructure.Labeling;

public sealed class TcpPrintDeviceTransport : IPrintDeviceTransport
{
    private const int MaxMarkerResponseBytes = 4096;

    public async Task SendPrinterAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(host, port, timeout);
        using var timeoutSource = CreateTimeoutSource(timeout);
        using var requestSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(host, port, requestSource.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(payload, requestSource.Token);
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
            }
        }
        catch (OperationCanceledException exception)
            when (IsTimeout(timeoutSource, cancellationToken))
        {
            throw new TimeoutException(
                $"Timed out sending printer payload to {host}:{port}.",
                exception);
        }
    }

    public async Task<string> SendMarkerAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(host, port, timeout);
        using var timeoutSource = CreateTimeoutSource(timeout);
        using var requestSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(host, port, requestSource.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(command, requestSource.Token);
            return await ReadResponseLineAsync(stream, requestSource.Token);
        }
        catch (OperationCanceledException exception)
            when (IsTimeout(timeoutSource, cancellationToken))
        {
            throw new TimeoutException(
                $"Timed out waiting for a marker response from {host}:{port}.",
                exception);
        }
    }

    private static async Task<string> ReadResponseLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var buffer = new byte[512];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            var lineEnd = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
            var bytesToWrite = lineEnd < 0 ? bytesRead : lineEnd;
            if (response.Length + bytesToWrite > MaxMarkerResponseBytes)
            {
                throw new IOException("Marker response line exceeds the maximum length.");
            }

            response.Write(buffer, 0, bytesToWrite);
            if (lineEnd >= 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(response.ToArray()).Trim();
    }

    private static CancellationTokenSource CreateTimeoutSource(TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        var timeoutSource = new CancellationTokenSource();
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(timeout);
        }

        return timeoutSource;
    }

    private static bool IsTimeout(
        CancellationTokenSource timeoutSource,
        CancellationToken cancellationToken) =>
        timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    private static void ValidateEndpoint(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("A device host is required.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ValidateTimeout(timeout);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }
}
