namespace LiveClipper.Core.EditorFx;

public static partial class AudioFxChain
{
    private static string A7Speech(Func<string, double> p, string tag)
    {
        var mix = p("mix") / 100;
        if (mix == 0) return "";
        var q = N(Math.Sqrt(.5));
        var mud = p("tone") == 1 ? 350 : 250;
        var presence = p("tone") == 1 ? 4000 : 2500;
        var eq = $"highpass=f=80:p=2:t=q:w={q}:r=f64,equalizer=f={mud}:t=q:w=1:g=-3:r=f64,equalizer=f={presence}:t=q:w=1:g=4:r=f64,highshelf=f=10000:p=2:t=q:w={q}:g=2:r=f64";
        var det = Coef(5);
        var expression = "st(8,0);st(9,0);while(lt(ld(9),min(nb_in_channels,8)),st(8,max(ld(8),val(ld(9))*val(ld(9))));st(9,ld(9)+1));";
        expression += $"st(0,{N(det)}*ld(0)+{N(1 - det)}*ld(8));st(0,{Flush("ld(0)", 1e-30)});st(3,10*log(ld(0)+1e-20)/log(10));";
        expression += "st(4,if(lt(ld(3),-50),max((ld(3)+50)*2,-18),0));";
        expression += Smooth(1, "ld(4)", Coef(30), N(Coef(3)), 1e-9) + ";";
        expression += $"st(4,{Curve("ld(3)", -24, 3, 6)});";
        expression += Smooth(2, "ld(4)", Coef(3), N(Coef(120)), 1e-9) + ";";
        // Keep EQ in double precision until the preview's final dry/wet Float32 write.
        var wet = $"{eq},aeval=exprs='{expression}val(ch)*pow(10,(ld(1)+ld(2)+3)/20)*{N(mix)}':c=same";
        return $"aformat=sample_fmts=flt,aformat=sample_fmts=dbl,asplit=2[{tag}d][{tag}w];[{tag}d]volume={N(1 - mix)}:precision=double[{tag}dv];[{tag}w]{wet}[{tag}wv];[{tag}dv][{tag}wv]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,aformat=sample_fmts=flt";
    }

    private static string A7Mastering(Func<string, double> p)
    {
        var q = N(Math.Sqrt(.5));
        var eq = "aformat=sample_fmts=flt,aformat=sample_fmts=dbl";
        if (p("eq_low") != 0) eq += $",lowshelf=f=100:t=q:w={q}:g={N(p("eq_low"))}:r=f64";
        if (p("eq_mid") != 0) eq += $",equalizer=f={N(p("eq_mid_freq"))}:t=q:w=1:g={N(p("eq_mid"))}:r=f64";
        if (p("eq_high") != 0) eq += $",highshelf=f=8000:t=q:w={q}:g={N(p("eq_high"))}:r=f64";
        var expression = "st(2,val(0));st(3,if(gte(nb_in_channels,2),val(1),0));";
        if (p("exciter") != 0)
        {
            var alpha = N(1 - Math.Exp(-2 * Math.PI * 3000 / 48000));
            var amount = N(.6 * p("exciter") / 100);
            var mode = p("exciter_mode");
            var shape = mode == 0 ? "clip(3*ld(5),-1,1)/3" : mode == 1 ? "tanh(3*ld(5))/3" : "(tanh(3*ld(5)+0.2)-tanh(0.2))/3";
            expression += $"st(0,{Flush($"ld(0)+{alpha}*(ld(2)-ld(0))", 1e-30)});st(5,ld(2)-ld(0));st(2,ld(2)+{amount}*({shape}-ld(5)*0.5)*2);";
            expression += $"st(1,{Flush($"ld(1)+{alpha}*(ld(3)-ld(1))", 1e-30)});st(5,ld(3)-ld(1));st(3,ld(3)+{amount}*({shape}-ld(5)*0.5)*2);";
        }
        expression += $"if(gte(nb_in_channels,2),st(5,0.5*(ld(2)+ld(3)));st(6,0.5*(ld(2)-ld(3))*{N(p("widener") / 100)});st(2,ld(5)+ld(6));st(3,ld(5)-ld(6)));";
        if (p("loudness") > 0)
        {
            var boost = N(Math.Pow(10, (12 * p("loudness") / 100) / 20));
            var ceiling = Math.Pow(10, -.3 / 20);
            expression += $"st(2,ld(2)*{boost});st(3,ld(3)*{boost});st(5,min(-0.3-20*log(max(max(abs(ld(2)),abs(ld(3))),1e-10))/log(10),0));";
            expression += $"st(4,if(lt(ld(5),ld(4)),ld(5),{N(Coef(60))}*ld(4)+{N(1 - Coef(60))}*ld(5)));st(2,ld(2)*pow(10,ld(4)/20));st(3,ld(3)*pow(10,ld(4)/20));";
            expression += $"st(2,{A7SoftClip("ld(2)", ceiling)});st(3,{A7SoftClip("ld(3)", ceiling)});";
        }
        var output = $"if(eq(ch,0),ld(2)*{N(Math.Pow(10, p("output") / 20))},if(eq(ch,1),ld(3)*{N(Math.Pow(10, p("output") / 20))},val(ch)))";
        return $"{eq},aformat=sample_fmts=flt,aeval=exprs='{expression}{output}':c=same,aformat=sample_fmts=flt";
    }

    private static string A7SoftClip(string value, double ceiling)
    {
        var t = $"min((abs({value})-{N(ceiling / 2)})/{N(ceiling)},1)";
        return $"if(lte(abs({value}),{N(ceiling / 2)}),{value},sgn({value})*({N(ceiling / 2)}+{N(ceiling / 2)}*(2*({t})-({t})*({t}))))";
    }
}
