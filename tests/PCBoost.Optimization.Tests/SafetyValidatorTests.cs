using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Optimization.Rollback;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class SafetyValidatorTests
{
    private readonly FakeSystemInfoProvider _system = new();

    private OptimizationSafetyValidator Validator() => new(_system);

    private static OptimizationPreview Preview(FakeOptimization o, bool applicable = true, bool reversible = true, RiskLevel risk = RiskLevel.Low, string target = "fake:x")
        => new(o.Id, o.Name, applicable, applicable ? null : TextRef.Of("reason"),
            [new PlannedChange(o.Id + ":1", TextRef.Literal("c"), target, true, reversible, risk)], risk, ImpactLevel.Low, reversible, false, false);

    private static OptimizationPlan Plan(OptimizationPreview preview, bool irreversibleConfirmed = false, bool highRiskConfirmed = false)
        => new(SessionType.Manual, [preview], preview.Changes.Select(c => c.Id).ToHashSet(), null, irreversibleConfirmed, highRiskConfirmed);

    [Fact]
    public void Validate_AllowsLowRiskReversibleApplicablePreview()
    {
        var o = new FakeOptimization("x");
        var preview = Preview(o);
        var result = Validator().Validate(o, preview, Plan(preview));
        Assert.True(result.Allowed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Validate_RefusesWhenWindowsBuildIsTooOld()
    {
        _system.Os = _system.Os with { BuildNumber = 17134 };
        var o = new FakeOptimization("x");
        var preview = Preview(o);
        var result = Validator().Validate(o, preview, Plan(preview));
        Assert.False(result.Allowed);
        Assert.Contains(result.Violations, v => v.Code == OptimizationSafetyValidator.CodeWindowsBuild && v.Blocking);
    }

    [Fact]
    public void Validate_RefusesModuleRequiringNewerBuild()
    {
        var o = new FakeOptimization("x") { MinimumWindowsBuild = 30000 };
        var preview = Preview(o);
        Assert.False(Validator().Validate(o, preview, Plan(preview)).Allowed);
    }

    [Fact]
    public void Validate_HighRisk_RequiresExplicitConfirmation()
    {
        var o = new FakeOptimization("x", RiskLevel.High);
        var preview = Preview(o, risk: RiskLevel.High);

        var refused = Validator().Validate(o, preview, Plan(preview));
        var allowed = Validator().Validate(o, preview, Plan(preview, highRiskConfirmed: true));

        Assert.False(refused.Allowed);
        Assert.Contains(refused.Violations, v => v.Code == OptimizationSafetyValidator.CodeHighRisk);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void Validate_IrreversibleSelected_RequiresExplicitConfirmation()
    {
        var o = new FakeOptimization("x", reversible: false);
        var preview = Preview(o, reversible: false);

        var refused = Validator().Validate(o, preview, Plan(preview));
        var allowed = Validator().Validate(o, preview, Plan(preview, irreversibleConfirmed: true));

        Assert.False(refused.Allowed);
        Assert.Contains(refused.Violations, v => v.Code == OptimizationSafetyValidator.CodeIrreversible);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void Validate_RefusesNonApplicablePreview_AndForbiddenPlannedTarget()
    {
        var o = new FakeOptimization("x");
        var notApplicable = Preview(o, applicable: false);
        var forbidden = Preview(o, target: @"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend");

        Assert.Contains(Validator().Validate(o, notApplicable, Plan(notApplicable)).Violations, v => v.Code == OptimizationSafetyValidator.CodeNotApplicable);
        Assert.Contains(Validator().Validate(o, forbidden, Plan(forbidden)).Violations, v => v.Code == OptimizationSafetyValidator.CodeForbiddenTarget);
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"C:\Windows\System32\drivers\x.sys")]
    [InlineData("service:WinDefend")]
    public void ValidateChange_RefusesForbiddenTargets(string target)
    {
        var change = new PendingChange(ChangeKinds.PowerScheme, "x", target, TextRef.Literal("x"),
            ChangeStateSerializer.Serialize(new PowerSchemeState(PowerScheme.Balanced, null)), true);
        var result = Validator().ValidateChange(change);
        Assert.False(result.Allowed);
        Assert.Contains(result.Violations, v => v.Code == OptimizationSafetyValidator.CodeForbiddenTarget);
    }

    [Fact]
    public void ValidateChange_RefusesUnknownKind_AndReversibleChangeWithoutBeforeState()
    {
        var unknown = new PendingChange("service.disable", "x", "power:1", TextRef.Literal("x"), "{}", true);
        var noState = new PendingChange(ChangeKinds.PowerScheme, "x", "power:1", TextRef.Literal("x"), null, true);

        Assert.Contains(Validator().ValidateChange(unknown).Violations, v => v.Code == OptimizationSafetyValidator.CodeUnknownKind);
        Assert.Contains(Validator().ValidateChange(noState).Violations, v => v.Code == OptimizationSafetyValidator.CodeMissingBeforeState);
    }

    [Fact]
    public void ValidateChange_AcceptsStartupApprovedChange()
    {
        var location = new RegistryLocation(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        var change = new PendingChange(ChangeKinds.RegistryValue, "x", $"{location} : Discord", TextRef.Literal("x"),
            ChangeStateSerializer.Serialize(RegistryValueState.Capture(location, "Discord", null)), true);
        Assert.True(Validator().ValidateChange(change).Allowed);
    }

    [Fact]
    public void ValidateChange_RefusesMicrosoftScheduledTask()
    {
        var change = new PendingChange(ChangeKinds.ScheduledTask, "x", @"task:\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", TextRef.Literal("x"),
            ChangeStateSerializer.Serialize(new ScheduledTaskState(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", true)), true);
        Assert.False(Validator().ValidateChange(change).Allowed);
    }
}
