namespace LiveClipper.Core.EditorFx;

public static partial class AudioFxChain
{
    private static string A8Crossover(string kind, double frequency)
    {
        var hz = Math.Pow(2, Math.Log2(frequency));
        var angle = 2 * Math.PI * hz / 48000;
        var cosine = Math.Cos(angle);
        var alpha = Math.Sin(angle) / (2 * Math.Sqrt(.5));
        var a0 = 1 + alpha;
        var a1 = -2 * cosine / a0;
        var a2 = (1 - alpha) / a0;
        var b0 = (kind == "lp" ? (1 - cosine) / 2 : kind == "hp" ? (1 + cosine) / 2 : 1 - alpha) / a0;
        var b1 = (kind == "lp" ? 1 - cosine : kind == "hp" ? -(1 + cosine) : -2 * cosine) / a0;
        var b2 = (kind == "ap" ? 1 + alpha : kind == "lp" ? (1 - cosine) / 2 : (1 + cosine) / 2) / a0;
        // TDF-II state and denormal flushing match the preview before any Float32 write.
        var expression = $"st(2,{N(b0)}*val(ch)+ld(0));st(0,{Flush($"{N(b1)}*val(ch)-({N(a1)})*ld(2)+ld(1)", 1e-30)});st(1,{Flush($"{N(b2)}*val(ch)-({N(a2)})*ld(2)", 1e-30)});ld(2)";
        var section = $"aeval=exprs='if(lt(ch,8),{expression},val(ch))':c=same";
        return kind == "ap" ? section : $"{section},{section}";
    }

    private const string A8LinkedPeak = "st(8,0);st(9,0);while(lt(ld(9),min(nb_in_channels,8)),st(8,max(ld(8),abs(val(ld(9)))));st(9,ld(9)+1));";

    private static string A8Band(Func<string, double> p, int band, bool anySolo)
    {
        var prefix = $"b{band}_";
        var bypass = p(prefix + "bypass") == 1;
        var level = anySolo && p(prefix + "solo") == 0 ? 0 : bypass ? 1 : Math.Pow(10, p(prefix + "gain") / 20);
        // The preview's unlinked path calls linDb on the signed sample, not abs(sample).
        var expression = p("link") == 1 ? A8LinkedPeak : "st(8,val(ch));";
        if (!bypass)
        {
            expression += $"st(2,{PeakDb});st(3,{Curve("ld(2)", p(prefix + "threshold"), p(prefix + "ratio"), 0)});";
            expression += Smooth(0, "ld(3)", Coef(p(prefix + "attack")), N(Coef(p(prefix + "release")))) + ";";
        }
        var gain = bypass ? "1" : "pow(10,ld(0)/20)";
        return $"aeval=exprs='if(lt(ch,8),{expression}val(ch)*({gain}*{N(level)}),{(band == 1 ? "val(ch)" : "0")})':c=same";
    }

    private static string A8Multiband(Func<string, double> p, string tag)
    {
        var f1 = p("xo1");
        var f2 = Math.Max(p("xo2"), f1 * 1.25);
        var f3 = Math.Min(Math.Max(p("xo3"), f2 * 1.25), 48000 * .45);
        f2 = Math.Min(f2, f3 / 1.25);
        var lp1 = A8Crossover("lp", f1); var hp1 = A8Crossover("hp", f1);
        var lp2 = A8Crossover("lp", f2); var hp2 = A8Crossover("hp", f2);
        var lp3 = A8Crossover("lp", f3); var hp3 = A8Crossover("hp", f3);
        var ap2 = A8Crossover("ap", f2); var ap3 = A8Crossover("ap", f3);
        var solo = p("b1_solo") == 1 || p("b2_solo") == 1 || p("b3_solo") == 1 || p("b4_solo") == 1;
        var graph = $"aformat=sample_fmts=flt,aformat=sample_fmts=dbl,asplit=4[{tag}i1][{tag}i2][{tag}i3][{tag}i4];";
        graph += $"[{tag}i1]{lp1},{ap2},{ap3},{A8Band(p, 1, solo)}[{tag}o1];";
        graph += $"[{tag}i2]{hp1},{lp2},{ap3},{A8Band(p, 2, solo)}[{tag}o2];";
        graph += $"[{tag}i3]{hp1},{hp2},{lp3},{A8Band(p, 3, solo)}[{tag}o3];";
        graph += $"[{tag}i4]{hp1},{hp2},{hp3},{A8Band(p, 4, solo)}[{tag}o4];";
        graph += $"[{tag}o1][{tag}o2][{tag}o3][{tag}o4]amix=inputs=4:duration=longest:dropout_transition=0:normalize=0,aeval=exprs='if(lt(ch,8),val(ch)*{N(Math.Pow(10, p("output") / 20))},val(ch))':c=same";
        if (p("lim_on") == 1)
        {
            var ceiling = Math.Pow(10, p("lim_threshold") / 20);
            var expression = A8LinkedPeak + $"st(2,min({N(p("lim_threshold"))}-({PeakDb}),0));";
            expression += $"if(lt(ld(2),ld(0)),st(0,ld(2)),{Smooth(0, "ld(2)", 0, N(Coef(p("lim_release"))))});";
            graph += $",aeval=exprs='if(lt(ch,8),{expression}clip(val(ch)*pow(10,ld(0)/20),{N(-ceiling)},{N(ceiling)}),val(ch))':c=same";
        }
        // Leave rematrixing after the completed DSP, never before its linked detector.
        return graph + ",aformat=sample_fmts=flt,aresample";
    }
}
