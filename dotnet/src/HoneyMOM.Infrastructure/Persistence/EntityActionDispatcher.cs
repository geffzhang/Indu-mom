using HoneyMOM.Application.Entities;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class EntityActionDispatcher(IEnumerable<IEntityActionHandler> handlers)
{
    private readonly IReadOnlyDictionary<(string Entity, string Action), IEntityActionHandler> _handlers =
        handlers.ToDictionary(
            handler => (handler.EntityName, handler.ActionName),
            handler => handler);

    public IEntityActionHandler? Find(string entityName, string actionName) =>
        _handlers.GetValueOrDefault((entityName, actionName));
}
