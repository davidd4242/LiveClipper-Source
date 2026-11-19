using LiveClipper.Core;
using Xunit;

namespace LiveClipper.Tests;

public class TimelineAudioTests
{
    [Theory]
    [InlineData(0,4,"aresample=48000:async=1:first_pts=0:min_comp=0:min_hard_comp=0,atrim=start=0:duration=4,asetpts=PTS-STARTPTS")]
    [InlineData(186.733333333333,131.126667,"aresample=48000:async=1:first_pts=0:min_comp=0:min_hard_comp=0,atrim=start=186.733333:duration=131.126667,asetpts=PTS-STARTPTS")]
    public void TimestampRepairPrecedesSourceTrim(double start,double duration,string expected)
        => Assert.Equal(expected,TimelineAudio.TrimFilter(start,duration));
}
