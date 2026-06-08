using System;
using System.IO;

namespace TradingFlow.Domain.Logging;

public static class SafeLogger
{
    private static readonly object _fileLock = new();

    /// <summary>
    /// Safely appends a serialized log line to a specific log file in a thread-safe manner.
    /// </summary>
    public static void LogToFile(string logFileName, string logLine)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFileName);
            lock (_fileLock)
            {
                File.AppendAllText(logPath, logLine + Environment.NewLine);
            }
        }
        catch
        {
            // Fail-safe to avoid disrupting flow if the logger/file system throws
        }
    }

    /// <summary>
    /// Executes a logging or metrics action safely, suppressing any exceptions to prevent disrupting the main application flow.
    /// This is also known as the Safe Logger pattern.
    /// </summary>
    public static void Execute(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Fail-safe to avoid disrupting flow if the logger/file system throws
        }
    }
}
