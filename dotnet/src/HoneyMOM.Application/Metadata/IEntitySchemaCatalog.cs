namespace HoneyMOM.Application.Metadata;

public interface IEntitySchemaCatalog
{
    IReadOnlyList<EntitySchema> Entities { get; }

    EntitySchema? Get(string entityName);
}
