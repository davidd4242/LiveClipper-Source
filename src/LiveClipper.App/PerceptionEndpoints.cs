using LiveClipper.Core;
using LiveClipper.Domain;

// R50-B: read-only perception routes. Media is resolved by clip id through Store (same
// convention as AdvancedStudioEndpoints.Clip(id)); no request carries a filesystem path and
// nothing here writes a timeline or project. Cuts are suggestions only.
public sealed record PerceptionShotsBody(double? Threshold);
public sealed record PerceptionLoudnessBody(double? Window);

public static class PerceptionEndpoints
{
    public static void Map(WebApplication app,Store store,MediaTools media)=>Map(app,store,new PerceptionService(media));

    // R51-B: same routes with a caller-supplied PerceptionService, so tests can inject a canned tool runner
    // (PerceptionEndpointsTests reaches the "no usable fps" 502 without a broken media file).
    public static void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app,Store store,PerceptionService perception)
    {
        string? SourceOf(string id)=>store.Clips().FirstOrDefault(x=>x.Id==id)?.Path;

        async Task<IResult> Shots(string id,double? threshold,CancellationToken token)
        {
            if(PerceptionService.ValidateThreshold(threshold) is string error)return Results.BadRequest(new {error,threshold=Echo(threshold)});
            var path=SourceOf(id);
            if(path is null)return Results.NotFound(new {error="视频不存在",id});
            if(!File.Exists(path))return Results.NotFound(new {error="源文件不存在",id});
            try
            {
                var result=await perception.DetectShotsAsync(path,threshold!.Value,token);
                return Results.Ok(new
                {
                    id,
                    threshold=result.Threshold,
                    fps=new {numerator=result.FrameRate.Numerator,denominator=result.FrameRate.Denominator,value=result.FrameRate.Value,source=result.FrameRate.Source},
                    suggestionOnly=true,
                    cuts=result.Cuts.Select(c=>new {t=c.Time,frame=c.Frame,rawT=c.RawTime,adjusted=c.Adjusted,adjustedBy=c.AdjustedBy})
                });
            }
            catch(InvalidOperationException ex){return Results.Problem(ex.Message,statusCode:502);}
        }

        async Task<IResult> Loudness(string id,double? window,CancellationToken token)
        {
            if(PerceptionService.ValidateWindow(window) is string error)return Results.BadRequest(new {error,window=Echo(window)});
            var path=SourceOf(id);
            if(path is null)return Results.NotFound(new {error="视频不存在",id});
            if(!File.Exists(path))return Results.NotFound(new {error="源文件不存在",id});
            try
            {
                var result=await perception.MeasureLoudnessAsync(path,window!.Value,token);
                return Results.Ok(new
                {
                    id,
                    window=result.Window,
                    hasAudio=result.HasAudio,
                    envelope=result.Envelope.Select(p=>new {t=p.T,end=p.End,lufs=p.Lufs,maxLufs=p.MaxLufs,samples=p.Samples}),
                    peak=new {momentaryLufs=result.MomentaryPeakLufs,samplePeakDbfs=result.SamplePeakDbfs},
                    integratedLufs=result.IntegratedLufs
                });
            }
            catch(InvalidOperationException ex){return Results.Problem(ex.Message,statusCode:502);}
        }

        // Query values arrive as strings so a malformed number is echoed in the 400 instead of being dropped by binding.
        app.MapGet("/api/studio/clips/{id}/perception/shots",(string id,string? threshold,CancellationToken token)=>Shots(id,PerceptionService.ParseNumber(threshold),token));
        app.MapPost("/api/studio/clips/{id}/perception/shots",(string id,string? threshold,PerceptionShotsBody? body,CancellationToken token)=>Shots(id,PerceptionService.ParseNumber(threshold)??body?.Threshold,token));
        app.MapGet("/api/studio/clips/{id}/perception/loudness",(string id,string? window,CancellationToken token)=>Loudness(id,PerceptionService.ParseNumber(window),token));
        app.MapPost("/api/studio/clips/{id}/perception/loudness",(string id,string? window,PerceptionLoudnessBody? body,CancellationToken token)=>Loudness(id,PerceptionService.ParseNumber(window)??body?.Window,token));
    }

    private static string? Echo(double? value)=>value is double v?v.ToString("R",System.Globalization.CultureInfo.InvariantCulture):null;
}
