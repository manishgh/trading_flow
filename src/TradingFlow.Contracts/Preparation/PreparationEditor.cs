namespace TradingFlow.Contracts.Preparation;
public sealed record PreparationField(string Key,string Label,string Kind);
public static class PreparationEditor
{
    public static PreparationField[] Fields { get; } = [
        new("nightlyAfterCloseMinutes","Nightly: minutes after close","integer"),
        new("morningStartBeforeOpenMinutes","Morning: minutes before open","integer"),
        new("publicationBeforeOpenMinutes","Stop publication: minutes before open","integer"),
        new("handoffBeforeOpenMinutes","Handoff expires: minutes before open","integer"),
        new("refreshMinutes","Morning refresh interval (minutes)","integer"),
        new("catchUpMinutes","Missed slot catch-up window (minutes; 0 skips)","integer"),
        new("maximumSourceAgeMinutes","Maximum source age (minutes; provider expiry still applies)","integer"),
        new("snapshotLifetimeMinutes","Maximum snapshot lifetime (minutes)","integer"),
        new("maximumSymbols","Symbol resource limit (1–500)","integer"),
        new("parallelSymbols","Parallel symbol collections (1–4)","integer"),
        new("historySessions","Required completed daily sessions (20–252)","integer"),
        new("maximumAttempts","Collection attempts (1–5)","integer"),
        new("retrySeconds","Retry delay (seconds)","integer"),
        new("collectionTimeoutSeconds","Collection timeout (seconds)","integer"),
        new("freshDiscoveries","Include fresh morning discoveries","boolean"),
        new("standaloneMorning","Allow morning collection without nightly preparation","boolean"),
        new("notifyReady","Notify when ready","boolean"),
        new("notifyBlocked","Notify when blocked","boolean"),
        new("notifyFailed","Notify on failure","boolean"),
        new("filters.minimumPrice","Minimum close price (USD; blank disables)","optional"),
        new("filters.minimumAverageShares","Minimum 20-session average shares (blank disables)","optional"),
        new("filters.minimumAverageDollarVolume","Minimum 20-session average dollar volume (blank disables)","optional"),
        new("filters.trendAverageDays","Require close above simple average (sessions; blank disables)","optionalInteger"),
        new("filters.maximumRangePercent","Maximum daily high-low / close (%) (blank disables)","optional"),
        new("gapAnnotationPercent","Annotate absolute gap above (%) (blank disables)","optional"),
        new("relativeVolumeAnnotation","Collect and annotate same-time RVOL above (ratio; blank disables)","optional"),
        new("earningsWithinDays","Earnings context window (days; blank uses 7)","optionalInteger")
    ];
}
