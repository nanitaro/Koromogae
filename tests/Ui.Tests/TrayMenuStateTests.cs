using Koromogae;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>トレイのアイコンの右クリックメニューの、文言と有効・無効 (再生と録音の状態で決まる)。</summary>
public class TrayMenuStateTests
{
    [Fact]
    public void When_stopped_playback_can_be_started_and_recording_is_greyed_out()
    {
        var s = TrayMenuState.For(running: false, recording: false);

        Assert.Equal("Tray.Play.Start", s.PlayKey);
        Assert.Equal("Tray.Rec.Start", s.RecordKey);
        Assert.False(s.RecordEnabled);                       // 再生中でなければグレーアウト
        Assert.Equal("Tray.Tip.Stopped", s.TipKey);
    }

    [Fact]
    public void When_playing_playback_can_be_stopped_and_recording_can_be_started()
    {
        var s = TrayMenuState.For(running: true, recording: false);

        Assert.Equal("Tray.Play.Stop", s.PlayKey);
        Assert.Equal("Tray.Rec.Start", s.RecordKey);
        Assert.True(s.RecordEnabled);
        Assert.Equal("Tray.Tip.Running", s.TipKey);
    }

    [Fact]
    public void When_recording_recording_can_be_stopped()
    {
        var s = TrayMenuState.For(running: true, recording: true);

        Assert.Equal("Tray.Play.Stop", s.PlayKey);
        Assert.Equal("Tray.Rec.Stop", s.RecordKey);
        Assert.True(s.RecordEnabled);
        Assert.Equal("Tray.Tip.Recording", s.TipKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Restore_and_exit_are_always_available(bool running, bool recording)
    {
        _ = TrayMenuState.For(running, recording);

        Assert.True(TrayMenuState.RestoreEnabled);
        Assert.True(TrayMenuState.ExitEnabled);
    }

    [Fact]
    public void Recording_is_enabled_exactly_when_playback_is_running()
    {
        foreach (bool running in new[] { false, true })
            foreach (bool recording in new[] { false, true })
                if (running || !recording)                   // 再生していないのに録音中、という組み合わせは起きない
                    Assert.Equal(running, TrayMenuState.For(running, recording).RecordEnabled);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Every_menu_text_exists_in_both_languages(string language)
    {
        var culture = language == "ja" ? Loc.Japanese : Loc.English;
        using (Loc.Override(culture))
        {
            var keys = new List<string> { TrayMenuState.RestoreKey, TrayMenuState.ExitKey };
            foreach (bool running in new[] { false, true })
                foreach (bool recording in new[] { false, true })
                {
                    var s = TrayMenuState.For(running, recording);
                    keys.AddRange(new[] { s.PlayKey, s.RecordKey, s.TipKey });
                }

            foreach (string key in keys.Distinct())
                Assert.NotEqual(key, Loc.T(key));            // 無いキーは、キー名がそのまま返る
        }
    }
}