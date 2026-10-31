using Microsoft.Data.Sqlite;

namespace TradingFlow.Cli;

/// <summary>Read-only replacement for the machine-specific database and candle scratch programs.</summary>
public static class DataInspectionCommand
{
    public const string Name = "inspect-data";

    /// <summary>Reads an explicitly named existing input; accepts fixed reports rather than arbitrary SQL or implicit database creation.</summary>
    public static void Run(string[] args, TextWriter output)
    {
        if (args.Length != 3 || !Int32.TryParse(args[2], out var count) || count <= 0)
            throw new ArgumentException("Usage: inspect-data <news|users|integrity|candles> <existing-file> <positive-row-count>.");
        var report = args[0];
        if (report is not ("news" or "users" or "integrity" or "candles"))
            throw new ArgumentException("Report must be news, users, integrity or candles.");
        var path = Path.GetFullPath(args[1]);
        if (!File.Exists(path)) throw new FileNotFoundException("Inspection requires an existing input; no database is created.", path);
        if (report == "candles")
        {
            foreach (var line in File.ReadLines(path).TakeLast(count)) output.WriteLine(line);
            return;
        }
        ReadDatabase(path, report, count, output);
    }

    /// <summary>Opens the supplied SQLite file in read-only mode and bounds the selected diagnostic report.</summary>
    private static void ReadDatabase(string path, string report, int count, TextWriter output)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = report switch
        {
            "news" => "SELECT Ticker, Headline, Timestamp FROM NewsItems ORDER BY Timestamp DESC LIMIT $count;",
            "users" => "SELECT UserName FROM AspNetUsers ORDER BY UserName LIMIT $count;",
            _ => "PRAGMA integrity_check;"
        };
        if (report != "integrity") command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        var emitted = 0;
        while (emitted < count && reader.Read())
        {
            output.WriteLine(String.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
            emitted++;
        }
    }
}
