using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace TradingFlow.Data.Evidence.Labeling;

public sealed record NewsCategoryPilotLabel(string WorkItemId, string ArticleId, string RevisionSha256,
    string Category, string? Notes);
public sealed record NewsCategoryPilotResult(string Version, string PacketSha256, string SelectionSha256,
    string OriginalWorkbookSha256, string CompletedWorkbookSha256, DateTimeOffset ObservedAtUtc,
    string? Reviewer, string? ReviewerReportedCompletion, int AssignedStories,
    ImmutableArray<NewsCategoryPilotLabel> Labels, ImmutableArray<string> UnreviewedWorkItemIds,
    bool PilotOnly = true, bool DevelopmentOnly = true, bool FullHumanProtocolSatisfied = false,
    bool HistoricalClassifierAvailabilityProven = false, bool TradingActivationAllowed = false);

/// <summary>Imports only category judgments. It never invents the other full-protocol human labels.</summary>
public static class NewsCategoryPilotReview
{
    public const string SelectionVersion = "news-category-pilot-selection-v1";
    public static readonly ImmutableArray<string> Categories = ["earnings","guidance","merger_acquisition",
        "regulatory","capital_structure","major_commercial_event","management","analyst_action","legal",
        "operational","macro_sector","promotional_low_information","unknown"];
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationship = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static NewsCategoryPilotResult Import(string workbookPath, string selectionPath, string expectedSelectionSha256,
        DateTimeOffset observedAtUtc)
    {
        if (NewsAssessmentWorkflow.FileHash(selectionPath) != expectedSelectionSha256)
            throw new InvalidDataException("Pilot selection differs from its frozen expected SHA256.");
        using var selection = JsonDocument.Parse(File.ReadAllBytes(selectionPath));
        var source = selection.RootElement;
        if (source.GetProperty("Version").GetString() != SelectionVersion || !source.GetProperty("PilotOnly").GetBoolean() ||
            !source.GetProperty("DevelopmentOnly").GetBoolean() || source.GetProperty("FullHumanProtocolSatisfied").GetBoolean())
            throw new InvalidDataException("A category-only workbook is a development pilot, never full-protocol ground truth.");
        var allowed = source.GetProperty("AllowedCategories").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (!allowed.SequenceEqual(Categories)) throw new InvalidDataException("Pilot categories differ from the frozen thirteen-category guide.");
        var packetSha = source.GetProperty("PacketSha256").GetString()!;
        var packetBytes = File.ReadAllBytes(source.GetProperty("SourcePacketPath").GetString()!);
        var packet = NewsAssessmentWorkflow.Read<NewsAssessmentPacket>(packetBytes);
        if (NewsAssessmentWorkflow.Hash(packetBytes) != packetSha) throw new InvalidDataException("Pilot source packet changed.");
        NewsAssessmentWorkflow.ValidatePacket(packet, packetSha);
        var sourceItems = packet.WorkItems.ToDictionary(x => x.Item.WorkItemId, StringComparer.Ordinal);
        var items = source.GetProperty("Items").EnumerateArray().ToArray();
        if (items.Length != 25 || source.GetProperty("ExpectedCount").GetInt32() != items.Length)
            throw new InvalidDataException("This pilot contract requires the frozen twenty-five assignments.");
        if (observedAtUtc.Offset != TimeSpan.Zero || items.Any(x =>
                !sourceItems.TryGetValue(x.GetProperty("WorkItemId").GetString()!, out var item) || item.HeldOut ||
                item.Item.ProviderArticleId != x.GetProperty("ArticleId").GetString() ||
                item.Item.NewsRevisionContentSha256 != x.GetProperty("RevisionSha256").GetString() ||
                observedAtUtc < item.ObservedReceiptAtUtc))
            throw new InvalidDataException("Pilot identity/revision/observation clock differs or consumes held-out stories.");
        var workbook = source.GetProperty("Workbook");
        if (workbook.GetProperty("Sheet").GetString() != "Review" || workbook.GetProperty("HeaderRow").GetInt32() != 6 ||
            workbook.GetProperty("FirstDataRow").GetInt32() != 7 || workbook.GetProperty("LastDataRow").GetInt32() != 31)
            throw new InvalidDataException("Unsupported pilot workbook layout.");
        var info = new FileInfo(workbookPath);
        if (info.Length > 16_000_000) throw new InvalidDataException("Pilot workbook exceeds the supported size.");
        using var archive = ZipFile.OpenRead(workbookPath);
        if (archive.Entries.Select(x => x.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new InvalidDataException("Repeated ZIP entry names are not allowed.");
        var shared = archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry
            ? ReadXml(sharedEntry).Root!.Elements(Main+"si").Select(x => String.Concat(x.Descendants(Main+"t").Select(t => t.Value))).ToArray()
            : [];
        var bookDocument=ReadXml(Require(archive,"xl/workbook.xml"));
        var relationships=ReadXml(Require(archive,"xl/_rels/workbook.xml.rels"));
        (XDocument Document,string Path) Sheet(string name)
        {
            var sheet=bookDocument.Descendants(Main+"sheet").Single(x=>(string?)x.Attribute("name")==name);
            var id=(string?)sheet.Attribute(Relationship+"id")??throw new InvalidDataException("Worksheet relationship missing.");
            var link=relationships.Descendants(PackageRelationship+"Relationship").Single(x=>(string?)x.Attribute("Id")==id);
            var target=(string?)link.Attribute("Target")??"";
            if((string?)link.Attribute("TargetMode")=="External"||target.Contains('\\')||target.Split('/').Contains(".."))
                throw new InvalidDataException("External or traversing worksheet target is not supported.");
            var path=target.StartsWith('/')?target.TrimStart('/') : "xl/"+target;
            return (ReadXml(Require(archive,path)),path);
        }
        string CellText(XElement cell,string address)
        {
            if(cell.Element(Main+"f") is not null)throw new InvalidDataException("Source and human judgment cells cannot contain formulas: "+address);
            var value=cell.Element(Main+"v")?.Value??"";
            return (string?)cell.Attribute("t") switch {
                "s"=>Int32.TryParse(value,out var index)&&index>=0&&index<shared.Length?shared[index]:throw new InvalidDataException("Invalid shared-string index."),
                "inlineStr"=>String.Concat(cell.Descendants(Main+"t").Select(t=>t.Value)),
                "e"=>throw new InvalidDataException("Excel error cell: "+address),
                null or "n"=>Decimal.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)?number.ToString("G29",CultureInfo.InvariantCulture):value,
                _=>value};
        }
        var supporting=source.GetProperty("SupportingSheets");
        if(!supporting.EnumerateObject().Select(x=>x.Name).Order(StringComparer.Ordinal).SequenceEqual(new[]{"Categories","Context"}))
            throw new InvalidDataException("Both frozen category definitions and context mapping are required.");
        foreach(var support in supporting.EnumerateObject())
        {
            var actual=Sheet(support.Name).Document.Descendants(Main+"c").ToDictionary(c=>(string)c.Attribute("r")!,c=>CellText(c,(string)c.Attribute("r")!),StringComparer.Ordinal);
            actual=actual.Where(x=>x.Value.Length>0).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            var frozen=support.Value.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetString()!,StringComparer.Ordinal);
            if(actual.Count!=frozen.Count||frozen.Any(x=>!actual.TryGetValue(x.Key,out var value)||value!=x.Value))
                throw new InvalidDataException("Supporting source text or story/category mapping changed: "+support.Name);
        }
        var (document,entryName)=Sheet("Review");
        var hyperlinkPart=Path.GetDirectoryName(entryName)!.Replace('\\','/')+"/_rels/"+Path.GetFileName(entryName)+".rels";
        var hyperlinkRelations=archive.GetEntry(hyperlinkPart) is {} hyperlinkEntry?ReadXml(hyperlinkEntry):new XDocument();
        var hyperlinks=document.Descendants(Main+"hyperlink").ToDictionary(x=>(string)x.Attribute("ref")!,x=> {
            var relationId=(string?)x.Attribute(Relationship+"id");
            return relationId is null?(string?)x.Attribute("location")??"":
                (string?)hyperlinkRelations.Descendants(PackageRelationship+"Relationship").Single(r=>(string?)r.Attribute("Id")==relationId).Attribute("Target")??"";
        },StringComparer.Ordinal);
        var uses1904=(string?)bookDocument.Descendants(Main+"workbookPr").FirstOrDefault()?.Attribute("date1904") is "1" or "true";
        var rows = document.Descendants(Main+"row").ToArray();
        var cellIndex = document.Descendants(Main+"c").ToDictionary(c => (string)c.Attribute("r")!, StringComparer.Ordinal);
        string Value(string address)
        {
            if (!cellIndex.TryGetValue(address,out var cell)) return "";
            return CellText(cell,address);
        }
        var expected = items.ToDictionary(x=>x.GetProperty("WorkItemId").GetString()!,StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var labels = ImmutableArray.CreateBuilder<NewsCategoryPilotLabel>();
        var unreviewed = ImmutableArray.CreateBuilder<string>();
        foreach (var row in rows.Where(r => (int?)r.Attribute("r") >= 7))
        {
            var number = (int?)row.Attribute("r") ?? throw new InvalidDataException("Missing worksheet row number.");
            var workId = Value("M"+number);
            if (workId.Length == 0) {
                if (Value("B"+number).Length > 0 || Value("K"+number).Length > 0) throw new InvalidDataException("Category row is missing its bound identity.");
                continue;
            }
            if (!expected.TryGetValue(workId,out var item) || !seen.Add(workId))
                throw new InvalidDataException("Unassigned or repeated pilot work-item ID.");
            if (Value("A"+number)!=item.GetProperty("Story").GetInt32().ToString(CultureInfo.InvariantCulture) ||
                Value("K"+number)!=item.GetProperty("ArticleId").GetString() ||
                Value("L"+number)!=item.GetProperty("RevisionSha256").GetString() || Value("N"+number)!=packetSha ||
                Value("C"+number)!=item.GetProperty("SourceHeadline").GetString() ||
                Value("D"+number)!=item.GetProperty("ContextExcerpt").GetString() || Value("E"+number)!=item.GetProperty("Symbols").GetString())
                throw new InvalidDataException("A source identity, headline, context or symbol changed in the completed workbook.");
            var original=sourceItems[workId].Item;
            var dateValue=Value("F"+number);
            var published=Double.TryParse(dateValue,NumberStyles.Float,CultureInfo.InvariantCulture,out var serial)
                ? (uses1904?new DateTimeOffset(1904,1,1,0,0,0,TimeSpan.Zero):new DateTimeOffset(1899,12,30,0,0,0,TimeSpan.Zero)).AddDays(serial)
                : DateTimeOffset.Parse(dateValue,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal);
            if(Math.Abs((published-original.PublishedAtUtc).TotalMilliseconds)>1 ||
                hyperlinks.GetValueOrDefault("H"+number,"")!=original.ArticleUrl)
                throw new InvalidDataException("An article publication time or linked source URL changed.");
            var category = Value("B"+number).Trim();
            if (category.Length == 0) { unreviewed.Add(workId); continue; }
            if (!Categories.Contains(category,StringComparer.Ordinal)) throw new InvalidDataException("Unknown human category: "+category);
            labels.Add(new(workId,Value("K"+number),Value("L"+number),category,Value("I"+number)));
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("Keep all assigned rows; leave unreviewed categories blank instead of deleting assignments.");
        return new("news-category-pilot-import-v1",packetSha,expectedSelectionSha256,
            workbook.GetProperty("Sha256").GetString()!,NewsAssessmentWorkflow.FileHash(workbookPath),observedAtUtc,
            Value("B4"),Value("F4"),expected.Count,labels.OrderBy(x=>x.WorkItemId,StringComparer.Ordinal).ToImmutableArray(),
            unreviewed.Order(StringComparer.Ordinal).ToImmutableArray());
    }

    private static ZipArchiveEntry Require(ZipArchive archive,string path) => archive.GetEntry(path)
        ?? throw new InvalidDataException("Missing workbook part: "+path);
    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        if(entry.Length>16_000_000) throw new InvalidDataException("Workbook XML part exceeds supported size.");
        using var stream=entry.Open();
        using var reader=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,
            XmlResolver=null,MaxCharactersInDocument=20_000_000 });
        return XDocument.Load(reader);
    }
}
