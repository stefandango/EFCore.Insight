using Xunit;

namespace EFCore.Insight.Tests;

public class InsightOptionsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var options = new InsightOptions();

        Assert.Equal("_ef-insight", options.RoutePrefix);
        Assert.Equal(1000, options.MaxStoredQueries);
        Assert.True(options.EnableRequestCorrelation);
    }

    [Fact]
    public void RoutePrefix_CanBeCustomized()
    {
        var options = new InsightOptions { RoutePrefix = "my-prefix" };

        Assert.Equal("my-prefix", options.RoutePrefix);
    }

    [Fact]
    public void MaxStoredQueries_CanBeCustomized()
    {
        var options = new InsightOptions { MaxStoredQueries = 500 };

        Assert.Equal(500, options.MaxStoredQueries);
    }

    [Fact]
    public void EnableRequestCorrelation_CanBeDisabled()
    {
        var options = new InsightOptions { EnableRequestCorrelation = false };

        Assert.False(options.EnableRequestCorrelation);
    }
}
