using System;
using Microsoft.Data.Sqlite;

var connectionString = ""Data Source=..\tradingflow.db"";
using var connection = new SqliteConnection(connectionString);
connection.Open();
var command = connection.CreateCommand();
command.CommandText = ""SELECT UserName FROM AspNetUsers"";
using var reader = command.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine(reader.GetString(0));
}
