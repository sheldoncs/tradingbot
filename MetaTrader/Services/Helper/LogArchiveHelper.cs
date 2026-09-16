namespace MetaTrader.Services.Helper
{
    using System;
    using System.IO;

    using System;
    using System.IO;

    public static class LogArchiveHelper
    {
        public static void ArchiveLogsOnStartup()
        {
            try
            {
                string appRoot = AppDomain.CurrentDomain.BaseDirectory;
                string logsDir = Path.Combine(appRoot, "logs");

                if (!Directory.Exists(logsDir))
                    Directory.CreateDirectory(logsDir);

                string archiveDir = Path.Combine(logsDir, "archive");

                if (!Directory.Exists(archiveDir))
                    Directory.CreateDirectory(archiveDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                foreach (string logFile in Directory.GetFiles(logsDir, "*.log"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(logFile);
                    string extension = Path.GetExtension(logFile);

                    string archivedFile = Path.Combine(
                        archiveDir,
                        $"{fileName}_{timestamp}{extension}"
                    );

                    File.Move(logFile, archivedFile);

                    File.WriteAllText(
                        logFile,
                        $"New log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
                    );
                }

                Console.WriteLine($"Logs archived successfully: {archiveDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Log archive failed: {ex.Message}");
            }
        }
    }
}
