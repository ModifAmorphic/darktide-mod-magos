namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for the minimal <c>libraryfolders.vdf</c> parser:
/// extracts library root paths from realistic Steam output, handles Windows
/// backslash escaping + whitespace, and degrades gracefully on empty input.
/// </summary>
public sealed class LibraryFoldersVdfTests
{
    [Fact]
    public void Parses_realistic_single_library_vdf()
    {
        var vdf = """
                  "libraryfolders"
                  {
                  	"0"
                  	{
                  		"path"		"/home/user/.local/share/Steam"
                  		"label"		""
                  		"contentid"		"1234567890123456789"
                  		"apps"
                  		{
                  			"1361210"		"12345678"
                  		}
                  	}
                  }
                  """;

        var libs = LibraryFoldersVdf.Parse(vdf);

        var lib = Assert.Single(libs);
        Assert.Equal("/home/user/.local/share/Steam", lib);
    }

    [Fact]
    public void Parses_multiple_libraries_in_document_order()
    {
        var vdf = """
                  "libraryfolders"
                  {
                  	"0"
                  	{
                  		"path"		"/home/user/.local/share/Steam"
                  	}
                  	"1"
                  	{
                  		"path"		"/mnt/games/steamlibrary"
                  	}
                  	"2"
                  	{
                  		"path"		"/media/external/SteamLibrary"
                  	}
                  }
                  """;

        var libs = LibraryFoldersVdf.Parse(vdf);

        Assert.Equal(3, libs.Count);
        Assert.Equal("/home/user/.local/share/Steam", libs[0]);
        Assert.Equal("/mnt/games/steamlibrary", libs[1]);
        Assert.Equal("/media/external/SteamLibrary", libs[2]);
    }

    [Fact]
    public void Unescapes_windows_backslash_paths()
    {
        // Windows Steam writes paths with VDF backslash escaping.
        var vdf = """
                  "libraryfolders"
                  {
                  	"0"
                  	{
                  		"path"		"C:\\Program Files (x86)\\Steam"
                  	}
                  }
                  """;

        var libs = LibraryFoldersVdf.Parse(vdf);

        var lib = Assert.Single(libs);
        Assert.Equal(@"C:\Program Files (x86)\Steam", lib);
    }

    [Fact]
    public void Handles_irregular_whitespace_between_key_and_value()
    {
        var vdf = """
                  "libraryfolders"
                  {
                  	"0"
                  	{
                  		"path"        "/home/user/Steam"
                  	}
                  }
                  """;

        var libs = LibraryFoldersVdf.Parse(vdf);

        var lib = Assert.Single(libs);
        Assert.Equal("/home/user/Steam", lib);
    }

    [Fact]
    public void Only_path_values_are_extracted_other_keys_ignored()
    {
        // "contentid" / app ids / numbered library keys must not leak in.
        var vdf = """
                  "libraryfolders"
                  {
                  	"0"
                  	{
                  		"path"		"/a/Steam"
                  		"contentid"		"999"
                  		"apps"
                  		{
                  			"1361210"		"1"
                  		}
                  	}
                  }
                  """;

        var libs = LibraryFoldersVdf.Parse(vdf);

        var lib = Assert.Single(libs);
        Assert.Equal("/a/Steam", lib);
    }

    [Fact]
    public void Empty_or_whitespace_input_yields_empty_list()
    {
        Assert.Empty(LibraryFoldersVdf.Parse(""));
        Assert.Empty(LibraryFoldersVdf.Parse("   \n\t  "));
    }

    [Fact]
    public void No_path_keys_yields_empty_list()
    {
        var vdf = """
                  "libraryfolders"
                  {
                  	"0"
                  	{
                  		"label"		""
                  	}
                  }
                  """;

        Assert.Empty(LibraryFoldersVdf.Parse(vdf));
    }
}
