namespace HoneyMOM.Application.Labeling;

public sealed record WorkOrderReleaseLabelContext(
    long WorkOrderId,
    string WorkOrderCode,
    long? ProductId,
    string? Customer,
    string RequestedBy);
