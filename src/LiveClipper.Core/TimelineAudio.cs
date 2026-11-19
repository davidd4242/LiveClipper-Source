using System.Globalization;

namespace LiveClipper.Core;

/// <summary>Decode on the container timeline, including missing audio intervals, before cutting.</summary>
public static class TimelineAudio
{
    // Match discontinuities down to the output sample grid; async=1 never time-stretches speech.
    public const string PreserveTimestampsFilter = "aresample=48000:async=1:first_pts=0:min_comp=0:min_hard_comp=0";

    public static string TrimFilter(double start,double duration)
        => $"{PreserveTimestampsFilter},atrim=start={start.ToString("0.######",CultureInfo.InvariantCulture)}:duration={duration.ToString("0.######",CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS";
}
