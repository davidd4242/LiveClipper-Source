namespace LiveClipper.Core.EditorFx;

/// <summary>Only allowlisted transition kernels reach FFmpeg; every other kind fails closed.</summary>
public static class TransitionMap
{
    public static bool IsSupported(string? kind)=>kind is
        "cross-dissolve" or "dip-to-black" or "dip-to-white" or
        "wipe-left" or "wipe-right" or "wipe-up" or "wipe-down" || BlendExpression(kind) is not null;

    // These expressions use encoded RGB, pixel centers and the preview's exact coverage ramp.
    // $P is replaced only by the validated local overlap time, never by project text.
    public static string? BlendExpression(string? kind)=>kind switch
    {
        "catalog-wipe-up"=>"st(0,clip(($P-(1-(Y+0.5)/H))/0.004+0.5,0,1));A*(1-ld(0))+B*ld(0)",
        "linear-wipe-up"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.004-(1-(Y+0.5)/H))/0.004,0,1));A*(1-ld(0))+B*ld(0))))",
        "catalog-wipe-right"=>"st(0,clip(($P-((X+0.5)/W))/0.004+0.5,0,1));A*(1-ld(0))+B*ld(0)",
        "linear-wipe-right"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.004-((X+0.5)/W))/0.004,0,1));A*(1-ld(0))+B*ld(0))))",
        "catalog-wipe-down"=>"st(0,clip(($P-((Y+0.5)/H))/0.004+0.5,0,1));A*(1-ld(0))+B*ld(0)",
        "linear-wipe-down"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.004-((Y+0.5)/H))/0.004,0,1));A*(1-ld(0))+B*ld(0))))",
        "catalog-wipe-left"=>"st(0,clip(($P-(1-(X+0.5)/W))/0.004+0.5,0,1));A*(1-ld(0))+B*ld(0)",
        "linear-wipe-left"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.004-(1-(X+0.5)/W))/0.004,0,1));A*(1-ld(0))+B*ld(0))))",
        "additive-dissolve"=>"min(255,A*min(1,2*(1-$P))+B*min(1,2*$P))", // R77 independent A/B additive kernel
        "non-additive-dissolve"=>"if(lte($P,0),A,if(gte($P,1),B,max(0,exp(log(A+1)*(1-$P)+log(B+1)*$P)-1)))", // R74T3 expression
        "iris-box"=>"st(0,clip(($P*1.004-max(abs(2*(X+0.5)/W-1),abs(2*(Y+0.5)/H-1)))/0.004,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "iris-cross"=>"st(0,clip(($P*1.004-min(abs(2*(X+0.5)/W-1),abs(2*(Y+0.5)/H-1)))/0.004,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "iris-diamond"=>"st(0,clip(($P*1.004-(abs(2*(X+0.5)/W-1)+abs(2*(Y+0.5)/H-1))/2)/0.004,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "iris-round"=>"st(0,clip(($P*1.004-hypot(abs(2*(X+0.5)/W-1),abs(2*(Y+0.5)/H-1))/sqrt(2))/0.004,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "barn-doors-vertical"=>"st(0,clip(($P*1.004-abs(2*(X+0.5)/W-1))/0.004,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "barn-doors-horizontal"=>"st(0,clip(($P*1.004-abs(2*(Y+0.5)/H-1))/0.004,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "soft-wipe-up"=>"st(0,clip(($P*1.18-(1-(Y+0.5)/H))/0.18,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "soft-wipe-right"=>"st(0,clip(($P*1.18-(X+0.5)/W)/0.18,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "soft-wipe-down"=>"st(0,clip(($P*1.18-(Y+0.5)/H)/0.18,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "soft-wipe-left"=>"st(0,clip(($P*1.18-(1-(X+0.5)/W))/0.18,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "gradient-wipe-up"=>"st(0,clip(($P*1.1-(1-(Y+0.5)/H))/0.1,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "gradient-wipe-right"=>"st(0,clip(($P*1.1-(X+0.5)/W)/0.1,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "gradient-wipe-down"=>"st(0,clip(($P*1.1-(Y+0.5)/H)/0.1,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "gradient-wipe-left"=>"st(0,clip(($P*1.1-(1-(X+0.5)/W))/0.1,0,1));A*(1-ld(0))+B*ld(0)", // R74T3 expression
        "panel-wipe"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip((clip($P*1.4-floor(((X+0.5)/W)*6)/6*0.4,0,1)*1.004-mod(((X+0.5)/W)*6,1))/0.004,0,1));A*(1-ld(0))+B*ld(0))))", // R79T5 fixed-default expression
        "vr-iris-wipe"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P-acos(clip(cos(((Y+0.5)/H-0.5)*PI)*cos(((X+0.5)/W-0.5)*PI*2),-1,1))/PI)/0.004+0.5,0,1));A*(1-ld(0))+B*ld(0))))", // R79T5 fixed-default expression
        "vr-gradient-wipe"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.2-(0.5-0.5*cos(((X+0.5)/W)*PI*2)))/0.2,0,1));A*(1-ld(0))+B*ld(0))))", // R79T5 fixed-default expression
        "flash"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,if(lt($P,0.5),4*$P*$P*$P,1-pow(-2*$P+2,3)/2));st(1,sin(PI*$P)*sin(PI*$P));255-(255-(A*(1-ld(0))+B*ld(0)))*(1-clip(ld(1),0,1)))))", // R79T5 fixed-default expression
        "ray"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,if(lt($P,0.5),4*$P*$P*$P,1-pow(-2*$P+2,3)/2));st(1,sin(PI*$P)*pow(0.5+0.5*cos(atan2(((Y+0.5)/H-0.5)*H/W,(X+0.5)/W-0.5)*18+$P*4),8)*exp(-hypot((X+0.5)/W-0.5,((Y+0.5)/H-0.5)*H/W)/0.5));255-(255-(A*(1-ld(0))+B*ld(0)))*(1-clip(ld(1),0,1)))))", // R79T5 fixed-default expression
        "vr-light-rays"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,if(lt($P,0.5),4*$P*$P*$P,1-pow(-2*$P+2,3)/2));st(1,sin(PI*$P)*pow(0.5+0.5*cos(atan2(((Y+0.5)/H-0.5)*H/W,(X+0.5)/W-0.5)*18+$P*4),8)*exp(-hypot((X+0.5)/W-0.5,((Y+0.5)/H-0.5)*H/W)/0.8));255-(255-(A*(1-ld(0))+B*ld(0)))*(1-clip(ld(1),0,1)))))", // R79T5 fixed-default expression
        "solarize"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,if(lt($P,0.5),4*$P*$P*$P,1-pow(-2*$P+2,3)/2));st(1,A*(1-ld(0))+B*ld(0));ld(1)+(if(gt(ld(1),128),255-ld(1),ld(1))-ld(1))*sin(PI*$P))))", // R79T5 fixed-default expression
        "flare"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,if(lt($P,0.5),4*$P*$P*$P,1-pow(-2*$P+2,3)/2));st(1,sin(PI*$P)*(exp(-pow(((X+0.5)/W-0.5)*cos(20*PI/180)+((Y+0.5)/H-0.5)*H/W*sin(20*PI/180)-($P-0.5)*1.6,2)*90-pow(-((X+0.5)/W-0.5)*sin(20*PI/180)+((Y+0.5)/H-0.5)*H/W*cos(20*PI/180),2)*15)+0.45*exp(-pow(-((X+0.5)/W-0.5)*sin(20*PI/180)+((Y+0.5)/H-0.5)*H/W*cos(20*PI/180),2)*300)*exp(-pow(((X+0.5)/W-0.5)*cos(20*PI/180)+((Y+0.5)/H-0.5)*H/W*sin(20*PI/180)-($P-0.5)*1.6,2)*4)));255-(255-(A*(1-ld(0))+B*ld(0)))*(1-clip(ld(1),0,1)))))", // R79T5 fixed-default expression
        "light-sweep"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,if(lt($P,0.5),4*$P*$P*$P,1-pow(-2*$P+2,3)/2));st(1,sin(PI*$P)*exp(-pow(((X+0.5)/W-0.5)*cos(70*PI/180)+((Y+0.5)/H-0.5)*H/W*sin(70*PI/180)-($P-0.5)*(abs(cos(70*PI/180))+abs(sin(70*PI/180))*H/W+160/W*4),2)/pow(160/W,2)));255-(255-(A*(1-ld(0))+B*ld(0)))*(1-clip(ld(1),0,1)))))", // R79T5 fixed-default expression
        "plateau-wipe"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.004-(clip(((X+0.5)/W)*0.78+floor(clip(((Y+0.5)/H)*H/W,0,1)*5)/5*0.22,0,1)))/0.004,0,1));A*(1-ld(0))+B*ld(0))))", // R79T5 fixed-default expression
        "shape-flow"=>"if(lte($P,0),A,if(gte($P,1),B,(st(0,clip(($P*1.004-(hypot(abs(2*(X+0.5)/W-1),abs(2*(Y+0.5)/H-1))/sqrt(2)))/0.004,0,1));A*(1-ld(0))+B*ld(0))))", // R79T5 fixed-default expression
        _=>null
    };

    public static string Join(string? kind,string outgoing,string incoming,string output,double offset,double duration,double fps)
        =>Join(kind,outgoing,incoming,output,offset,duration,fps,"yuv420p");

    public static string Join(string? kind,string outgoing,string incoming,string output,double offset,double duration,double fps,string dipPixelFormat)
    {
        RequireKind(kind);
        if(!double.IsFinite(offset)||offset<0||!double.IsFinite(duration)||duration<=0||!double.IsFinite(fps)||fps<=0)
            throw new ArgumentException("transition-invalid-timing");
        static string N(double value)=>value.ToString("G17",System.Globalization.CultureInfo.InvariantCulture);
        if(kind is "dip-to-black" or "dip-to-white")
            return DipJoin(kind,outgoing,incoming,output,offset,duration,fps,dipPixelFormat);
        var expression=BlendExpression(kind);
        if(expression is null)return $"{outgoing}{incoming}xfade=transition={Xfade(kind)}:duration={N(duration)}:offset={N(offset)}{output}";
        var label=output.Trim('[',']');
        // blend's N starts at one. Frame progress avoids microsecond quantization at narrow edges.
        var expressionAtTime=expression.Replace("$P",$"clip((N-1)/({N(duration)}*{N(fps)}),0,1)",StringComparison.Ordinal);
        // Rebuild both overlap clocks on the same CFR grid: subtracting separately rounded
        // AVTB timestamps can differ by 1us and make blend framesync reuse the previous frame.
        var spanFrames=Math.Max(1,(int)Math.Round(duration*fps,MidpointRounding.AwayFromZero));
        var offsetFrames=Math.Max(0,(int)Math.Round(offset*fps,MidpointRounding.AwayFromZero));
        var tail=offset==0
            ? $"{outgoing}trim=end_frame={spanFrames},setpts=N/({N(fps)}*TB),format=gbrp[{label}a]"
            : $"{outgoing}split[{label}pre][{label}tail];[{label}pre]trim=end_frame={offsetFrames},setpts=N/({N(fps)}*TB),format=gbrp[{label}p];[{label}tail]trim=start_frame={offsetFrames}:end_frame={offsetFrames+spanFrames},setpts=N/({N(fps)}*TB),format=gbrp[{label}a]";
        return tail+$";{incoming}split[{label}head][{label}post];[{label}head]trim=end_frame={spanFrames},setpts=N/({N(fps)}*TB),format=gbrp[{label}b];[{label}post]trim=start_frame={spanFrames},setpts=N/({N(fps)}*TB),format=gbrp[{label}q];[{label}a][{label}b]blend=all_expr='{expressionAtTime}':shortest=1[{label}m];"
            +(offset==0?$"[{label}m][{label}q]concat=n=2:v=1:a=0":$"[{label}p][{label}m][{label}q]concat=n=3:v=1:a=0")
            // concat accumulates rounded segment durations; do not pass that drift to a later xfade.
            +$",settb=AVTB,setpts=round(N/({N(fps)}*TB)){output}";
    }

    private static int DipFrameCount(double duration,double fps)
    {
        // Visual phase only: ResolveTransitions keeps the project timeline grid.
        // Auto-fps may legitimately make duration*fps fractional; never reject that.
        var rawFrames=duration*fps;
        var nearest=Math.Max(1,Math.Round(rawFrames,MidpointRounding.AwayFromZero));
        if(!double.IsFinite(rawFrames)||nearest>int.MaxValue)
            throw new ArgumentException("transition-dip-frame-count-out-of-range");
        return checked((int)nearest);
    }

    private static string DipBlendOptions(string kind,string pixelFormat,int frames)
    {
        var centers=pixelFormat switch
        {
            "yuv420p"=>new[]{kind=="dip-to-black"?16:235,128,128},
            "yuv420p10le"=>new[]{kind=="dip-to-black"?64:940,512,512},
            "gbrp"=>new[]{kind=="dip-to-black"?0:255,kind=="dip-to-black"?0:255,kind=="dip-to-black"?0:255},
            _=>throw new ArgumentException($"unsupported-dip-pixel-format:{pixelFormat}")
        };
        var span=frames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var q=$"clip(N-1,0,{span})";
        string Plane(int center)=>$"if(lte({q},0),A,if(gte({q},{span}),B,if(lte(2*({q}),{span}),floor((A*({span}-2*({q}))+{center}*2*({q})+{span}/2)/{span}),floor(({center}*(2*{span}-2*({q}))+B*(2*({q})-{span})+{span}/2)/{span}))))";
        return $"c0_expr='{Plane(centers[0])}':c1_expr='{Plane(centers[1])}':c2_expr='{Plane(centers[2])}'";
    }

    private static string DipJoin(string kind,string outgoing,string incoming,string output,double offset,double duration,double fps,string pixelFormat)
    {
        static string N(double value)=>value.ToString("G17",System.Globalization.CultureInfo.InvariantCulture);
        var frames=DipFrameCount(duration,fps);
        var span=frames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var blend=DipBlendOptions(kind,pixelFormat,frames);
        var label=output.Trim('[',']');
        // Extend A before offset trim, then bound it to D frames. B is never padded.
        // This is boundary extension for a visual phase, not a timeline duration change.
        var tail=offset==0
            ? $"{outgoing}tpad=stop_mode=clone:stop=-1,trim=end_frame={span},setpts=N/({N(fps)}*TB),format={pixelFormat}[{label}a]"
            : $"{outgoing}split[{label}pre][{label}tail];[{label}pre]trim=end={N(offset)},setpts=N/({N(fps)}*TB),format={pixelFormat}[{label}p];[{label}tail]tpad=stop_mode=clone:stop=-1,trim=start={N(offset)},trim=end_frame={span},setpts=N/({N(fps)}*TB),format={pixelFormat}[{label}a]";
        return tail+$";{incoming}split[{label}head][{label}post];[{label}head]trim=end_frame={span},setpts=N/({N(fps)}*TB),format={pixelFormat}[{label}b];[{label}post]trim=start_frame={span},setpts=N/({N(fps)}*TB),format={pixelFormat}[{label}q];[{label}a][{label}b]blend={blend}:shortest=1[{label}m];"
            +(offset==0?$"[{label}m][{label}q]concat=n=2:v=1:a=0":$"[{label}p][{label}m][{label}q]concat=n=3:v=1:a=0")
            +$",settb=AVTB,setpts=round(N/({N(fps)}*TB)){output}";
    }

    public static string RequireKind(string? kind)=>IsSupported(kind)
        ? kind!
        : throw new ArgumentException($"unsupported_transition:{kind}");

    public static void RequireOutputSize(string? kind,int width,int height)
    {
        RequireKind(kind);
        // R79T5 pixel equivalence was proved only on the preview's fixed 1280x720 canvas.
        if(kind is "panel-wipe" or "vr-iris-wipe" or "vr-gradient-wipe" or "flash" or "ray" or "vr-light-rays" or "solarize" or "flare" or "light-sweep" or "plateau-wipe" or "shape-flow")
            if(width!=1280||height!=720)
                throw new ArgumentException($"unsupported_transition:{kind}:output_size:{width}x{height}:requires_1280x720");
    }

    public static void RequireTrack(string? kind,int declaredTrack,int? itemTrack=null)
    {
        RequireKind(kind);
        // Only V1 reaches Join; overlays and audio tracks do not run a transition kernel.
        if(BlendExpression(kind) is not null&&(declaredTrack!=1||(itemTrack is not null&&itemTrack!=1)))
            throw new ArgumentException($"unsupported_transition:{kind}");
    }

    public static string Xfade(string? kind)=>RequireKind(kind) switch
    {
        "cross-dissolve"=>"fade",
        "dip-to-black"=>"fadeblack",
        "dip-to-white"=>"fadewhite",
        "wipe-left"=>"wipeleft",
        "wipe-right"=>"wiperight",
        "wipe-up"=>"wipeup",
        "wipe-down"=>"wipedown",
        _=>throw new ArgumentException($"unsupported_transition:{kind}")
    };
}
