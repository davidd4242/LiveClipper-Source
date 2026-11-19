using System.Text;

namespace LiveClipper.Core;

public sealed class AudioLibraryService(StudioStore studio,string storageRoot)
{
    private const int Rate=22050;
    private sealed record Recipe(string Id,string Name,string Kind,string Style,double Seconds,Func<double,double> Wave);
    private static double Tone(double t,double frequency)=>Math.Sin(2*Math.PI*frequency*t);
    private static double Pulse(double t,double period,double decay)=>Math.Exp(-((t%period)/decay));
    private static double Beat(double t,double bpm)=>t*bpm/60;
    private static double Note(int semitone)=>440*Math.Pow(2,semitone/12d);
    private static readonly Recipe[] Builtins=
    [
        new("builtin-chill","夜间漫游","music","舒缓电子",16,t=>
        {
            var notes=new[]{-9,-5,-2,3,-7,-3,0,5};var beat=Beat(t,96);var n=notes[(int)(beat*2)%notes.Length];
            return .17*Tone(t,Note(n))*Pulse(t,60d/96/2,.25)+.08*Tone(t,Note(notes[(int)(beat/4)%4]-12))+.035*Tone(t,Note(n+12));
        }),
        new("builtin-upbeat","开场冲刺","music","轻快节奏",16,t=>
        {
            var beat=Beat(t,120);var notes=new[]{-5,-5,2,0,-3,-3,4,2};var bass=Note(notes[(int)beat%8]-12);
            var kick=.2*Math.Sin(2*Math.PI*(65-30*(t%(60d/120)))*t)*Pulse(t,.5,.075);
            return .13*Tone(t,bass)*Pulse(t,.5,.18)+.12*Tone(t,Note(notes[(int)(beat*2)%8]+12))*Pulse(t,.25,.08)+kick;
        }),
        new("builtin-suspense","悬念渐起","music","悬念氛围",16,t=>
        {
            var rise=.35+.65*t/16;var pulse=Pulse(t,.8,.32);
            return rise*(.11*Tone(t,110)+.065*Tone(t,116.5)+.11*Tone(t,220)*pulse+.025*Tone(t,880));
        }),
        new("builtin-chime","胜利提示","effect","提示",1.4,t=>.4*(Tone(t,660)+.5*Tone(t,880)+.2*Tone(t,1320))*Math.Exp(-3*t)),
        new("builtin-pop","轻弹点击","effect","轻快",.45,t=>.65*Tone(t,420-280*t)*Math.Exp(-14*t)),
        new("builtin-whoosh","转场掠过","effect","转场",1,t=>
        {
            var env=Math.Sin(Math.PI*t);var noise=Math.Sin(2*Math.PI*(300+2400*t)*t)+.3*Math.Sin(2*Math.PI*(790+3400*t)*t);
            return .28*env*noise;
        }),
        new("builtin-riser","悬念上升","effect","悬念",2,t=>.23*(t/2)*Tone(t,150+420*t)*Math.Min(1,(2-t)*4))
    ];
    public void EnsureBuiltins()
    {
        var directory=Path.Combine(storageRoot,"audio-library","built-in");Directory.CreateDirectory(directory);
        foreach(var recipe in Builtins)
        {
            var path=Path.Combine(directory,recipe.Id+".wav");
            if(!File.Exists(path))WriteWave(path,recipe.Seconds,recipe.Wave);
            studio.SaveAsset(new AudioAsset(recipe.Id,recipe.Name,recipe.Kind,recipe.Style,path,true));
        }
    }
    public AudioAsset Import(string sourceName,Stream source,string kind,string style)
    {
        if(kind is not ("music" or "effect"))throw new ArgumentException("请选择音乐或音效");
        var extension=Path.GetExtension(sourceName).ToLowerInvariant();
        if(extension is not (".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg"))throw new ArgumentException("支持 WAV、MP3、M4A、FLAC 或 OGG");
        var id=Guid.NewGuid().ToString("N");var directory=Path.Combine(storageRoot,"audio-library","imports");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,id+extension);
        using(var output=File.Create(path))source.CopyTo(output);
        var name=Path.GetFileNameWithoutExtension(sourceName);
        var asset=new AudioAsset(id,name.Length>100?name[..100]:name,kind,style.Length>40?style[..40]:style,path,false);
        studio.SaveAsset(asset);return asset;
    }
    private static void WriteWave(string path,double seconds,Func<double,double> wave)
    {
        var samples=(int)(seconds*Rate);
        using var file=File.Create(path);using var writer=new BinaryWriter(file,Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+samples*2);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(Rate);writer.Write(Rate*2);writer.Write((short)2);writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(samples*2);
        for(var i=0;i<samples;i++)
        {
            var t=i/(double)Rate;var fade=Math.Min(1,Math.Min(t*12,(seconds-t)*12));
            var value=Math.Clamp(wave(t)*Math.Max(0,fade),-1,1);
            writer.Write((short)Math.Round(value*32767));
        }
    }
}
