using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LiveClipper.Domain;

namespace LiveClipper.Core;

public sealed record PremiereMediaInfo(int Width,int Height,int Timebase,bool Ntsc,bool HasAudio,int Channels,int SampleRate,double Duration,double VideoStart=0,double AudioStart=0,double FormatStart=0,bool RequiresNormalization=false)
{
    public double Fps=>Ntsc?Timebase*1000d/1001:Timebase;
}
public sealed record PremiereExportResult(string Directory,string XmlPath,string SrtPath,string[] Warnings);

/// <summary>Original-media XMEML exchange, not a rendered movie or a native Premiere project.</summary>
public sealed class PremiereExportService(Store store,StudioStore studio,MediaTools media,string outputRoot,Func<string,CancellationToken,Task<PremiereMediaInfo>>? probe=null)
{
    public async Task<PremiereExportResult> ExportAsync(string clipId,string name,EditorProject project,CancellationToken token=default,bool normalizeVariableFrameRate=false,EditorProjectIdentity? projectIdentity=null)
    {
        name=name.Trim();if(name.Length is 0 or >100)throw new ArgumentException("导出名称须为 1–100 个字符");
        project=JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
        ValidateIdentity(clipId,project,projectIdentity);
        ValidateBasics(project);
        var items=EditorRenderService.Items(project,clipId);
        var clips=store.Clips().ToDictionary(c=>c.Id,StringComparer.Ordinal);
        var ids=items.Select(i=>i.ClipId).Distinct(StringComparer.Ordinal).ToArray();
        var metadata=new Dictionary<string,PremiereMediaInfo>();
        foreach(var id in ids)
        {
            if(!clips.TryGetValue(id,out var clip)||!File.Exists(clip.Path))throw new ArgumentException("源素材不存在，无法导出 Premiere 工程包："+id);
            var info=await (probe?.Invoke(clip.Path,token)??ProbeAsync(clip.Path,token));
            if(info.Timebase<=0||info.Timebase>120||info.HasAudio&&(info.Channels is <1 or >2||info.SampleRate<=0))throw new NotSupportedException("首版仅支持整数或 NTSC 帧率、单声道或双声道素材；超过两个声道暂不支持");
            if(items.Any(i=>i.ClipId==id&&i.Track<=3)&&(info.Width<=0||info.Height<=0))throw new NotSupportedException("视频轨上的素材没有可用视频流");
            if(items.Any(i=>i.ClipId==id&&i.End>info.Duration+1/info.Fps))throw new ArgumentException("剪辑源出点超过素材时长："+id);
            if(Math.Abs(info.FormatStart)>.00001)throw new NotSupportedException("首版不支持非零容器起点素材，无法可靠确定容器时长，未导出");
            if(info.RequiresNormalization&&!normalizeVariableFrameRate)throw new NotSupportedException("素材是可变帧率或有非零流起点，请勾选转换为独立 CFR 素材后导出；原素材不会修改");
            metadata[id]=info;
        }
        var widths=items.Where(i=>!i.AudioDetached&&metadata[i.ClipId].HasAudio).Select(i=>metadata[i.ClipId].Channels).Distinct().ToArray();
        if(widths.Length>1)throw new NotSupportedException("当前不支持单声道与双声道混排；未改变原素材");
        var channels=widths.FirstOrDefault(2);
        // Freeze subtitles before asynchronous copying; never save/flush the source project.
        IReadOnlyList<TranscriptSegment> Source(string id)
        {
            if(project.CaptionOverrides?.TryGetValue(id,out var edited)==true)return EditorRenderService.NormalizeCaptions(edited);
            if(project.SourceTranscripts?.TryGetValue(id,out var frozen)==true)return frozen;
            var json=store.TranscriptJson(clips[id].EventId);return json is null?[]:TranscriptExport.Parse(json).Segments;
        }
        var subtitles=project.TimelineCaptions is {} independent?TimelineCaptionTools.ToSegments(independent):
            EditorRenderService.MapSubtitles(items.Where(i=>i.Track==1).ToArray(),Source,clipId,project.SelectedSegments,null,project.Fps,projectIdentity is null&&!EditorRenderService.HasCaptionOverride(project,clipId));
        var root=Path.GetFullPath(outputRoot);Directory.CreateDirectory(root);
        var idName=Guid.NewGuid().ToString("N");var staging=Path.Combine(root,".partial-"+idName);var final=Path.Combine(root,idName);
        Directory.CreateDirectory(staging);
        var staged=await ProjectCopyMediaStager.StageAsync(staging,ids.Select(id=>new ProjectCopyMediaStager.Source(id,clips[id].Path)).ToArray(),token);
        // Normalize the package's only media root without reusing external paths.
        Directory.Move(staged.StagingDirectory,Path.Combine(staging,"media"));
        var paths=ids.ToDictionary(id=>id,id=>Path.Combine(final,"media",Path.GetRelativePath(staged.StagingDirectory,staged.Clips[id].Media.Path)));
        var normalizedCount=0;
        foreach(var id in ids.Where(id=>metadata[id].RequiresNormalization))
        {
            var copied=Path.Combine(staging,Path.GetRelativePath(final,paths[id]));var normalized=Path.Combine(Path.GetDirectoryName(copied)!,"normalized.mov");
            await NormalizeAsync(copied,normalized,metadata[id],token);
            var check=await ProbeAsync(normalized,token);
            if(check.RequiresNormalization||Math.Abs(check.Duration-metadata[id].Duration)>1/metadata[id].Fps+.002)throw new InvalidOperationException("CFR 素材验证失败，未发布 Premiere 导出目录");
            File.Delete(copied);paths[id]=Path.Combine(final,Path.GetRelativePath(staging,normalized));metadata[id]=check;normalizedCount++;
        }
        var xml=BuildXml(name,project,items,metadata,paths,channels,projectIdentity);
        await File.WriteAllTextAsync(Path.Combine(staging,"timeline.xml"),xml.ToString(),new UTF8Encoding(false),token);
        await File.WriteAllTextAsync(Path.Combine(staging,"timeline.srt"),PremiereSrt(subtitles,project.Fps),new UTF8Encoding(false),token);
        string[] warnings=["这是 Premiere XML 交换包，不是 .prproj。尚未在实际 Premiere 环境验证兼容性。","跨电脑移动后请在 Premiere 重连 media 目录；timeline.srt 需单独导入字幕轨，字幕样式不包含在 XML 中。"];
        if(normalizedCount>0)warnings=[..warnings,$"{normalizedCount} 个素材已转换为独立 H.264 + PCM CFR MOV；起点/剪点量化误差不超过一输出帧，需在 Premiere 复核。"] ;
        await File.WriteAllTextAsync(Path.Combine(staging,"README.txt"),"Premiere XML 交换包\n\n请完整复制本目录。先在 Premiere 导入 timeline.xml；移动目录或跨电脑使用时，请重连 media 目录中的素材。再单独导入 timeline.srt 到字幕轨。\nXML 使用本次导出素材的本地文件 URI，未承诺跨平台免重连。\n单声道序列仅使用源通道1和一个输出；双声道序列左右声道分别路由至输出1/2，不支持单声道与双声道混排；静态主音量以线性增益导出，原声音量或动态自动化暂不支持。\n本包不生成 .prproj，不包含字幕样式；未在实际 Premiere 环境验证兼容性。\n未勾选转换时素材为独立原文件副本；勾选后，仅需规范化的素材转换为 H.264 + PCM CFR MOV，原素材不变。\nCFR 转换保留容器共同时间原点，视频前导空白补黑、音频前导空白补静音；起点和剪点量化误差上限一输出帧。转换会额外占用磁盘并耗时，请在 Premiere 复核同步。\n"+string.Join("\n",warnings),new UTF8Encoding(false),token);
        token.ThrowIfCancellationRequested();Directory.Move(staging,final);
        return new(final,Path.Combine(final,"timeline.xml"),Path.Combine(final,"timeline.srt"),warnings);
    }
    private async Task<PremiereMediaInfo> ProbeAsync(string path,CancellationToken token)
    {
        var json=await media.RunAsync(media.Ffprobe,["-v","error","-show_streams","-show_format","-of","json",path],token);
        using var document=JsonDocument.Parse(json);var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var videos=streams.Where(s=>s.GetProperty("codec_type").GetString()=="video").ToArray();
        var audios=streams.Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
        if(videos.Length>1||audios.Length>1||videos.Length+audios.Length==0)throw new NotSupportedException("首版不支持多视频流、多音频流或无音视频流素材");
        var timebase=30;var ntsc=false;var width=0;var height=0;var variable=false;
        if(videos.Length==1)
        {
            var video=videos[0];var rate=video.GetProperty("avg_frame_rate").GetString()!;
            (timebase,ntsc)=ParseRate(video.GetProperty("r_frame_rate").GetString()!);
            try{variable=ParseRate(rate)!=(timebase,ntsc);}catch(NotSupportedException){variable=true;}
            width=video.GetProperty("width").GetInt32();height=video.GetProperty("height").GetInt32();
            if(video.TryGetProperty("sample_aspect_ratio",out var sar)&&sar.GetString() is not (null or "N/A" or "1:1" or "0:1"))throw new NotSupportedException("首版不支持非方形像素素材");
            if(video.TryGetProperty("side_data_list",out var side)&&side.EnumerateArray().Any(s=>s.TryGetProperty("rotation",out var r)&&r.GetDouble()!=0))throw new NotSupportedException("首版不支持带旋转元数据的素材");
        }
        var duration=double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        static double Start(JsonElement value)=>value.TryGetProperty("start_time",out var start)?double.Parse(start.GetString()!,CultureInfo.InvariantCulture):0;
        var formatStart=Start(document.RootElement.GetProperty("format"));var videoStart=videos.Length==1?Start(videos[0]):formatStart;var audioStart=audios.Length==1?Start(audios[0]):formatStart;
        return new(width,height,timebase,ntsc,audios.Length==1,audios.Length==1?audios[0].GetProperty("channels").GetInt32():0,audios.Length==1?int.Parse(audios[0].GetProperty("sample_rate").GetString()!,CultureInfo.InvariantCulture):0,duration,videoStart,audioStart,formatStart,variable||Math.Abs(videoStart-formatStart)>.00001||Math.Abs(audioStart-formatStart)>.00001);
    }
    internal static string[] NormalizationArguments(string input,string output,PremiereMediaInfo info)
    {
        if(Math.Abs(info.FormatStart)>.00001)throw new NotSupportedException("首版不支持非零容器起点素材");
        var videoOffset=info.VideoStart-info.FormatStart;var audioOffset=info.AudioStart-info.FormatStart;
        if(videoOffset<-.00001||audioOffset<-.00001||!double.IsFinite(info.Duration)||info.Duration<=0)throw new NotSupportedException("素材流起点早于容器起点或时长无效，无法安全转换");
        string N(double value)=>value.ToString("0.#########",CultureInfo.InvariantCulture);
        var rate=info.Ntsc?$"{info.Timebase*1000}/1001":info.Timebase.ToString(CultureInfo.InvariantCulture);
        var video=$"[0:v:0]setpts=PTS-STARTPTS,tpad=start_mode=add:start_duration={N(Math.Max(0,videoOffset))}:color=black,fps={rate}:start_time=0,tpad=stop_mode=clone:stop_duration={N(info.Duration)},trim=duration={N(info.Duration)}[v]";
        var audio=$"[0:a:0]asetpts=PTS-({N(info.FormatStart)})/TB,{TimelineAudio.PreserveTimestampsFilter},apad,atrim=duration={N(info.Duration)}[a]";
        if(info.Width<=0)throw new NotSupportedException("首版 CFR 转换只支持包含视频的素材");
        return ["-hide_banner","-y","-copyts","-i",input,"-filter_complex",video+(info.HasAudio?";"+audio:""),"-map","[v]",..(info.HasAudio?new[]{"-map","[a]"}:Array.Empty<string>()),"-c:v","libx264","-preset","fast","-crf","18","-pix_fmt","yuv420p","-fps_mode","cfr",..(info.HasAudio?new[]{"-c:a","pcm_s16le"}:Array.Empty<string>()),"-movflags","+faststart",output];
    }
    private Task<string> NormalizeAsync(string input,string output,PremiereMediaInfo info,CancellationToken token)=>media.RunAsync(media.Ffmpeg,NormalizationArguments(input,output,info),token);
    internal static (int Timebase,bool Ntsc) ParseRate(string value)
    {
        var parts=value.Split('/');if(parts.Length!=2||!long.TryParse(parts[0],out var n)||!long.TryParse(parts[1],out var d)||n<=0||d<=0)throw new NotSupportedException("素材帧率无效");
        if(n%d==0&&n/d<=120)return ((int)(n/d),false);
        foreach(var rate in new[]{24,30,60,120})if(n*1001==d*rate*1000L)return (rate,true);
        throw new NotSupportedException("首版仅支持整数或 1000/1001 NTSC 素材帧率："+value);
    }
    internal static void ValidateIdentity(string projectId,EditorProject project,EditorProjectIdentity? identity)
    {
        if(identity is null)return;
        if(identity.Id!=projectId||identity.Fps!=project.Fps||identity.Width<=0||identity.Height<=0||
            (project.CanvasWidth is {} cw&&cw!=identity.Width)||(project.CanvasHeight is {} ch&&ch!=identity.Height))
            throw new ArgumentException("Independent project identity or canvas does not match export request.");
        if(project.Timeline is not {Length:>0})throw new ArgumentException("Independent project has no timeline segments to export.");
    }

