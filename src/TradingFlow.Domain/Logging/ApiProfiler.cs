using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingFlow.Domain.Logging;

public static class ApiProfiler
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<RequestMetric>> _metricsByService = new(StringComparer.OrdinalIgnoreCase);
    public static readonly DateTimeOffset AppStartTime = DateTimeOffset.UtcNow;
    private static Action<RequestMetric>? _metricSink;

    public sealed record RequestMetric(
        string Service,
        string Endpoint,
        string Method,
        double DurationMs,
        bool IsSuccess,
        string? ErrorMessage,
        DateTimeOffset Timestamp);

    public static void RecordRequest(string service, string endpoint, string method, double durationMs, bool isSuccess, string? errorMessage = null)
    {
        var metric = new RequestMetric(service, endpoint, method, durationMs, isSuccess, errorMessage, DateTimeOffset.UtcNow);
        
        var queue = _metricsByService.GetOrAdd(service, _ => new ConcurrentQueue<RequestMetric>());
        queue.Enqueue(metric);
        while (queue.Count > 100)
        {
            queue.TryDequeue(out _);
        }

        _metricSink?.Invoke(metric);
    }

    public static void ConfigureMetricSink(Action<RequestMetric>? metricSink)
    {
        _metricSink = metricSink;
    }

    public static async Task<T> ProfileAsync<T>(string service, string endpoint, string method, Func<Task<T>> apiCall)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool isSuccess = false;
        string? errorMessage = null;
        try
        {
            var result = await apiCall();
            isSuccess = true;
            return result;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            RecordRequest(service, endpoint, method, stopwatch.Elapsed.TotalMilliseconds, isSuccess, errorMessage);
        }
    }

    public static async Task ProfileAsync(string service, string endpoint, string method, Func<Task> apiCall)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool isSuccess = false;
        string? errorMessage = null;
        try
        {
            await apiCall();
            isSuccess = true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            RecordRequest(service, endpoint, method, stopwatch.Elapsed.TotalMilliseconds, isSuccess, errorMessage);
        }
    }

    public static ProfilerSummary GetSummary(string service)
    {
        var profilerName = $"{service} API Profiler";
        if (!_metricsByService.TryGetValue(service, out var queue))
        {
            return new ProfilerSummary(profilerName, AppStartTime, 0, 0, 0, 0, 0, Array.Empty<EndpointSummary>());
        }

        var list = queue.ToList();
        if (list.Count == 0) return new ProfilerSummary(profilerName, AppStartTime, 0, 0, 0, 0, 0, Array.Empty<EndpointSummary>());

        var avg = list.Average(x => x.DurationMs);
        var min = list.Min(x => x.DurationMs);
        var max = list.Max(x => x.DurationMs);
        var successRate = list.Count(x => x.IsSuccess) * 100.0 / list.Count;

        var endpointGroups = list.GroupBy(x => $"{x.Method} {x.Endpoint}")
            .Select(g => new EndpointSummary(
                g.Key,
                g.Count(),
                g.Average(x => x.DurationMs),
                g.Min(x => x.DurationMs),
                g.Max(x => x.DurationMs),
                g.Count(x => x.IsSuccess) * 100.0 / g.Count()
            ))
            .ToArray();

        return new ProfilerSummary(profilerName, AppStartTime, list.Count, avg, min, max, successRate, endpointGroups);
    }
}

public sealed record ProfilerSummary(
    string Name,
    DateTimeOffset AppStartTime,
    int TotalRequests,
    double AvgDurationMs,
    double MinDurationMs,
    double MaxDurationMs,
    double SuccessRate,
    IReadOnlyList<EndpointSummary> Endpoints);

public sealed record EndpointSummary(
    string Route,
    int Count,
    double AvgDurationMs,
    double MinDurationMs,
    double MaxDurationMs,
    double SuccessRate);
