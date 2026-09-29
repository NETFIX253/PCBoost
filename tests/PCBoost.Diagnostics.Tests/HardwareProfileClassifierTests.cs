using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Hardware;
using PCBoost.Diagnostics.Tests.TestSupport;

namespace PCBoost.Diagnostics.Tests;

public sealed class HardwareProfileClassifierTests
{
    private readonly HardwareProfileClassifier _classifier = new();

    private static CpuInfo Cpu(int cores) => new("CPU", "Vendor", cores, cores * 2, null, null, null);

    private static MemoryInfo Ram(double gib) => new((long)(gib * ByteSize.GiB), 0, 0, 0, null, null);

    private static StorageDrive[] SystemDrive(StorageMediaType media, double freePercent = 50)
        => [Reports.Drive(256L * ByteSize.GiB, (long)(256L * ByteSize.GiB * freePercent / 100), media)];

    private static GpuInfo Integrated() => new("Intel UHD", GpuVendor.Intel, 128 * ByteSize.MiB, 4L * ByteSize.GiB, null, false, true);

    private static GpuInfo Dedicated(double vramGiB) => new("GeForce", GpuVendor.Nvidia, (long)(vramGiB * ByteSize.GiB), 8L * ByteSize.GiB, null, false, false);

    private HardwareProfile Classify(int cores, double ramGiB, StorageMediaType media, params GpuInfo[] gpus)
        => _classifier.Classify(Cpu(cores), Ram(ramGiB), SystemDrive(media), gpus);

    [Fact]
    public void Four_gigabytes_is_legacy()
    {
        var profile = Classify(4, 4, StorageMediaType.Ssd, Integrated());
        Assert.Equal(HardwareTier.LegacyLowResource, profile.Tier);
        Assert.True(profile.LowMemory);
        Assert.Equal("Diag_Profile_Reason_Ram", profile.Reasons[0].Key);
        Assert.Equal(4, profile.Reasons[0].Args[0]);
    }

    [Fact]
    public void Usable_memory_slightly_below_installed_is_rounded_up()
    {
        // 4 Go installés, 3,8 Gio utilisables → toujours ≤ 4 Go ; 16 Go installés, 15,8 Gio utilisables → ≥ 16 Go.
        Assert.Equal(HardwareTier.LegacyLowResource, Classify(4, 3.8, StorageMediaType.Ssd).Tier);
        Assert.Equal(HardwareTier.HighEnd, Classify(8, 15.8, StorageMediaType.Ssd, Dedicated(5.9)).Tier);
    }

    [Fact]
    public void Hdd_with_two_cores_is_legacy_even_with_more_memory()
    {
        var profile = Classify(2, 8, StorageMediaType.Hdd, Integrated());
        Assert.Equal(HardwareTier.LegacyLowResource, profile.Tier);
        Assert.True(profile.SystemDriveIsHdd);
        Assert.True(profile.FewCores);
        Assert.Equal("Diag_Profile_Reason_Hdd", profile.Reasons[0].Key);
        Assert.Equal("Diag_Profile_Reason_Cores", profile.Reasons[1].Key);
    }

    [Fact]
    public void Six_gigabytes_is_entry()
        => Assert.Equal(HardwareTier.Entry, Classify(4, 6, StorageMediaType.Ssd, Integrated()).Tier);

    [Fact]
    public void Two_cores_on_ssd_is_entry()
    {
        var profile = Classify(2, 16, StorageMediaType.Ssd, Dedicated(8));
        Assert.Equal(HardwareTier.Entry, profile.Tier);
        Assert.Equal("Diag_Profile_Reason_Cores", profile.Reasons[0].Key);
    }

    [Theory]
    [InlineData(4, true)]   // ≤ 4 cœurs, GPU dédié
    [InlineData(6, false)]  // 6 cœurs, pas de GPU dédié
    public void Eight_gigabytes_with_few_cores_or_no_dedicated_gpu_is_low_end(int cores, bool dedicatedGpu)
    {
        var profile = Classify(cores, 8, StorageMediaType.Ssd, dedicatedGpu ? Dedicated(4) : Integrated());
        Assert.Equal(HardwareTier.LowEnd, profile.Tier);
        Assert.Equal(dedicatedGpu, profile.HasDedicatedGpu);
    }

    [Fact]
    public void Eight_gigabytes_six_cores_dedicated_gpu_is_mid_range()
        => Assert.Equal(HardwareTier.MidRange, Classify(6, 8, StorageMediaType.Ssd, Dedicated(4)).Tier);

    [Fact]
    public void Thirty_two_gigabytes_eight_cores_eight_gigabyte_gpu_is_high_end()
    {
        var profile = Classify(8, 32, StorageMediaType.Ssd, Integrated(), Dedicated(8));
        Assert.Equal(HardwareTier.HighEnd, profile.Tier);
        Assert.True(profile.HasDedicatedGpu);
        Assert.False(profile.LowMemory);
        Assert.Contains(profile.Reasons, r => r.Key == "Diag_Profile_Reason_DedicatedGpu" && (int)r.Args[0] == 8);
    }

    [Theory]
    [InlineData(16, 8, 4.0)]  // GPU < 6 Go
    [InlineData(16, 6, 8.0)]  // < 8 cœurs
    [InlineData(12, 8, 8.0)]  // < 16 Go
    public void High_end_requires_all_three_criteria(double ram, int cores, double vram)
        => Assert.Equal(HardwareTier.MidRange, Classify(cores, ram, StorageMediaType.Ssd, Dedicated(vram)).Tier);

    [Fact]
    public void Zero_memory_is_unknown()
    {
        var profile = Classify(8, 0, StorageMediaType.Ssd, Dedicated(8));
        Assert.Equal(HardwareTier.Unknown, profile.Tier);
        Assert.Equal("Diag_Profile_Reason_Unknown", Assert.Single(profile.Reasons).Key);
    }

    [Fact]
    public void Unknown_core_count_is_never_treated_as_few_cores()
    {
        var hdd = Classify(0, 8, StorageMediaType.Hdd, Dedicated(4));
        Assert.NotEqual(HardwareTier.LegacyLowResource, hdd.Tier);
        Assert.False(hdd.FewCores);
        Assert.DoesNotContain(hdd.Reasons, r => r.Key == "Diag_Profile_Reason_Cores");
        Assert.Equal(HardwareTier.MidRange, hdd.Tier);
    }

    [Fact]
    public void Low_system_drive_space_is_flagged_with_a_reason()
    {
        var profile = _classifier.Classify(Cpu(6), Ram(16), SystemDrive(StorageMediaType.Ssd, freePercent: 8), [Dedicated(4)]);
        Assert.True(profile.LowSystemDriveSpace);
        Assert.Contains(profile.Reasons, r => r.Key == "Diag_Profile_Reason_LowSpace");
    }

    [Fact]
    public void Software_adapter_is_not_a_dedicated_gpu()
    {
        var basic = new GpuInfo("Microsoft Basic Render Driver", GpuVendor.Microsoft, null, null, null, true, false);
        var profile = Classify(8, 32, StorageMediaType.Ssd, basic);
        Assert.False(profile.HasDedicatedGpu);
        Assert.Equal(HardwareTier.MidRange, profile.Tier);
    }
}
