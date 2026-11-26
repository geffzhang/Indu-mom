using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Labeling;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HoneyMOM.Api.Tests;

internal sealed class ApiTestHost : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"honeymom-api-test-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTestHost(bool enableLabelQueueWorker = false)
    {
        Directory.CreateDirectory(_directory);
        var connectionString = $"Data Source={Path.Combine(_directory, "test.db")};Pooling=False";
        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connectionString)
            .Options;
        using (var context = new HoneyMomDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<HoneyMomDbContext>>();
                services.RemoveAll<HoneyMomDbContext>();
                services.AddDbContext<HoneyMomDbContext>(options => options.UseSqlite(connectionString));

                if (!enableLabelQueueWorker)
                {
                    var workerRegistration = services.SingleOrDefault(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType == typeof(LabelQueueWorker));
                    if (workerRegistration is not null)
                    {
                        services.Remove(workerRegistration);
                    }
                }
            }));
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    public IServiceScope CreateScope() => _factory.Services.CreateScope();

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
