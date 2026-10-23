using System;
using System.IO;

namespace VideoConverterSetup;

/// <summary>Plain text log in %TEMP%\VideoConverterSetup.log, rewritten on every run (for support).</summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "VideoConverterSetup.log");
    private static readonly object Gate = new();

    public static void Start(string args)
    {
        try { File.WriteAllText(FilePath, ""); } catch { }
        Write($"VideoConverter Setup {typeof(Log).Assembly.GetName().Version} on {Environment.OSVersion} (64-bit: {Environment.Is64BitOperatingSystem}), args: {args}");
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}"); } catch { }
        }
    }
}
