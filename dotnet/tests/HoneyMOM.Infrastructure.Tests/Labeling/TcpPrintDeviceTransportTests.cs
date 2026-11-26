using System.Net;
using System.Net.Sockets;
using System.Text;
using HoneyMOM.Infrastructure.Labeling;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Labeling;

public sealed class TcpPrintDeviceTransportTests
{
    [Fact]
    public async Task MarkerTransportHonorsTimeoutAndCallerCancellation()
    {
        var transport = new TcpPrintDeviceTransport();
        var timeoutListener = new TcpListener(IPAddress.Loopback, 0);
        timeoutListener.Start();
        var timeoutEndpoint = (IPEndPoint)timeoutListener.LocalEndpoint;
        using var timeoutServer = new CancellationTokenSource();
        var timeoutRequestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutServerTask = HoldMarkerConnectionAsync(
            timeoutListener,
            timeoutRequestReceived,
            timeoutServer.Token);

        await Assert.ThrowsAsync<TimeoutException>(() => transport.SendMarkerAsync(
            IPAddress.Loopback.ToString(),
            timeoutEndpoint.Port,
            Encoding.UTF8.GetBytes("MARK timeout\r\n"),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None));
        await timeoutRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeoutServer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timeoutServerTask);
        timeoutListener.Stop();

        var cancellationListener = new TcpListener(IPAddress.Loopback, 0);
        cancellationListener.Start();
        var cancellationEndpoint = (IPEndPoint)cancellationListener.LocalEndpoint;
        using var cancellationServer = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var cancellationRequestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationServerTask = HoldMarkerConnectionAsync(
            cancellationListener,
            cancellationRequestReceived,
            cancellationServer.Token);
        var sendTask = transport.SendMarkerAsync(
            IPAddress.Loopback.ToString(),
            cancellationEndpoint.Port,
            Encoding.UTF8.GetBytes("MARK cancellation\r\n"),
            TimeSpan.FromSeconds(5),
            callerCancellation.Token);

        await cancellationRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        cancellationServer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationServerTask);
        cancellationListener.Stop();
    }

    [Fact]
    public async Task PrinterAndMarkerTransportUseLoopbackOnlyInTests()
    {
        var transport = new TcpPrintDeviceTransport();
        var printerListener = new TcpListener(IPAddress.Loopback, 0);
        printerListener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var printerEndpoint = (IPEndPoint)printerListener.LocalEndpoint;
        var printerPayload = Encoding.UTF8.GetBytes("^XA\n^XZ\n");
        var receivePrinterTask = ReceiveToEndAsync(printerListener, timeout.Token);

        await transport.SendPrinterAsync(
            IPAddress.Loopback.ToString(),
            printerEndpoint.Port,
            printerPayload,
            TimeSpan.FromSeconds(2),
            timeout.Token);
        var receivedPayload = await receivePrinterTask;
        printerListener.Stop();

        Assert.Equal(IPAddress.Loopback, printerEndpoint.Address);
        Assert.Equal(printerPayload, receivedPayload);

        var markerListener = new TcpListener(IPAddress.Loopback, 0);
        markerListener.Start();
        var markerEndpoint = (IPEndPoint)markerListener.LocalEndpoint;
        const string command = "MARK PJ-1 WO-42\r\n";
        var receiveMarkerTask = ReceiveMarkerAndAcknowledgeAsync(markerListener, timeout.Token);

        var response = await transport.SendMarkerAsync(
            IPAddress.Loopback.ToString(),
            markerEndpoint.Port,
            Encoding.UTF8.GetBytes(command),
            TimeSpan.FromSeconds(2),
            timeout.Token);
        var receivedCommand = await receiveMarkerTask;
        markerListener.Stop();

        Assert.Equal(IPAddress.Loopback, markerEndpoint.Address);
        Assert.Equal(command.TrimEnd('\r', '\n'), receivedCommand);
        Assert.Equal("OK", response);
    }

    private static async Task<byte[]> ReceiveToEndAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var payload = new MemoryStream();
        var buffer = new byte[256];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            payload.Write(buffer, 0, bytesRead);
        }

        return payload.ToArray();
    }

    private static async Task<string?> ReceiveMarkerAndAcknowledgeAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 256,
            leaveOpen: true);
        var command = await reader.ReadLineAsync(cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("OK\r\n"), cancellationToken);
        return command;
    }

    private static async Task HoldMarkerConnectionAsync(
        TcpListener listener,
        TaskCompletionSource requestReceived,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var buffer = new byte[128];
        var lineReceived = false;
        while (!lineReceived)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
            {
                return;
            }

            lineReceived = buffer.AsSpan(0, bytesRead).Contains((byte)'\n');
        }

        requestReceived.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
