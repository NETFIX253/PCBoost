using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Detection.Scanners;

namespace PCBoost.Gaming.Tests;

public sealed class VdfParserTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"123456789"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"123"
        			"730"		"456"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"Jeux \"rapides\""
        		"apps"
        		{
        			"1245620"		"789"
        		}
        	}
        	"2"
        	{
        		"path"		"E:\\Games\\Steam Lib"
        	}
        }
        """;

    [Fact]
    public void Parses_multiple_libraries_with_escaped_paths()
    {
        var folders = SteamLibraryScanner.ParseLibraryFolders(LibraryFolders);

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\Games\Steam Lib"], folders);
    }

    [Fact]
    public void Unescapes_quotes_and_keeps_nested_blocks()
    {
        var root = VdfParser.Parse(LibraryFolders);
        var second = root["libraryfolders"]!["1"]!;

        Assert.Equal("Jeux \"rapides\"", second.GetValue("label"));
        Assert.True(second["apps"]!.IsBlock);
        Assert.Equal("789", second["apps"]!.GetValue("1245620"));
    }

    [Fact]
    public void Parses_legacy_library_format()
    {
        const string legacy = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1690000000"
            	"ContentStatsID"		"-123"
            	"1"		"D:\\SteamLibrary"
            	"2"		"F:\\Steam"
            }
            """;

        Assert.Equal([@"D:\SteamLibrary", @"F:\Steam"], SteamLibraryScanner.ParseLibraryFolders(legacy));
    }

    [Fact]
    public void Parses_app_manifest_case_insensitively()
    {
        const string acf = """
            "AppState"
            {
            	"appid"		"1245620"
            	"Universe"		"1"
            	"name"		"ELDEN RING"
            	"StateFlags"		"4"
            	"installdir"		"ELDEN RING"
            	"UserConfig" { "language" "french" }
            }
            """;

        var app = VdfParser.Parse(acf)["appstate"]!;

        Assert.Equal("1245620", app.GetValue("AppID"));
        Assert.Equal("ELDEN RING", app.GetValue("Name"));
        Assert.Equal("french", app["userconfig"]!.GetValue("language"));
    }

    [Fact]
    public void Tolerates_comments_conditionals_bare_tokens_and_truncation()
    {
        const string text = """
            // commentaire en tête
            "Root"
            {
            	key	bare_value
            	"cond"	"win"	[$WIN32]
            	"other"	[$X360] "value"
            	"unterminated"
            	{
            		"inner"	"x"
            """;

        var root = VdfParser.Parse(text)["Root"]!;

        Assert.Equal("bare_value", root.GetValue("key"));
        Assert.Equal("win", root.GetValue("cond"));
        Assert.Equal("value", root.GetValue("other"));
        Assert.Equal("x", root["unterminated"]!.GetValue("inner"));
    }

    [Fact]
    public void Keeps_unknown_escape_sequences_and_handles_empty_input()
    {
        Assert.Equal(@"D:\Games\Steam", VdfParser.Parse("\"p\" \"D:\\Games\\Steam\"").GetValue("p"));
        Assert.Empty(VdfParser.Parse(null).Children);
        Assert.Empty(VdfParser.Parse("}}}").Children);
    }
}
