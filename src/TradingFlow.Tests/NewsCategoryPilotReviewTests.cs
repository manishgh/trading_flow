using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TradingFlow.Data.Evidence.Labeling;

namespace TradingFlow.Tests;

public sealed class NewsCategoryPilotReviewTests
{
    [Fact]
    public void BlankWorkbookImportsZeroJudgmentsWithoutPretendingFullCertification()
    {
        using var fixture=new Fixture();fixture.Workbook();
        var result=fixture.Import();
        Assert.Empty(result.Labels);Assert.Equal(25,result.UnreviewedWorkItemIds.Length);
        Assert.False(result.FullHumanProtocolSatisfied);Assert.False(result.TradingActivationAllowed);
    }

    [Fact]
    public void PartialCategoryJudgmentsRemainBoundAfterRowsAreReordered()
    {
        using var fixture=new Fixture();fixture.Workbook(category:"earnings",reverse:true,onlyFirst:true);
        var result=fixture.Import();
        Assert.Single(result.Labels);Assert.Equal(24,result.UnreviewedWorkItemIds.Length);
        Assert.All(result.Labels,x=>Assert.Equal("earnings",x.Category));
        Assert.False(result.FullHumanProtocolSatisfied);
    }

    [Theory]
    [InlineData("headline")]
    [InlineData("identity")]
    [InlineData("invalid-category")]
    [InlineData("formula")]
    [InlineData("deleted-row")]
    [InlineData("context")]
    [InlineData("guide")]
    [InlineData("source-url")]
    [InlineData("published")]
    public void ChangedSourcesInvalidCategoriesFormulasAndRemovedAssignmentsAreRejected(string change)
    {
        using var fixture=new Fixture();fixture.Workbook(change:change);
        Assert.Throws<InvalidDataException>(()=>fixture.Import());
    }

    [Fact]
    public void SelectionAndOriginalPacketCannotBeSilentlyChanged()
    {
        using var fixture=new Fixture();fixture.Workbook();
        var sha=NewsAssessmentWorkflow.FileHash(fixture.SelectionPath);
        File.AppendAllText(fixture.SelectionPath," ");
        Assert.Throws<InvalidDataException>(()=>NewsCategoryPilotReview.Import(fixture.WorkbookPath,fixture.SelectionPath,sha,Fixture.Clock));
    }

