namespace PosSync.UnitTests.Support;

internal static class RepoPaths
{
    /// <summary>The repository root (the directory holding PosSync.slnx), found by walking up from the test binaries.</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PosSync.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (PosSync.slnx) not found above " + AppContext.BaseDirectory);
    }
}
