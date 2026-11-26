namespace HoneyMOM.Application.Labeling;

public interface IWorkOrderReleaseLabelService
{
    Task<int> QueueAsync(
        WorkOrderReleaseLabelContext workOrder,
        CancellationToken cancellationToken);
}
