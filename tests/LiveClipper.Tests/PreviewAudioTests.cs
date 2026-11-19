using System.Net;
using System.Net.Http.Headers;
using LiveClipper.Core;
using LiveClipper.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace LiveClipper.Tests;
public sealed class PreviewAudioTests
{
    [Fact]
    public async Task RealHttpPcmProxyPreservesAudioOffsetAndPtsGap_RangeCacheAndSourceImmutable()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"PreviewAudio-"+Guid.NewGuid().ToString("N"))).FullName;
        var media=new MediaTools(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Links","ffmpeg.exe"));
        var source=Path.Combine(root,"gap.mp4");
        await media.RunAsync(media.Ffmpeg,["-n","-f","lavfi","-i","color=s=64x64:r=25:d=5","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=3","-filter_complex",@"[1:a]asetpts=PTS+0.5/TB+gte(T\,1)/TB[a]","-map","0:v","-map","[a]","-c:v","libx264","-preset","ultrafast","-c:a","aac",source],default);
        var original=File.ReadAllBytes(source);
        var store=new Store(Path.Combine(root,"fixture.db"));store.Migrate();var studio=new StudioStore(store.DatabasePath);studio.Migrate();
        var clip=new Clip("gap","event",source,null,DateTimeOffset.UtcNow);store.SaveClip(clip);
        var settings=new LiveSettings{StorageRoot=Path.Combine(root,"storage"),RenderWorkDirectory=Path.Combine(root,"configured-work")};
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");await using var app=builder.Build();
        StudioEndpoints.Map(app,store,studio,null!,null!,null!,null!,null!,media,null!,settings);await app.StartAsync();
        using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        const string url="/api/studio/clips/gap/preview-audio.wav";
        var response=await client.GetAsync(url);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("audio/wav",response.Content.Headers.ContentType!.MediaType);
        var wav=await response.Content.ReadAsByteArrayAsync();var samples=ReadPcm(wav);Assert.InRange(samples.Length/(48000d*2),4.999,5.001);
        double Rms(double time){var start=(int)(time*48000)*2;return Math.Sqrt(samples.Skip(start).Take(4800*2).Select(x=>(double)x*x).Average());}
        Assert.True(Rms(.1)<10);Assert.True(Rms(.8)>100);Assert.True(Rms(1.8)<10);Assert.True(Rms(2.8)>100);Assert.True(Rms(4.8)<10);
        var cached=Assert.Single(Directory.GetFiles(Path.Combine(settings.RenderWorkDirectory,"preview-audio"),"*.wav"));var stamp=File.GetLastWriteTimeUtc(cached);
        using var range=new HttpRequestMessage(HttpMethod.Get,url);range.Headers.Range=new RangeHeaderValue(0,43);var partial=await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent,partial.StatusCode);Assert.Equal(44,(await partial.Content.ReadAsByteArrayAsync()).Length);Assert.Equal(stamp,File.GetLastWriteTimeUtc(cached));
        Assert.Equal(original,File.ReadAllBytes(source));Assert.False(Directory.Exists(Path.Combine(settings.StorageRoot,"render-work")));
        File.SetLastWriteTimeUtc(source,File.GetLastWriteTimeUtc(source).AddSeconds(2));Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(url)).StatusCode);Assert.Equal(2,Directory.GetFiles(Path.Combine(settings.RenderWorkDirectory,"preview-audio"),"*.wav").Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(settings.RenderWorkDirectory,"preview-audio"),"*.partial.wav"));
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new PreviewAudioService(media).GetAsync(source,settings.RenderWorkDirectory,cancel.Token));
    }
    private static short[] ReadPcm(byte[] wav)
    {
        using var stream=new MemoryStream(wav);using var reader=new BinaryReader(stream);Assert.Equal("RIFF",new string(reader.ReadChars(4)));reader.ReadInt32();Assert.Equal("WAVE",new string(reader.ReadChars(4)));
        while(stream.Position+8<=stream.Length)
        {
            var kind=new string(reader.ReadChars(4));var length=reader.ReadInt32();
            if(kind=="fmt "){var start=stream.Position;reader.ReadInt16();Assert.Equal(2,reader.ReadInt16());Assert.Equal(48000,reader.ReadInt32());stream.Position=start+length+(length%2);}
            else if(kind=="data"){var bytes=reader.ReadBytes(length);return Enumerable.Range(0,bytes.Length/2).Select(i=>BitConverter.ToInt16(bytes,i*2)).ToArray();}
            else stream.Position+=length+(length%2);
        }
        throw new Exception("WAV data chunk missing");
    }
}
