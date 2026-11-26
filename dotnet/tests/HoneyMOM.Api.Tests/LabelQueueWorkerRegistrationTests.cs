using HoneyMOM.Application.Labeling;
using HoneyMOM.Infrastructure.Labeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class LabelQueueWorkerRegistrationTests
{
    [Fact]
    public void LabelWorkerIsRegisteredInApiHost()
    {
        using var host = new ApiTestHost(enableLabelQueueWorker: true);
        using var firstScope = host.CreateScope();
        using var secondScope = host.CreateScope();

        Assert.Same(
            TimeProvider.System,
            firstScope.ServiceProvider.GetRequiredService<TimeProvider>());
        var firstReleaseService =
            firstScope.ServiceProvider.GetRequiredService<IWorkOrderReleaseLabelService>();
        var secondReleaseService =
            secondScope.ServiceProvider.GetRequiredService<IWorkOrderReleaseLabelService>();
        Assert.NotSame(firstReleaseService, secondReleaseService);
        var firstProcessor = firstScope.ServiceProvider.GetRequiredService<ILabelJobProcessor>();
        var secondProcessor = secondScope.ServiceProvider.GetRequiredService<ILabelJobProcessor>();
        Assert.NotSame(firstProcessor, secondProcessor);
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IPrintDeviceTransport>(),
            secondScope.ServiceProvider.GetRequiredService<IPrintDeviceTransport>());
        Assert.Single(
            firstScope.ServiceProvider.GetServices<IHostedService>()
                .OfType<LabelQueueWorker>());
    }

    [Fact]
    public void ApiTestHostDisablesLabelWorkerByDefault()
    {
        using var host = new ApiTestHost();
        using var scope = host.CreateScope();

        Assert.Empty(
            scope.ServiceProvider.GetServices<IHostedService>()
                .OfType<LabelQueueWorker>());
    }
}
