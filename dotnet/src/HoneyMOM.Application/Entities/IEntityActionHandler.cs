using HoneyMOM.Application.Security;

namespace HoneyMOM.Application.Entities;

public interface IEntityActionHandler
{
    string EntityName { get; }
    string ActionName { get; }

    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        EntityActionContext context,
        CancellationToken cancellationToken);
}
