using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Domain.News;

namespace TradingFlow.Cli;

/// <summary>Small category-only human pilot and a separately frozen, automated screening model.</summary>
public static class NewsCategoryPilotCommand
{
    public const string Name="news-category-pilot";
    public static void Run(string[] args)
    {
        if(args.Length<1) throw new ArgumentException("Specify predict-rules or import.");
        var mode=args[0]; var parameters=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var i=1;i<args.Length;i+=2)
            if(i+1>=args.Length||!parameters.TryAdd(args[i],args[i+1])) throw new ArgumentException("Use unique named options with values.");
        string Need(string name)=>parameters.TryGetValue(name,out var value)?value:throw new ArgumentException("Missing "+name);
        var now=DateTimeOffset.UtcNow;
        object result;
        if(mode=="predict-rules")
        {
            if(parameters.Keys.Except(["--packet","--packet-sha256","--output"],StringComparer.Ordinal).Any())
                throw new ArgumentException("Prediction accepts only packet, packet-sha256 and output; never human labels.");
            var bytes=File.ReadAllBytes(Need("--packet")); var sha=Need("--packet-sha256");
            if(NewsAssessmentWorkflow.Hash(bytes)!=sha) throw new InvalidDataException("News packet changed.");
            var packet=NewsAssessmentWorkflow.Read<NewsAssessmentPacket>(bytes);
            NewsAssessmentWorkflow.ValidatePacket(packet,sha);
            var predictions=packet.WorkItems.Select(item=> {
                if(now<item.ObservedReceiptAtUtc) throw new InvalidDataException("Classification cannot precede source capture.");
                return new { item.Item.WorkItemId,item.Item.ProviderArticleId,item.Item.NewsRevisionContentSha256,item.HeldOut,
                    Classification=EconomicNewsRuleClassifier.Classify(new(item.Item.Headline,item.Item.Summary,item.Body,
                        item.Item.Symbols,item.Item.NewsProvider),now) };
            }).ToArray();
            result=new {Version="economic-news-category-pilot-predictions-v1",PacketSha256=sha,
                ClassifierVersion=EconomicNewsRuleClassifier.Version,
                ClassifierArtifactSha256=NewsAssessmentWorkflow.FileHash(typeof(EconomicNewsRuleClassifier).Assembly.Location),
                ModelFrozenAtUtc=now,TrainingWorkItemIds=ImmutableArray<string>.Empty,
                HumanFeedbackWorkItemIds=ImmutableArray<string>.Empty,Predictions=predictions,
                AccuracyMeasured=false,HumanAnnotationsGenerated=0,FullHumanProtocolSatisfied=false,
                HistoricalClassifierAvailabilityProven=false,TradingActivationAllowed=false};
        }
        else if(mode=="import")
        {
            if(parameters.Keys.Except(["--workbook","--selection","--selection-sha256","--output"],StringComparer.Ordinal).Any())
                throw new ArgumentException("Import accepts only workbook, selection, selection-sha256 and output.");
            result=NewsCategoryPilotReview.Import(Need("--workbook"),Need("--selection"),Need("--selection-sha256"),now);
        }
        else throw new ArgumentException("Unknown category-pilot mode.");
        var payload=NewsAssessmentWorkflow.Bytes(result); var output=Path.GetFullPath(Need("--output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if(File.Exists(output))
        {
            if(!File.ReadAllBytes(output).SequenceEqual(payload))throw new IOException("Refusing to overwrite a different immutable pilot result.");
        }
        else
        {
            var temporary=output+".writing-"+Guid.NewGuid().ToString("N");
            try {File.WriteAllBytes(temporary,payload);File.Move(temporary,output,false);}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        Console.WriteLine(JsonSerializer.Serialize(new {Output=output,Sha256=NewsAssessmentWorkflow.Hash(payload),
            Mode=mode,FullHumanProtocolSatisfied=false,TradingActivationAllowed=false}));
    }
}
