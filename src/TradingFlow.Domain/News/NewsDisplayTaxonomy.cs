using System.Linq.Expressions;

namespace TradingFlow.Domain.News;

public sealed record NewsDisplayOption(string Key, string Label);
public sealed record NewsDisplayTopics(IReadOnlyList<string> Categories, IReadOnlyList<string> Sectors,
    string Rationale, string Version);

/// <summary>
/// Query-time, multi-label reading topics. These rules make no stock-impact, historical
/// sector-membership or trading-catalyst claim and never change NewsEvidenceClassification.
/// The same expression trees are used by database filters and displayed metadata.
/// </summary>
public static class NewsDisplayTaxonomy
{
    public const string Version = "display-topics-v1";
    private sealed record Rule(NewsDisplayOption Option, string[] Terms,
        Expression<Func<PersistedNewsItem, bool>> Predicate);

    private static readonly Rule[] SectorRules = [
        Topic("technology", "Technology", "technology", "software", "semiconductor", "semiconductors", "chip", "chips", "artificial intelligence", "cloud computing", "cybersecurity"),
        Topic("healthcare", "Healthcare", "healthcare", "health care", "pharma", "pharmaceutical", "biotech", "biotechnology", "drug", "drugs", "fda", "clinical trial"),
        Topic("financials", "Financials", "bank", "banks", "banking", "insurance", "insurer", "insurers", "asset management", "financial services"),
        Topic("energy", "Energy", "oil", "gas", "energy", "petroleum", "opec", "crude", "refinery"),
        Topic("industrials", "Industrials", "industrial", "industrials", "manufacturing", "aerospace", "defense", "defence", "airline", "airlines", "railroad", "shipping"),
        Topic("consumer-discretionary", "Consumer discretionary", "retail", "retailer", "retailers", "automotive", "automaker", "automakers", "vehicle", "vehicles", "restaurant", "restaurants", "travel"),
        Topic("consumer-staples", "Consumer staples", "consumer staples", "grocery", "groceries", "beverage", "beverages", "food", "tobacco", "household products"),
        Topic("utilities", "Utilities", "utilities", "utility", "electricity", "power grid", "water utility"),
        Topic("real-estate", "Real estate", "real estate", "reit", "reits", "housing", "property", "mortgage", "mortgages"),
        Topic("materials", "Materials", "materials", "mining", "miner", "miners", "steel", "copper", "aluminum", "aluminium", "chemical", "chemicals", "gold"),
        Topic("communication-services", "Communication services", "telecom", "telecommunications", "wireless", "media", "streaming", "advertising", "social media")
    ];
    private static readonly Rule[] CategoryRules = [
        WithMetadata(Topic("company", "Company", "earnings", "revenue", "dividend", "merger", "acquisition", "buyback", "ipo"),
            x => ((x.Ticker ?? "").ToUpper() != "MARKET" && x.Ticker != null && x.Ticker != "") || (x.Provider ?? "").ToLower() == "sec_edgar"),
        new(new("sector", "Sector"), [], Or(SectorRules.Select(x => x.Predicate))),
        WithMetadata(Topic("financial", "Financial / macro", "inflation", "cpi", "ppi", "jobs", "payroll", "payrolls", "unemployment", "gdp", "yield", "yields", "treasury", "treasuries", "interest rate", "interest rates", "currency", "currencies", "dollar", "recession", "economy", "economic", "central bank", "central banks", "monetary policy", "federal reserve", "fed", "earnings", "revenue", "profit", "profits", "debt", "credit", "bond", "bonds"),
            x => (x.Provider ?? "").ToLower() == "federal_reserve"),
        WithMetadata(Topic("policy", "Government / policy", "government", "policy", "regulation", "regulations", "regulatory", "legislation", "congress", "senate", "tariff", "tariffs", "sanction", "sanctions", "tax", "taxes", "fiscal", "federal reserve", "central bank", "export control", "export controls", "antitrust"),
            x => (x.Provider ?? "").ToLower() == "federal_reserve" || (x.Provider ?? "").ToLower() == "us_government"),
        WithMetadata(Topic("global", "Global events", "war", "conflict", "conflicts", "geopolitical", "geopolitics", "military", "missile", "missiles", "invasion", "ceasefire", "nato", "sanction", "sanctions", "diplomatic", "diplomacy", "iran", "ukraine", "russia", "israel", "gaza", "taiwan", "middle east", "red sea"),
            x => (x.Provider ?? "").ToLower() == "un_news")
    ];
    private static readonly Expression<Func<PersistedNewsItem, bool>> MarketPredicate = Not(Or(CategoryRules.Select(x => x.Predicate)));
    public static IReadOnlyList<NewsDisplayOption> Categories { get; } =
        new[] { new NewsDisplayOption("all", "All news") }.Concat(CategoryRules.Select(x => x.Option)).Append(new("market", "Other market")).ToArray();
    public static IReadOnlyList<NewsDisplayOption> Sectors { get; } = SectorRules.Select(x => x.Option).ToArray();

