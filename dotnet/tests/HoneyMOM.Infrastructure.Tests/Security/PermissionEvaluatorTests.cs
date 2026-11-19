using HoneyMOM.Application.Security;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Security;

public sealed class PermissionEvaluatorTests
{
    [Fact]
    public void WildcardPatternsSupportPositiveAndNegativeRules()
    {
        var caller = new CallerPrincipal(
            null,
            "operator",
            "Operator",
            [
                new CallerRole(
                    "quality",
                    ["quality.*", "!quality.ncr.delete"],
                    null)
            ]);

        Assert.True(PermissionEvaluator.Allows(caller, "quality.ncr.read"));
        Assert.False(PermissionEvaluator.Allows(caller, "quality.ncr.delete"));
        Assert.False(PermissionEvaluator.Allows(caller, "production.work_order.read"));
    }

    [Fact]
    public void PermissionsAreGrantedByAnyRoleWithoutCrossRoleNegation()
    {
        var caller = new CallerPrincipal(
            null,
            "manager",
            "Manager",
            [
                new CallerRole("allow", ["quality.ncr.delete"], null),
                new CallerRole("exclude", ["*", "!quality.ncr.delete"], null)
            ]);

        Assert.True(PermissionEvaluator.Allows(caller, "quality.ncr.delete"));
    }
}
