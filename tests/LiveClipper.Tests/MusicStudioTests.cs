using System.Globalization;
using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 音乐工作室 v1.1 数据面：BPM/beat 分析、节拍循环建议、ducking 包络、响度测量与降级路径。
/// 规格：docs/music-studio-v1.1.md；分析结果存 studio_documents（scope=music-analysis / music-ducking）。
/// </summary>
public sealed class MusicStudioTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public MusicStudioTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;

    private static VolumeKeyframe K(double time,double db)=>new(time,db);

    private static MusicVoiceInterval V(double start,double end)=>new(start,end);

    [Fact]
    public void BuildBeats_FromBpm_ProducesGridAndDegradesToEmpty()
    {
        var beats=MusicGenerationService.BuildBeats(120,2.0);
        Assert.Equal(new[]{0d,0.5,1d,1.5},beats);

        var slow=MusicGenerationService.BuildBeats(60,2.5);
        Assert.Equal(new[]{0d,1d,2d},slow);

        Assert.Empty(MusicGenerationService.BuildBeats(null,10));
        Assert.Empty(MusicGenerationService.BuildBeats(0,10));
        Assert.Empty(MusicGenerationService.BuildBeats(-120,10));
        Assert.Empty(MusicGenerationService.BuildBeats(120,0));
    }

    [Fact]
    public void BeatInterval_IsSixtyOverBpm()
    {
        Assert.Equal(0.5,MusicGenerationService.BeatInterval(120),6);
        Assert.Equal(0.46875,MusicGenerationService.BeatInterval(128),6);
        Assert.Equal(0,MusicGenerationService.BeatInterval(0),6);
        Assert.Equal(0,MusicGenerationService.BeatInterval(-10),6);
    }

    [Fact]
    public void SuggestBeatLoop_AlignsToWholeBarsAndClampsToDuration()
    {
        // 120 BPM → 每拍 0.5s，每小节 2s；目标 3.2s 取 1 小节。
        var oneBar=MusicGenerationService.SuggestBeatLoop(120,10,3.2,4);
        Assert.NotNull(oneBar);
        Assert.Equal(2,oneBar!.Seconds,6);
        Assert.Equal(4,oneBar.BeatCount);
        Assert.Equal(1,oneBar.BarCount);
        Assert.Equal("bar-aligned",oneBar.Method);

        // 目标超过音频时长时裁剪到整小节并封顶在音频长度内。
        var clamped=MusicGenerationService.SuggestBeatLoop(120,8,30,4);
        Assert.NotNull(clamped);
        Assert.Equal(8,clamped!.Seconds,6);
        Assert.Equal(4,clamped.BarCount);

        // 三拍子：每小节 1.5s。
        var triple=MusicGenerationService.SuggestBeatLoop(120,10,10,3);
        Assert.NotNull(triple);
        Assert.Equal(9,triple!.Seconds,6);

        Assert.Null(MusicGenerationService.SuggestBeatLoop(null,10,30,4));
        Assert.Null(MusicGenerationService.SuggestBeatLoop(0,10,30,4));
        Assert.Null(MusicGenerationService.SuggestBeatLoop(120,0,30,4));
    }

    [Fact]
    public void MergeVoiceIntervals_ClipsSortsMergesAndDropsTooShort()
    {
        var merged=MusicGenerationService.MergeVoiceIntervals(new[]
        {
            V(3.0,4.0),
            V(1.0,2.0),
            V(2.05,2.5),   // 与上一段间隔 0.05s → 合并
            V(0.0,0.1),    // 太短（<0.12s）→ 丢弃
            V(-2,0.5),     // 裁剪到 0
            V(9,12),       // 裁剪到 10
        },0,10);
        Assert.Equal(4,merged.Length);
        Assert.Equal(0,merged[0].Start,6);
        Assert.Equal(0.5,merged[0].End,6);
        Assert.Equal(1,merged[1].Start,6);
        Assert.Equal(2.5,merged[1].End,6);
        Assert.Equal(3,merged[2].Start,6);
        Assert.Equal(4,merged[2].End,6);
        Assert.Equal(9,merged[3].Start,6);
        Assert.Equal(10,merged[3].End,6);
    }

    [Fact]
    public void BuildDuckingKeyframes_DucksBeforeDuringAndAfterVoice()
    {
        var keys=MusicGenerationService.BuildDuckingKeyframes(new[]{V(2,3)},0,10,-12,0.2,0.3);
        Assert.Equal(5,keys.Length);
        Assert.Equal(0,keys[0].Time,3);Assert.Equal(0,keys[0].Db,6);
        Assert.Equal(1.8,keys[1].Time,3);Assert.Equal(0,keys[1].Db,6);
        Assert.Equal(2,keys[2].Time,3);Assert.Equal(-12,keys[2].Db,6);
        Assert.Equal(3,keys[3].Time,3);Assert.Equal(-12,keys[3].Db,6);
        Assert.Equal(3.3,keys[4].Time,3);Assert.Equal(0,keys[4].Db,6);
        // 时间轴单调递增，渲染端可直接线性插值。
        Assert.True(keys.Zip(keys.Skip(1)).All(pair=>pair.First.Time<pair.Second.Time));
    }

    [Fact]
    public void BuildDuckingKeyframes_ClampsDbAndCapsAt64()
    {
        var voices=Enumerable.Range(0,40).Select(index=>V(index*1.0,index*1.0+0.6)).ToArray();
        var keys=MusicGenerationService.BuildDuckingKeyframes(voices,0,40,100,0.1,0.1);
        Assert.True(keys.Length<=64);
        Assert.All(keys,key=>Assert.InRange(key.Db,-60,12));
        Assert.All(keys,key=>Assert.InRange(key.Time,0,40));

        // 无人生区间时至少保留起点 0dB，避免生成空包络。
        var empty=MusicGenerationService.BuildDuckingKeyframes(Array.Empty<MusicVoiceInterval>(),0,10,-14,0.1,0.2);
        Assert.Single(empty);
        Assert.Equal(0,empty[0].Db,6);
    }

    [Fact]
    public void ParseWorkerResult_ReadsBpmBeatsAndToleratesMissingValues()
    {
        var parsed=MusicGenerationService.ParseWorkerResult("""{"bpm":128.0,"beats":[0,0.46875,0.9375],"method":"builtin-preset","confidence":1.0,"durationSeconds":12.0,"degraded":false}""");
        Assert.NotNull(parsed);
        Assert.Equal(128,parsed!.Bpm!.Value,6);
        Assert.Equal(new[]{0d,0.46875,0.9375},parsed.Beats);
        Assert.Equal("builtin-preset",parsed.Method);
        Assert.Equal(1.0,parsed.Confidence,6);
        Assert.Equal(12.0,parsed.DurationSeconds,6);

        var nullBpm=MusicGenerationService.ParseWorkerResult("""{"bpm":null,"beats":[],"method":"musicgen-autocorrelation"}""");
        Assert.NotNull(nullBpm);
        Assert.Null(nullBpm!.Bpm);
        Assert.Empty(nullBpm.Beats);

        Assert.Null(MusicGenerationService.ParseWorkerResult(null));
        Assert.Null(MusicGenerationService.ParseWorkerResult("{not json"));
        Assert.Null(MusicGenerationService.ParseWorkerResult(""));
    }

    [Fact]
    public void AnalyzePcm_DetectsTempoFromPulseTrain_AndDegradesOnSilence()
    {
        const int rate=16000;
        var samples=new float[8*rate];
        var random=new Random(7);
        for(var beat=0;beat<16;beat++)
        {
            var start=beat*rate/2; // 120 BPM：每 0.5s 一拍
            var click=rate*20/1000;
            for(var index=0;index<click&&start+index<samples.Length;index++)
                samples[start+index]=(float)((random.NextDouble()*2-1)*0.6*(1-index/(double)click));
        }
        var analysis=MusicGenerationService.AnalyzePcm(samples,rate);
        Assert.NotNull(analysis);
        Assert.NotNull(analysis!.Bpm);
        Assert.InRange(analysis.Bpm!.Value,116,124);
        Assert.NotEmpty(analysis.Beats);

        var silence=MusicGenerationService.AnalyzePcm(new float[rate*3],rate);
        Assert.Null(silence);
        Assert.Null(MusicGenerationService.AnalyzePcm(Array.Empty<float>(),rate));
    }

    [Fact]
    public void ReadWavMono_ReadsPcm16AndRejectsGarbage()
    {
        var root=NewTempRoot("wav");
        try
        {
            var path=TestAudio.WriteSineWav(Path.Combine(root,"tone.wav"),22050,1.0);
            var read=MusicGenerationService.ReadWavMono(path);
            Assert.NotNull(read);
            Assert.Equal(22050,read!.Value.SampleRate);
            Assert.Equal(22050,read.Value.Samples.Length);
            Assert.Contains(read.Value.Samples,value=>Math.Abs(value)>0.05f);

            var garbage=Path.Combine(root,"bad.wav");
            File.WriteAllBytes(garbage,[1,2,3,4,5,6,7,8]);
            Assert.Null(MusicGenerationService.ReadWavMono(garbage));
            Assert.Null(MusicGenerationService.ReadWavMono(Path.Combine(root,"missing.wav")));
        }
        finally{TryDelete(root);}
    }

    [Fact]
    public void ParseLoudnormLufs_ReadsMeasuredValueAndRejectsGarbage()
    {
        const string output="""
            [Parsed_loudnorm_0 @ 0000] 
            {
                "input_i" : "-16.72",
                "input_tp" : "-2.10",
                "input_lra" : "3.20",
                "input_thresh" : "-27.11",
                "output_i" : "-14.00"
            }
            """;
        Assert.Equal(-16.7,MusicGenerationService.ParseLoudnormLufs(output)!.Value,6);
        Assert.Null(MusicGenerationService.ParseLoudnormLufs(null));
        Assert.Null(MusicGenerationService.ParseLoudnormLufs("no json here"));
        Assert.Null(MusicGenerationService.ParseLoudnormLufs("""{"input_i":"-inf"}"""));
    }

    [Fact]
    public void SaveLoadAnalysis_RoundtripsThroughStudioDocuments()
    {
        var root=NewTempRoot("documents");
        try
        {
            var studio=new StudioStore(Path.Combine(root,"liveclipper.db"));
            studio.Migrate();
            var analysis=new MusicBeatAnalysis(120,new[]{0d,0.5,1d},"builtin-preset",1.0,12,MeasuredLufs:-16.5,TargetLufs:-14,LoudnessMethod:"ffmpeg-loudnorm",AnalyzedAt:DateTimeOffset.UtcNow);
            MusicGenerationService.SaveAnalysis(studio,"asset-1",analysis);
            var loaded=MusicGenerationService.LoadAnalysis(studio,"asset-1");
            Assert.NotNull(loaded);
            Assert.Equal(120,loaded!.Bpm!.Value,6);
            Assert.Equal(new[]{0d,0.5,1d},loaded.Beats);
            Assert.Equal(-16.5,loaded.MeasuredLufs!.Value,6);
            Assert.Null(MusicGenerationService.LoadAnalysis(studio,"missing"));

            var envelope=new MusicDuckingEnvelope("clip-1","music-1","project-document",-14,new[]{V(1,2)},new[]{K(0,0),K(1,-14),K(2,-14),K(2.3,0)},[],["测试告警"],DateTimeOffset.UtcNow);
            MusicGenerationService.SaveDucking(studio,envelope);
            var ducked=MusicGenerationService.LoadDucking(studio,"clip-1");
            Assert.NotNull(ducked);
            Assert.Equal("clip-1",ducked!.ClipId);
            Assert.Equal("music-1",ducked.MusicAssetId);
            Assert.Equal(-14,ducked.TargetDb,6);
            Assert.Single(ducked.VoiceIntervals);
            Assert.Equal(4,ducked.Keyframes.Length);
            Assert.Equal("测试告警",Assert.Single(ducked.Warnings));
            Assert.Null(MusicGenerationService.LoadDucking(studio,"clip-missing"));
        }
        finally{TryDelete(root);}
    }

    [Fact]
    public async Task LoudnormIntegration_MeasuresSyntheticWav()
    {
        var ffmpeg=TestFfmpeg();
        // 原来用 File.Exists(ffmpeg) 判据：TestFfmpeg 回落为裸名 "ffmpeg" 时恒为 false，PATH 上有 ffmpeg 也永远跳过。
        var media=new MediaTools(ffmpeg);
        if(!media.Available()){_output.WriteLine("[skip] 未找到 ffmpeg，loudnorm 集成测试跳过（设置 LIVECLIPPER_TEST_FFMPEG 指向 ffmpeg 可执行文件）");return;}
        var root=NewTempRoot("loudnorm");
        try
        {
            var wav=TestAudio.WriteSineWav(Path.Combine(root,"tone.wav"),22050,2.0);
            var lufs=await MusicGenerationService.MeasureLoudnessAsync(media,wav,CancellationToken.None);
            Assert.NotNull(lufs);
            Assert.InRange(lufs!.Value,-40,0);
        }
        finally{TryDelete(root);}
    }

    private static string TestFfmpeg()
    {
        var fromEnv=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG");
        if(!string.IsNullOrWhiteSpace(fromEnv)&&File.Exists(fromEnv))return fromEnv;
        var bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveClipper","tools","ffmpeg","bin","ffmpeg.exe");
        return File.Exists(bundled)?bundled:"ffmpeg";
    }

    private static string NewTempRoot(string scenario)
    {
        var root=Path.Combine(Path.GetTempPath(),"liveclipper-music-tests",scenario+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try{if(Directory.Exists(path))Directory.Delete(path,true);}catch{}
    }
}
