namespace Kobian.Tests.Architecture;

internal static class RepoRoot
{
    /// <summary>Szuka katalogu z Kobian.sln, idąc w górę od katalogu testu.</summary>
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Nie znaleziono Kobian.sln nad " + AppContext.BaseDirectory);
    }
}
