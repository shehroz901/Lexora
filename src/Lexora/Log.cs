namespace Lexora;

/// <summary>Tiny rolling log at %AppData%\Lexora\log.txt for diagnosing problems.</summary>
static class Log
{
    static readonly object Gate = new();
    static string PathName => System.IO.Path.Combine(Settings.Folder, "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
