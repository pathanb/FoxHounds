namespace FoxHounds.TestSupport;

/// <summary>Locates the repository root (the folder holding FoxHounds.sln) from the test binaries.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "FoxHounds.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("FoxHounds.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Full path of a file given relative to the repository root.</summary>
    public static string File(string relativePath) => Path.Combine(Find(), relativePath);
}
