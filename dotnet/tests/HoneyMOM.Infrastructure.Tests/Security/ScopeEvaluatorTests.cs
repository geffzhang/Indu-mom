using HoneyMOM.Application.Security;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Security;

public sealed class ScopeEvaluatorTests
{
    [Fact]
    public void ExplicitUserScopeOverridesRoleScopes()
    {
        var caller = new CallerPrincipal(
            1,
            "user",
            "User",
            [new CallerRole("line", [], Scope(("lines", [7])))],
            ExplicitScope: Scope(("plants", [2])));

        var effective = ScopeEvaluator.ResolveEffectiveScope(caller);

        Assert.Equal([2], effective!.Plants);
        Assert.Empty(effective.Workshops);
        Assert.Empty(effective.Lines);
    }

    [Fact]
    public void RoleScopesUnionOnlyWhenEveryRoleHasAScope()
    {
        var restricted = new CallerPrincipal(
            1,
            "user",
            "User",
            [
                new CallerRole("line-a", [], Scope(("lines", [7]))),
                new CallerRole("line-b", [], Scope(("lines", [8])))
            ]);
        var unrestricted = new CallerPrincipal(
            2,
            "unrestricted",
            "Unrestricted",
            [
                new CallerRole("line", [], Scope(("lines", [7]))),
                new CallerRole("all", [], null)
            ]);

        Assert.Equal([7, 8], ScopeEvaluator.ResolveEffectiveScope(restricted)!.Lines);
        Assert.Null(ScopeEvaluator.ResolveEffectiveScope(unrestricted));
    }

    [Fact]
    public void ApiKeyWithoutExplicitScopeDoesNotInheritRoleScope()
    {
        var caller = new CallerPrincipal(
            null,
            "apikey:gateway",
            "Gateway",
            [new CallerRole("scoped", [], Scope(("lines", [7])))],
            IsApiKey: true);

        Assert.Null(ScopeEvaluator.ResolveEffectiveScope(caller));
    }

    [Fact]
    public void OrphanAndUnassignedRowsFollowReferenceScope()
    {
        var scope = new ScopeSelection([], [], [99, 100]);
        var hierarchy = new ScopeHierarchy(
            new Dictionary<int, int?> { [10] = 3, [11] = 4 },
            new Dictionary<int, int?> { [99] = null, [100] = 10, [101] = 11 });
        var visible = ScopeEvaluator.ResolveVisibleLocations(scope, hierarchy);

        Assert.Contains(99, visible.Lines);
        Assert.Contains(10, visible.Workshops);
        Assert.Contains(3, visible.Plants);
        Assert.True(ScopeEvaluator.IsRowVisible(scope, visible, "line", null));
        Assert.True(ScopeEvaluator.IsRowVisible(scope, visible, "line", 99));
        Assert.False(ScopeEvaluator.IsRowVisible(scope, visible, "line", 101));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> Scope(
        params (string Key, int[] Values)[] values) =>
        values.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<int>)item.Values,
            StringComparer.Ordinal);
}
