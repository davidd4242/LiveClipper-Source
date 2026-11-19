using System.Globalization;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class ParametricEqFilterTests
{
    private static readonly (string Key,double Min,double Max,double Default)[] Specs =
    [
        ("master_gain",-30,30,0),
        ("hp_on",0,1,0),("hp_freq",10,20000,30),("hp_slope",0,3,0),
        ("low_on",0,1,1),("low_freq",20,20000,100),("low_gain",-30,30,0),("low_q",.1,30,.707),
        ("b1_on",0,1,1),("b1_freq",20,20000,200),("b1_gain",-30,30,0),("b1_q",.1,30,1),
        ("b2_on",0,1,1),("b2_freq",20,20000,500),("b2_gain",-30,30,0),("b2_q",.1,30,1),
        ("mid_on",0,1,1),("mid_freq",20,20000,1000),("mid_gain",-30,30,0),("mid_q",.1,30,1),
        ("b4_on",0,1,1),("b4_freq",20,20000,2000),("b4_gain",-30,30,0),("b4_q",.1,30,1),
        ("b5_on",0,1,1),("b5_freq",20,20000,5000),("b5_gain",-30,30,0),("b5_q",.1,30,1),
        ("high_on",0,1,1),("high_freq",20,20000,8000),("high_gain",-30,30,0),("high_q",.1,30,.707),
        ("lp_on",0,1,0),("lp_freq",20,22000,18000),("lp_slope",0,3,0)
    ];

    public static IEnumerable<object[]> ParameterLimits()=>Specs.Select(s=>new object[]{s.Key,s.Min,s.Max});
    private static Dictionary<string,double> Params(params (string Key,double Value)[] values)
        =>values.ToDictionary(x=>x.Key,x=>x.Value);
    private static TimelineItem Item(Dictionary<string,double>? parameters=null,bool enabled=true)
        =>new("item","clip",0,4,Effects:[new("eq","parametric_eq",enabled,parameters)]);
    private static Dictionary<string,double> Normalize(Dictionary<string,double>? parameters=null)
        =>Assert.Single(EditorRenderService.NormalizeAudioEffects(Item(parameters))).Params!;
    private static string Filters(params (string Key,double Value)[] parameters)
        =>EditorRenderService.AudioEffectFilters(Item(Params(parameters)));
    private static string N(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("parametric_eq")]
    [InlineData("PARAMETRIC_EQ")]
    [InlineData(" Parametric-Eq ")]
    [InlineData("parametriceq")]
    public void TypeAliasesNormalize(string type)=>Assert.Equal("parametric_eq",EditorRenderService.AudioEffectType(type));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultsMatchAll35EngineSpecs(bool emptyDictionary)
    {
        var normalized=Normalize(emptyDictionary?new():null);
        Assert.Equal(35,normalized.Count);
        foreach(var spec in Specs)Assert.Equal(spec.Default,normalized[spec.Key]);
        Assert.Equal("",EditorRenderService.AudioEffectFilters(Item(normalized)));
    }

    [Theory]
    [MemberData(nameof(ParameterLimits))]
    public void EveryParameterClampsAtBothBounds(string key,double min,double max)
    {
        Assert.Equal(min,Normalize(Params((key,min-10000)))[key]);
        Assert.Equal(max,Normalize(Params((key,max+10000)))[key]);
    }

    [Theory]
    [InlineData("hp_on",.49,0)]
    [InlineData("hp_on",.5,1)]
    [InlineData("lp_on",.5,1)]
    [InlineData("low_on",.5,1)]
    [InlineData("b1_on",.5,1)]
    [InlineData("b2_on",.5,1)]
    [InlineData("mid_on",.5,1)]
    [InlineData("b4_on",.5,1)]
    [InlineData("b5_on",.5,1)]
    [InlineData("high_on",.5,1)]
    [InlineData("hp_slope",.5,1)]
    [InlineData("hp_slope",2.5,3)]
    [InlineData("lp_slope",1.5,2)]
    public void DiscreteParametersRoundHalfUpLikeHost(string key,double raw,double expected)
        =>Assert.Equal(expected,Normalize(Params((key,raw)))[key]);

    [Fact]
    public void ContinuousValuesAndCaseInsensitiveKeysArePreserved()
    {
        var normalized=Normalize(Params(("B2_FREQ",1234.56789),("b2_gain",1.25),("b2_q",2.75),("master_gain",.25)));
        Assert.Equal(1234.56789,normalized["b2_freq"]);
        Assert.Equal(1.25,normalized["b2_gain"]);
        Assert.Equal(2.75,normalized["b2_q"]);
        Assert.Equal(.25,normalized["master_gain"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteKnownOrUnknownParameterDropsWholeEffect(double value)
    {
        foreach(var key in new[]{"b2_gain","hp_on","hp_slope","unknown"})
        {
            var item=Item(Params((key,value),("master_gain",6)));
            Assert.Empty(EditorRenderService.NormalizeAudioEffects(item));
            Assert.Equal("",EditorRenderService.AudioEffectFilters(item));
        }
    }

    [Fact]
    public void UnknownLegacyTypeKeysDoNotEnterThe35KeyContract()
    {
        var normalized=Normalize(Params(("b2_type",5),("b3_gain",9),("b7_q",2)));
        Assert.Equal(35,normalized.Count);
        Assert.DoesNotContain("b2_type",normalized.Keys);
        Assert.DoesNotContain("b3_gain",normalized.Keys);
        Assert.DoesNotContain("b7_q",normalized.Keys);
    }

    [Fact]
    public void DisabledEffectIsRetainedButBypassed()
    {
        var item=Item(Params(("hp_on",1),("b2_gain",9),("master_gain",6)),false);
        var effect=Assert.Single(EditorRenderService.NormalizeAudioEffects(item));
        Assert.False(effect.Enabled);
        Assert.Equal(35,effect.Params!.Count);
        Assert.Equal("",EditorRenderService.AudioEffectFilters(item));
    }

    [Fact]
    public void HighPassOffDoesNotGenerateHighPass()
    {
        var chain=Filters(("hp_on",0),("hp_slope",3),("b2_gain",9));
        Assert.DoesNotContain("highpass",chain);
        Assert.StartsWith("equalizer=",chain);
    }

    [Fact]
    public void LowPassOffDoesNotGenerateLowPass()
    {
        var chain=Filters(("lp_on",0),("lp_slope",3),("b2_gain",9));
        Assert.DoesNotContain("lowpass",chain);
        Assert.StartsWith("equalizer=",chain);
    }

    [Theory]
    [InlineData("hp","highpass",0,1)]
    [InlineData("hp","highpass",1,2)]
    [InlineData("hp","highpass",2,3)]
    [InlineData("hp","highpass",3,4)]
    [InlineData("lp","lowpass",0,1)]
    [InlineData("lp","lowpass",1,2)]
    [InlineData("lp","lowpass",2,3)]
    [InlineData("lp","lowpass",3,4)]
    public void SlopesSelectOneToFourSecondOrderStages(string prefix,string filter,int slope,int stages)
    {
        var chain=Filters(($"{prefix}_on",1),($"{prefix}_freq",1000),($"{prefix}_slope",slope));
        var parts=chain.Split(',');
        Assert.Equal(stages,parts.Length);
        Assert.All(parts,part=>Assert.Equal($"{filter}=f=1000:p=2:t=q:w=0.707107:r=f64",part));
    }

    [Theory]
    [InlineData("low","lowshelf",100,.707)]
    [InlineData("b1","equalizer",200,1)]
    [InlineData("b2","equalizer",500,1)]
    [InlineData("mid","equalizer",1000,1)]
    [InlineData("b4","equalizer",2000,1)]
    [InlineData("b5","equalizer",5000,1)]
    [InlineData("high","highshelf",8000,.707)]
    public void SevenFixedBandsUseEngineFrequencyGainAndQ(string prefix,string filter,double frequency,double q)
    {
        Assert.Equal($"{filter}=f={N(frequency)}:t=q:w={N(q)}:g=9:r=f64",Filters(($"{prefix}_gain",9)));
        Assert.Equal($"{filter}=f=1234:t=q:w=2:g=-9:r=f64",Filters(($"{prefix}_freq",1234),($"{prefix}_q",2),($"{prefix}_gain",-9)));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("b1")]
    [InlineData("b2")]
    [InlineData("mid")]
    [InlineData("b4")]
    [InlineData("b5")]
    [InlineData("high")]
    public void OffOrZeroGainBandsDoNotGenerateFilters(string prefix)
    {
        Assert.Equal("",Filters(($"{prefix}_on",0),($"{prefix}_gain",9)));
        Assert.Equal("",Filters(($"{prefix}_on",1),($"{prefix}_gain",0)));
    }

    [Fact]
    public void MasterGainUsesDbAndSkipsZero()
    {
        Assert.Equal("",Filters(("master_gain",0)));
        Assert.Equal("volume=6dB:precision=double",Filters(("master_gain",6)));
        Assert.Equal("volume=-6dB:precision=double",Filters(("master_gain",-6)));
    }

    [Fact]
    public void ChainOrderIsHighPassLowPassSevenBandsThenMaster()
    {
        var parameters=Params(("hp_on",1),("lp_on",1),("master_gain",6));
        foreach(var prefix in new[]{"low","b1","b2","mid","b4","b5","high"})parameters[prefix+"_gain"]=3;
        var chain=EditorRenderService.AudioEffectFilters(Item(parameters));
        Assert.StartsWith("highpass=",chain);
        var ordered=new[]{"highpass=","lowpass=","lowshelf=","equalizer=f=200:","equalizer=f=500:","equalizer=f=1000:","equalizer=f=2000:","equalizer=f=5000:","highshelf=","volume=6dB"};
        var previous=-1;
        foreach(var token in ordered)
        {
            var index=chain.IndexOf(token,StringComparison.Ordinal);
            Assert.True(index>previous,$"{token} out of order: {chain}");
            previous=index;
        }
        Assert.EndsWith("volume=6dB:precision=double",chain);
        Assert.Equal(10,chain.Split(',').Length);
    }

    [Fact]
    public void AllSwitchCombinationsGenerateOnlySelectedFilters()
    {
        for(var mask=0;mask<16;mask++)
        {
            var chain=Filters(("hp_on",mask&1),("lp_on",(mask>>1)&1),("b2_on",(mask>>2)&1),("b2_gain",9),("master_gain",(mask&8)==0?0:6));
            Assert.Equal((mask&1)!=0,chain.Contains("highpass=",StringComparison.Ordinal));
            Assert.Equal((mask&2)!=0,chain.Contains("lowpass=",StringComparison.Ordinal));
            Assert.Equal((mask&4)!=0,chain.Contains("equalizer=",StringComparison.Ordinal));
            Assert.Equal((mask&8)!=0,chain.Contains("volume=",StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EqStaysAfterSpeedAndBeforeEnvelopeAndFades()
    {
        var item=Item(Params(("b2_gain",9),("master_gain",6))) with
        {
            Speed=2,VolumeDb=-6,VolumeKeyframes=[new(0,0),new(1,-3)],FadeIn=.1,FadeOut=.1
        };
        var chain=EditorRenderService.AudioFilterChain(item);
        Assert.StartsWith("volume=0.501187,atempo=2,aresample=48000,equalizer=",chain);
        var band=chain.IndexOf("equalizer=",StringComparison.Ordinal);
        var master=chain.IndexOf("volume=6dB",StringComparison.Ordinal);
        var envelope=chain.IndexOf("volume=volume='",StringComparison.Ordinal);
        var fade=chain.IndexOf("afade=t=in",StringComparison.Ordinal);
        Assert.True(band<master&&master<envelope&&envelope<fade,chain);
    }

    [Fact]
    public void ActiveEqResamplesBeforeBiquadsAtTheExportSampleRate()
    {
        var item=Item(Params(("hp_on",1),("hp_freq",20000),("lp_on",1)));
        Assert.StartsWith("aresample=48000,highpass=f=20000:",EditorRenderService.AudioFilterChain(item));
        Assert.DoesNotContain("aresample",EditorRenderService.AudioEffectFilters(item));
    }

    [Fact]
    public void EqDoesNotResampleForZeroOrDisabledEffects()
    {
        Assert.Equal("",EditorRenderService.AudioFilterChain(Item()));
        Assert.Equal("",EditorRenderService.AudioFilterChain(Item(Params(("hp_on",1)),false)));
        Assert.Equal("",EditorRenderService.AudioFilterChain(Item(Params(("b2_on",0),("b2_gain",9)))));
        var item=Item() with {Effects=[new("before","gate"),new("eq","parametric_eq")]};
        Assert.StartsWith("agate=",EditorRenderService.AudioFilterChain(item));
        Assert.DoesNotContain("aresample",EditorRenderService.AudioFilterChain(item));
    }

    [Fact]
    public void EqResamplesOnceWhenSeveralActiveEqEffectsAreChained()
    {
        var item=Item() with {Effects=[new("first","parametric_eq",true,Params(("b1_gain",6))),new("second","parametric_eq",true,Params(("master_gain",3)))]};
        var chain=EditorRenderService.AudioFilterChain(item);
        Assert.StartsWith("aresample=48000,equalizer=",chain);
        Assert.Equal(1,chain.Split(',').Count(part=>part=="aresample=48000"));
    }

    [Fact]
    public void EqPreservesEffectArrayOrderAndFourEffectLimit()
    {
        var item=Item() with {Effects=[new("before","gate"),new("eq","parametric_eq",true,Params(("b2_gain",9))),new("after","comp")]};
        var chain=EditorRenderService.AudioEffectFilters(item);
        Assert.StartsWith("agate=",chain);
        Assert.True(chain.IndexOf("agate=",StringComparison.Ordinal)<chain.IndexOf("equalizer=",StringComparison.Ordinal));
        Assert.True(chain.IndexOf("equalizer=",StringComparison.Ordinal)<chain.IndexOf("acompressor=",StringComparison.Ordinal));
        Assert.Equal(4,EditorRenderService.NormalizeAudioEffects(Enumerable.Range(0,5).Select(i=>new AudioEffectInstance($"eq{i}","parametric_eq"))).Length);
    }
}
