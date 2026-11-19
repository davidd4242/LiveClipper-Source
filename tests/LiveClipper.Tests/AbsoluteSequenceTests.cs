using LiveClipper.Core;
namespace LiveClipper.Tests;
public sealed class AbsoluteSequenceTests
{
    private static TimelineItem Item(string id,double at,double length=2)=>new(id,"source",10,10+length,SequenceStart:at);
    [Fact] public void NineClipsKeepTwoFourFrameOverlapsAndPreserveTransitions()
    {
        var starts=new[]{0,2,4-4d/30,6-4d/30,8-4d/30,10-8d/30,12-8d/30,14-8d/30,16-8d/30};
        var items=starts.Select((t,i)=>Item("i"+i,t)).ToArray();
        var transitions=new[]{new EditorTransition("t1","i1",1,"cross-dissolve",22d/30,"center","preserve"),new EditorTransition("t2","i3",1,"cross-dissolve",.8,"center","preserve")};
        var cuts=EditorRenderService.ResolveTransitions(items,transitions,30);Assert.Equal(2,cuts.Length);
        var actual=EditorRenderService.SequenceStarts(items,cuts);
        for(var i=0;i<items.Length;i++){Assert.Equal(starts[i],actual[items[i].Id],8);Assert.Equal(10,items[i].Start);Assert.Equal(12,items[i].End);}
        Assert.Equal(starts[^1]+2,EditorRenderService.SequenceDuration(items,cuts),8);
        Assert.Equal(starts[^1]+2,EditorRenderService.TimelineDuration(items),8);
        var graph=EditorRenderService.AbsoluteSequenceFilter(items,cuts,starts[^1]+2,1280,720,30,"yuv420p");
        Assert.True(graph.IndexOf("[8:v]")<graph.IndexOf("[0:v]"));Assert.Contains("amix=inputs=9",graph);Assert.Contains("normalize=0",graph);Assert.DoesNotContain("acrossfade",graph);
        Assert.Contains("adelay=185600S",graph);Assert.Contains("[abstw0]",graph);Assert.Contains("repeatlast=0",graph);
    }
    [Fact] public void ExplicitOverlapMapsSourceCaptionsWithoutMovingThem()
    {
        var items=new[]{Item("a",0),Item("b",56d/30)};
        var mapped=EditorRenderService.MapSubtitles(items,_=>[new TranscriptSegment(10.2,10.7,"caption")],"source",null);
        Assert.Equal(.2,mapped[0].Start,8);Assert.Equal(56d/30+.2,mapped[1].Start,8);
    }
    [Fact] public void InvalidMixedOrLegacyTransitionLayoutsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>EditorRenderService.SequenceStarts([Item("a",0),new("b","source",0,2)],[]));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.SequenceStarts([Item("a",double.NaN)],[]));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions([Item("a",0),Item("b",2)],[new("t","b",1,"cross-dissolve",.8,"center")],30));
        Assert.Throws<ArgumentException>(()=>EditorRenderService.ResolveTransitions([Item("a",0),Item("b",1.9)],[new("t","b",1,"cross-dissolve",.8,"center","preserve")],30));
    }
    [Fact] public void AbsolutePlacementJsonRoundtripsButExchangeIsExplicitlyRejected()
    {
        var p=new EditorProject{Start=0,End=12,Fps=30,Timeline=[Item("a",0),Item("b",1.9)]};
        var copy=System.Text.Json.JsonSerializer.Deserialize<EditorProject>(System.Text.Json.JsonSerializer.Serialize(p))!;
        Assert.Equal(1.9,copy.Timeline![1].SequenceStart);EditorRenderService.Validate(copy);
        Assert.Throws<NotSupportedException>(()=>PremiereExportService.ValidateBasics(copy));Assert.Throws<NotSupportedException>(()=>JianyingExportService.Validate(copy));
    }

    [Fact] public void ActualNineClipStructureKeeps736FramesAnd19Captions()
    {
        var ranges=new[]{(0,146),(146,282),(282,357),(357,460),(456,495),(491,568),(568,576),(576,719),(719,736)};
        var items=ranges.Select((r,i)=>new TimelineItem("i"+i,"source",i+10,i+10+(r.Item2-r.Item1)/30d,SequenceStart:r.Item1/30d)).ToArray();
        var captions=Enumerable.Range(0,19).Select(i=>new TimelineCaption{Id="c"+i,Start=i,End=i+.5,Text="caption"}).ToArray();
        var p=new EditorProject{Start=0,End=34,Fps=30,Timeline=items,TimelineCaptions=captions,Transitions=[new("t1","i1",1,"cross-dissolve",22d/30,"center","preserve"),new("t2","i3",1,"cross-dissolve",.8,"center","preserve")]};
        EditorRenderService.Validate(p);Assert.Equal(736d/30,EditorRenderService.SequenceDuration(p),8);
        var copy=System.Text.Json.JsonSerializer.Deserialize<EditorProject>(System.Text.Json.JsonSerializer.Serialize(p))!;
        Assert.Equal(items,copy.Timeline);Assert.Equal(captions,copy.TimelineCaptions);
        Assert.Equal(2,EditorRenderService.ResolveTransitions(copy).Length);
        Assert.Throws<ArgumentException>(()=>EditorRenderService.Validate(p with{Timeline=items.Select(i=>i with{Track=2}).ToArray()}));
    }

    [Fact] public async Task RealSourceAbsoluteJoinPreservesVideoPriorityAndAudioMix()
    {
        var source=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_SOURCE");
        var output=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_OUTPUT");
        if(string.IsNullOrEmpty(source)||string.IsNullOrEmpty(output))return;
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")??"ffmpeg";
        var media=new MediaTools(ffmpeg);
        async Task<byte[]> Capture(params string[] args)
        {
            using var process=MediaTools.Start(ffmpeg,args);
            var error=process.StandardError.ReadToEndAsync();
            using var bytes=new MemoryStream();await process.StandardOutput.BaseStream.CopyToAsync(bytes);
            await process.WaitForExitAsync();Assert.True(process.ExitCode==0,await error);return bytes.ToArray();
        }
        async Task<byte[]> Frame(string path,double time)=>await Capture("-v","error","-ss",time.ToString(System.Globalization.CultureInfo.InvariantCulture),"-i",path,"-vf","scale=1280:720,format=yuv420p","-frames:v","1","-f","rawvideo","pipe:1");
        async Task<byte[]> ReferenceFrame(double sourceStart,double localTime)
        {
            var one=new[]{new TimelineItem("ref","source",sourceStart,sourceStart+2,SequenceStart:0)};
            var graph=EditorRenderService.AbsoluteSequenceFilter(one,[],2,1280,720,30,"yuv420p").Replace("[0:v]fps=","[0:v]setpts=PTS-STARTPTS,scale=1280:720,fps=30,tpad=stop_mode=clone:stop_duration=1,trim=duration=2,setpts=PTS-STARTPTS,fps=").Replace("[0:a]apad","[0:a]aresample=48000,apad");
            graph+=$";[vout]select='eq(n,{(int)Math.Round(localTime*30)})',format=yuv420p[frame];[aout]anullsink";
            return await Capture("-v","error","-ss",sourceStart.ToString(System.Globalization.CultureInfo.InvariantCulture),"-t","2","-i",source,"-filter_complex",graph,"-map","[frame]","-frames:v","1","-f","rawvideo","pipe:1");
        }
        async Task<float[]> Audio(string path,double time,double length)
        {
            var b=await Capture("-v","error","-ss",time.ToString(System.Globalization.CultureInfo.InvariantCulture),"-i",path,"-t",length.ToString(System.Globalization.CultureInfo.InvariantCulture),"-vn","-ac","2","-ar","48000","-f","f32le","pipe:1");
            var samples=new float[b.Length/4];Buffer.BlockCopy(b,0,samples,0,b.Length);return samples;
        }
        var contactRows=new List<object>();
        var contact=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_CONTACT");
        var kinds=new[]{"none","cross-dissolve","dip-to-black","dip-to-white","wipe-right","additive-dissolve","non-additive-dissolve","iris-box","iris-cross","iris-diamond","iris-round","barn-doors-vertical","soft-wipe-right","gradient-wipe-right","panel-wipe","vr-iris-wipe","vr-gradient-wipe","flash","ray","vr-light-rays","solarize","flare","light-sweep","plateau-wipe","shape-flow"};
        var newWipes=new[]{"up","right","down","left"}.SelectMany(dir=>new[]{"catalog-wipe-"+dir,"linear-wipe-"+dir}).ToArray();
        kinds=Environment.GetEnvironmentVariable("LIVECLIPPER_WIPE_ONLY")=="1"?new[]{"none"}.Concat(newWipes).ToArray():kinds.Concat(newWipes).ToArray();
        if(Environment.GetEnvironmentVariable("LIVECLIPPER_BASIC_JOIN_ONLY")=="1")kinds=["none","cross-dissolve"];
        foreach(var kind in kinds)
        {
            var preserve=kind!="none";
            var items=new[]{new TimelineItem("a","source",0,2,SequenceStart:0),new TimelineItem("b","source",10,12,SequenceStart:preserve?2:1.8)};
            EditorTransition[] transitions=preserve?[new("t","b",1,kind,.8,"center","preserve")]:[];
            var cuts=EditorRenderService.ResolveTransitions(items,transitions,30);var duration=EditorRenderService.SequenceDuration(items,cuts);
            var pixel=LiveClipper.Core.EditorFx.TransitionMap.BlendExpression(kind) is null?"yuv420p":"gbrp";
            var graph=EditorRenderService.AbsoluteSequenceFilter(items,cuts,duration,1280,720,30,pixel);
            for(var i=0;i<2;i++)graph=graph.Replace($"[{i}:v]fps=",$"[{i}:v]setpts=PTS-STARTPTS,scale=1280:720,fps=30,tpad=stop_mode=clone:stop_duration=1,trim=duration=2,setpts=PTS-STARTPTS,fps=").Replace($"[{i}:a]apad",$"[{i}:a]aresample=48000,apad");
            await media.RunAsync(ffmpeg,["-v","error","-y","-ss","0","-t","2","-i",source,"-ss","10","-t","2","-i",source,"-filter_complex",graph,"-map","[vout]","-map","[aout]","-t",duration.ToString(System.Globalization.CultureInfo.InvariantCulture),"-r","30","-c:v","ffv1","-level","3","-c:a","pcm_f32le",output],CancellationToken.None);
            var decoded=await Capture("-v","error","-i",output,"-an","-vf","scale=16:16","-pix_fmt","gray","-f","rawvideo","pipe:1");
            Assert.Equal((int)Math.Round(duration*30),decoded.Length/256);
            if(!preserve)
            {
                var actual=await Frame(output,1.9);var expected=await ReferenceFrame(0,1.9);
                Assert.Equal(expected.Length,actual.Length);
                var mean=actual.Zip(expected,(a,b)=>Math.Abs(a-b)).Average();Assert.True(mean<1,$"first-video priority pixel mean {mean}");
                var mix=await Audio(output,0,3.8);var a=await Audio(source,0,2);var b=await Audio(source,10,2);
                const int firstOffset=177600,secondOffset=4800,count=9600;
                Assert.True(mix.Length>=firstOffset+count && a.Length>=firstOffset+count && b.Length>=secondOffset+count,$"PCM lengths {mix.Length}/{a.Length}/{b.Length}");
                var error=Enumerable.Range(0,count).Select(i=>Math.Abs(mix[firstOffset+i]-a[firstOffset+i]-b[secondOffset+i])).Average();
                Assert.True(error<.005,$"audio mix mean error {error}");
                Console.WriteLine($"REAL absolute priority: frames={decoded.Length/256}, pixelMAE={mean}, audioMAE={error}");
            }
            else
            {
                // Before/after the 0.8 second window, the same source clocks are retained.
                foreach(var pair in new[]{(At:1.5,Source:1.5),(At:2.5,Source:10.5)})
                {
                    var actual=await Frame(output,pair.At);var expected=await ReferenceFrame(pair.Source<2?0:10,pair.Source<2?pair.Source:pair.Source-10);
                    var mean=actual.Zip(expected,(a,b)=>Math.Abs(a-b)).Average();Assert.True(mean<1,$"preserve outside-window pixel mean {mean}");
                }
                var audio=await Audio(output,0,4);var sourceA=await Audio(source,0,2);var sourceB=await Audio(source,10,2);
                var audioErrorA=Enumerable.Range(0,9600).Select(i=>Math.Abs(audio[177600+i]-sourceA[177600+i])).Average();
                var audioErrorB=Enumerable.Range(0,9600).Select(i=>Math.Abs(audio[196800+i]-sourceB[4800+i])).Average();
                Assert.True(audioErrorA<.005&&audioErrorB<.005,$"{kind} preserve hard-cut audio mismatch {audioErrorA}/{audioErrorB}");
                Console.WriteLine($"REAL {kind} audio MAE={audioErrorA}/{audioErrorB}");
                var middle=await Frame(output,2);var lastA=await ReferenceFrame(0,59d/30);var firstB=await ReferenceFrame(10,0);
                var blendError=Enumerable.Range(0,middle.Length).Select(i=>Math.Abs(middle[i]-(lastA[i]+firstB[i])/2d)).Average();
                if(kind=="cross-dissolve")Assert.True(blendError<1.1,$"preserve midpoint blend error {blendError}; means actual={middle.Average(x=>(double)x)} A={lastA.Average(x=>(double)x)} B={firstB.Average(x=>(double)x)}");
                Console.WriteLine($"REAL {kind} midpoint vs linear blend MAE={blendError} (linear equality asserted only for cross-dissolve)");
                if(!string.IsNullOrEmpty(contact))
                {
                    var images=new List<string>();
                    foreach(var at in new[]{1.8,2.0,2.2})images.Add(Convert.ToBase64String(await Capture("-v","error","-ss",at.ToString(System.Globalization.CultureInfo.InvariantCulture),"-i",output,"-vf","scale=320:180","-frames:v","1","-c:v","png","-f","image2pipe","pipe:1")));
                    contactRows.Add(new{kind,images});
                }
                Assert.Equal(.8,cuts.Single().EffectiveDuration,8);
                Console.WriteLine($"REAL {kind} preserve: frames={decoded.Length/256}, duration={duration}, window=1.6..2.4");
            }
        }
        if(!string.IsNullOrEmpty(contact))
        {
            var script="import sys,json,base64,io;from PIL import Image,ImageDraw;rows=json.load(sys.stdin);im=Image.new('RGB',(980,44+len(rows)*210),(24,26,31));d=ImageDraw.Draw(im);d.text((10,10),'Real source / preserve 0.8s / 25%, 50%, 75% at 1.8, 2.0, 2.2s',fill='white');[(d.text((10,44+n*210),r['kind'],fill='white'),[im.paste(Image.open(io.BytesIO(base64.b64decode(b))).convert('RGB'),(10+j*320,64+n*210)) for j,b in enumerate(r['images'])]) for n,r in enumerate(rows)];im.save(sys.argv[1])";
            // Keep all thumbnails in memory; the contact sheet is the only additional artifact.
            var psi=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_PYTHON")??"python"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true};
            psi.ArgumentList.Add("-c");psi.ArgumentList.Add(script);psi.ArgumentList.Add(contact);
            using var process=System.Diagnostics.Process.Start(psi)!;var error=process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(System.Text.Json.JsonSerializer.Serialize(contactRows));process.StandardInput.Close();await process.WaitForExitAsync();Assert.True(process.ExitCode==0,await error);
            Console.WriteLine($"CONTACT {contactRows.Count} rows: {contact}");
        }
    }

    [Fact] public async Task RealSourceWipeEdgesMatchNativeCoverageWithoutChangingLegacyHardWipes()
    {
        foreach(var dir in new[]{"up","right","down","left"})
        {
            Assert.Null(LiveClipper.Core.EditorFx.TransitionMap.BlendExpression("wipe-"+dir));
            Assert.Contains("xfade=transition=wipe"+dir,LiveClipper.Core.EditorFx.TransitionMap.Join("wipe-"+dir,"[a]","[b]","[out]",0,.8,30));
            foreach(var family in new[]{"catalog-wipe","linear-wipe"})Assert.NotNull(LiveClipper.Core.EditorFx.TransitionMap.BlendExpression(family+"-"+dir));
        }
        var source=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_SOURCE");if(string.IsNullOrEmpty(source))return;
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")??"ffmpeg";
        async Task<byte[]> Run(string[] args,byte[]? input=null)
        {
            var info=new System.Diagnostics.ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input is not null};foreach(var arg in args)info.ArgumentList.Add(arg);
            using var process=System.Diagnostics.Process.Start(info)!;var error=process.StandardError.ReadToEndAsync();using var output=new MemoryStream();var read=process.StandardOutput.BaseStream.CopyToAsync(output);
            if(input is not null){await process.StandardInput.BaseStream.WriteAsync(input);process.StandardInput.Close();}await read;await process.WaitForExitAsync();Assert.True(process.ExitCode==0,await error);return output.ToArray();
        }
        async Task<byte[]> SourceFrame(string time)=>await Run(["-v","error","-ss",time,"-i",source,"-vf","scale=1280:720","-frames:v","1","-pix_fmt","rgb24","-f","rawvideo","pipe:1"]);
        var a=await SourceFrame("1.9");var b=await SourceFrame("10.0");var input=a.Concat(b).ToArray();Assert.Equal(1280*720*3,a.Length);Assert.Equal(a.Length,b.Length);
        var witness=0;
        foreach(var family in new[]{"catalog-wipe","linear-wipe"})foreach(var dir in new[]{"up","right","down","left"})
        {
            var kind=family+"-"+dir;var maximum=0;var compared=0;
            foreach(var phase in new[]{0d,.125,.25,.5,.75,.875,1})
            {
                var expression=LiveClipper.Core.EditorFx.TransitionMap.BlendExpression(kind)!.Replace("$P",phase.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var graph=$"[0:v]split[sa][sb];[sa]trim=start_frame=0:end_frame=1,setpts=PTS-STARTPTS,format=gbrp[a];[sb]trim=start_frame=1:end_frame=2,setpts=PTS-STARTPTS,format=gbrp[b];[a][b]blend=all_expr='{expression}':shortest=1,format=rgb24[out]";
                var actual=await Run(["-v","error","-filter_complex_threads","1","-f","rawvideo","-pixel_format","rgb24","-video_size","1280x720","-framerate","1","-i","pipe:0","-filter_complex",graph,"-map","[out]","-frames:v","1","-f","rawvideo","pipe:1"],input);Assert.Equal(a.Length,actual.Length);
                for(var y=0;y<720;y++)for(var x=0;x<1280;x++)
                {
                    var field=dir switch{"right"=>(x+.5)/1280,"left"=>1-(x+.5)/1280,"down"=>(y+.5)/720,_=>1-(y+.5)/720};
                    var coverage=family=="catalog-wipe"?Math.Clamp((phase-field)/.004+.5,0,1):phase<=0?0:phase>=1?1:Math.Clamp((phase*1.004-field)/.004,0,1);
                    // Include the feather/front ROI and exact endpoints; never average away a bad boundary pixel.
                    if(phase>0&&phase<1&&Math.Abs(field-phase)>.008)continue;
                    for(var c=0;c<3;c++)
                    {
                        var i=(y*1280+x)*3+c;var expected=(int)Math.Round(a[i]*(1-coverage)+b[i]*coverage,MidpointRounding.ToEven);var delta=Math.Abs(actual[i]-expected);maximum=Math.Max(maximum,delta);compared++;
                        if(coverage>0&&coverage<1&&Math.Abs(expected-(field<phase?b[i]:a[i]))>1)witness++;
                    }
                }
            }
            Assert.True(maximum<=1,$"{kind} edge ROI max error {maximum}");Console.WriteLine($"REAL ROI {kind}: 7 phases, {compared} channels, max={maximum}");
        }
        Assert.True(witness>0,"Real source must expose the old hard-edge mismatch, not just a flat identical ROI");Console.WriteLine($"REAL ROI hard-edge negative witnesses={witness}");
    }

    [Fact] public async Task RealSourcePreserve60FpsSingleFrameAndShortNeighbors()
    {
        var source=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_SOURCE");var output=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_OUTPUT");if(string.IsNullOrEmpty(source)||string.IsNullOrEmpty(output))return;
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")??"ffmpeg";var media=new MediaTools(ffmpeg);
        static string N(double n)=>n.ToString("G17",System.Globalization.CultureInfo.InvariantCulture);
        async Task<byte[]> Capture(params string[] args){using var p=MediaTools.Start(ffmpeg,args);var error=p.StandardError.ReadToEndAsync();using var bytes=new MemoryStream();await p.StandardOutput.BaseStream.CopyToAsync(bytes);await p.WaitForExitAsync();Assert.True(p.ExitCode==0,await error);return bytes.ToArray();}
        string Normalize(string graph,TimelineItem[] items){for(var i=0;i<items.Length;i++)graph=graph.Replace($"[{i}:v]fps=",$"[{i}:v]setpts=PTS-STARTPTS,scale=320:180,fps=60,tpad=stop_mode=clone:stop_duration=1,trim=duration={N(items[i].End-items[i].Start)},setpts=PTS-STARTPTS,fps=").Replace($"[{i}:a]apad",$"[{i}:a]aresample=48000,apad");return graph;}
        async Task<byte[]> Reference(TimelineItem item,int frame){var one=new[]{item with{SequenceStart=0}};var graph=Normalize(EditorRenderService.AbsoluteSequenceFilter(one,[],item.End-item.Start,320,180,60,"yuv420p"),one)+$";[vout]select='eq(n,{frame})',format=yuv420p[frame];[aout]anullsink";return await Capture("-v","error","-ss",N(item.Start),"-t",N(Math.Max(.5,item.End-item.Start)),"-i",source,"-filter_complex",graph,"-map","[frame]","-frames:v","1","-f","rawvideo","pipe:1");}
        foreach(var serial in new[]{false,true})foreach(var shortNeighbor in new[]{false,true})foreach(var align in new[]{"start","center","end"})
        {
            var leftFrames=shortNeighbor?3:30;var items=new[]{new TimelineItem("a","source",0,leftFrames/60d,SequenceStart:0),new TimelineItem("b","source",10,10.5,SequenceStart:leftFrames/60d)};
            var cuts=EditorRenderService.ResolveTransitions(items,[new("t","b",1,"cross-dissolve",shortNeighbor?.8:1d/60,align,"preserve")],60);var cut=Assert.Single(cuts);var span=shortNeighbor?3:1;Assert.Equal(span/60d,cut.EffectiveDuration,8);
            var duration=(leftFrames+30)/60d;var graph=Normalize(serial?EditorRenderService.SerialSequenceFilter(2,[leftFrames/60d,.5],cuts,new Dictionary<string,int>{{"a",0},{"b",1}},60,new EditorProject { Fps=60 }):EditorRenderService.AbsoluteSequenceFilter(items,cuts,duration,320,180,60,"yuv420p"),items);
            await media.RunAsync(ffmpeg,["-v","error","-y","-ss","0","-t",N(Math.Max(.5,items[0].End)),"-i",source,"-ss","10","-t","0.5","-i",source,"-filter_complex",graph,"-map","[vout]","-map","[aout]","-t",N(duration),"-r","60","-c:v","ffv1","-c:a","pcm_f32le",output],CancellationToken.None);
            var frames=await Capture("-v","error","-i",output,"-an","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");const int bytesPerFrame=320*180*3/2;Assert.Equal(leftFrames+30,frames.Length/bytesPerFrame);
            var before=align=="start"?0:align=="end"?span:span/2;var first=leftFrames-before;var after=first+span;
            foreach(var check in new[]{(Output:first,Item:items[0],Local:Math.Min(leftFrames-1,first)),(Output:after,Item:items[1],Local:after-leftFrames)})
            {
                var expected=await Reference(check.Item,check.Local);Assert.Equal(bytesPerFrame,expected.Length);var offset=check.Output*bytesPerFrame;var max=0;long sum=0;for(var i=0;i<bytesPerFrame;i++){var d=Math.Abs(frames[offset+i]-expected[i]);max=Math.Max(max,d);sum+=d;}var mean=sum/(double)bytesPerFrame;Assert.True(mean<1,$"serial={serial} {align} D{span} frame{check.Output} MAE={mean} max={max}");Console.WriteLine($"serial={serial} 60fps {align} D={span} frame={check.Output} referenceMAE={mean} max={max}");
            }
            Console.WriteLine($"serial={serial} 60fps {align} D={span}: frames={frames.Length/bytesPerFrame}, seconds={duration}, windowFrames=[{first},{after})");
        }
        var legacyItems=new[]{new TimelineItem("a","source",0,.5),new TimelineItem("b","source",10,10.5)};
        var legacyCuts=EditorRenderService.ResolveTransitions(legacyItems,[new("t","b",1,"cross-dissolve",.1,"center","overlap")],60);
        var legacyGraph=Normalize(EditorRenderService.SerialSequenceFilter(2,[.5,.5],legacyCuts,new Dictionary<string,int>{{"a",0},{"b",1}},60,new EditorProject{Fps=60}),legacyItems);
        await media.RunAsync(ffmpeg,["-v","error","-y","-ss","0","-t","0.5","-i",source,"-ss","10","-t","0.5","-i",source,"-filter_complex",legacyGraph,"-map","[vout]","-map","[aout]","-t","0.9","-r","60","-c:v","ffv1","-c:a","pcm_f32le",output],CancellationToken.None);
        var legacyFrames=await Capture("-v","error","-i",output,"-an","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");const int legacyFrameBytes=320*180*3/2;Assert.Equal(54,legacyFrames.Length/legacyFrameBytes);
        foreach(var check in new[]{(Output:0,Item:legacyItems[0],Local:0),(Output:53,Item:legacyItems[1],Local:29)})
        {
            var expected=await Reference(check.Item,check.Local);Assert.Equal(legacyFrameBytes,expected.Length);long sum=0;for(var i=0;i<legacyFrameBytes;i++)sum+=Math.Abs(legacyFrames[check.Output*legacyFrameBytes+i]-expected[i]);var mean=sum/(double)legacyFrameBytes;Assert.True(mean<1,$"legacy overlap endpoint frame{check.Output} MAE={mean}");Console.WriteLine($"serial legacy overlap frame={check.Output} MAE={mean}; frames=54 duration=.9");
        }
        var wipeItems=new[]{new TimelineItem("a","source",0,4),new TimelineItem("b","source",4,8)};
        var wipeCuts=EditorRenderService.ResolveTransitions(wipeItems,[new("t","b",1,"linear-wipe-right",.8,"center","preserve")],30);
        var wipeGraph=EditorRenderService.SerialSequenceFilter(2,[4,4],wipeCuts,new Dictionary<string,int>{{"a",0},{"b",1}},30,new EditorProject{Fps=30});
        for(var i=0;i<2;i++)wipeGraph=wipeGraph.Replace($"[{i}:v]fps=",$"[{i}:v]setpts=PTS-STARTPTS,scale=320:180,fps=30,tpad=stop_mode=clone:stop_duration=1,trim=end_frame=120,setpts=PTS-STARTPTS,fps=").Replace($"[{i}:a]apad",$"[{i}:a]aresample=48000,apad");
        var wipeSource=Environment.GetEnvironmentVariable("LIVECLIPPER_EXISTING_25FPS_SOURCE")??source;
        await media.RunAsync(ffmpeg,["-v","error","-y","-ss","0","-t","4","-i",wipeSource,"-ss","4","-t","4","-i",wipeSource,"-filter_complex",wipeGraph,"-map","[vout]","-map","[aout]","-t","8","-r","30","-c:v","ffv1","-c:a","pcm_f32le",output],CancellationToken.None);
        var oldWipeGraph=wipeGraph.Replace("trim=end_frame=108","trim=end=3.6000000000000001").Replace("trim=start_frame=108:end_frame=132","trim=start=3.6000000000000001:duration=0.80000000000000004").Replace("trim=end_frame=24","trim=duration=0.80000000000000004").Replace("trim=start_frame=24","trim=start=0.80000000000000004");
        var oldFrames=await Capture("-v","error","-ss","0","-t","4","-i",wipeSource,"-ss","4","-t","4","-i",wipeSource,"-filter_complex",oldWipeGraph+";[aout]atrim=duration=8,anullsink","-map","[vout]","-t","8","-r","30","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");Assert.Equal(239,oldFrames.Length/legacyFrameBytes);Console.WriteLine("reverse witness: old seconds-based split gives 239 frames");
        var wipeFrames=await Capture("-v","error","-i",output,"-an","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");Assert.Equal(240,wipeFrames.Length/legacyFrameBytes);Console.WriteLine("serial linear-wipe-right .8 center: 240 frames / 8 seconds");
    }
    [Fact] public async Task RealSourceAbsoluteFractionalStartsMatchNativeFrames()
    {
        var source=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_SOURCE");var output=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_OUTPUT");if(string.IsNullOrEmpty(source)||string.IsNullOrEmpty(output))return;
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")??"ffmpeg";var media=new MediaTools(ffmpeg);
        async Task<byte[]> Capture(params string[] args){using var p=MediaTools.Start(ffmpeg,args);var error=p.StandardError.ReadToEndAsync();using var bytes=new MemoryStream();await p.StandardOutput.BaseStream.CopyToAsync(bytes);await p.WaitForExitAsync();Assert.True(p.ExitCode==0,await error);return bytes.ToArray();}
        const int size=320*180*3/2;
        var items=new[]{new TimelineItem("a","source",0,1,SequenceStart:.01),new TimelineItem("b","source",10,11,SequenceStart:1.01)};
        string Normalize(string graph)=>graph.Replace("[0:v]fps=","[0:v]setpts=PTS-STARTPTS,scale=320:180,fps=").Replace("[1:v]fps=","[1:v]setpts=PTS-STARTPTS,scale=320:180,fps=").Replace("[0:a]apad","[0:a]aresample=48000,apad").Replace("[1:a]apad","[1:a]aresample=48000,apad");
        async Task<byte[]> Reference(int input,int frame)=>await Capture("-v","error","-ss",input==0?"0":"10","-t","1","-i",source,"-vf",$"setpts=PTS-STARTPTS,scale=320:180,fps=30,select='eq(n,{frame})'","-an","-frames:v","1","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");
        foreach(var align in new[]{"none","start","center","end"})
        {
            var cuts=EditorRenderService.ResolveTransitions(items,align=="none"?[]:[new("t","b",1,"cross-dissolve",1d/30,align,"preserve")],30);
            var graph=Normalize(EditorRenderService.AbsoluteSequenceFilter(items,cuts,2.01,320,180,30,"yuv420p"));
            Assert.Contains("adelay=480S",graph);Assert.Contains("adelay=48480S",graph);
            await media.RunAsync(ffmpeg,["-v","error","-y","-ss","0","-t","1","-i",source,"-ss","10","-t","1","-i",source,"-filter_complex",graph,"-map","[vout]","-map","[aout]","-t","2.01","-r","30","-c:v","ffv1","-c:a","pcm_f32le",output],CancellationToken.None);
            var frames=await Capture("-v","error","-i",output,"-an","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");Assert.Equal(60,frames.Length/size);
            var boundary=align=="end"?29:30;
            foreach(var check in new[]{(At:0,Source:0,Local:0),(At:boundary,Source:align=="none"?1:0,Local:align=="none"?0:29),(At:31,Source:1,Local:1),(At:59,Source:1,Local:29)})
            {
                var expected=await Reference(check.Source,check.Local);Assert.Equal(size,expected.Length);long sum=0;for(var i=0;i<size;i++)sum+=Math.Abs(frames[check.At*size+i]-expected[i]);var mean=sum/(double)size;Assert.True(mean<1,$"fractional {align} frame{check.At} MAE={mean}");Console.WriteLine($"fractional {align} frame={check.At} MAE={mean}");
            }
            var audio=await Capture("-v","error","-i",output,"-vn","-ar","48000","-ac","2","-f","f32le","pipe:1");Assert.Equal(96480,audio.Length/8);Console.WriteLine($"fractional {align}: video60frames/2s audio96480samples/2.01s");
        }
    }

    [Fact] public async Task RealSourceAbsoluteSubframeClipsKeepOneFrame()
    {
        var source=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_SOURCE");var output=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_OUTPUT");if(string.IsNullOrEmpty(source)||string.IsNullOrEmpty(output))return;
        var ffmpeg=Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")??"ffmpeg";var media=new MediaTools(ffmpeg);
        static string N(double n)=>n.ToString("G17",System.Globalization.CultureInfo.InvariantCulture);
        async Task<byte[]> Capture(params string[] args){using var p=MediaTools.Start(ffmpeg,args);var error=p.StandardError.ReadToEndAsync();using var bytes=new MemoryStream();await p.StandardOutput.BaseStream.CopyToAsync(bytes);await p.WaitForExitAsync();Assert.True(p.ExitCode==0,await error);return bytes.ToArray();}
        async Task<byte[]> Reference(double start,int frame)=>await Capture("-v","error","-ss",N(start),"-t","1","-i",source,"-vf",$"setpts=PTS-STARTPTS,scale=320:180,fps=30,select='eq(n,{frame})'","-an","-frames:v","1","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");
        var tiny=new TimelineItem("tiny","source",0,.01,SequenceStart:0);
        var cases=new[]{(Name:"solo",Items:new[]{tiny},Duration:.01,Frame:0,Source:0d,Local:0,Count:1),
            (Name:"tail",Items:new[]{new TimelineItem("a","source",0,1,SequenceStart:0),tiny with{Start=10,End=10.01,SequenceStart=1.02}},Duration:1.03,Frame:31,Source:10d,Local:0,Count:32),
            (Name:"disabled",Items:new[]{tiny with{Disabled=true}},Duration:.01,Frame:0,Source:0d,Local:0,Count:1),
            (Name:"priority",Items:new[]{tiny,new TimelineItem("b","source",10,11,SequenceStart:0)},Duration:1d,Frame:0,Source:0d,Local:0,Count:30)};
        const int size=320*180*3/2;
        foreach(var c in cases)
        {
            var graph=EditorRenderService.AbsoluteSequenceFilter(c.Items,[],c.Duration,320,180,30,"yuv420p");var args=new List<string>{"-v","error","-y"};
            for(var i=0;i<c.Items.Length;i++)
            {
                args.AddRange(["-ss",N(c.Items[i].Start),"-t","1","-i",source]);
                graph=graph.Replace($"[{i}:v]fps=",$"[{i}:v]setpts=PTS-STARTPTS,scale=320:180,fps=").Replace($"[{i}:a]apad",$"[{i}:a]aresample=48000,apad");
            }
            Assert.Equal(c.Count,EditorRenderService.AbsoluteVideoFrames(c.Items,c.Duration,30));
            args.AddRange(["-filter_complex",graph,"-map","[vout]","-map","[aout]","-t",N(Math.Max(c.Duration,c.Count/30d)),"-r","30","-fps_mode","cfr","-c:v","ffv1","-c:a","pcm_f32le",output]);await media.RunAsync(ffmpeg,args,CancellationToken.None);
            var frames=await Capture("-v","error","-i",output,"-an","-pix_fmt","yuv420p","-f","rawvideo","pipe:1");Assert.Equal(c.Count,frames.Length/size);
            if(c.Name=="disabled"){Assert.All(frames.Take(320*180),x=>Assert.Equal((byte)16,x));}
            else{var expected=await Reference(c.Source,c.Local);Assert.Equal(size,expected.Length);long sum=0;for(var i=0;i<size;i++)sum+=Math.Abs(frames[c.Frame*size+i]-expected[i]);Assert.True(sum/(double)size<1,$"{c.Name} first frame MAE={sum/(double)size}");}
            if(c.Name=="priority"){var next=await Reference(10,1);long sum=0;for(var i=0;i<size;i++)sum+=Math.Abs(frames[size+i]-next[i]);Assert.True(sum/(double)size<1);}
            var audio=await Capture("-v","error","-i",output,"-vn","-ar","48000","-ac","2","-f","f32le","pipe:1");Assert.Equal((int)Math.Round(c.Duration*48000),audio.Length/8);Console.WriteLine($"subframe {c.Name}: frames={c.Count} audioSamples={audio.Length/8}");
        }
    }

    [Fact] public async Task RealSourceFinishAbsoluteNoAudioFormats()
    {
        var source=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_SOURCE");var output=Environment.GetEnvironmentVariable("LIVECLIPPER_REAL_TRANSITION_OUTPUT");var db=Environment.GetEnvironmentVariable("LIVECLIPPER_READONLY_EXISTING_DB");if(string.IsNullOrEmpty(source)||string.IsNullOrEmpty(output)||string.IsNullOrEmpty(db))return;
        var work=Path.GetDirectoryName(output)!;var media=new MediaTools(Environment.GetEnvironmentVariable("LIVECLIPPER_TEST_FFMPEG")??"ffmpeg");
        // Explicit read-only existing store: no migration, no synthetic database or source.
        var renderer=new EditorRenderService(new Store(db+";Mode=ReadOnly"),new StudioStore(db+";Mode=ReadOnly"),media,work);
        var finish=typeof(EditorRenderService).GetMethod("FinishAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        foreach(var format in new[]{"gif","png"})
        {
            var target=Path.Combine(work,"absolute-finish-noaudio."+(format=="gif"?"gif":"zip"));
            var plan=new EditorProject{Start=0,End=.2,Fps=30,Format=format,BurnSubtitles=false,MusicAssetId="missing-test-music",EffectAssetId="missing-test-effect",Timeline=[new("a","source",0,.2,SequenceStart:0,SoundAssetId:"missing-test-clip-sound")]};
            var task=(Task)finish.Invoke(renderer,["source",plan,output,Array.Empty<TranscriptSegment>(),.2,30d,null,target,work,CancellationToken.None])!;await task;
            Assert.Equal("missing-test-music",plan.MusicAssetId);Assert.Equal("missing-test-effect",plan.EffectAssetId);Assert.Equal("missing-test-clip-sound",plan.Timeline![0].SoundAssetId);
            Assert.True(new FileInfo(target).Length>0);
            if(format=="gif"){var bytes=await File.ReadAllBytesAsync(target);Assert.Equal("GIF",System.Text.Encoding.ASCII.GetString(bytes,0,3));}
            else{using var zip=System.IO.Compression.ZipFile.OpenRead(target);Assert.Equal(6,zip.Entries.Count);Assert.All(zip.Entries,x=>Assert.EndsWith(".png",x.Name));}
            Console.WriteLine($"Finish actual {format}: nonempty valid output; no audio path completed");
        }
        var audiblePlan=new EditorProject{Start=0,End=.2,Fps=30,Format="h264",BurnSubtitles=false,MusicAssetId="missing-test-music",Timeline=[new("a","source",0,.2,SequenceStart:0)]};
        var rejected=(Task)finish.Invoke(renderer,["source",audiblePlan,output,Array.Empty<TranscriptSegment>(),.2,30d,null,Path.Combine(work,"absolute-finish-noaudio.gif"),work,CancellationToken.None])!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>rejected);Assert.Equal("missing-test-music",audiblePlan.MusicAssetId);Console.WriteLine("Finish audio-enabled missing asset remains rejected before rendering");
    }

}

