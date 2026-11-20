using System.Text.Json;

namespace HoneyMOM.Application.Metadata;

public interface IEntitySchemaCatalog
{
    IReadOnlyList<EntitySchema> Entities { get; }
    IReadOnlyList<JsonElement> RawEntities { get; }

    EntitySchema? Get(string entityName);
}
