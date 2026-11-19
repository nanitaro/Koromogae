using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>起動時の引数 (--minimized: 最小化で起動 / --start: 自動で再生を始める)。</summary>
public class StartupArgsTests
{
    [Fact]
    public void Without_arguments_the_app_starts_normally()
    {
        var a = StartupArgs.Parse(Array.Empty<string>());

        Assert.False(a.Minimized);
        Assert.False(a.Start);
        Assert.Empty(a.Unknown);
        Assert.False(StartupArgs.Parse(null).Minimized);
    }

    [Theory]
    [InlineData("--minimized")]
    [InlineData("-minimized")]
    [InlineData("/minimized")]
    [InlineData("--MINIMIZED")]
    [InlineData("/Minimized")]
    public void Minimized_is_accepted_in_every_spelling(string arg)
    {
        var a = StartupArgs.Parse(new[] { arg });

        Assert.True(a.Minimized);
        Assert.False(a.Start);                       // 2 つは独立
        Assert.Empty(a.Unknown);
    }

    [Theory]
    [InlineData("--start")]
    [InlineData("-start")]
    [InlineData("/start")]
    [InlineData("--START")]
    public void Start_is_accepted_in_every_spelling(string arg)
    {
        var a = StartupArgs.Parse(new[] { arg });

        Assert.True(a.Start);
        Assert.False(a.Minimized);
        Assert.Empty(a.Unknown);
    }

    [Theory]
    [InlineData("--minimized", "--start")]
    [InlineData("--start", "--minimized")]
    [InlineData("/start", "-minimized")]
    public void Both_can_be_given_in_any_order(string first, string second)
    {
        var a = StartupArgs.Parse(new[] { first, second });

        Assert.True(a.Minimized);
        Assert.True(a.Start);
        Assert.Empty(a.Unknown);
    }

    [Theory]
    [InlineData("--foo")]
    [InlineData("--minimizedx")]                     // 前方一致では認めない
    [InlineData("--startx")]
    [InlineData("minimized")]                        // 先頭の記号が無いものは、引数として扱わない
    [InlineData("start")]
    public void Unknown_arguments_are_ignored_and_reported(string arg)
    {
        var a = StartupArgs.Parse(new[] { arg });

        Assert.False(a.Minimized);
        Assert.False(a.Start);
        Assert.Equal(new[] { arg }, a.Unknown);
    }

    [Fact]
    public void An_unknown_argument_does_not_stop_the_known_ones()
    {
        var a = StartupArgs.Parse(new[] { "--foo", "--start", "bar", "--minimized" });

        Assert.True(a.Minimized);
        Assert.True(a.Start);
        Assert.Equal(new[] { "--foo", "bar" }, a.Unknown);
    }

    [Fact]
    public void Empty_arguments_are_ignored()
    {
        var a = StartupArgs.Parse(new[] { "", "  " });

        Assert.False(a.Minimized);
        Assert.False(a.Start);
        Assert.Equal(new[] { "  " }, a.Unknown);     // 空白だけのものは、知らない引数として返す (空文字だけは無視)
    }
}