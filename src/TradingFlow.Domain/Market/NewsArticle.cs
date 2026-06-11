using System;
using System.Collections.Generic;

namespace TradingFlow.Domain.Market;

public sealed record NewsArticle(
    string Provider,
    string Id,
    string Headline,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Symbols,
    string? Summary = null,
    string? Content = null,
    string? Source = null,
    string? Url = null,
    DateTimeOffset? UpdatedAt = null);
