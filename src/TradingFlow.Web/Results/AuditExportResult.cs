using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TradingFlow.Domain.Audit;
using TradingFlow.Web.Pages;

namespace TradingFlow.Web.Exports;

/// <summary>Full snapshot export with at most 500 records retained per read.</summary>
public sealed class AuditExportResult(IDecisionAuditRepository repository, string runName,
    DecisionAuditQuery query, bool json, Func<DecisionAuditRecord, AuditRecordView> project) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var token = context.HttpContext.RequestAborted;
        var name = String.Concat(runName.Where(c => Char.IsLetterOrDigit(c) || c is '-' or '_'));
        response.ContentType = json ? "application/json; charset=utf-8" : "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"audit-{(name.Length == 0 ? "run" : name)}.{(json ? "json" : "csv")}\"";
        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(false), 4096, leaveOpen: true);
        if (json) await writer.WriteAsync("[".AsMemory(), token);
        else await writer.WriteLineAsync("LocalTime,Ticker,Strategy,Decision,ReasonKey,Reason,Explanation".AsMemory(), token);
        var first = true;
        for (var offset = 0; ; offset += 500)
        {
            var page = await repository.QueryAsync(runName, query with { Skip = offset, Take = 500 }, token);
            foreach (var raw in page.Records)
            {
                if (json)
                {
                    if (!first) await writer.WriteAsync(",".AsMemory(), token);
                    await writer.WriteAsync(JsonSerializer.Serialize(raw).AsMemory(), token);
                    first = false;
                }
                else
                {
                    var record = project(raw);
                    var fields = new[] { record.LocalTime, record.Ticker, record.StrategyName, record.Decision,
                        record.ReasonKey, record.RawReason, record.Explanation };
                    await writer.WriteLineAsync(String.Join(",", fields.Select(Csv)).AsMemory(), token);
                }
            }
            await writer.FlushAsync(token);
            if (page.Records.Count < 500) break;
        }
        if (json) await writer.WriteAsync("]".AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private static string Csv(string value)
    {
        // Quoting alone does not prevent spreadsheet formula execution.
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
            value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
