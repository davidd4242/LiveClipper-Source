using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class TranscriptionAudioTimingTests
{
    [Fact]
    public void NewTranscriptRecordsMediaPtsTimebaseWithoutChangingCueTimes()
    {
        const string json="{\"language\":\"zh\",\"segments\":[{\"start\":2.5,\"end\":3,\"text\":\"原句\"}]}";
        var stamped=TranscriptionService.StampMediaTimebase(json);
        using var doc=System.Text.Json.JsonDocument.Parse(stamped);
        Assert.Equal("media-pts-v1",doc.RootElement.GetProperty("audioTimebase").GetString());
        Assert.Equal(2.5,Assert.Single(TranscriptExport.Parse(stamped).Segments).Start);
    }
    [Fact]
    public async Task RealGap_ShortInputAndSplitInputsKeepSameSourceAxisAndMergedOffsets()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"TranscriptionTiming-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
        var media=new MediaTools(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe"));
        var source=Path.Combine(root,"gap.mp4");
        await media.RunAsync(media.Ffmpeg,["-n","-f","lavfi","-i","color=s=64x64:r=25:d=6","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=4","-filter_complex",@"[1:a]asetpts=PTS+0.5/TB+gte(T\,1)/TB[a]","-map","0:v","-map","[a]","-c:v","libx264","-preset","ultrafast","-c:a","aac",source],default);
        var original=File.ReadAllBytes(source);var settings=new LiveSettings{StorageRoot=root,RenderWorkDirectory=Path.Combine(root,"work")};
        var service=new TranscriptionService(new Store(Path.Combine(root,"unused.db")),settings,media);
        var whole=Path.Combine(root,"whole.wav");await service.PrepareInputAsync(source,whole,6,default);
        var shortSamples=ReadMono(whole);Assert.Equal(96000,shortSamples.Length);
        var chunks=await service.SplitAsync(source,Path.Combine(root,"split"),2,default);Assert.True(chunks.Count>=3);
        var joined=chunks.SelectMany(ReadMono).ToArray();Assert.Equal(shortSamples.Length,joined.Length);
        Assert.Equal(shortSamples,joined);
        double Rms(double t)=>Math.Sqrt(joined.Skip((int)(t*16000)).Take(1600).Select(x=>(double)x*x).Average());
        Assert.True(Rms(.1)<10);Assert.True(Rms(.8)>100);Assert.True(Rms(1.8)<10);Assert.True(Rms(2.8)>100);Assert.True(Rms(5.8)<10);
        var legacy=Path.Combine(root,"legacy.wav");await media.RunAsync(media.Ffmpeg,["-n","-i",source,"-vn","-ac","1","-ar","16000","-c:a","pcm_s16le",legacy],default);
        Assert.True(ReadMono(legacy).Length/16000d<4.2);
        var offset=0d;var parts=new List<TranscriptMerge.Part>();
        foreach(var chunk in chunks){parts.Add(new(offset,"{\"language\":\"zh\",\"segments\":[{\"start\":0,\"end\":0.1,\"text\":\"fixture\"}]}"));offset+=await media.DurationAsync(chunk,default);}
        Assert.InRange(offset,5.9999,6.0001);var merged=TranscriptExport.Parse(TranscriptMerge.Merge(parts).Json);
        Assert.InRange(merged.Segments[1].Start,1.99,2.04);Assert.InRange(merged.Segments[2].Start,3.99,4.04);
        Assert.Equal(original,File.ReadAllBytes(source));Assert.False(File.Exists(Path.Combine(root,"unused.db")));
        }
        finally{Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase);Directory.Delete(root,true);}
    }
    private static short[] ReadMono(string path)
    {
        using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);reader.ReadBytes(12);
        while(stream.Position+8<=stream.Length)
        {
            var id=new string(reader.ReadChars(4));var size=reader.ReadInt32();
            if(id=="fmt "){var pos=stream.Position;reader.ReadInt16();Assert.Equal(1,reader.ReadInt16());Assert.Equal(16000,reader.ReadInt32());stream.Position=pos+size+(size%2);}
            else if(id=="data"){var data=reader.ReadBytes(size);return Enumerable.Range(0,data.Length/2).Select(i=>BitConverter.ToInt16(data,i*2)).ToArray();}
            else stream.Position+=size+(size%2);
        }
        throw new Exception("No PCM data");
    }
}
