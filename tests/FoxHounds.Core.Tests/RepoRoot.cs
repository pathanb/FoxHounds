namespace FoxHounds.Core.Tests;

/// <summary>Locates the repository root (the folder holding FoxHounds.sln) from the test binaries.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FoxHounds.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("FoxHounds.sln not found above " + AppContext.BaseDirectory);
    }
}
