using System.Text.Json;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Mobile.Pages;
namespace TradingFlow.Mobile.Services;
public sealed record PreparationLink(string Backend,Guid WorkflowId,Guid? OccurrenceId,Guid? RevisionId);
public sealed record PreparationLocalAlert(DateTimeOffset Timestamp,string Message,PreparationLink Link);
public static class PreparationNotifications
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static CancellationTokenSource? lifetime;
    private static bool opening;
    private static string HistoryKey=>"PreparationAlerts:"+AppServices.Api.BaseUrl+":"+AppServices.Api.CurrentUserName;
    public static PreparationLink? Pending {get;set;}
    public static PreparationLocalAlert[] History()
    {try{return JsonSerializer.Deserialize<PreparationLocalAlert[]>(Preferences.Get(HistoryKey,"[]"),Json)??[];}catch(JsonException){return [];}}
    public static void Record(PreparationLocalAlert alert)=>Preferences.Set(HistoryKey,JsonSerializer.Serialize(new[]{alert}.Concat(History()).DistinctBy(x=>(x.Link.Backend,x.Link.RevisionId)).Take(100),Json));
    public static void Start() {if(lifetime is not null)return;AppServices.Api.SessionChanged+=SessionChanged;lifetime=new();_ = PollAsync(lifetime.Token);}
    private static void SessionChanged(object? sender,EventArgs e){Stop();Start();}
    public static void Stop() {AppServices.Api.SessionChanged-=SessionChanged;lifetime?.Cancel();lifetime?.Dispose();lifetime=null;}
    private static async Task PollAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                var generation=AppServices.Api.SessionVersion;var backend=AppServices.Api.BaseUrl;var user=await AppServices.Api.GetAuthenticationStateAsync(token);
                if(user is not null)
                {
                    var key="PreparationCursor:"+backend+":"+user.UserName;var cursor=Preferences.Get(key,0L);
                    var events=await AppServices.Api.Workflow.PreparationAlertsAsync(cursor,token);
                    foreach(var item in events)
                    {
                        token.ThrowIfCancellationRequested();if(generation!=AppServices.Api.SessionVersion||backend!=AppServices.Api.BaseUrl||user.UserName!=AppServices.Api.CurrentUserName)break;
                        var target=JsonSerializer.Deserialize<PreparationAlertTarget>(item.PayloadJson,Json)!;
                        var link=new PreparationLink(backend,target.WorkflowId,target.OccurrenceId,target.RevisionId);
                        if(!History().Any(x=>x.Link==link)){Record(new(item.OccurredAtUtc,target.Readiness,link));LocalSignalNotificationService.ShowSignal("Preparation",target.Readiness,"Preparation is "+target.Readiness+". Open the exact review snapshot.",preparation:link);}
                        cursor=Math.Max(cursor,item.EventSequence);Preferences.Set(key,cursor);
                    }
                }
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){return;}
            catch(Exception){ /* Durable history and cursor are retried after reconnect. */ }
            try{await Task.Delay(TimeSpan.FromSeconds(30),token);}catch(OperationCanceledException){return;}
        }
    }
    public static async Task OpenPendingAsync()
    {
        if(opening||Pending is not {} link||Shell.Current is null)return;opening=true;
        try
        {
            if(link.Backend!=AppServices.Api.BaseUrl){await Shell.Current.DisplayAlertAsync("Preparation notification","Select the notification's backend before opening its evidence.","OK");return;}
            if(await AppServices.Api.GetAuthenticationStateAsync() is null){await Shell.Current.GoToAsync(nameof(LoginPage));return;}
            var query=new ShellNavigationQueryParameters {["workflow"]=link.WorkflowId.ToString()};if(link.OccurrenceId is {} occurrence)query["occurrence"]=occurrence.ToString();if(link.RevisionId is {} revision)query["revision"]=revision.ToString();
            await Shell.Current.GoToAsync(nameof(PreparationPage),query);if(Pending==link)Pending=null;
        }
        finally{opening=false;}
    }
}
