using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using PCBoost.Core.Common;

namespace PCBoost.Core.Tests.CrossCutting;

/// <summary>
/// Parité des ressources de tous les modules : les assemblies PCBoost.*.dll présentes à côté des tests sont découvertes
/// dynamiquement (aucune référence de compilation requise). Pour chaque ressource « *.Resources.Strings.resources »,
/// la culture neutre (fr) et la culture « en » doivent avoir exactement les mêmes clés, sans valeur vide.
/// </summary>
public sealed partial class ResourceParityTests
{
    private const string ResourceSuffix = ".Resources.Strings.resources";

    public static TheoryData<string> ResourceAssemblies()
    {
        var data = new TheoryData<string>();
        foreach (var file in DiscoverAssemblies())
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Fact]
    public void Discovery_finds_at_least_the_infrastructure_resources()
        => Assert.Contains(DiscoverAssemblies(), f => Path.GetFileName(f) == "PCBoost.Infrastructure.dll");

    [Theory]
    [MemberData(nameof(ResourceAssemblies))]
    public void French_and_english_resources_have_the_same_keys_and_no_empty_values(string assemblyFile)
    {
        foreach (var resource in LoadResources(assemblyFile))
        {
            var missingInEnglish = resource.French.Keys.Except(resource.English.Keys).Order().ToList();
            var missingInFrench = resource.English.Keys.Except(resource.French.Keys).Order().ToList();
            Assert.True(missingInEnglish.Count == 0, $"{resource.Name} : clés absentes en anglais : {string.Join(", ", missingInEnglish)}");
            Assert.True(missingInFrench.Count == 0, $"{resource.Name} : clés absentes en français : {string.Join(", ", missingInFrench)}");

            var empty = resource.French.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "fr:" + p.Key)
                .Concat(resource.English.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "en:" + p.Key)).ToList();
            Assert.True(empty.Count == 0, $"{resource.Name} : valeurs vides : {string.Join(", ", empty)}");
        }
    }

    [Theory]
    [MemberData(nameof(ResourceAssemblies))]
    public void Templates_are_valid_and_use_the_same_arguments_in_both_languages(string assemblyFile)
    {
        var dummyArgs = Enumerable.Range(0, 10).Cast<object>().ToArray();
        foreach (var resource in LoadResources(assemblyFile))
        {
            var problems = new List<string>();
            foreach (var (key, french) in resource.French)
            {
                if (!resource.English.TryGetValue(key, out var english)) continue;
                foreach (var (language, text) in new[] { ("fr", french), ("en", english) })
                {
                    try
                    {
                        _ = string.Format(CultureInfo.InvariantCulture, text ?? string.Empty, dummyArgs);
                    }
                    catch (FormatException)
                    {
                        problems.Add($"{language}:{key} (format invalide)");
                    }
                }
                if (!Placeholders(french).SetEquals(Placeholders(english)))
                    problems.Add($"{key} (arguments différents entre fr et en)");
            }
            Assert.True(problems.Count == 0, $"{resource.Name} : {string.Join(", ", problems)}");
        }
    }

    [Fact]
    public void Every_error_kind_has_a_resource()
    {
        var keys = DiscoverAssemblies().SelectMany(LoadNeutralResources).SelectMany(r => r.Values.Keys).ToHashSet(StringComparer.Ordinal);

        var missing = Enum.GetNames<OperationErrorKind>().Select(n => "Error_" + n).Where(k => !keys.Contains(k)).ToList();

        Assert.True(missing.Count == 0, "Clés d'erreur manquantes : " + string.Join(", ", missing));
    }

    [Fact]
    public void No_key_is_defined_with_different_texts_by_two_modules()
    {
        var definitions = DiscoverAssemblies()
            .SelectMany(f => LoadNeutralResources(f).SelectMany(r => r.Values.Select(p => (Module: r.Name, p.Key, p.Value))))
            .GroupBy(d => d.Key, StringComparer.Ordinal)
            .Where(g => g.Select(d => d.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(d => d.Module))})")
            .ToList();

        Assert.True(definitions.Count == 0, "Clés définies différemment par plusieurs modules : " + string.Join("; ", definitions));
    }

    private static IReadOnlyList<string> DiscoverAssemblies()
        => Directory.GetFiles(AppContext.BaseDirectory, "PCBoost.*.dll")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return !name.EndsWith(".Tests", StringComparison.Ordinal) && name != "PCBoost.TestUtilities";
            })
            .Where(f => NeutralResourceNames(f).Count > 0)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> NeutralResourceNames(string file)
    {
        try
        {
            return Assembly.LoadFrom(file).GetManifestResourceNames().Where(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal)).ToList();
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    private sealed record ResourceSet(string Name, Dictionary<string, string?> French, Dictionary<string, string?> English);

    private sealed record NeutralResource(string Name, Dictionary<string, string?> Values);

    private static IEnumerable<NeutralResource> LoadNeutralResources(string path)
    {
        var assembly = Assembly.LoadFrom(path);
        foreach (var name in NeutralResourceNames(path))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            yield return new NeutralResource(name[..^".resources".Length], Read(stream));
        }
    }

    private static IReadOnlyList<ResourceSet> LoadResources(string assemblyFile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyFile);
        var assembly = Assembly.LoadFrom(path);
        var satellitePath = Path.Combine(AppContext.BaseDirectory, "en", Path.GetFileNameWithoutExtension(assemblyFile) + ".resources.dll");
        Assert.True(File.Exists(satellitePath), $"{assemblyFile} : ressources anglaises absentes ({satellitePath}).");
        var satellite = Assembly.LoadFrom(satellitePath);

        var result = new List<ResourceSet>();
        foreach (var neutralName in NeutralResourceNames(path))
        {
            var baseName = neutralName[..^".resources".Length];
            var englishName = baseName + ".en.resources";
            using var neutralStream = assembly.GetManifestResourceStream(neutralName)!;
            using var englishStream = satellite.GetManifestResourceStream(englishName);
            Assert.True(englishStream is not null, $"{assemblyFile} : ressource « {englishName} » absente de la satellite « en ».");
            result.Add(new ResourceSet(baseName, Read(neutralStream), Read(englishStream!)));
        }
        return result;
    }

    private static Dictionary<string, string?> Read(Stream stream)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var reader = new ResourceReader(stream);
        foreach (DictionaryEntry entry in reader)
            values[(string)entry.Key] = entry.Value as string;
        return values;
    }

    private static HashSet<int> Placeholders(string? text)
        => text is null ? [] : PlaceholderPattern().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
