using Microsoft.Data.Sqlite;
using TradingFlow.Cli;

namespace TradingFlow.Tests;

public sealed class DataInspectionCommandTests
{
    /// <summary>Inspection never creates an absent database, even when a supported report was requested.</summary>
    [Fact]
    public void MissingDatabase_IsRejectedWithoutCreation()
    {
        var path = Path.Combine(Path.GetTempPath(), "tf-inspection-" + Guid.NewGuid().ToString("N") + ".db");
        Assert.Throws<FileNotFoundException>(() => DataInspectionCommand.Run(["news", path, "5"], TextWriter.Null));
        Assert.False(File.Exists(path));
    }

    /// <summary>The consolidated report reads only the requested rows and leaves the source bytes unchanged.</summary>
    [Fact]
    public void DatabaseAndCandles_RequireExplicitInputsAndRespectRowCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "fixture.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE AspNetUsers(UserName TEXT); INSERT INTO AspNetUsers VALUES ('second'), ('first');";
                command.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(database);
            var output = new StringWriter();
            DataInspectionCommand.Run(["users", database, "1"], output);
            Assert.Equal("first" + Environment.NewLine, output.ToString());
            Assert.Equal(before, File.ReadAllBytes(database));
            Assert.Throws<ArgumentException>(() => DataInspectionCommand.Run(["DELETE FROM AspNetUsers", database, "1"], TextWriter.Null));
            var candles = Path.Combine(root, "fixture.jsonl");
            File.WriteAllLines(candles, ["old", "latest"]);
            output.GetStringBuilder().Clear();
            DataInspectionCommand.Run(["candles", candles, "1"], output);
            Assert.Equal("latest" + Environment.NewLine, output.ToString());
        }
        finally
        {
            // Delete only this unique fixture's immediate files; never enumerate or remove operator directories.
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
