using LiveClipper.Core;
namespace LiveClipper.Tests;
public class SafeCacheCleanupTests
{
    [Fact]
    public void ScanAndConfirm_OnlyOwnedOldUnchangedWaveform_NotUserFiles()
    {
        var root=Path.Combine(Path.GetTempPath(),"LiveClipper-cache-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"waveforms"));
        try {
            var id=Guid.NewGuid().ToString("N");var sources=new Dictionary<string,string>{{id,Path.Combine(root,"source.mp4")}};
            var service=new SafeCacheCleanupService(root,()=>sources);
            string Make(string name) {var path=Path.Combine(root,"waveforms",name);File.WriteAllBytes(path,[137,80,78,71,13,10,26,10,1]);File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddHours(-2));return path;}
            var valid=Make(id+"-900x96.png");var unknown=Make("user.png");var recent=Make(id+"-800x96.png");File.SetLastWriteTimeUtc(recent,DateTime.UtcNow);
            var changed=Make(id+"-700x96.png");var scan=service.Scan();Assert.Equal(2,scan.Count);Assert.Equal(18,scan.Bytes);
            File.AppendAllText(changed,"changed");
            var result=service.Clean(scan.Token);Assert.Equal(1,result.DeletedCount);Assert.Equal(1,result.SkippedCount);
            Assert.False(File.Exists(valid));Assert.True(File.Exists(unknown));Assert.True(File.Exists(recent));Assert.True(File.Exists(changed));
            Assert.Throws<InvalidOperationException>(()=>service.Clean(scan.Token));
            var collision=Make(id+"-600x96.png");sources[id]=collision;
            using var held=new FileStream(changed,FileMode.Open,FileAccess.Read,FileShare.None);
            Assert.Equal(0,service.Scan().Count);
        } finally {Directory.Delete(root,true);}
    }
}
