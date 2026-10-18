namespace Glint;

// Tiny append-only log in %LOCALAPPDATA%\Glint\glint.log.
public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(Paths.LocalData, "glint.log");

    static Log()
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000)
                File.Delete(FilePath);
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
    }
}

public static class Paths
{
    public static readonly string Roaming = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glint"));

    public static readonly string LocalData = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint"));

    public static readonly string ImageCache = Ensure(Path.Combine(LocalData, "cache", "images"));

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
