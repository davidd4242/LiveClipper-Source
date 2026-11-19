namespace LiveClipper.Tests;

internal static class TestAudio
{
    internal static string WriteSineWav(string path,int sampleRate,double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var count=(int)(sampleRate*seconds);
        using var stream=File.Create(path);
        using var writer=new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36+count*2);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate*2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(count*2);
        for(var index=0;index<count;index++)
        {
            var value=(short)(Math.Sin(2*Math.PI*440*index/sampleRate)*0.3*32767);
            writer.Write(value);
        }
        return path;
    }
}
