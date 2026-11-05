using System.Xml.Linq;

namespace FoxHounds.Core.Tests.Architecture;

/// <summary>T-ARCH-01: every project reference must be allowed by spec 4.2 as amended by decision D-13.</summary>
public sealed class ProjectReferenceTests
{
    // Keep in sync with docs/DECISIONS.md, D-13. A project missing from this table fails the test,
    // so a new project cannot slip in without its references being decided first.
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["FoxHounds.Core"] = [],
        ["FoxHounds.Engine"] = ["FoxHounds.Core"],
        ["FoxHounds.Solver"] = ["FoxHounds.Core"],
        ["FoxHounds.Agents"] = ["FoxHounds.Core", "FoxHounds.Engine", "FoxHounds.Solver"],
        ["FoxHounds.Sim"] = ["FoxHounds.Core", "FoxHounds.Engine", "FoxHounds.Solver", "FoxHounds.Agents"],
        ["FoxHounds.Tuning"] = ["FoxHounds.Core", "FoxHounds.Engine", "FoxHounds.Solver", "FoxHounds.Agents", "FoxHounds.Sim"],
        ["FoxHounds.Cli"] = ["FoxHounds.Core", "FoxHounds.Engine", "FoxHounds.Solver", "FoxHounds.Agents", "FoxHounds.Sim", "FoxHounds.Tuning"],
        ["FoxHounds.App"] = ["FoxHounds.Core", "FoxHounds.Engine", "FoxHounds.Solver", "FoxHounds.Agents"],
        ["FoxHounds.Bench"] = ["FoxHounds.Core", "FoxHounds.Engine", "FoxHounds.Solver"],
        ["FoxHounds.Core.Tests"] = ["FoxHounds.Core"],
        ["FoxHounds.Engine.Tests"] = ["FoxHounds.Engine", "FoxHounds.Core", "FoxHounds.Solver", "FoxHounds.Agents"],
        ["FoxHounds.Solver.Tests"] = ["FoxHounds.Solver", "FoxHounds.Core"],
        ["FoxHounds.Agents.Tests"] = ["FoxHounds.Agents", "FoxHounds.Core"],
        ["FoxHounds.Sim.Tests"] = ["FoxHounds.Sim", "FoxHounds.Core"],
        ["FoxHounds.Tuning.Tests"] = ["FoxHounds.Tuning", "FoxHounds.Core"],
        ["FoxHounds.App.Tests"] = ["FoxHounds.App", "FoxHounds.Core"],
    };

    [Fact]
    public void Every_project_reference_is_allowed()
    {
        var violations = new List<string>();
        var projects = FindProjects();
        Assert.NotEmpty(projects);

        foreach (var (name, path) in projects)
        {
            if (!Allowed.TryGetValue(name, out var allowed))
            {
                violations.Add($"{name}: not in the allowed-reference table (add it to D-13 first)");
                continue;
            }

            foreach (var reference in ReadProjectReferences(path))
            {
                if (Array.IndexOf(allowed, reference) < 0)
                {
                    violations.Add($"{name} -> {reference}: not allowed");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Every_project_in_the_table_exists()
    {
        var names = FindProjects().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(Allowed.Keys, name => Assert.Contains(name, names));
    }

    private static List<(string Name, string Path)> FindProjects()
    {
        string root = RepoRoot.Find();
        return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(root, p))
            .Select(p => (Path.GetFileNameWithoutExtension(p), p))
            .OrderBy(p => p.Item1, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or ".git");
    }

    private static IEnumerable<string> ReadProjectReferences(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        foreach (var element in doc.Descendants("ProjectReference"))
        {
            string include = (string?)element.Attribute("Include") ?? "";
            yield return Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
        }
    }
}
