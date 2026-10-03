using Android.App;
using Android.Content;
using TradingFlow.Mobile.Services;
using Android.Content.PM;
using Android.OS;

namespace TradingFlow.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ReadCandidateIntent(Intent);
    }
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ReadCandidateIntent(intent);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await CandidateNavigation.OpenPendingAsync(); await PreparationNotifications.OpenPendingAsync(); } catch { }
        });
    }
    private static void ReadCandidateIntent(Intent? intent)
    {
        if (Guid.TryParse(intent?.GetStringExtra("preparationWorkflow"), out var workflow) &&
            Guid.TryParse(intent?.GetStringExtra("preparationOccurrence"), out var occurrence) &&
            Guid.TryParse(intent?.GetStringExtra("preparationRevision"), out var revision))
            PreparationNotifications.Pending = new(intent.GetStringExtra("preparationBackend") ?? "", workflow, occurrence, revision);
        if (Guid.TryParse(intent?.GetStringExtra("candidateRunId"), out var run) &&
            Guid.TryParse(intent?.GetStringExtra("candidateId"), out var candidate))
            CandidateNavigation.Pending = new(run, candidate, intent.GetStringExtra("articleId"), intent.GetStringExtra("candidateBackend"));
    }
}
