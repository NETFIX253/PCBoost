using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;

namespace PCBoost.Core.Tests.Domain;

public sealed class CleanupCatalogTests
{
    private static readonly KnownFolder[] MachineWideRoots = [KnownFolder.WindowsTemp, KnownFolder.ProgramData, KnownFolder.WindowsDirectory];
    private static readonly KnownFolder[] PersonalFolders =
        [KnownFolder.UserProfile, KnownFolder.Documents, KnownFolder.Downloads, KnownFolder.Desktop, KnownFolder.Pictures, KnownFolder.Videos, KnownFolder.Music];

    [Fact]
    public void Identifiers_are_unique_case_insensitively()
    {
        var ids = CleanupCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public void Elevation_is_required_exactly_for_machine_wide_locations()
    {
        foreach (var category in CleanupCatalog.All.Where(c => !c.IsRecycleBin))
        {
            var machineWide = category.Targets.Any(t => MachineWideRoots.Contains(t.Root));
            Assert.True(machineWide == category.RequiresElevation, $"{category.Id} : élévation incohérente");
        }
    }

    [Fact]
    public void Recycle_bin_is_handled_by_a_dedicated_provider()
    {
        var bin = CleanupCatalog.Find(CleanupCatalog.RecycleBin)!;

        Assert.True(bin.IsRecycleBin);
        Assert.Empty(bin.Targets);
        Assert.False(bin.SelectedByDefault);
        Assert.Single(CleanupCatalog.All, c => c.IsRecycleBin);
    }

    [Fact]
    public void No_category_targets_personal_folders()
        => Assert.All(CleanupCatalog.All.SelectMany(c => c.Targets), t => Assert.DoesNotContain(t.Root, PersonalFolders));

    [Fact]
    public void Only_safe_categories_are_selected_by_default()
        => Assert.All(CleanupCatalog.All.Where(c => c.SelectedByDefault), c => Assert.Equal(SafetyCategory.Safe, c.Safety));

    [Fact]
    public void Wildcards_are_single_segments_and_paths_never_escape()
    {
        foreach (var target in CleanupCatalog.All.SelectMany(c => c.Targets))
        {
            var segments = target.RelativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain("..", segments);
            Assert.DoesNotContain(".", segments);
            Assert.True(segments.Count(s => s == "*") <= 1, target.RelativePath);
            Assert.All(segments.Where(s => s.Contains('*')), s => Assert.Equal("*", s));
        }
    }

    [Fact]
    public void Browser_categories_declare_their_blocking_process()
    {
        Assert.Contains("msedge.exe", CleanupCatalog.Find(CleanupCatalog.EdgeCache)!.BlockingProcesses);
        Assert.Contains("chrome.exe", CleanupCatalog.Find(CleanupCatalog.ChromeCache)!.BlockingProcesses);
        Assert.Contains("firefox.exe", CleanupCatalog.Find(CleanupCatalog.FirefoxCache)!.BlockingProcesses);
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown_ids()
    {
        Assert.Same(CleanupCatalog.Find(CleanupCatalog.UserTemp), CleanupCatalog.Find("USER-TEMP"));
        Assert.Null(CleanupCatalog.Find("documents"));
        Assert.Null(CleanupCatalog.Find(""));
    }

    [Fact]
    public void ToCategory_uses_resource_keys_derived_from_the_id()
    {
        var category = CleanupCatalog.Find(CleanupCatalog.ShaderCache)!.ToCategory();

        Assert.Equal("Cleanup_directx-shader-cache_Name", category.Name.Key);
        Assert.Equal("Cleanup_directx-shader-cache_Description", category.Description.Key);
        Assert.Equal(SafetyCategory.Advanced, category.Safety);
        Assert.Equal(TimeSpan.FromDays(1), category.MinimumFileAge);
    }
}
