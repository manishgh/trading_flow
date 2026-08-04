using System;
using System.IO;
using System.Text.Json;
using System.Linq;

class Program
{
    static void Main()
    {
        // Try to read the 5m candles for PLTR from the local candle store
        var path = @"C:\project\trading_flow\data\candles\paper\earnings-monitor\alpaca\5m\PLTR\2026-08-03.jsonl";
        if (!File.Exists(path))
        {
            Console.WriteLine("File not found: " + path);
            return;
        }
        
        var lines = File.ReadAllLines(path);
        foreach (var line in lines.TakeLast(20))
        {
            Console.WriteLine(line);
        }
    }
}