    public static Expression<Func<PersistedNewsItem, bool>> CategoryPredicate(string key) => key switch {
        "all" or "" => x => true,
        "market" => MarketPredicate,
        _ => CategoryRules.Single(x => x.Option.Key == key).Predicate
    };
    public static Expression<Func<PersistedNewsItem, bool>> SectorPredicate(string key) =>
        SectorRules.Single(x => x.Option.Key == key).Predicate;

    private static readonly Func<PersistedNewsItem, bool>[] CategoryMatchers = CategoryRules.Select(x => x.Predicate.Compile()).ToArray();
    private static readonly Func<PersistedNewsItem, bool>[] SectorMatchers = SectorRules.Select(x => x.Predicate.Compile()).ToArray();
    public static NewsDisplayTopics Describe(PersistedNewsItem item)
    {
        var categories = CategoryRules.Where((_, index) => CategoryMatchers[index](item)).Select(x => x.Option.Key).ToArray();
        var sectors = SectorRules.Where((_, index) => SectorMatchers[index](item)).Select(x => x.Option.Key).ToArray();
        return new(categories.Length == 0 ? ["market"] : categories, sectors,
            "Inferred reading topics from whole-word headline/summary rules and provider/ticker metadata; sector labels describe topics, not company membership or stock impact.", Version);
    }

    private static Rule Topic(string key, string label, params string[] terms)
    {
        // Padding plus punctuation-to-space normalization prevents 'war' matching 'awards'.
        // Keep this SQL-translatable; no Regex, Invoke, or client-side row filtering.
        Expression<Func<PersistedNewsItem, string>> text = x => " " + ((x.Headline ?? "") + " " + (x.Summary ?? "")).ToLower()
            .Replace(".", " ").Replace(",", " ").Replace(":", " ").Replace(";", " ").Replace("!", " ").Replace("?", " ")
            .Replace("(", " ").Replace(")", " ").Replace("[", " ").Replace("]", " ").Replace("{", " ").Replace("}", " ")
            .Replace("-", " ").Replace("/", " ").Replace("\\", " ").Replace("\"", " ").Replace("'", " ")
            .Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Replace("–", " ").Replace("—", " ") + " ";
        var matches = terms.Select(term => (Expression)Expression.Call(text.Body, nameof(string.Contains), Type.EmptyTypes,
            Expression.Constant(" " + term + " "))).ToArray();
        return new(new(key, label), terms, Expression.Lambda<Func<PersistedNewsItem, bool>>(BalancedOr(matches), text.Parameters));
    }
    private static Rule WithMetadata(Rule rule, Expression<Func<PersistedNewsItem, bool>> metadata) =>
        rule with { Predicate = Or([rule.Predicate, metadata]) };
    private static Expression<Func<PersistedNewsItem, bool>> Or(IEnumerable<Expression<Func<PersistedNewsItem, bool>>> predicates)
    {
        var parameter = Expression.Parameter(typeof(PersistedNewsItem), "item");
        var expressions = predicates.Select(x => new ParameterSubstitution(x.Parameters[0], parameter).Visit(x.Body)!).ToArray();
        return Expression.Lambda<Func<PersistedNewsItem, bool>>(BalancedOr(expressions), parameter);
    }
    private static Expression BalancedOr(IReadOnlyList<Expression> expressions) => expressions.Count switch {
        0 => Expression.Constant(false), 1 => expressions[0],
        _ => Expression.OrElse(BalancedOr(expressions.Take(expressions.Count / 2).ToArray()), BalancedOr(expressions.Skip(expressions.Count / 2).ToArray()))
    };
    private static Expression<Func<PersistedNewsItem, bool>> Not(Expression<Func<PersistedNewsItem, bool>> expression) =>
        Expression.Lambda<Func<PersistedNewsItem, bool>>(Expression.Not(expression.Body), expression.Parameters);
    private sealed class ParameterSubstitution(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : base.VisitParameter(node);
    }
}
