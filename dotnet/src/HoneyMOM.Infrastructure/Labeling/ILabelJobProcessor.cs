namespace HoneyMOM.Infrastructure.Labeling;

public interface ILabelJobProcessor
{
    Task<IReadOnlyDictionary<string, object?>?> ProcessJobAsync(
        long jobId,
        CancellationToken cancellationToken);
}
