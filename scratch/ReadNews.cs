using System;
using Microsoft.Data.Sqlite;

var conn = new SqliteConnection("Data Source=C:\\project\\trading_flow\\tradingflow.db");
conn.Open();
var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT Ticker, Headline, Timestamp FROM NewsItems ORDER BY Timestamp DESC LIMIT 50";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader[0]} | {reader[1]} | {reader[2]}");
}
