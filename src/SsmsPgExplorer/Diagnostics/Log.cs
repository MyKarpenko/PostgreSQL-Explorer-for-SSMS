using System;
using System.IO;

namespace SsmsPgExplorer.Diagnostics
{
    /// <summary>
    /// Append-only diagnostic log at %APPDATA%\SsmsPgExplorer\log.txt (rolled at 1 MB).
    /// Never logs passwords, tokens or query results.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SsmsPgExplorer", "log.txt");

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{System.Diagnostics.Process.GetCurrentProcess().Id}] {level} {message}{Environment.NewLine}");
                }
            }
            catch { /* diagnostics must never break the extension */ }
        }
    }
}
