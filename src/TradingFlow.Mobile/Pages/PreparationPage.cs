using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Contracts.V1;
using TradingFlow.Mobile.Services;
namespace TradingFlow.Mobile.Pages;

public sealed class PreparationPage : ContentPage,IQueryAttributable
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly VerticalStackLayout content=new() {Padding=16,Spacing=12};
    private readonly Label status=new() {Text="Preparation is discovery only. Research and paper actions remain separate."};
    private CancellationTokenSource? lifetime;
    private Guid? requestedWorkflow,requestedOccurrence,requestedRevision;
    private PreparationRunRequest? pendingRun;
    private PreparationHandoffRequest? pendingHandoff;
    private CreateCandidateRunRequest? pendingCandidate;
    private PreparationCurationRequest? pendingCuration;
    public PreparationPage() {
        Title="Daily review";Content=new ScrollView {Content=content};content.Add(status);content.Add(Button("Retry connection",ListAsync));
        var menu=new ToolbarItem {Text="Workspace"};menu.Clicked+=async(_,_)=>{
            var choice=await DisplayActionSheetAsync("Workspace", "Cancel", null, "Candidates", "Sources", "Activity", "News", "Earnings");
            var route=choice switch {"Candidates"=>nameof(CandidatesPage),"Sources"=>nameof(WishlistsPage),"Activity"=>nameof(NotificationsPage),"News"=>nameof(NewsPage),"Earnings"=>nameof(EarningsPage),_=>null};
            if(route is not null)await Shell.Current.GoToAsync(route);
        };ToolbarItems.Add(menu);
    }
    public void ApplyQueryAttributes(IDictionary<string,object> query)
    {
        if(query.TryGetValue("workflow",out var w)&&Guid.TryParse(w.ToString(),out var id))requestedWorkflow=id;
        if(query.TryGetValue("occurrence",out var o)&&Guid.TryParse(o.ToString(),out var occurrence))requestedOccurrence=occurrence;
        if(query.TryGetValue("revision",out var r)&&Guid.TryParse(r.ToString(),out var revision))requestedRevision=revision;
    }
    protected override async void OnAppearing()
    {base.OnAppearing();lifetime?.Cancel();lifetime=new();AppServices.Api.SessionChanged+=SessionChanged;await Safe(ListAsync);}
    protected override void OnDisappearing() {AppServices.Api.SessionChanged-=SessionChanged;lifetime?.Cancel();base.OnDisappearing();}
    private void SessionChanged(object? sender,EventArgs e) {lifetime?.Cancel();pendingRun=null;pendingHandoff=null;content.Clear();content.Add(status);status.Text="Connection changed. Sign in and reopen preparation.";}
    private CancellationToken Token=>lifetime?.Token ?? new CancellationToken(true);
    private async Task Safe(Func<Task> action)
    {
        try {await action();}
        catch(OperationCanceledException) {}
        catch(HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.Unauthorized)
        {status.Text="Sign in to review preparation.";if(requestedWorkflow is {} id)PreparationNotifications.Pending=new(AppServices.Api.BaseUrl,id,requestedOccurrence,requestedRevision);await Shell.Current.GoToAsync(nameof(LoginPage));}
        catch(Exception ex) {status.Text=ex.Message;await DisplayAlertAsync("Preparation",ex.Message,"OK");}
    }
    private Button Button(string title,Func<Task> action) {var b=new Button {Text=title};b.Clicked+=async(_,_)=>await Safe(action);return b;}
    private static Label Label(string text)=>new(){Text=text,LineBreakMode=LineBreakMode.WordWrap};
    private static string When(DateTimeOffset value)=>TimeZoneInfo.ConvertTimeBySystemTimeZoneId(value,"America/New_York").ToString("g")+" New York · "+value.ToLocalTime().ToString("g")+" local · "+value.ToString("O");
    private void Reset() {content.Clear();content.Add(status);}
    private async Task ListAsync()
    {
        var rows=await AppServices.Api.Workflow.PreparationAsync(Token);Token.ThrowIfCancellationRequested();Reset();status.Text="Preparation is discovery only. Research and paper actions remain separate.";
        content.Add(Button("New workflow",()=>EditAsync(null)));content.Add(Button("Refresh workflows",ListAsync));
        foreach(var row in rows)content.Add(Button(row.Settings.Name+(row.Settings.Enabled?" · enabled":" · paused"),()=>OpenAsync(row.WorkflowId)));
        content.Add(Label("Preparation alerts"));
        foreach(var alert in PreparationNotifications.History().Where(x=>x.Link.Backend==AppServices.Api.BaseUrl))
            content.Add(Button(alert.Timestamp.ToLocalTime().ToString("g")+" · "+alert.Message,async()=>{PreparationNotifications.Pending=alert.Link;await PreparationNotifications.OpenPendingAsync();}));
        if(requestedWorkflow is {} id){await OpenAsync(id);requestedWorkflow=null;}
    }
    private async Task EditAsync(PreparationDefinition? definition)
    {
        var user=await AppServices.Api.GetAuthenticationStateAsync(Token);
        var backend=AppServices.Api.BaseUrl;var key="PreparationDraft:"+backend+":"+user?.UserName+":"+(definition?.WorkflowId.ToString()??"new");
        JsonNode node=JsonSerializer.SerializeToNode(definition?.Settings??new PreparationSettings(),Json)!;
        try {var draft=Preferences.Get(key,"");if(draft.Length>0)node=JsonNode.Parse(draft)!;}catch(JsonException){}
        var body=new VerticalStackLayout {Padding=16,Spacing=10};var message=Label("New workflows start disabled. Review upcoming exchange sessions before enabling.");body.Add(message);
        void Retain()=>Preferences.Set(key,node.ToJsonString());
        var name=new Entry {Text=node["name"]?.GetValue<string>(),Placeholder="Workflow name"};name.TextChanged+=(_,_)=>{node["name"]=name.Text;Retain();};body.Add(Label("Name"));body.Add(name);
        var merge=new Picker {Title="Membership",ItemsSource=new[]{"union","intersection"},SelectedItem=node["merge"]?.GetValue<string>()};merge.SelectedIndexChanged+=(_,_)=>{node["merge"]=merge.SelectedItem?.ToString();Retain();};body.Add(merge);
        var sourceCards=new VerticalStackLayout();body.Add(Label("Discovery sources"));body.Add(sourceCards);
        void Sources()
        {
            sourceCards.Clear();var array=node["sources"]!.AsArray();foreach(var source in array.ToArray())
            {var line=new VerticalStackLayout();line.Add(Label(source!["kind"]+" · "+source["key"]));var required=new Switch {IsToggled=source["required"]?.GetValue<bool>()??true};required.Toggled+=(_,_)=>{source["required"]=required.IsToggled;Retain();};line.Add(Label("Required source"));line.Add(required);line.Add(Button("Remove source",()=>{array.Remove(source);Retain();Sources();return Task.CompletedTask;}));sourceCards.Add(line);}
        }
        Sources();body.Add(Button("Add wishlist",async()=>{var lists=await AppServices.Api.GetWishlistsAsync();var names=lists?.Select(x=>x.Name+" · "+x.Id.ToString()[..8]).ToArray()??[];
            var choice=await DisplayActionSheetAsync("Wishlist","Cancel",null,names);var index=Array.IndexOf(names,choice);if(index<0)return;var list=lists!.ElementAt(index);node["sources"]!.AsArray().Add(JsonSerializer.SerializeToNode(new PreparationSource("wishlist",list.Id.ToString(),true,list.Id),Json));Retain();Sources();}));
        body.Add(Button("Add Finviz query",async()=>{var query=await DisplayPromptAsync("Finviz discovery","Saved query","Add","Cancel");if(String.IsNullOrWhiteSpace(query))return;node["sources"]!.AsArray().Add(JsonSerializer.SerializeToNode(new PreparationSource("finviz",query,false,Query:query),Json));Retain();Sources();}));
        body.Add(Label("US equity exchange calendar · America/New_York"));
        var numeric=new List<(PreparationField Field,Entry Entry)>();
        foreach(var field in PreparationEditor.Fields)
        {
            var parts=field.Key.Split('.');var parent=parts.Length==1?node:node[parts[0]]!;var property=parts[^1];body.Add(Label(field.Label));
            if(field.Kind=="boolean") {var control=new Switch {IsToggled=parent[property]?.GetValue<bool>()??false};control.Toggled+=(_,_)=>{parent[property]=control.IsToggled;Retain();};body.Add(control);}
            else {var entry=new Entry {Keyboard=Keyboard.Numeric,Text=parent[property]?.ToJsonString()??""};numeric.Add((field,entry));entry.TextChanged+=(_,_)=>{if(String.IsNullOrWhiteSpace(entry.Text))parent[property]=null;else if(decimal.TryParse(entry.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var number))parent[property]=JsonValue.Create(number);Retain();};body.Add(entry);}
        }
        body.Add(Button("Save settings",async()=>{
            if(backend!=AppServices.Api.BaseUrl)throw new InvalidOperationException("Backend changed. Reopen the editor.");
            foreach(var item in numeric)if(!String.IsNullOrWhiteSpace(item.Entry.Text)&&!decimal.TryParse(item.Entry.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out _))throw new InvalidOperationException("Enter a valid number for "+item.Field.Label);
            var settings=node.Deserialize<PreparationSettings>(Json)??throw new InvalidOperationException("Invalid settings.");
            var saved=await AppServices.Api.Workflow.SavePreparationAsync(new(definition?.WorkflowId??Guid.Empty,definition?.Revision??0,settings));
            Preferences.Remove(key);await Navigation.PopAsync();await OpenAsync(saved.WorkflowId);
        }));
        await Navigation.PushAsync(new ContentPage {Title="Workflow settings",Content=new ScrollView {Content=body}});
    }
    private async Task OpenAsync(Guid id)
    {
        var detail=await AppServices.Api.Workflow.PreparationAsync(id,Token);Token.ThrowIfCancellationRequested();Reset();var def=detail.Definition;
        content.Add(Label(def.Settings.Name+" · revision "+def.Revision+" · "+(def.Settings.Enabled?"enabled":"paused")));
        content.Add(Button("Edit workflow",()=>EditAsync(def)));content.Add(Button(def.Settings.Enabled?"Pause workflow":"Enable workflow",async()=>{await AppServices.Api.Workflow.SavePreparationAsync(new(id,def.Revision,def.Settings with{Enabled=!def.Settings.Enabled}),Token);await OpenAsync(id);}));
        if(detail.PlanningError is not null)content.Add(Label(detail.PlanningError));
        foreach(var slot in detail.Upcoming.Take(8))content.Add(Label(slot.Phase+" for "+slot.TargetDate+" · "+When(slot.ScheduledAtUtc)));
        foreach(var phase in new[]{"nightly","morning"})content.Add(Button("Run "+phase+" now",async()=>{if(pendingRun?.Phase!=phase)pendingRun=new(phase,Guid.NewGuid().ToString("N"));await AppServices.Api.Workflow.RunPreparationAsync(id,pendingRun!,Token);pendingRun=null;await OpenAsync(id);}));
        content.Add(Button("Refresh history",()=>OpenAsync(id)));
        foreach(var row in detail.History)
        {
            content.Add(Label(row.Slot.Phase+" · "+row.Slot.TargetDate+" · "+row.Status+" · "+When(row.Slot.ScheduledAtUtc)));
            if(row.Error is not null)content.Add(Label(row.Error));
            if(row.Snapshot is not null)content.Add(Button("Review snapshot",()=>ReviewAsync(def,row)));
            else if(row.Status is "queued" or "running" or "cancelling")content.Add(Button("Cancel occurrence",async()=>{await AppServices.Api.Workflow.CancelPreparationAsync(id,row.Id,Token);await OpenAsync(id);}));
        }
        if(requestedOccurrence is {} occurrence)
        {var row=detail.History.FirstOrDefault(x=>x.Id==occurrence&&x.Snapshot?.RevisionId==requestedRevision);if(row is not null)await ReviewAsync(def,row);else status.Text="The exact notification revision is unavailable.";requestedOccurrence=null;requestedRevision=null;}
    }
    private async Task ReviewAsync(PreparationDefinition definition,PreparationOccurrence occurrence)
    {
        var snapshot=occurrence.Snapshot!;var selected=snapshot.Symbols.Where(x=>x.Readiness=="complete").Select(x=>x.Symbol).ToHashSet();pendingHandoff=null;pendingCandidate=null;pendingCuration=null;
        var body=new VerticalStackLayout {Padding=16,Spacing=10};body.Add(Label(occurrence.Slot.Phase+" · "+snapshot.Readiness));body.Add(Label("Cutoff: "+When(snapshot.CutoffUtc)));body.Add(Label("Expires: "+When(snapshot.ExpiresAtUtc)));
        foreach(var text in snapshot.Reasons)body.Add(Label(text));foreach(var source in snapshot.Sources)body.Add(Label(source.Source.Kind+" "+source.Source.Key+" · "+source.Status+" · "+source.ObservedAtUtc+" · "+source.Reason));
        body.Add(Label("Removed: "+String.Join(", ",snapshot.RemovedSymbols)));var rows=new VerticalStackLayout();body.Add(rows);var displayed=0;
        void More()
        {
            foreach(var symbol in snapshot.Symbols.Skip(displayed).Take(30))
            {
                rows.Add(Label(symbol.Symbol+" · "+symbol.Change+" · "+symbol.Readiness));var check=new CheckBox {IsChecked=selected.Contains(symbol.Symbol),IsEnabled=symbol.Readiness=="complete"};check.CheckedChanged+=(_,_)=>{if(check.IsChecked)selected.Add(symbol.Symbol);else selected.Remove(symbol.Symbol);pendingHandoff=null;pendingCandidate=null;pendingCuration=null;};rows.Add(check);
                rows.Add(Button("Evidence for "+symbol.Symbol,async()=>{var details=String.Join("\n",symbol.Reasons.Concat(symbol.Context));await DisplayAlertAsync(symbol.Symbol,$"Daily close {symbol.Close}; {symbol.HistoryCount} sessions; {symbol.Feed}/{symbol.Adjustment}\nGap {symbol.GapPercent}; same-time RVOL {symbol.SameTimeRelativeVolume}\n{details}\n{String.Join("\n",symbol.News.Select(x=>x.Headline))}\nEvidence hash {symbol.EvidenceSha256}","OK");}));
            }
            foreach(var symbol in snapshot.Symbols.Skip(displayed).Take(30))
            {
                rows.Add(Button("Market detail: "+symbol.Symbol,()=>Shell.Current.GoToAsync(nameof(SymbolDetailPage)+"?ticker="+Uri.EscapeDataString(symbol.Symbol))));
                foreach(var article in symbol.News)
                    if(Uri.TryCreate(article.Url,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https")rows.Add(Button(article.Headline,async()=>{await NewsNavigation.OpenAsync(uri.AbsoluteUri);}));
                    else rows.Add(Label(article.Headline+" · article link unavailable"));
            }
            displayed+=30;
        }
        More();body.Add(Button("Show next 30",()=>{More();return Task.CompletedTask;}));
        var reason=new Entry {Placeholder="Reason for accepted membership"};reason.TextChanged+=(_,_)=>{pendingHandoff=null;pendingCandidate=null;pendingCuration=null;};body.Add(reason);
        var handoff=Button("Open research candidates",async()=>{
            var catalog=await AppServices.Api.Workflow.CatalogAsync("backtest");var options=catalog.Strategies.Select(x=>x.Name+" · "+x.Identity.SemanticVersion+" · "+x.Identity.ContentSha256[..8]).ToArray();var choice=await DisplayActionSheetAsync("Exact research strategy","Cancel",null,options);var index=Array.IndexOf(options,choice);if(index<0)return;
            pendingHandoff??=new(snapshot.RevisionId,selected.Order().ToArray(),reason.Text??"",Guid.NewGuid().ToString("N"));
            var accepted=await AppServices.Api.Workflow.HandoffPreparationAsync(definition.WorkflowId,occurrence.Id,pendingHandoff);
            if(pendingCandidate is null || pendingCandidate.Strategies[0]!=catalog.Strategies[index].Identity)
                pendingCandidate=new(ContractVersions.VersionOne,Guid.NewGuid().ToString("N"),"backtest",accepted.UniverseSnapshotId,[catalog.Strategies[index].Identity],snapshot.CutoffUtc);
            var run=await AppServices.Api.Workflow.StartCandidatesAsync(pendingCandidate);pendingCandidate=null;
            pendingHandoff=null;await Shell.Current.GoToAsync(nameof(CandidatesPage)+"?runId="+run.CandidateRunId);
        });handoff.IsEnabled=occurrence.Slot.Phase=="morning"&&snapshot.Readiness!="unavailable"&&DateTimeOffset.UtcNow<snapshot.ExpiresAtUtc;body.Add(handoff);
        body.Add(Button("Review authorized paper run with this snapshot",async()=>{
            pendingHandoff??=new(snapshot.RevisionId,selected.Order().ToArray(),reason.Text??"",Guid.NewGuid().ToString("N"));
            await AppServices.Api.Workflow.HandoffPreparationAsync(definition.WorkflowId,occurrence.Id,pendingHandoff);
            await Shell.Current.GoToAsync(nameof(PaperPage)+"?preparationKey="+Uri.EscapeDataString(pendingHandoff.IdempotencyKey));
        }));
        var additions=new Entry {Placeholder="Add symbols: AAPL, MSFT"};body.Add(additions);additions.TextChanged+=(_,_)=>pendingCuration=null;
        body.Add(Button("Collect curated revision",async()=>{
            pendingCuration??=new(snapshot.RevisionId,(additions.Text??"").Split([',',' ','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(x=>x.ToUpperInvariant()).ToArray(),snapshot.Symbols.Where(x=>!selected.Contains(x.Symbol)).Select(x=>x.Symbol).ToArray(),reason.Text??"",Guid.NewGuid().ToString("N"));
            await AppServices.Api.Workflow.CuratePreparationAsync(definition.WorkflowId,occurrence.Id,pendingCuration);pendingCuration=null;
            await DisplayAlertAsync("Curation queued","Return to history to review the new collected revision.","OK");
        }));
        await Navigation.PushAsync(new ContentPage {Title="Snapshot review",Content=new ScrollView {Content=body}});
    }
}
