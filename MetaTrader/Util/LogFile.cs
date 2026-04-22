namespace MetaTrader.Util
{
    using System;
    using System.IO;
    using System.Threading.Tasks;

    public static class LogFile
    {
        private static readonly object _lock = new object();

        private static string LogDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Logs");

        private static string LogFilePath =>
            Path.Combine(LogDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.log");

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(LogDirectory))
                        Directory.CreateDirectory(LogDirectory);

                    File.AppendAllText(LogFilePath,
                        $"{DateTime.UtcNow:HH:mm:ss} | {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Do not crash the app if logging fails
            }
        }

        public static async Task WriteAsync(string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    Directory.CreateDirectory(LogDirectory);

                var logLine = $"{DateTime.UtcNow:HH:mm:ss} | {message}{Environment.NewLine}";

                await File.AppendAllTextAsync(LogFilePath, logLine);
            }
            catch
            {
                // ignore exceptions
            }
        }
    }

}
