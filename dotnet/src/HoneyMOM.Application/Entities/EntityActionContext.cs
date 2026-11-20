using System.Text.Json;
using HoneyMOM.Application.Security;

namespace HoneyMOM.Application.Entities;

public sealed record EntityActionContext(
    string EntityName,
    long EntityId,
    IReadOnlyDictionary<string, object?> Row,
    IReadOnlyDictionary<string, JsonElement> Body,
    CallerPrincipal Caller);
