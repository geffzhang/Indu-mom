namespace HoneyMOM.Application.Security;

public sealed record ScopeSelection(
    IReadOnlyList<int> Plants,
    IReadOnlyList<int> Workshops,
    IReadOnlyList<int> Lines);

public sealed record ScopeHierarchy(
    IReadOnlyDictionary<int, int?> WorkshopPlants,
    IReadOnlyDictionary<int, int?> LineWorkshops);

public sealed record VisibleLocationIds(
    IReadOnlySet<int> Plants,
    IReadOnlySet<int> Workshops,
    IReadOnlySet<int> Lines);

public static class ScopeEvaluator
{
    public static ScopeSelection? ResolveEffectiveScope(CallerPrincipal caller)
    {
        var explicitScope = Normalize(caller.ExplicitScope);
        if (explicitScope is not null || caller.IsApiKey)
        {
            return explicitScope;
        }

        if (caller.Roles.Count == 0)
        {
            return null;
        }

        var roleScopes = caller.Roles
            .Select(role => Normalize(role.Scope))
            .ToArray();
        if (roleScopes.Any(scope => scope is null))
        {
            return null;
        }

        return new ScopeSelection(
            roleScopes.SelectMany(scope => scope!.Plants).Distinct().Order().ToArray(),
            roleScopes.SelectMany(scope => scope!.Workshops).Distinct().Order().ToArray(),
            roleScopes.SelectMany(scope => scope!.Lines).Distinct().Order().ToArray());
    }

    public static VisibleLocationIds ResolveVisibleLocations(
        ScopeSelection scope,
        ScopeHierarchy hierarchy)
    {
        var plants = scope.Plants.ToHashSet();
        var workshops = scope.Workshops
            .Concat(hierarchy.WorkshopPlants
                .Where(entry => entry.Value is int plant && plants.Contains(plant))
                .Select(entry => entry.Key))
            .ToHashSet();
        var lines = scope.Lines
            .Concat(hierarchy.LineWorkshops
                .Where(entry => entry.Value is int workshop && workshops.Contains(workshop))
                .Select(entry => entry.Key))
            .ToHashSet();

        var visibleWorkshops = new HashSet<int>(workshops);
        foreach (var line in lines)
        {
            if (hierarchy.LineWorkshops.TryGetValue(line, out var workshop) && workshop is int value)
            {
                visibleWorkshops.Add(value);
            }
        }

        var visiblePlants = new HashSet<int>(plants);
        foreach (var workshop in visibleWorkshops)
        {
            if (hierarchy.WorkshopPlants.TryGetValue(workshop, out var plant) && plant is int value)
            {
                visiblePlants.Add(value);
            }
        }

        return new VisibleLocationIds(visiblePlants, visibleWorkshops, lines);
    }

    public static bool IsRowVisible(
        ScopeSelection? scope,
        VisibleLocationIds? visible,
        string target,
        int? assignedId)
    {
        if (scope is null || assignedId is null || visible is null)
        {
            return true;
        }

        return target switch
        {
            "plant" => visible.Plants.Contains(assignedId.Value),
            "workshop" => visible.Workshops.Contains(assignedId.Value),
            "line" => visible.Lines.Contains(assignedId.Value),
            _ => true
        };
    }

    private static ScopeSelection? Normalize(
        IReadOnlyDictionary<string, IReadOnlyList<int>>? scope)
    {
        if (scope is null)
        {
            return null;
        }

        var normalized = new ScopeSelection(
            NormalizeIds(scope, "plants"),
            NormalizeIds(scope, "workshops"),
            NormalizeIds(scope, "lines"));
        return normalized.Plants.Count == 0 &&
            normalized.Workshops.Count == 0 &&
            normalized.Lines.Count == 0
                ? null
                : normalized;
    }

    private static IReadOnlyList<int> NormalizeIds(
        IReadOnlyDictionary<string, IReadOnlyList<int>> scope,
        string key) =>
        scope.TryGetValue(key, out var ids)
            ? ids.Distinct().Order().ToArray()
            : [];
}