    private sealed class Fixture:IDisposable
    {
        private static readonly XNamespace Ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        public static readonly DateTimeOffset Clock=DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        private readonly string root=Path.Combine(Directory.GetCurrentDirectory(),"tf-category-pilot-test-"+Guid.NewGuid().ToString("N"));
        private readonly NewsAssessmentWorkItem[] items;
        private readonly string packetSha;
        public string WorkbookPath=>Path.Combine(root,"review.xlsx");
        public string SelectionPath=>Path.Combine(root,"selection.json");
        public Fixture()
        {
            Directory.CreateDirectory(root);
            var payload=Path.Combine(root,"page.json");
            File.WriteAllText(payload,JsonSerializer.Serialize(new {news=Enumerable.Range(1,50).Select(id=>new {
                id,created_at="2026-08-17T11:56:00Z",updated_at="2026-08-17T11:56:00Z",headline="Example headline "+id,
                summary=(string?)null,content=(string?)null,url="https://example.invalid/"+id,symbols=new[]{"SYM"}})}));
            File.WriteAllText(Path.Combine(root,"receipt.json"),JsonSerializer.Serialize(new {HttpStatus=200,HistoricalFirstReceiptClaim=false,
                CapturedUtc="2026-10-19T12:00:00Z",Payload="page.json",PayloadSha256=NewsAssessmentWorkflow.FileHash(payload)}));
            File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(new {Windows=new[]{new {PaginationComplete=true,Error=(string?)null,Receipts=new[]{"receipt.json"}}}}));
            var packet=NewsAssessmentWorkflow.Export(root,Path.Combine(root,"summary.json"),new(NewsAssessmentAvailabilityBasis.ObservedReceipt),50);
            var packetPath=Path.Combine(root,"packet.json");File.WriteAllBytes(packetPath,NewsAssessmentWorkflow.Bytes(packet));
            packetSha=NewsAssessmentWorkflow.FileHash(packetPath);items=packet.WorkItems.Where(x=>!x.HeldOut).Take(25).ToArray();
            File.WriteAllText(SelectionPath,JsonSerializer.Serialize(new {Version=NewsCategoryPilotReview.SelectionVersion,PilotOnly=true,DevelopmentOnly=true,
                FullHumanProtocolSatisfied=false,AllowedCategories=NewsCategoryPilotReview.Categories,PacketSha256=packetSha,SourcePacketPath=packetPath,
                ExpectedCount=25,Workbook=new {Sheet="Review",HeaderRow=6,FirstDataRow=7,LastDataRow=31,Sha256=new string('a',64)},
                SupportingSheets=new {Context=new Dictionary<string,string>{{"A1","Context"}},Categories=new Dictionary<string,string>{{"A1","Guide"}}},
                Items=items.Select((x,index)=>new {Story=index+1,WorkItemId=x.Item.WorkItemId,ArticleId=x.Item.ProviderArticleId,RevisionSha256=x.Item.NewsRevisionContentSha256,
                    SourceHeadline=x.Item.Headline,ContextExcerpt="Headline only",Symbols="SYM"})}));
        }
        public void Workbook(string category="",bool reverse=false,string? change=null,bool onlyFirst=false)
        {
            var list=reverse?items.Reverse().ToArray():items;
            var rows=list.Select((item,index)=> {
                var r=index+7;
                XElement Cell(string column,string value)=>new(Ns+"c",new XAttribute("r",column+r),new XAttribute("t","inlineStr"),new XElement(Ns+"is",new XElement(Ns+"t",value)));
                var result=new XElement(Ns+"row",new XAttribute("r",r),Cell("A",(Array.IndexOf(items,item)+1).ToString(CultureInfo.InvariantCulture)),
                    Cell("B",change=="invalid-category"&&index==0?"invalid":onlyFirst&&index>0?"":category),
                    Cell("C",change=="headline"&&index==0?"changed":item.Item.Headline),Cell("D","Headline only"),Cell("E","SYM"),
                    Cell("F",(change=="published"&&index==0?item.Item.PublishedAtUtc.AddMinutes(1):item.Item.PublishedAtUtc).ToString("O")),
                    Cell("K",item.Item.ProviderArticleId),Cell("L",item.Item.NewsRevisionContentSha256),
                    Cell("M",change=="identity"&&index==0?"wrong":item.Item.WorkItemId),Cell("N",packetSha));
                if(change=="formula"&&index==0)result.Elements(Ns+"c").First().Add(new XElement(Ns+"f","1+1"));
                return result;
            }).ToList();
            if(change=="deleted-row")rows.RemoveAt(0);
            using var archive=ZipFile.Open(WorkbookPath,ZipArchiveMode.Create);
            void Write(string path,string xml){using var stream=new StreamWriter(archive.CreateEntry(path).Open());stream.Write(xml);}
            Write("xl/workbook.xml","<workbook xmlns=\""+Ns+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Review\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Context\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Categories\" sheetId=\"3\" r:id=\"rId3\"/></sheets></workbook>");
            Write("xl/_rels/workbook.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Target=\"worksheets/sheet3.xml\"/></Relationships>");
            XNamespace relation="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Write("xl/worksheets/sheet1.xml",new XElement(Ns+"worksheet",new XElement(Ns+"sheetData",rows),new XElement(Ns+"hyperlinks",list.Select((item,index)=>
                new XElement(Ns+"hyperlink",new XAttribute("ref","H"+(index+7)),new XAttribute(relation+"id","url"+index))))).ToString());
            XNamespace package="http://schemas.openxmlformats.org/package/2006/relationships";
            Write("xl/worksheets/_rels/sheet1.xml.rels",new XElement(package+"Relationships",list.Select((item,index)=>new XElement(package+"Relationship",
                new XAttribute("Id","url"+index),new XAttribute("Target",change=="source-url"&&index==0?"https://example.invalid/wrong":item.Item.ArticleUrl),new XAttribute("TargetMode","External")))).ToString());
            void Support(string path,string value)=>Write(path,new XElement(Ns+"worksheet",new XElement(Ns+"sheetData",new XElement(Ns+"row",new XAttribute("r",1),
                new XElement(Ns+"c",new XAttribute("r","A1"),new XAttribute("t","inlineStr"),new XElement(Ns+"is",new XElement(Ns+"t",value)))))).ToString());
            Support("xl/worksheets/sheet2.xml",change=="context"?"Different body":"Context");
            Support("xl/worksheets/sheet3.xml",change=="guide"?"Different category definitions":"Guide");
        }
        public NewsCategoryPilotResult Import()=>NewsCategoryPilotReview.Import(WorkbookPath,SelectionPath,NewsAssessmentWorkflow.FileHash(SelectionPath),Clock);
        public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
