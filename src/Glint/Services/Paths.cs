namespace Glint;

internal sealed record AppDirectories(string Roaming, string LocalData)
{
    internal static AppDirectories Resolve(string applicationDirectory, string roaming, string local)
    {
        string data = Path.Combine(applicationDirectory, "Data");
        return File.Exists(Path.Combine(data, "portable.flag"))
            ? new(data, data)
            : new(Path.Combine(roaming, "Glint"), Path.Combine(local, "Glint"));
    }
}

public static class Paths
{
    public static readonly string ApplicationDirectory =
        Environment.ProcessPath is { } process &&
        !Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(process)!
            : AppContext.BaseDirectory;

    static readonly AppDirectories Directories = AppDirectories.Resolve(ApplicationDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static readonly string Roaming = Ensure(Directories.Roaming);
    public static readonly string LocalData = Ensure(Directories.LocalData);
    public static readonly string ImageCache = Ensure(Path.Combine(LocalData, "cache", "images"));

    static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
