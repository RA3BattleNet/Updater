using Ra3.BattleNet.Updater.Client;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void WhenLockIsHeld_SecondRunIsRejectedWithoutTouchingAnything()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        var cache = Path.Combine(root, "UpdaterCache");
        Directory.CreateDirectory(cache);

        // 模拟"已有另一个实例在更新"：独占占住锁文件
        using var held = new FileStream(
            Path.Combine(cache, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var cfg = new UpdateConfig
        {
            RootPath = root,
            CacheDir = TestSupport.TestCacheDir(root),
            // 故意给一个连不上的地址：如果没被锁挡住，这里会去联网并返回别的错误
            ManifestUrl = "http://127.0.0.1:1/manifest.xml",
        };

        var result = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.AlreadyRunning, result.Reason);
        Assert.Equal(0, result.BytesDownloaded);
    }
}
