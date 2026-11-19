using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class EffectiveTranscriptTests
{
    private const string Original="""{"language":"zh","segments":[{"start":1,"end":3,"text":"原始字幕"}]}""";

    [Fact]
    public void NonEmptyOverrideWinsOverOriginalAndFrozenSnapshot()
    {
        var edited=new TranscriptSegment(2,4,"修改后的字幕");
        var project=new EditorProject
        {
            CaptionOverrides=new(){["a"]=[edited]},
            SourceTranscripts=new(){["a"]=[new(0,1,"旧快照")]}
        };

        var result=EffectiveTranscript.Resolve(project,"a",Original);

        Assert.Equal("override",result.Source);
        Assert.Equal(edited,Assert.Single(result.Segments));
        Assert.Equal(edited,Assert.Single(TranscriptExport.Parse(result.CanonicalJson).Segments));
        Assert.DoesNotContain("原始字幕",TranscriptExport.Parse(result.CanonicalJson).Text);
    }

    [Fact]
    public void ExplicitEmptyOverrideDoesNotFallBack()
    {
        var project=new EditorProject{CaptionOverrides=new(){["a"]=[]}};

        var result=EffectiveTranscript.Resolve(project,"a",Original);

        Assert.Equal("override",result.Source);
        Assert.Empty(result.Segments);
        Assert.Empty(TranscriptExport.Parse(result.CanonicalJson).Segments);
        Assert.NotEqual(EffectiveTranscript.Resolve(null,"a",Original).Digest,result.Digest);
    }

    [Fact]
    public void MissingOverrideUsesOriginalNotAnotherSourceOrFrozenSnapshot()
    {
        var project=new EditorProject
        {
            CaptionOverrides=new(){["b"]=[new(0,1,"其他源")]},
            SourceTranscripts=new(){["a"]=[new(0,1,"旧快照")]}
        };

        var result=EffectiveTranscript.Resolve(project,"a",Original);

        Assert.Equal("original",result.Source);
        Assert.Equal(new TranscriptSegment(1,3,"原始字幕"),Assert.Single(result.Segments));
    }

    [Fact]
    public void MultipleSourcesRemainIndependentIncludingExplicitEmpty()
    {
        var project=new EditorProject{CaptionOverrides=new()
        {
            ["a"]=[new(2,5,"A 独有字幕")],
            ["b"]=[new(4,6,"B 独有字幕")],
            ["empty"]=[]
        }};

        Assert.Equal("A 独有字幕",Assert.Single(EffectiveTranscript.Resolve(project,"a",Original).Segments).Text);
        Assert.Equal("B 独有字幕",Assert.Single(EffectiveTranscript.Resolve(project,"b",Original).Segments).Text);
        Assert.Empty(EffectiveTranscript.Resolve(project,"empty",Original).Segments);
        Assert.Equal("original",EffectiveTranscript.Resolve(project,"A",Original).Source);
        Assert.Equal("原始字幕",Assert.Single(EffectiveTranscript.Resolve(project,"missing",Original).Segments).Text);
    }

    [Theory]
    [InlineData(1,3,"不同文字")]
    [InlineData(1.001,3,"原始字幕")]
    [InlineData(1,3.001,"原始字幕")]
    public void DigestAndCacheKeyChangeWithEffectiveTextOrTimes(double start,double end,string text)
    {
        var original=EffectiveTranscript.Resolve(null,"a",Original);
        var edited=EffectiveTranscript.Resolve(new EditorProject{CaptionOverrides=new(){["a"]=[new(start,end,text)]}},"a",Original);
        var baselineOverride=EffectiveTranscript.Resolve(new EditorProject{CaptionOverrides=new(){["a"]=[new(1,3,"原始字幕")]}},"a",Original);

        Assert.NotEqual(original.CanonicalJson,edited.CanonicalJson);
        Assert.NotEqual(original.Digest,edited.Digest);
        Assert.NotEqual(original.CacheKey,edited.CacheKey);
        Assert.NotEqual(baselineOverride.Digest,edited.Digest);
        Assert.NotEqual(baselineOverride.CacheKey,edited.CacheKey);
    }

    [Fact]
    public void DigestIsStableCanonicalSha256AndCacheSeparatesSourcesAndOrigins()
    {
        var original=EffectiveTranscript.Resolve(null,"a",Original);
        var reordered=EffectiveTranscript.Resolve(null,"a","""{ "segments": [ { "text": "原始字幕", "end": 3.0, "start": 1.0 } ], "language": "en" }""");
        var duplicate=EffectiveTranscript.Resolve(null,"b",Original);
        var sameOverride=EffectiveTranscript.Resolve(new EditorProject{CaptionOverrides=new(){["a"]=[new(1,3,"原始字幕")]}},"a",null);
        var expected=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.CanonicalJson))).ToLowerInvariant();

        Assert.Equal(expected,original.Digest);
        Assert.Equal(64,original.Digest.Length);
        Assert.Equal(original.CanonicalJson,reordered.CanonicalJson);
        Assert.Equal(original.Digest,reordered.Digest);
        Assert.Equal(original.CacheKey,reordered.CacheKey);
        Assert.Equal(original.Digest,duplicate.Digest);
        Assert.NotEqual(original.CacheKey,duplicate.CacheKey);
        Assert.Equal(original.Digest,sameOverride.Digest);
        Assert.NotEqual(original.CacheKey,sameOverride.CacheKey);
    }

    [Fact]
    public void CanonicalJsonDoesNotDependOnCurrentCulture()
    {
        var previous=CultureInfo.CurrentCulture;
        try
        {
            var project=new EditorProject{CaptionOverrides=new(){["a"]=[new(1.25,3.75,"字幕")]}};
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("en-US");
            var english=EffectiveTranscript.Resolve(project,"a",null);
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var french=EffectiveTranscript.Resolve(project,"a",null);
            Assert.Equal(english,french with {Segments=english.Segments});
            using var json=JsonDocument.Parse(french.CanonicalJson);
            Assert.Equal(1.25,json.RootElement.GetProperty("segments")[0].GetProperty("start").GetDouble());
        }
        finally{CultureInfo.CurrentCulture=previous;}
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"segments\":null}")]
    [InlineData("{\"segments\":[{\"start\":0,\"end\":1}]}")]
    [InlineData("{\"segments\":[{\"start\":\"bad\",\"end\":1,\"text\":\"x\"}]}")]
    public void MissingOrMalformedTranscriptReturnsSafeCanonicalEmpty(string? json)
    {
        var result=EffectiveTranscript.Resolve(null,"a",json);

        Assert.Equal("original",result.Source);
        Assert.Empty(result.Segments);
        Assert.Empty(TranscriptExport.Parse(result.CanonicalJson).Segments);
        Assert.Equal(EffectiveTranscript.Resolve(null,"a",null).Digest,result.Digest);
        Assert.False(string.IsNullOrWhiteSpace(result.CacheKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"captionOverrides\":17}")]
    public void MissingOrMalformedSavedProjectFallsBackToOriginal(string? savedJson)
    {
        var result=EffectiveTranscript.FromProjectJson(savedJson,"a",Original);

        Assert.Equal("original",result.Source);
        Assert.Equal("原始字幕",Assert.Single(result.Segments).Text);
    }

    [Theory]
    [InlineData("captionOverrides","start","end","text")]
    [InlineData("CaptionOverrides","Start","End","Text")]
    public void SavedProjectUsesOverridesWithWebOrPersistedPropertyCasing(string overridesName,string startName,string endName,string textName)
    {
        var saved=$"{{\"{overridesName}\":{{\"a\":[{{\"{startName}\":2,\"{endName}\":5,\"{textName}\":\"edited\"}}],\"b\":[]}}}}";

        Assert.Equal("edited",Assert.Single(EffectiveTranscript.FromProjectJson(saved,"a",Original).Segments).Text);
        var empty=EffectiveTranscript.FromProjectJson(saved,"b",Original);
        Assert.Equal("override",empty.Source);
        Assert.Empty(empty.Segments);
        Assert.Equal("original",EffectiveTranscript.FromProjectJson(saved,"missing",Original).Source);
    }

    [Fact]
    public void OverrideIsNormalizedWithoutMutatingOrReorderingInput()
    {
        TranscriptSegment[] input=[new(4,6,"second"),new(-1,2,"first"),new(3,3,"invalid"),new(0,2," "),new(double.NaN,2,"invalid")];
        var result=EffectiveTranscript.Resolve(new EditorProject{CaptionOverrides=new(){["a"]=input}},"a",null);

        Assert.Equal(new[]{new TranscriptSegment(4,6,"second"),new TranscriptSegment(0,2,"first")},result.Segments);
        Assert.Equal(-1,input[1].Start);
        Assert.NotSame(input,result.Segments);
        Assert.Equal("override",result.Source);
    }
}
