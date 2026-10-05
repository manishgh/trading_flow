using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class DeskGridFilterTests
{
    private static DeskGridQuery Query(decimal? minChange = null, decimal? minZ = null, string? tracked = null) => new(null,null,null,null,minChange,null,minZ,null,tracked);
    [Fact]
    public void UnknownNumericEvidenceIsExcludedAndNegativeChangeAndZ20AreValid()
    {
        var query = Query(-3,-2);
        DeskGridFilter.Validate(query);
        Assert.True(DeskGridFilter.Matches(new("AAA","Alpha",10,1,-2,-1,false),query));
        Assert.False(DeskGridFilter.Matches(new("AAA","Alpha",10,1,null,-1,false),query));
        Assert.False(DeskGridFilter.Matches(new("AAA","Alpha",10,1,-2,null,false),query));
        Assert.True(DeskGridFilter.Matches(new("AAA","Alpha",null,null,null,null,false),Query()));
        Assert.False(DeskGridFilter.Matches(new("AAA","Alpha",10,1,-2,-1,false),Query(tracked:"tracked")));
    }
    [Fact]
    public void FilteringAndStableSortingPrecedeBoundedPaging()
    {
        var rows = Enumerable.Range(0,500).Select(i => new DeskGridValues($"T{i:D3}","Fixture",i,1,i-250,null,i%2 == 0));
        var query = Query(0,tracked:"tracked");
        var sorted = rows.Where(x => DeskGridFilter.Matches(x,query)).OrderByDescending(x => x.ChangePct).ThenBy(x => x.Ticker);
        var page = DeskGridPage<DeskGridValues>.Create(sorted,2,25);
        Assert.Equal(125,page.TotalMatching); Assert.Equal(5,page.PageCount); Assert.Equal(25,page.Rows.Count);
        Assert.Equal("T448",page.Rows[0].Ticker);
        Assert.Equal(100,DeskGridPage<DeskGridValues>.Create(sorted,Int32.MaxValue,Int32.MaxValue).Size);
        Assert.Equal(2,DeskGridPage<DeskGridValues>.Create(sorted,Int32.MaxValue,Int32.MaxValue).Number);
    }
    [Fact]
    public void InvertedBoundsAndUnsupportedPositionFiltersAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => DeskGridFilter.Validate(Query(2) with { MaxChange=1 }));
        Assert.Throws<InvalidOperationException>(() => DeskGridFilter.Validate(Query(minZ:2) with { MaxVolumeZ20=1 }));
        Assert.Throws<InvalidOperationException>(() => DeskGridFilter.Validate(Query(tracked:"typo")));
    }
}
