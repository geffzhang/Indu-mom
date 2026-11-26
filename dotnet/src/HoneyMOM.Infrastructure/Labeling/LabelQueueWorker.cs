using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HoneyMOM.Infrastructure.Labeling;

public sealed class LabelQueueWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LabelQueueWorker>? _logger;

    public LabelQueueWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<LabelQueueWorker>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverSendingJobsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_logger is null)
                {
                    throw;
                }

                _logger.LogError(exception, "Failed while processing the label queue.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecoverSendingJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var now = _timeProvider.GetLocalNow().DateTime;
        await context.Set<Dictionary<string, object?>>("print_job")
            .Where(row => EF.Property<string>(row, "status") == "sending")
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(row => EF.Property<string>(row, "status"), "queued")
                    .SetProperty(row => EF.Property<DateTime?>(row, "next_try"), now)
                    .SetProperty(row => EF.Property<DateTime?>(row, "updated_at"), now),
                cancellationToken);
    }

    private async Task ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<HoneyMomDbContext>();
        var now = _timeProvider.GetLocalNow().DateTime;
        var jobIds = await context.Set<Dictionary<string, object?>>("print_job")
            .AsNoTracking()
            .Where(row =>
                EF.Property<string>(row, "kind") != "browser" &&
                EF.Property<string>(row, "status") == "queued" &&
                (EF.Property<DateTime?>(row, "next_try") == null ||
                 EF.Property<DateTime?>(row, "next_try") <= now))
            .OrderBy(row => EF.Property<int>(row, "id"))
            .Take(BatchSize)
            .Select(row => EF.Property<int>(row, "id"))
            .ToListAsync(cancellationToken);
        var processor = services.GetRequiredService<ILabelJobProcessor>();

        foreach (var jobId in jobIds)
        {
            await processor.ProcessJobAsync(jobId, cancellationToken);
        }
    }
}