    internal static void ValidateBasics(EditorProject p,string target="Premiere")
    {
        EditorRenderService.Validate(p);
        if(p.Timeline?.Any(i=>i.SequenceStart is not null)==true)throw new NotSupportedException(target+" does not support absolute overlapping sequence placement; no clips were moved or trimmed.");
        if(p.Fps is not (24 or 25 or 30 or 50 or 60))throw new NotSupportedException("首版序列帧率仅支持 24/25/30/50/60");
        if(p.Transitions is {Length:>0}||p.MusicAssetId is not null||p.EffectAssetId is not null||p.VoiceOnly||p.VoiceVolume!=1||p.TrackOpacities?.Any(v=>v!=1)==true)
            throw new NotSupportedException(target+" 首版不支持转场、全局音乐/音效、分离人声或原声音量/透明度处理");
        foreach(var i in p.Timeline??[])
            if(i.Speed!=1||i.NestedSequenceId is not null||i.Zoom!=1||i.Rotation!=0||i.Opacity!=1||i.PositionX!=0||i.PositionY!=0||i.Filter!="none"||i.Motion!="none"||i.StickerAssetId is not null||i.SoundAssetId is not null||!string.IsNullOrEmpty(i.TitleText)||i.GraphicKind is not null||i.FadeIn!=0||i.FadeOut!=0||i.VolumeDb!=0||i.Grade is not null&&i.Grade!=new ColorGrade()||i.VolumeKeyframes is {Length:>0}||i.Keyframes?.Count>0||i.Masks?.Any(m=>m.Enabled)==true||i.Effects?.Any(e=>e.Enabled)==true||i.CatalogFx?.Any(e=>e.Enabled)==true||i.CatalogAudioFx?.Any(e=>e.Enabled)==true)
                throw new NotSupportedException(target+" 首版无法保真导出该片段的变速、嵌套、图形、变换或效果："+i.Id);
        if(p.ReplicaExtension is {} extension)
        {
            if(extension.Compatibility.TryGetProperty("warnings",out var warnings)&&warnings.EnumerateArray().Any(w=>w.GetString() is {} s&&(s.StartsWith("projection_unimplemented"))))throw new NotSupportedException("当前工程含尚未映射的 native 效果，不能保真导出 "+target);
            CheckNative(extension.Document,target);
        }
    }
    private static void CheckNative(JsonElement document,string target)
    {
        if(!document.TryGetProperty("sequence",out var sequence))throw new NotSupportedException(target+" 不支持该 native 工程结构");
        foreach(var group in new[]{"videoTracks","audioTracks"})
            if(sequence.TryGetProperty(group,out var tracks))foreach(var track in tracks.EnumerateArray())
                if(track.TryGetProperty("clips",out var clips))foreach(var clip in clips.EnumerateArray())
                    foreach(var field in new[]{"effects","gpuFx","gpuEffects","effectGraph","mask","masks","transition","nestedSequenceId","multicam"})
                        if(clip.TryGetProperty(field,out var value)&&value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)&&!(value.ValueKind==JsonValueKind.Array&&!value.EnumerateArray().Any(e=>e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty("enabled",out var en)||en.ValueKind!=JsonValueKind.False))&&!(value.ValueKind==JsonValueKind.Object&&!value.EnumerateObject().Any()))
                            throw new NotSupportedException(target+" 首版不支持 native 片段字段："+field);
    }
    internal static string PremiereFileUrl(string path)
    {
        var full=Path.GetFullPath(path).Replace('\\','/');
        if(full.StartsWith("//",StringComparison.Ordinal))throw new NotSupportedException("Premiere 首版仅支持本地磁盘素材路径，不支持 UNC 路径");
        return "file://localhost/"+string.Join("/",full.TrimStart('/').Split('/').Select(segment=>Uri.EscapeDataString(segment).Replace("%3A","%3a",StringComparison.Ordinal)));
    }
    internal static long SourceTicks(double seconds)=>(long)Math.Round((decimal)seconds*254016000000m,0,MidpointRounding.AwayFromZero);
    internal static string PremiereSrt(IEnumerable<TranscriptSegment> segments,int fps)
    {
        if(fps<=0)throw new ArgumentException("字幕序列帧率无效");
        static string Stamp(long frame,int rate)
        {
            // Round upward to an integer millisecond so Premiere's floor(ms * fps / 1000)
            // reconstructs the requested frame, rather than the preceding frame.
            var ms=(frame*1000+rate-1)/rate;
            return $"{ms/3600000:00}:{ms/60000%60:00}:{ms/1000%60:00},{ms%1000:000}";
        }
        var lines=segments.Where(s=>s.End>s.Start&&!string.IsNullOrWhiteSpace(s.Text)).Select((s,index)=>
        {
            var start=Math.Max(0,Frames(s.Start,fps));var end=Math.Max(start+1,Frames(s.End,fps));
            return $"{index+1}\n{Stamp(start,fps)} --> {Stamp(end,fps)}\n{s.Text.Trim()}\n";
        });
        return string.Join("\n",lines);
    }
    private static long Frames(double seconds,double fps)=>(long)Math.Round(seconds*fps,MidpointRounding.AwayFromZero);
    private static XElement Rate(int timebase,bool ntsc)=>new("rate",new XElement("timebase",timebase),new XElement("ntsc",ntsc?"TRUE":"FALSE"));
    private static XDocument BuildXml(string name,EditorProject project,TimelineItem[] items,Dictionary<string,PremiereMediaInfo> metadata,Dictionary<string,string> paths,int channels,EditorProjectIdentity? projectIdentity=null)
    {
        var starts=EditorRenderService.SequenceStarts(items,[]);var first=metadata.Values.FirstOrDefault(m=>m.Width>0)??new(1280,720,project.Fps,false,false,0,0,0);
        var height=project.Resolution==0?first.Height:Math.Min(first.Height,project.Resolution);height=Math.Max(2,height/2*2);
        var width=Math.Max(2,(int)Math.Round(first.Width*(double)height/first.Height)/2*2);
        if(projectIdentity is not null){width=projectIdentity.Width;height=projectIdentity.Height;}
        else if(project.CanvasWidth is {} cw && project.CanvasHeight is {} ch){width=cw;height=ch;}
        else if(project.ReplicaExtension?.Document.TryGetProperty("sequence",out var native)==true&&native.TryGetProperty("width",out var nw)&&native.TryGetProperty("height",out var nh))
        {width=nw.GetInt32();height=nh.GetInt32();if(width<=0||height<=0)throw new NotSupportedException("native 序列尺寸无效");}
        var videoTracks=Enumerable.Range(0,3).Select(_=>new XElement("track")).ToArray();
        // Preserve each logical track slot and emit only its actual channel count.
        var audioTracks=Enumerable.Range(0,6*channels).Select(index=>new XElement("track",new XAttribute("premiereTrackType",channels==1?"Mono":"Stereo"),new XAttribute("currentExplodedTrackIndex",index%channels),new XAttribute("totalExplodedTrackCount",channels))).ToArray();
        var fileIds=paths.Keys.Select((id,index)=>(id,fileId:"file-"+(index+1))).ToDictionary(x=>x.id,x=>x.fileId);
        var emitted=new HashSet<string>();
        XElement FileElement(string id)
        {
            var fid=fileIds[id];if(!emitted.Add(id))return new XElement("file",new XAttribute("id",fid));
            var info=metadata[id];var m=new XElement("media");
            if(info.Width>0)m.Add(new XElement("video",new XElement("samplecharacteristics",Rate(info.Timebase,info.Ntsc),new XElement("width",info.Width),new XElement("height",info.Height),new XElement("anamorphic","FALSE"),new XElement("pixelaspectratio","square"),new XElement("fielddominance","none"))));
            if(info.HasAudio)m.Add(new XElement("audio",new XElement("samplecharacteristics",new XElement("depth",16),new XElement("samplerate",info.SampleRate)),new XElement("channelcount",info.Channels)));
            return new XElement("file",new XAttribute("id",fid),new XElement("name",Path.GetFileName(paths[id])),new XElement("pathurl",PremiereFileUrl(paths[id])),Rate(info.Timebase,info.Ntsc),new XElement("duration",Frames(info.Duration,info.Fps)),m);
        }
        var serial=0;
        foreach(var item in items)
        {
            var info=metadata[item.ClipId];var track=EditorRenderService.TrackOf(item);var parts=new List<(XElement Node,string Id,string Type,int Track,int Index)>();
            XElement Part(string id,string type,int channel)
            {
                var node=new XElement("clipitem",new XAttribute("id",id),new XElement("masterclipid","master-"+fileIds[item.ClipId]),new XElement("name",item.Id),new XElement("enabled",item.Disabled?"FALSE":"TRUE"),new XElement("duration",Frames(info.Duration,project.Fps)),Rate(project.Fps,false),
                    new XElement("start",Frames(starts[item.Id],project.Fps)),new XElement("end",Frames(starts[item.Id]+item.End-item.Start,project.Fps)),new XElement("in",Frames(item.Start,project.Fps)),new XElement("out",Frames(item.End,project.Fps)),new XElement("pproTicksIn",SourceTicks(item.Start)),new XElement("pproTicksOut",SourceTicks(item.End)),FileElement(item.ClipId));
                if(type=="audio")
                {
                    node.Add(new XAttribute("premiereChannelType",channels==1?"mono":"stereo"));
                    node.Add(new XElement("sourcetrack",new XElement("mediatype","audio"),new XElement("trackindex",channel)));
                    // Apple XMEML Topics: Audio Levels / Level is linear gain, not decibels.
                    // With auxiliary mixing/effects rejected, distributing a static master gain is linear-equivalent.
                    if(project.MasterVolume!=1)node.Add(new XElement("filter",new XElement("effect",
                        new XElement("name","Audio Levels"),new XElement("effectid","audiolevels"),new XElement("effectcategory","audiolevels"),new XElement("effecttype","audiolevels"),new XElement("mediatype","audio"),
                        new XElement("parameter",new XElement("parameterid","level"),new XElement("name","Level"),new XElement("valuemin",0),new XElement("valuemax",3.98109),new XElement("value",project.MasterVolume)))));
                }
                return node;
            }
            serial++;
            if(track<=3){var node=Part("clip-"+serial+"-v","video",0);videoTracks[track-1].Add(node);parts.Add((node,node.Attribute("id")!.Value,"video",track,videoTracks[track-1].Elements("clipitem").Count()));}
            if(info.HasAudio&&!item.AudioDetached)for(var channel=1;channel<=info.Channels;channel++)
            {
                var index=(track-1)*channels+channel;var node=Part("clip-"+serial+"-a"+channel,"audio",channel);audioTracks[index-1].Add(node);parts.Add((node,node.Attribute("id")!.Value,"audio",index,audioTracks[index-1].Elements("clipitem").Count()));
            }
            foreach(var part in parts)foreach(var linked in parts)part.Node.Add(new XElement("link",new XElement("linkclipref",linked.Id),new XElement("mediatype",linked.Type),new XElement("trackindex",linked.Track),new XElement("clipindex",linked.Index),linked.Type=="audio"?new XElement("groupindex",1):null));
        }
        for(var i=0;i<videoTracks.Length;i++)videoTracks[i].Add(new XElement("enabled",project.HiddenTracks?.Contains(i+1)==true?"FALSE":"TRUE"),new XElement("locked","FALSE"));
        for(var i=0;i<audioTracks.Length;i++)audioTracks[i].Add(new XElement("outputchannelindex",i%channels+1),new XElement("enabled",(i/channels==0&&project.BaseAudioMuted)||!EditorRenderService.IncludeTrackAudio(i/channels+1,project.MutedTracks,project.SoloedTracks)?"FALSE":"TRUE"),new XElement("locked","FALSE"));
        var video=new XElement("video",new XElement("format",new XElement("samplecharacteristics",Rate(project.Fps,false),new XElement("width",width),new XElement("height",height),new XElement("anamorphic","FALSE"),new XElement("pixelaspectratio","square"),new XElement("fielddominance","none"))),videoTracks);
        var audio=new XElement("audio",new XElement("numOutputChannels",channels),new XElement("outputs",Enumerable.Range(1,channels).Select(channel=>new XElement("group",new XElement("index",channel),new XElement("numchannels",1),new XElement("downmix",0),new XElement("channel",new XElement("index",channel))))),new XElement("format",new XElement("samplecharacteristics",new XElement("depth",16),new XElement("samplerate",48000))),audioTracks);
        return new XDocument(new XDeclaration("1.0","UTF-8",null),new XDocumentType("xmeml",null,null,null),new XElement("xmeml",new XAttribute("version","5"),new XElement("sequence",new XAttribute("id","sequence-1"),new XAttribute("explodedTracks","true"),new XElement("name",name),new XElement("duration",Frames(EditorRenderService.TimelineDuration(items),project.Fps)),Rate(project.Fps,false),new XElement("media",video,audio))));
    }
}
