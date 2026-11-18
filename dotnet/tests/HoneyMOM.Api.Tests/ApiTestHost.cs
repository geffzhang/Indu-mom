using HoneyMOM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HoneyMOM.Api.Tests;

internal sealed class ApiTestHost : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"honeymom-api-test-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTestHost()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = $"Data Source={Path.Combine(_directory, "test.db")};Pooling=False";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<HoneyMomDbContext>>();
                services.RemoveAll<HoneyMomDbContext>();
                services.AddDbContext<HoneyMomDbContext>(options => options.UseSqlite(connectionString));
            }));

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>().Database.EnsureCreated();
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
