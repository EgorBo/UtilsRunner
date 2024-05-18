/// <summary>
/// Just some simple console logging
/// </summary>
public sealed class Logger
{
    private static readonly object Lock = new();
    private static readonly string LogFolder = Environment.GetEnvironmentVariable("EGORBOT_LOG_PATH") ?? "";

    private static string GetLogPath() => 
        Path.Combine(LogFolder, DateTime.UtcNow.Date.Date.ToString("yyyy_MM_dd") + ".log");

    public static void Debug(string? str)
    {
        if (str == null)
            return;

        lock (Lock)
        {
            Console.WriteLine(str);
            File.AppendAllText(GetLogPath(), $"[DEBUG] {str}\n");
        }
    }

    public static void Error(string? str)
    {
        if (str == null)
            return;

        lock (Lock)
        {
            var color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(str);
            File.AppendAllText(GetLogPath(), $"[ERROR] {str}\n");
            Console.ForegroundColor = color;
        }
    }

    public static void Info(string? str)
    {
        if (str == null)
            return;

        lock (Lock)
        {
            var color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(str);
            File.AppendAllText(GetLogPath(), $"[INFO] {str}\n");
            Console.ForegroundColor = color;
        }
    }
}
