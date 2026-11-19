using System.IO;
using Koromogae;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>二重起動の判定に使う名前 (保存先ごと) と、最小化でトレイへ格納する設定。</summary>
public class SingleInstanceNameTests
{
    [Fact]
    public void The_same_data_folder_gives_the_same_names_whatever_the_spelling()
    {
        string a = SingleInstanceName.MutexName(@"C:\Apps\Koromogae");

        Assert.Equal(a, SingleInstanceName.MutexName(@"c:\apps\koromogae"));          // 大文字小文字
        Assert.Equal(a, SingleInstanceName.MutexName(@"C:\Apps\Koromogae\"));         // 末尾の区切り
        Assert.Equal(a, SingleInstanceName.MutexName("C:/Apps/Koromogae"));           // / と \
        Assert.Equal(a, SingleInstanceName.MutexName(@"  C:\Apps\Koromogae  "));      // 前後の空白
        Assert.Equal(SingleInstanceName.ActivateEventName(@"C:\Apps\Koromogae"), SingleInstanceName.ActivateEventName(@"c:\APPS\koromogae\"));
    }

    [Fact]
    public void Different_data_folders_give_different_names_so_portable_copies_can_run_together()
    {
        // 別の場所にコピーしたポータブル版は、保存先 (本体のフォルダ) が違うので、互いに妨げない
        Assert.NotEqual(SingleInstanceName.MutexName(@"D:\copy1\Koromogae"), SingleInstanceName.MutexName(@"D:\copy2\Koromogae"));
        Assert.NotEqual(SingleInstanceName.ActivateEventName(@"D:\copy1\Koromogae"), SingleInstanceName.ActivateEventName(@"D:\copy2\Koromogae"));
        Assert.NotEqual(SingleInstanceName.MutexName(@"C:\Users\a\AppData\Roaming\Koromogae"), SingleInstanceName.MutexName(@"D:\copy1\Koromogae"));
    }

    [Fact]
    public void The_mutex_and_the_event_have_separate_session_local_names_that_are_valid()
    {
        string mutex = SingleInstanceName.MutexName(@"C:\x");
        string activate = SingleInstanceName.ActivateEventName(@"C:\x");

        Assert.NotEqual(mutex, activate);
        Assert.StartsWith(@"Local\", mutex);
        Assert.StartsWith(@"Local\", activate);
        Assert.True(mutex.Length < 200 && activate.Length < 200);
        Assert.DoesNotContain('\\', mutex[@"Local\".Length..]);   // 名前の中に区切りが入らない (保存先のパスをそのまま使わない)
        Assert.DoesNotContain('\\', activate[@"Local\".Length..]);
        using var m = new Mutex(false, mutex);                  // 実際に作れる名前
        using var e = new EventWaitHandle(false, EventResetMode.AutoReset, activate);
    }

    [Fact]
    public void Minimizing_to_the_tray_is_off_by_default_and_in_an_old_settings_file()
    {
        Assert.False(AppSettings.DefaultMinimizeToTray);
        Assert.False(new AppSettings().MinimizeToTray);

        string dir = Path.Combine(Path.GetTempPath(), "KoromogaeUiNames_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"SampleRate":48000}""");             // この設定が無い古いファイル
            Assert.False(AppSettings.Load(path).MinimizeToTray);

            File.WriteAllText(path, """{"MinimizeToTray":true}""");
            Assert.True(AppSettings.Load(path).MinimizeToTray);

            Assert.True(new AppSettings { MinimizeToTray = true }.Save(path));
            Assert.True(AppSettings.Load(path).MinimizeToTray);               // 保存して読み直しても変わらない
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
