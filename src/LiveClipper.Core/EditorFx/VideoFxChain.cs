using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace LiveClipper.Core.EditorFx;

public sealed record CatalogFxInstance(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("params")] Dictionary<string, double> Params);

public static partial class VideoFxChain
{
    public const string InvalidChain = "catalog-fx-invalid-chain";
    public const string UnsupportedId = "catalog-fx-unsupported-id";
    public const string UnsupportedParam = "catalog-fx-unsupported-param";
    public const string InvalidParam = "catalog-fx-invalid-param";
    public const double MaxRadius = 1024;
    public const double MaxSharpenAmount = 500;
    public const double MaxTransformScale = 1000;
    public const double MaxTransformCoordinate = 1_000_000;

    public static void Validate(IReadOnlyList<CatalogFxInstance> effects)
    {
        if (effects is null) throw new ArgumentException(InvalidChain);
        foreach (var effect in effects)
        {
            if (effect is null || effect.Params is null) throw new ArgumentException(InvalidChain);
            if (IsV14(effect.Id)) { ValidateV14(effect); continue; }
            if (IsV13(effect.Id)) { ValidateV13(effect); continue; }
            if (IsV12(effect.Id)) { ValidateV12(effect); continue; }
            if (IsV11(effect.Id)) { ValidateV11(effect); continue; }
            if (IsV10(effect.Id)) { ValidateV10(effect); continue; }
            if (IsV9(effect.Id)) { ValidateV9(effect); continue; }
            if (IsV8(effect.Id)) { ValidateV8(effect); continue; }
            // AUDIT0: unsharp_mask (linear-light preview) and brightness_contrast (per-RGB preview)
            // have no maxRGB<=1/alphaMax=0 equivalence proof for encoded-YUV unsharp/eq.
            // Keep both entire IDs outside the export whitelist, including disabled/no-op instances.
            if (effect.Id is not ("transform" or "offset" or "rotate_3d" or "horizontal_flip" or "vertical_flip" or "gaussian_blur"
                or "sharpen" or "tint"
                or "proc_amp" or "black_white" or "invert" or "gamma_correction" or "vignette" or "channel_mix" or "color_pass"
                or "directional_blur" or "channel_blur" or "reduce_interlace_flicker" or "extract" or "asc_cdl"
                or "video_limiter" or "color_replace" or "rounded_crop" or "levels" or "posterize" or "color_balance" or "cineon_converter"
                or "mosaic" or "find_edges" or "emboss" or "color_emboss" or "leave_color" or "change_to_color" or "three_way_color" or "median"
                or "lighting_effects" or "lumetri"))
                throw new ArgumentException(UnsupportedId);
            foreach (var (key, value) in effect.Params)
            {
                var bounds = (effect.Id, key) switch
                {
                    ("transform", "scale_x" or "scale_y") => (1d, MaxTransformScale),
                    ("transform", "rotation") or ("rotate_3d", "rot_z") => (-360d, 360d),
                    ("transform", "anchor_x" or "anchor_y" or "position_x" or "position_y")
                        or ("offset", "shift_x" or "shift_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                    ("offset", "blend") => (0d, 100d),
                    ("gaussian_blur", "radius") => (0d, MaxRadius),
                    ("sharpen", "amount") => (0d, MaxSharpenAmount),
                    ("tint", "amount") => (0d, 100d),
                    ("tint", "black_r" or "black_g" or "black_b" or "white_r" or "white_g" or "white_b") => (0d, 1d),
                    ("proc_amp", "brightness") => (-100d, 100d),
                    ("proc_amp", "contrast" or "saturation") => (0d, 200d),
                    ("proc_amp", "hue") => (-360d, 360d),
                    // Alpha inversion cannot survive the current alpha-less export/composite path.
                    ("invert", "channel") => (0d, 3d),
                    ("invert", "blend") => (0d, 100d),
                    ("gamma_correction", "gamma") => (0d, 100d),
                    ("vignette", "amount" or "roundness") => (-100d, 100d),
                    ("vignette", "midpoint" or "feather") => (0d, 100d),
                    ("vignette" or "color_pass", "color_r" or "color_g" or "color_b") => (0d, 1d),
                    ("channel_mix", "rr" or "rg" or "rb" or "rc" or "gr" or "gg" or "gb" or "gc" or "br" or "bg" or "bb" or "bc") => (-200d, 200d),
                    ("channel_mix", "monochrome") => (0d, 1d),
                    ("color_pass", "similarity") => (0d, 100d),
                    ("color_pass", "reverse") => (0d, 1d),
                    ("directional_blur", "length") => (0d, 8d),
                    ("directional_blur", "direction") => (-360d, 360d),
                    ("channel_blur", "red" or "green" or "blue" or "alpha") or ("reduce_interlace_flicker", "softness") => (0d, 8d),
                    ("channel_blur", "dimensions") => (0d, 2d),
                    ("channel_blur", "repeat_edge") or ("extract", "invert") or ("video_limiter", "gamut_warning")
                        or ("color_replace", "solid") or ("color_balance", "preserve") => (0d, 1d),
                    ("extract", "black" or "white") or ("levels", "in_black" or "in_white" or "out_black" or "out_white") => (0d, 255d),
                    ("extract", "softness") => (0d, 100d),
                    ("asc_cdl", "r_slope" or "g_slope" or "b_slope" or "r_power" or "g_power" or "b_power" or "saturation") => (0d, 4d),
                    ("asc_cdl", "r_offset" or "g_offset" or "b_offset") => (-1d, 1d),
                    ("video_limiter", "clip_level") => (90d, 109d),
                    ("video_limiter", "compression" or "axis") => (0d, 3d),
                    ("video_limiter", "warning_r" or "warning_g" or "warning_b")
                        or ("color_replace", "target_r" or "target_g" or "target_b" or "replace_r" or "replace_g" or "replace_b")
                        or ("rounded_crop", "border_r" or "border_g" or "border_b") => (0d, 1d),
                    ("color_replace", "similarity") or ("rounded_crop", "left" or "right" or "top" or "bottom") => (0d, 100d),
                    ("rounded_crop", "radius" or "feather" or "border") => (0d, 1024d),
                    ("levels", "gamma") => (1d, 500d),
                    ("posterize", "levels") => (2d, 256d),
                    ("color_balance", "shadow_r" or "shadow_g" or "shadow_b" or "mid_r" or "mid_g" or "mid_b" or "hi_r" or "hi_g" or "hi_b") => (-100d, 100d),
                    ("cineon_converter", "conversion") => (0d, 2d),
                    ("cineon_converter", "black10" or "white10") => (0d, 1023d),
                    ("cineon_converter", "black_internal" or "white_internal") => (0d, 255d),
                    ("cineon_converter", "gamma") => (.01d, 10d),
                    ("cineon_converter", "rolloff") => (0d, 100d),
                    ("mosaic", "horizontal" or "vertical") => (1d, 4096d),
                    ("mosaic", "sharp") or ("find_edges", "invert") => (0d, 1d),
                    ("find_edges" or "emboss", "blend") or ("leave_color", "amount" or "tolerance" or "softness")
                        or ("change_to_color", "hue_tol" or "softness") => (0d, 100d),
                    ("emboss" or "color_emboss", "direction") => (-360d, 360d),
                    ("emboss" or "color_emboss", "relief") => (0d, 100d),
                    ("emboss" or "color_emboss", "contrast") => (0d, 500d),
                    ("leave_color", "color_r" or "color_g" or "color_b")
                        or ("change_to_color", "from_r" or "from_g" or "from_b" or "to_r" or "to_g" or "to_b") => (0d, 1d),
                    ("three_way_color", "shadow_hue" or "mid_hue" or "hi_hue") => (-360d, 360d),
                    ("three_way_color", "shadow_balance" or "mid_balance" or "hi_balance" or "shadow_threshold" or "hi_threshold" or "shadow_softness" or "hi_softness") => (0d, 100d),
                    ("three_way_color", "shadow_level" or "mid_level" or "hi_level") => (-100d, 100d),
                    ("three_way_color", "saturation") => (0d, 200d),
                    ("median", "radius") => (0d, 4096d),
                    ("lighting_effects", "position_x" or "position_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                    ("lighting_effects", "radius") => (0d, MaxTransformCoordinate),
                    ("lighting_effects", "intensity" or "ambient") => (0d, 100d),
                    ("lighting_effects", "color_r" or "color_g" or "color_b") => (0d, 1d),
                    ("lumetri", "basic_on") => (0d, 1d),
                    ("lumetri", "temperature" or "tint" or "contrast" or "highlights" or "shadows" or "whites" or "blacks") => (-100d, 100d),
                    ("lumetri", "exposure") => (-5d, 5d),
                    ("lumetri", "saturation") => (0d, 200d),
                    _ => throw new ArgumentException(UnsupportedParam),
                };
                if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                    || ((effect.Id, key) is ("invert", "channel") or ("channel_mix", "monochrome") or ("color_pass", "reverse")
                        or ("channel_blur", "dimensions" or "repeat_edge") or ("extract", "invert") or ("video_limiter", "compression" or "axis" or "gamut_warning")
                        or ("color_replace", "solid") or ("color_balance", "preserve") or ("cineon_converter", "conversion")
                        or ("mosaic", "sharp") or ("find_edges", "invert") or ("lumetri", "basic_on")) && value != Math.Truncate(value))
                    throw new ArgumentException(InvalidParam);
            }
            if (effect.Id == "video_limiter" && effect.Params.TryGetValue("clip_level", out var level) && level is not (90 or 95 or 100 or 103 or 109))
                throw new ArgumentException(InvalidParam);
            foreach (var pair in effect.Id == "transform" ? new[] { "anchor", "position" }
                : effect.Id == "offset" ? new[] { "shift" }
                : effect.Id == "lighting_effects" ? new[] { "position" } : Array.Empty<string>())
                if (effect.Params.ContainsKey(pair + "_x") != effect.Params.ContainsKey(pair + "_y"))
                    throw new ArgumentException(InvalidParam);
        }
    }

    public static string Build(IReadOnlyList<CatalogFxInstance> effects)
    {
        Validate(effects); // Disabled entries must still obey the persisted contract.
        var filters = new List<string>();
        foreach (var effect in effects)
        {
            if (!effect.Enabled) continue;
            var p = effect.Params;
            if (effect.Id is "transform" or "offset" or "rotate_3d") filters.Add(BuildGeometry(effect));
            else if (effect.Id == "horizontal_flip") filters.Add("hflip");
            else if (effect.Id == "vertical_flip") filters.Add("vflip");
            else if (effect.Id == "gaussian_blur")
            {
                var blur = BuildExactGaussian(p);
                if (blur.Length > 0) filters.Add(blur);
            }
            else if (effect.Id == "sharpen") filters.Add(BuildExactSharpen(p));
            else if (effect.Id == "tint")
            {
                var tint = BuildTint(p);
                if (tint.Length > 0) filters.Add(tint);
            }
            else
            {
                var colorFilter = BuildV14(effect.Id, p) ?? BuildV13(effect.Id, p) ?? BuildV12(effect.Id, p) ?? BuildV11(effect.Id, p) ?? BuildV10(effect.Id, p) ?? BuildV9(effect.Id, p) ?? BuildV8(effect.Id, p) ?? BuildV7(effect.Id, p) ?? BuildV6(effect.Id, p) ?? BuildV5(effect.Id, p) ?? BuildColor(effect.Id, p);
                if (colorFilter.Length > 0) filters.Add(colorFilter);
            }
        }
        return string.Join(',', filters);
    }

    private static readonly string[] Rgb = { "r", "g", "b" };
    private static string ByteExpr(string expression) => "floor(clip(" + expression + ",0,255)+0.5)";
    private static string RgbExpr(IReadOnlyList<string> expressions, string alpha = "alpha(X,Y)")
        => "format=gbrap,geq=" + string.Join(':', Rgb.Select((channel, i) => channel + "='" + expressions[i] + "'")) + ":a='" + alpha + "':interpolation=nearest";
    private static string RgbLut(string expression)
        => "format=gbrap,lutrgb=" + string.Join(':', Rgb.Select(channel => channel + "='" + ByteExpr(expression) + "'"));

    // Match the preview's sRGB decode, linear Rec.709 sum and 4096-bin encode LUT.
    // Its Float32 LUT vs these double expressions can differ by one output code value.
    private static string GrayExpr()
    {
        string Decode(string channel)
        {
            var v = channel + "(X,Y)/255";
            return "if(lte(" + v + ",0.04045),(" + v + ")/12.92,pow(((" + v + ")+0.055)/1.055,2.4))";
        }
        var linear = "0.2126*(" + Decode("r") + ")+0.7152*(" + Decode("g") + ")+0.0722*(" + Decode("b") + ")";
        return "st(0,floor(clip(" + linear + ",0,1)*4096)/4096);"
            + ByteExpr("255*if(lte(ld(0),0.0031308),ld(0)*12.92,1.055*pow(ld(0),1/2.4)-0.055)");
    }

    private static string BuildTint(Dictionary<string, double> p)
    {
        var amount = p.GetValueOrDefault("amount", 100) / 100;
        if (amount == 0) return "";
        // Keep the LUT's encoded luminance unrounded until endpoint and amount interpolation.
        var linear = "0.2126*" + Linear("r(X,Y)") + "+0.7152*" + Linear("g(X,Y)") + "+0.0722*" + Linear("b(X,Y)");
        var luminance = "st(0,floor(clip(" + linear + ",0,1)*4096)/4096);st(1,if(lte(ld(0),0.0031308),ld(0)*12.92,1.055*pow(ld(0),1/2.4)-0.055));";
        return RgbExpr(Rgb.Select(c =>
        {
            var black = p.GetValueOrDefault("black_" + c, 0);
            var white = p.GetValueOrDefault("white_" + c, 1);
            return luminance + GeometryByte(c + "(X,Y)+(255*(" + N(black) + "+(" + N(white - black) + ")*ld(1))-" + c + "(X,Y))*" + N(amount));
        }).ToArray());
    }

    private static string BuildExactSharpen(Dictionary<string, double> p)
    {
        var amount = N(p.GetValueOrDefault("amount", 100) / 100);
        // sigma=1 yields [0,0,1] on each axis: a clamped linear 3x3 mean.
        // Amount zero still passes through the preview's 4096-bin encode LUT.
        return RgbExpr(Rgb.Select(c =>
        {
            var taps = new List<string>();
            for (var y = -1; y <= 1; y++)
                for (var x = -1; x <= 1; x++)
                    taps.Add(Linear(c + "(clip(X+(" + N(x) + "),0,W-1),clip(Y+(" + N(y) + "),0,H-1))"));
            var original = Linear(c + "(X,Y)");
            return Encoded(original + "+(" + original + "-(" + string.Join('+', taps) + ")/9)*" + amount);
        }).ToArray());
    }

    private static string ExactIntegral(string channel, string x, string y)
        => "if(gte(" + x + ",0)*gte(" + y + ",0)," + channel + "sum(clip(" + x + ",0,W-1),clip(" + y + ",0,H-1)),0)";

    private static string BuildExactGaussian(Dictionary<string, double> p)
    {
        var radius = p.GetValueOrDefault("radius", 0);
        if (radius <= .3) return "";
        var filters = new List<string> { FloatExpr(new[] { Linear("floor(r(X,Y)*255+0.5)"), Linear("floor(g(X,Y)*255+0.5)"), Linear("floor(b(X,Y)*255+0.5)"), "alpha(X,Y)" }, true) };
        // The final preview caps sigma separately by W/H, then runs three horizontal
        // boxes followed by three vertical boxes. Float planes retain each pass's store.
        for (var axis = 0; axis < 2; axis++)
            for (var pass = 0; pass < 3; pass++)
            {
                var coordinate = axis == 0 ? "X" : "Y";
                var dimension = axis == 0 ? "W" : "H";
                var init = "st(0,min(" + N(radius) + ",max(" + dimension + ",8)*2));st(1,floor(sqrt(4*ld(0)*ld(0)+1)));st(1,ld(1)-not(mod(ld(1),2)));"
                    + "st(2,floor((12*ld(0)*ld(0)-3*ld(1)*ld(1)-12*ld(1)-9)/(-4*ld(1)-4)+0.5));st(3,max(0,(ld(1)+if(lt(" + N(pass) + ",ld(2)),0,2)-1)/2));";
                var lo = coordinate + "-ld(3)-1";
                var hi = coordinate + "+ld(3)";
                var values = Rgb.Select(c =>
                {
                    var sum = axis == 0
                        ? ExactIntegral(c, hi, "Y") + "-" + ExactIntegral(c, lo, "Y") + "-" + ExactIntegral(c, hi, "Y-1") + "+" + ExactIntegral(c, lo, "Y-1")
                        : ExactIntegral(c, "X", hi) + "-" + ExactIntegral(c, "X", lo) + "-" + ExactIntegral(c, "X-1", hi) + "+" + ExactIntegral(c, "X-1", lo);
                    return init + "(" + sum + ")/(2*ld(3)+1)";
                }).ToArray();
                // Integral differences implement zero extension without a radius-sized expression.
                filters.Add(FloatExpr(new[] { values[0], values[1], values[2], "alpha(X,Y)" }));
            }
        filters.Add(FloatExpr(new[] { "(" + Encoded("r(X,Y)") + ")/255", "(" + Encoded("g(X,Y)") + ")/255", "(" + Encoded("b(X,Y)") + ")/255", "alpha(X,Y)" }));
        filters.Add("zscale=rangein=full:range=full:dither=none,format=gbrap");
        return string.Join(',', filters);
    }

    private static string BuildColor(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        if (id == "black_white") return RgbExpr(Rgb.Select(_ => GrayExpr()).ToArray());
        if (id == "gamma_correction")
        {
            var gamma = V("gamma", 100) / 10;
            return gamma == 1 ? "" : RgbLut(gamma == 0 ? "255" : "pow(val/255," + N(gamma) + ")*255");
        }
        if (id == "invert")
        {
            var channel = (int)V("channel", 0);
            var blend = V("blend", 0) / 100;
            if (blend == 1) return "";
            if (blend == 0) return "format=gbrap,negate=components=" + (channel == 0 ? "r+g+b" : Rgb[channel - 1]);
            var expression = "(255-val)*" + N(1 - blend) + "+val*" + N(blend);
            return "format=gbrap,lutrgb=" + string.Join(':', Rgb.Select((c, i) => c + "='" + (channel == 0 || channel == i + 1 ? ByteExpr(expression) : "val") + "'"));
        }
        if (id == "channel_mix")
        {
            var rows = Rgb.Select(c => V("monochrome", 0) == 1 ? "r" : c).ToArray();
            var coefficients = new List<string>();
            var expressions = new List<string>();
            var hasOffset = false;
            for (var row = 0; row < 3; row++)
            {
                var terms = new List<string>();
                for (var col = 0; col < 3; col++)
                {
                    var coefficient = V(rows[row] + Rgb[col], rows[row] == Rgb[col] ? 100 : 0) / 100;
                    coefficients.Add(Rgb[row] + Rgb[col] + "=" + N(coefficient));
                    terms.Add(N(coefficient) + "*" + Rgb[col] + "(X,Y)");
                }
                var offset = V(rows[row] + "c", 0) / 100 * 255;
                hasOffset |= offset != 0;
                expressions.Add(ByteExpr(string.Join('+', terms) + "+" + N(offset)));
            }
            // A constant must be applied before clipping, not after an intermediate mixer.
            return hasOffset ? RgbExpr(expressions) : "format=gbrap,colorchannelmixer=" + string.Join(':', coefficients);
        }
        if (id == "color_pass")
        {
            var distance = string.Join('+', Rgb.Select((c, i) => "pow(" + c + "(X,Y)/255-" + N(V("color_" + c, i == 2 ? 1 : 0)) + ",2)"));
            var keep = "lte(sqrt(" + distance + ")," + N(V("similarity", 30) / 100 * 1.2) + ")";
            if (V("reverse", 0) == 1) keep = "not(" + keep + ")";
            return RgbExpr(Rgb.Select(c => "if(" + keep + "," + c + "(X,Y)," + GrayExpr() + ")").ToArray());
        }
        if (id == "vignette")
        {
            var amount = V("amount", -50) / 100;
            if (Math.Abs(amount) < 1e-5) return "";
            var mid = V("midpoint", 50) / 100;
            var round = V("roundness", 0) / 100;
            var feather = Math.Max(V("feather", 50) / 100, .01);
            var power = round < 0 ? 2 - round * 6 : 2;
            var nx = "((X+0.5)/W*2-1)" + (round > 0 ? "*(1+(W/H-1)*" + N(round) + ")" : "");
            var distance = "pow(pow(abs(" + nx + ")," + N(power) + ")+pow(abs((Y+0.5)/H*2-1)," + N(power) + "),1/" + N(power) + ")/pow(2,1/" + N(power) + ")";
            var edge = "st(0,clip((" + distance + "-(" + N(mid - feather * .5) + "))/" + N(feather) + ",0,1));st(0,ld(0)*ld(0)*(3-2*ld(0))*" + N(Math.Abs(amount)) + ");";
            return RgbExpr(Rgb.Select(c => edge + ByteExpr(c + "(X,Y)+(" + N(255 * (amount > 0 ? 1 : V("color_" + c, 0))) + "-" + c + "(X,Y))*ld(0)")).ToArray());
        }
        if (id == "proc_amp")
        {
            var brightness = V("brightness", 0) / 100 * .4;
            var contrast = V("contrast", 0) / 100;
            var hue = V("hue", 0) / 360;
            var saturation = V("saturation", 100) / 100;
            string Adjust(string value) => ByteExpr("((" + value + "-0.5)*" + N(contrast) + "+0.5+" + N(brightness) + ")*255");
            if (hue == 0 && saturation == 1 || contrast == 0)
                return brightness == 0 && contrast == 1 ? "" : RgbLut("((val/255-0.5)*" + N(contrast) + "+0.5+" + N(brightness) + ")*255");
            // hue operates on YUV; the preview rotates HSL in encoded RGB instead.
            var hsl = "st(0,r(X,Y)/255);st(1,g(X,Y)/255);st(2,b(X,Y)/255);"
                + "st(3,max(ld(0),max(ld(1),ld(2))));st(4,min(ld(0),min(ld(1),ld(2))));st(5,(ld(3)+ld(4))/2);st(6,ld(3)-ld(4));"
                + "st(7,if(eq(ld(6),0),0,clip(ld(6)/if(gt(ld(5),0.5),2-ld(3)-ld(4),ld(3)+ld(4))*" + N(saturation) + ",0,1)));"
                + "st(8,if(eq(ld(6),0),0,if(eq(ld(3),ld(0)),(ld(1)-ld(2))/ld(6)+if(lt(ld(1),ld(2)),6,0),if(eq(ld(3),ld(1)),(ld(2)-ld(0))/ld(6)+2,(ld(0)-ld(1))/ld(6)+4))/6));"
                + "st(8,mod(ld(8)+" + N(hue + 2) + ",1));st(9,if(lt(ld(5),0.5),ld(5)*(1+ld(7)),ld(5)+ld(7)-ld(5)*ld(7)));st(4,2*ld(5)-ld(9));";
            var offsets = new[] { 1d / 3, 0, -1d / 3 };
            return RgbExpr(offsets.Select(offset => hsl + "st(3,mod(ld(8)+" + N(offset + 1) + ",1));"
                + Adjust("if(eq(ld(7),0),ld(5),if(lt(ld(3),1/6),ld(4)+(ld(9)-ld(4))*6*ld(3),if(lt(ld(3),1/2),ld(9),if(lt(ld(3),2/3),ld(4)+(ld(9)-ld(4))*(2/3-ld(3))*6,ld(4)))))")).ToArray());
        }
        throw new ArgumentException(UnsupportedId);
    }

    private static string? BuildV7(string id, Dictionary<string, double> p)
    {
        if (id == "lumetri") return BuildLumetriBasic(p);
        if (id != "lighting_effects") return null;
        var x = p.ContainsKey("position_x") ? N(p["position_x"]) : "W*0.35";
        var y = p.ContainsKey("position_y") ? N(p["position_y"]) : "H*0.25";
        var radius = p.ContainsKey("radius") ? N(Math.Max(1, p["radius"])) : "max(1,max(W,H)*0.75)";
        var ambient = N(p.GetValueOrDefault("ambient", 55) / 100);
        var intensity = N(p.GetValueOrDefault("intensity", 70) / 100);
        var falloff = "max(0,1-hypot(X+0.5-(" + x + "),Y+0.5-(" + y + "))/(" + radius + "))";
        var defaults = new[] { 1d, .94d, .82d };
        // The final preview multiplies encoded RGB at pixel centres and leaves alpha untouched.
        return RgbExpr(Rgb.Select((c, i) => "st(0," + falloff + ");" + GeometryByte(c + "(X,Y)*(" + ambient + "+" + intensity + "*ld(0)*ld(0)*" + N(p.GetValueOrDefault("color_" + c, defaults[i])) + ")")).ToArray());
    }

    private static string BuildLumetriBasic(Dictionary<string, double> p)
    {
        var enabled = p.GetValueOrDefault("basic_on", 1) != 0;
        double Basic(string key, double fallback = 0) => enabled ? p.GetValueOrDefault(key, fallback) : fallback;
        var temp = Basic("temperature") / 100;
        var tint = Basic("tint") / 100;
        var gains = new[] { 1 + .35 * temp, 1 - .3 * tint, 1 - .35 * temp };
        var exposure = Math.Pow(2, Basic("exposure"));
        var black = -Basic("blacks") / 100 * .15;
        var white = 1 - Basic("whites") / 100 * .15;
        var span = Math.Max(white - black, .001);
        var shadows = Basic("shadows") / 100;
        var highlights = Basic("highlights") / 100;
        var contrast = Basic("contrast") / 100;
        var saturation = enabled ? p.GetValueOrDefault("saturation", 100) / 100 : 1;
        // The decode table is Float32 in the preview. Exact rounding matters at LUT-bin boundaries.
        var prefix = string.Join(";", Rgb.Select((c, i) => "st(" + i + ",(" + LumetriEncoded("(" + PreviewFloat32(Linear(c + "(X,Y)")) + ")*" + N(gains[i]) + "*" + N(exposure)) + "-" + N(black) + ")/" + N(span) + ")")) + ";";
        prefix += "st(3,0.2126*ld(0)+0.7152*ld(1)+0.0722*ld(2));"
            + "st(4,max(0,ld(3)+" + N(shadows) + "*0.35*pow(clip(1-ld(3),0,1),3)+" + N(highlights) + "*0.35*pow(clip(ld(3),0,1),3)));"
            + "st(5,if(gt(ld(3),0.00001),min(ld(4)/ld(3),1+max(ld(4)-ld(3),0)/0.02),1));"
            + "st(6,ld(4)-max(ld(3),0)*ld(5));"
            + "st(0,ld(0)*ld(5)+ld(6));st(1,ld(1)*ld(5)+ld(6));st(2,ld(2)*ld(5)+ld(6));";
        if (Math.Abs(contrast) > .0001)
        {
            var k = 1 + contrast;
            prefix += string.Join(";", Rgb.Select((_, i) => "st(7,clip(ld(" + i + "),0,1));st(" + i + "," + (k >= 1
                ? "ld(7)+(ld(7)*ld(7)*(3-2*ld(7))-ld(7))*" + N(k - 1)
                : "0.5+(ld(7)-0.5)*" + N(k)) + ")")) + ";";
        }
        prefix += "st(3,0.2126*ld(0)+0.7152*ld(1)+0.0722*ld(2));";
        return RgbExpr(Rgb.Select((_, i) => prefix + GeometryByte("(ld(3)+(ld(" + i + ")-ld(3))*" + N(saturation) + ")*255")).ToArray());
    }

    // geq evaluates doubles; explicitly round positive normal values to IEEE-754 binary32.
    private static string PreviewFloat32(string value)
        => "(st(9," + value + ");if(lte(ld(9),0),0,st(8,pow(2,floor(log(ld(9))/log(2))-23));st(7,ld(9)/ld(8));ld(8)*if(eq(ld(7)-floor(ld(7)),0.5),2*floor(ld(7)/2+0.5),floor(ld(7)+0.5))))";

    private static string LumetriEncoded(string value)
    {
        var index = "(st(7," + value + ");floor(clip(ld(7),0,1)*4096)/4096)";
        return "(st(6," + index + ");" + PreviewFloat32("if(lte(ld(6),0.0031308),ld(6)*12.92,1.055*pow(ld(6),1/2.4)-0.055)") + ")";
    }

    private static string? BuildV6(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        if (id == "mosaic")
        {
            var bw = "max(W/" + N(V("horizontal", 10)) + ",1)";
            var bh = "max(H/" + N(V("vertical", 10)) + ",1)";
            var prefix = "st(0,clip((floor(X/(" + bw + "))+0.5)*(" + bw + ")-0.5,0,W-1));st(1,clip((floor(Y/(" + bh + "))+0.5)*(" + bh + ")-0.5,0,H-1));st(2,floor(ld(0)));st(3,floor(ld(1)));st(4,ld(0)-ld(2));st(5,ld(1)-ld(3));";
            string Sample(string c) => prefix + "st(6," + c + "(ld(2),ld(3))+(" + c + "(min(ld(2)+1,W-1),ld(3))-" + c + "(ld(2),ld(3)))*ld(4));st(7," + c + "(ld(2),min(ld(3)+1,H-1))+(" + c + "(min(ld(2)+1,W-1),min(ld(3)+1,H-1))-" + c + "(ld(2),min(ld(3)+1,H-1)))*ld(4));" + GeometryByte("ld(6)+(ld(7)-ld(6))*ld(5)");
            // The preview ignores sharp; it always samples encoded RGBA at block centres.
            return RgbExpr(Rgb.Select(Sample).ToArray(), Sample("alpha"));
        }
        if (id == "median")
        {
            var radius = Math.Floor(V("radius", 0) + .5);
            if (radius == 0) return "";
            var r = "min(max(W,H)," + N(radius) + ")";
            var values = new List<string>();
            foreach (var c in Rgb)
            {
                var expression = "";
                for (var y = -1; y <= 1; y++) for (var x = -1; x <= 1; x++)
                    expression += "st(" + ((y + 1) * 3 + x + 1) + "," + c + "(clip(X+(" + x + ")*(" + r + "),0,W-1),clip(Y+(" + y + ")*(" + r + "),0,H-1)));";
                var pairs = new[] { 1, 2, 4, 5, 7, 8, 0, 1, 3, 4, 6, 7, 1, 2, 4, 5, 7, 8, 0, 3, 5, 8, 4, 7, 3, 6, 1, 4, 2, 5, 4, 7, 4, 2, 6, 4, 4, 2 };
                for (var i = 0; i < pairs.Length; i += 2)
                {
                    var a = pairs[i]; var b = pairs[i + 1];
                    expression += "st(9,ld(" + a + "));st(" + a + ",min(ld(9),ld(" + b + ")));st(" + b + ",max(ld(9),ld(" + b + ")));";
                }
                values.Add(expression + "ld(4)");
            }
            return RgbExpr(values);
        }
        if (id == "find_edges" || id == "emboss" || id == "color_emboss")
        {
            var decode = FloatExpr(Rgb.Select(c => Linear("floor(" + c + "(X,Y)*255+0.5)")).Append("alpha(X,Y)").ToArray(), true);
            string L(int x, int y) => "(0.2126*r(clip(X+(" + x + "),0,W-1),clip(Y+(" + y + "),0,H-1))+0.7152*g(clip(X+(" + x + "),0,W-1),clip(Y+(" + y + "),0,H-1))+0.0722*b(clip(X+(" + x + "),0,W-1),clip(Y+(" + y + "),0,H-1)))";
            var gx = L(1, -1) + "+2*" + L(1, 0) + "+" + L(1, 1) + "-" + L(-1, -1) + "-2*" + L(-1, 0) + "-" + L(-1, 1);
            var gy = L(-1, 1) + "+2*" + L(0, 1) + "+" + L(1, 1) + "-" + L(-1, -1) + "-2*" + L(0, -1) + "-" + L(1, -1);
            var prefix = "st(0," + gx + ");st(1," + gy + ");";
            var blend = V("blend", 0) / 100;
            var output = new List<string>();
            if (id == "find_edges")
            {
                prefix += "st(2,min(sqrt(ld(0)*ld(0)+ld(1)*ld(1)),1));";
                var m = V("invert", 0) == 1 ? "ld(2)" : "1-ld(2)";
                foreach (var c in Rgb)
                {
                    var original = "floor(255*if(lte(" + c + "(X,Y),0.0031308),12.92*" + c + "(X,Y),1.055*pow(" + c + "(X,Y),1/2.4)-0.055)+0.5)";
                    output.Add(prefix + ByteExpr("255*(" + m + ")*" + N(1 - blend) + "+" + original + "*" + N(blend)) + "/255");
                }
            }
            else
            {
                var angle = V("direction", 45) * Math.PI / 180;
                var strength = V("relief", id == "color_emboss" ? 1 : 1.8) * V("contrast", 100) / 100;
                prefix += "st(2,(ld(0)*" + N(Math.Cos(angle)) + "+ld(1)*" + N(-Math.Sin(angle)) + ")*" + N(strength / 8) + ");";
                foreach (var c in Rgb) output.Add(prefix + "(" + Encoded(id == "color_emboss" ? c + "(X,Y)+ld(2)" : "(0.214041+ld(2))+(" + c + "(X,Y)-(0.214041+ld(2)))*" + N(blend)) + ")/255");
            }
            return decode + "," + FloatExpr(output.Append("alpha(X,Y)").ToArray()) + ",zscale=rangein=full:range=full:dither=none,format=gbrap";
        }
        if (id == "leave_color" || id == "change_to_color")
        {
            var from = Rgb.Select(c => V((id == "leave_color" ? "color_" : "from_") + c, c == "b" ? 1 : 0)).ToArray();
            var fh = V6Hue(from[0], from[1], from[2]);
            var prefix = V6Hsl();
            var tolerance = V(id == "leave_color" ? "tolerance" : "hue_tol", 30) / 100;
            var softness = V("softness", 30) / 100 * (id == "leave_color" ? 1 : .3) + .0001;
            prefix += "st(4,abs(ld(0)-" + N(fh) + "));st(4,clip((min(ld(4),1-ld(4))*" + (id == "leave_color" ? "2" : "1") + "-" + N(tolerance) + ")/" + N(softness) + ",0,1));";
            if (id == "leave_color")
            {
                return RgbExpr(Rgb.Select(c => prefix + Encoded("(" + Linear(c + "(X,Y)") + ")+(0.2126*(" + Linear("r(X,Y)") + ")+0.7152*(" + Linear("g(X,Y)") + ")+0.0722*(" + Linear("b(X,Y)") + ")-(" + Linear(c + "(X,Y)") + "))*" + N(V("amount", 100) / 100) + "*ld(4)")).ToArray());
            }
            var to = Rgb.Select(c => V("to_" + c, c == "g" ? 1 : 0)).ToArray();
            var shift = V6Hue(to[0], to[1], to[2]) - fh;
            prefix += "st(0,mod(mod(ld(0)+" + N(shift) + "*(1-ld(4)),1)+1,1));";
            return RgbExpr(new[] { 1d / 3, 0, -1d / 3 }.Select(offset => prefix + ByteExpr("255*(" + V6HslChannel(offset) + ")")).ToArray());
        }
        if (id == "three_way_color")
        {
            var wheels = new List<double[]>();
            foreach (var range in new[] { "shadow", "mid", "hi" })
            {
                var hue = ((V(range + "_hue", 0) % 360) + 360) % 360 / 360;
                var color = new[] { V6Wheel(hue + 1d / 3), V6Wheel(hue), V6Wheel(hue - 1d / 3) };
                var mean = (color[0] + color[1] + color[2]) / 3;
                wheels.Add(color.Select(c => (c - mean) * V(range + "_balance", 0) / 200).Append(V(range + "_level", 0) / 400).ToArray());
            }
            string Ramp(string range)
            {
                var threshold = V(range + "_threshold", 50) / 100;
                var soft = V(range + "_softness", 0) / 200;
                var lo = Math.Max(0, threshold - soft); var hi = Math.Min(1, threshold + soft);
                return "(st(9,clip((ld(3)-" + N(lo) + ")/" + N(Math.Max(hi - lo, .0001)) + ",0,1));ld(9)*ld(9)*(3-2*ld(9)))";
            }
            var prefix = "st(3,(0.2126*r(X,Y)+0.7152*g(X,Y)+0.0722*b(X,Y))/255);st(4,1-" + Ramp("shadow") + ");st(5," + Ramp("hi") + ");st(6,max(0,1-ld(4)-ld(5)));st(7,ld(4)+ld(6)+ld(5));st(4,ld(4)/ld(7));st(5,ld(5)/ld(7));st(6,ld(6)/ld(7));";
            for (var i = 0; i < 3; i++) prefix += "st(" + i + "," + Rgb[i] + "(X,Y)/255+" + N(wheels[0][i]) + "*ld(4)+" + N(wheels[1][i]) + "*ld(6)+" + N(wheels[2][i]) + "*ld(5)+(" + N(wheels[0][3]) + "*ld(4)+" + N(wheels[1][3]) + "*ld(6)+" + N(wheels[2][3]) + "*ld(5)));";
            prefix += "st(3,0.2126*ld(0)+0.7152*ld(1)+0.0722*ld(2));";
            return RgbExpr(Rgb.Select((_, i) => prefix + Encoded("ld(3)+(ld(" + i + ")-ld(3))*" + N(V("saturation", 100) / 100))).ToArray());
        }
        return null;
    }

    private static double V6Hue(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var d = max - min;
        return d == 0 ? 0 : max == r ? ((g - b) / d + (g < b ? 6 : 0)) / 6 : max == g ? ((b - r) / d + 2) / 6 : ((r - g) / d + 4) / 6;
    }

    private static double V6Wheel(double hue)
    {
        if (hue < 0) hue += 1;
        if (hue > 1) hue -= 1;
        return hue < 1d / 6 ? 6 * hue : hue < .5 ? 1 : hue < 2d / 3 ? (2d / 3 - hue) * 6 : 0;
    }

    private static string V6Hsl()
        => "st(5,r(X,Y)/255);st(6,g(X,Y)/255);st(7,b(X,Y)/255);st(8,max(ld(5),max(ld(6),ld(7))));st(9,min(ld(5),min(ld(6),ld(7))));st(2,(ld(8)+ld(9))/2);st(3,ld(8)-ld(9));st(1,if(eq(ld(3),0),0,ld(3)/if(gt(ld(2),0.5),2-ld(8)-ld(9),ld(8)+ld(9))));st(0,if(eq(ld(3),0),0,if(eq(ld(8),ld(5)),((ld(6)-ld(7))/ld(3)+if(lt(ld(6),ld(7)),6,0))/6,if(eq(ld(8),ld(6)),((ld(7)-ld(5))/ld(3)+2)/6,((ld(5)-ld(6))/ld(3)+4)/6))));";

    private static string V6HslChannel(double offset)
        => "st(5,if(lt(ld(2),0.5),ld(2)*(1+ld(1)),ld(2)+ld(1)-ld(2)*ld(1)));st(6,2*ld(2)-ld(5));st(7,ld(0)+" + N(offset) + ");st(7,if(lt(ld(7),0),ld(7)+1,if(gt(ld(7),1),ld(7)-1,ld(7))));if(eq(ld(1),0),ld(2),if(lt(ld(7),1/6),ld(6)+(ld(5)-ld(6))*6*ld(7),if(lt(ld(7),1/2),ld(5),if(lt(ld(7),2/3),ld(6)+(ld(5)-ld(6))*(2/3-ld(7))*6,ld(6)))))";

    private static string? BuildV5(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        if (id == "levels")
        {
            var ib = V("in_black", 0) / 255;
            var iw = Math.Max(V("in_white", 255) / 255, ib + .001);
            return RgbLut("(" + N(V("out_black", 0)) + "+pow(clip((val/255-" + N(ib) + ")/" + N(iw - ib) + ",0,1)," + N(100 / V("gamma", 100)) + ")*" + N(V("out_white", 255) - V("out_black", 0)) + ")");
        }
        if (id == "posterize")
        {
            var count = V("levels", 8) - 1;
            return RgbLut("floor(val/255*" + N(count) + "+0.5)/" + N(count) + "*255");
        }
        if (id == "cineon_converter")
        {
            var black = V("black10", 95);
            var white = Math.Max(black + 1, V("white10", 685));
            var lo = V("black_internal", 0) / 255;
            var span = Math.Max(V("white_internal", 255) / 255 - lo, 1e-8);
            var gamma = V("gamma", 1.7);
            var roll = V("rolloff", 20) / 100;
            var floor = Math.Pow(10, (black - white) * .002 / gamma);
            if (V("conversion", 0) == 1)
                return RgbLut("(" + N(white) + "+log(max(1e-12," + N(floor) + "+(val/255-" + N(lo) + ")/" + N(span) + "*" + N(1 - floor) + "))/log(10)*" + N(gamma / .002) + ")/1023*255");
            var lin = "st(0,(pow(10,(val/255*1023-" + N(white) + ")*" + N(.002 / gamma) + ")-" + N(floor) + ")/" + N(1 - floor) + ");";
            if (roll > 0) lin += "st(0,if(gt(ld(0)," + N(1 - roll) + ")," + N(1 - roll) + "+" + N(roll) + "*(1-exp(-(ld(0)-1+" + N(roll) + ")/" + N(roll) + ")),ld(0)));";
            return RgbLut(lin + (V("conversion", 0) == 2 ? "(" + N(black) + "+clip(ld(0),0,1)*" + N(white - black) + ")/1023*255" : "(" + N(lo) + "+ld(0)*" + N(span) + ")*255"));
        }
        if (id == "asc_cdl" || id == "color_balance")
        {
            var output = new List<string>();
            var prefix = "";
            if (id == "color_balance") prefix = "st(3,(0.2126*r(X,Y)+0.7152*g(X,Y)+0.0722*b(X,Y))/255);st(4,pow(1-ld(3),2));st(5,pow(ld(3),2));st(6,max(0,1-ld(4)-ld(5)));";
            for (var i = 0; i < 3; i++)
            {
                var c = Rgb[i];
                output.Add(id == "asc_cdl"
                    ? "pow(max(" + c + "(X,Y)/255*" + N(V(c + "_slope", 1)) + "+" + N(V(c + "_offset", 0)) + ",0)," + N(V(c + "_power", 1)) + ")"
                    : c + "(X,Y)/255+" + N(V("shadow_" + c, 0) / 400) + "*ld(4)+" + N(V("mid_" + c, 0) / 400) + "*ld(6)+" + N(V("hi_" + c, 0) / 400) + "*ld(5)");
            }
            for (var i = 0; i < 3; i++) prefix += "st(" + i + "," + output[i] + ");";
            prefix += "st(7,0.2126*ld(0)+0.7152*ld(1)+0.0722*ld(2));";
            return RgbExpr(Rgb.Select((_, i) => prefix + ByteExpr("255*(" + (id == "asc_cdl" ? "ld(7)+(ld(" + i + ")-ld(7))*" + N(V("saturation", 1)) : "ld(" + i + ")" + (V("preserve", 0) == 1 ? "+ld(3)-ld(7)" : "")) + ")")).ToArray());
        }
        if (id == "video_limiter")
        {
            var limit = V("clip_level", 100) / 100;
            var compression = V("compression", 0);
            var knee = limit * (1 - compression * .1);
            var axis = V("axis", 0);
            string Compress(string v) => compression == 0 ? "min(" + v + "," + N(limit) + ")" : "if(lte(" + v + "," + N(knee) + "),min(" + v + "," + N(limit) + ")," + N(knee) + "+" + N(limit - knee) + "*(1-exp(-(" + v + "-" + N(knee) + ")/" + N(Math.Max(limit - knee, 1e-6)) + ")))";
            var prefix = "st(0,r(X,Y)/255);st(1,g(X,Y)/255);st(2,b(X,Y)/255);st(3,0.2126*ld(0)+0.7152*ld(1)+0.0722*ld(2));st(4,max(ld(0),max(ld(1),ld(2))));st(5,ld(4)-min(ld(0),min(ld(1),ld(2))));";
            var bad = axis == 0 ? "gt(ld(3)," + N(limit) + ")" : axis == 1 ? "gt(ld(5)," + N(limit) + ")" : "max(gt(ld(4)," + N(limit) + "),gt(ld(3)," + N(limit) + "))";
            prefix += "st(6," + (axis == 1 ? "ld(3)" : Compress("ld(3)")) + ");st(7,if(gt(ld(3),1e-6),ld(6)/ld(3),1));st(8," + (axis == 0 ? "1" : "min(1," + Compress("ld(5)") + "/max(ld(5),1e-6))") + ");";
            return RgbExpr(Rgb.Select((c, i) => prefix + ByteExpr("255*" + (V("gamut_warning", 0) == 1 ? "if(" + bad + "," + N(V("warning_" + c, 1)) + "," : "(") + (axis == 3 ? Compress("ld(" + i + ")") : "ld(6)+(ld(" + i + ")-ld(3))*ld(7)*ld(8)") + ")")).ToArray());
        }
        if (id == "color_replace")
        {
            var replace = Rgb.Select(c => V("replace_" + c, c == "r" ? 1 : 0)).ToArray();
            var max = replace.Max(); var min = replace.Min(); var d = max - min;
            var light = (max + min) / 2;
            var saturation = d == 0 ? 0 : d / (light > .5 ? 2 - max - min : max + min);
            var hue = d == 0 ? 0 : max == replace[0] ? ((replace[1] - replace[2]) / d + (replace[1] < replace[2] ? 6 : 0)) / 6
                : max == replace[1] ? ((replace[2] - replace[0]) / d + 2) / 6 : ((replace[0] - replace[1]) / d + 4) / 6;
            var sim = V("similarity", 30) / 100 * 1.2;
            var distance = "sqrt(" + string.Join('+', Rgb.Select(c => "pow(" + c + "(X,Y)/255-" + N(V("target_" + c, c == "b" ? 1 : 0)) + ",2)")) + ")";
            var prefix = "st(0,clip((" + distance + "-" + N(sim * .85) + ")/" + N(Math.Max(Math.Max(sim, 1e-4) - sim * .85, 1e-6)) + ",0,1));st(0,1-ld(0)*ld(0)*(3-2*ld(0)));";
            prefix += "st(1,(max(r(X,Y),max(g(X,Y),b(X,Y)))+min(r(X,Y),min(g(X,Y),b(X,Y))))/510);st(2,if(lt(ld(1),0.5),ld(1)*" + N(1 + saturation) + ",ld(1)+" + N(saturation) + "-ld(1)*" + N(saturation) + "));st(3,2*ld(1)-ld(2));";
            var target = new List<string>();
            var offsets = new[] { 1d / 3, 0, -1d / 3 };
            for (var i = 0; i < 3; i++)
            {
                var t = ((hue + offsets[i]) % 1 + 1) % 1;
                target.Add(V("solid", 0) == 1 ? N(replace[i]) : saturation == 0 ? "ld(1)" : t < 1d / 6 ? "ld(3)+(ld(2)-ld(3))*" + N(6 * t) : t < .5 ? "ld(2)" : t < 2d / 3 ? "ld(3)+(ld(2)-ld(3))*" + N((2d / 3 - t) * 6) : "ld(3)");
            }
            return RgbExpr(Rgb.Select((c, i) => prefix + ByteExpr(c + "(X,Y)+(255*(" + target[i] + ")-" + c + "(X,Y))*ld(0)")).ToArray());
        }
        if (id == "rounded_crop")
        {
            if (V("left", 0) + V("right", 0) >= 100 || V("top", 0) + V("bottom", 0) >= 100) return RgbExpr(new[] { "0", "0", "0" }, "0");
            var hw = "W*" + N((1 - (V("left", 0) + V("right", 0)) / 100) / 2);
            var hh = "H*" + N((1 - (V("top", 0) + V("bottom", 0)) / 100) / 2);
            var prefix = "st(0,min(" + N(V("radius", 40)) + ",min(" + hw + "," + hh + ")));st(1,abs(X+0.5-W*" + N((1 + (V("left", 0) - V("right", 0)) / 100) / 2) + ")-(" + hw + ")+ld(0));st(2,abs(Y+0.5-H*" + N((1 + (V("top", 0) - V("bottom", 0)) / 100) / 2) + ")-(" + hh + ")+ld(0));st(3,hypot(max(ld(1),0),max(ld(2),0))+min(max(ld(1),ld(2)),0)-ld(0));";
            var feather = V("feather", 0); var border = V("border", 0);
            var alpha = prefix + "st(4,clip((ld(3)+" + N(feather) + ")/" + N(Math.Max(.5, feather) + feather) + ",0,1));" + ByteExpr("alpha(X,Y)*(1-ld(4)*ld(4)*(3-2*ld(4)))");
            return RgbExpr(Rgb.Select(c => border == 0 ? c + "(X,Y)" : prefix + "st(4,clip(ld(3)+" + N(border + .5) + ",0,1));" + ByteExpr(c + "(X,Y)+(" + N(V("border_" + c, 1) * 255) + "-" + c + "(X,Y))*ld(4)*ld(4)*(3-2*ld(4))")).ToArray(), alpha);
        }
        if (id == "directional_blur")
        {
            var length = V("length", 0) * 2;
            if (length < .5) return "";
            var steps = Math.Min(Math.Max((int)Math.Ceiling(length), 2), 64);
            var angle = V("direction", 0) * Math.PI / 180;
            var expressions = new List<string>();
            foreach (var c in Rgb)
            {
                var sum = new List<string>();
                for (var s = 0; s < steps; s++)
                {
                    var t = (double)s / (steps - 1) - .5;
                    var x = "clip(X+(" + N(Math.Sin(angle) * length * t) + "),0,W-1)";
                    var y = "clip(Y+(" + N(-Math.Cos(angle) * length * t) + "),0,H-1)";
                    var prefix = "st(0," + x + ");st(1," + y + ");st(2,floor(ld(0)));st(3,floor(ld(1)));st(4,ld(0)-ld(2));st(5,ld(1)-ld(3));";
                    var a = c + "(ld(2),ld(3))"; var b = c + "(min(ld(2)+1,W-1),ld(3))";
                    var d = c + "(ld(2),min(ld(3)+1,H-1))"; var e = c + "(min(ld(2)+1,W-1),min(ld(3)+1,H-1))";
                    sum.Add("(" + prefix + "st(6," + a + "+(" + b + "-" + a + ")*ld(4));st(7," + d + "+(" + e + "-" + d + ")*ld(4));ld(6)+(ld(7)-ld(6))*ld(5))");
                }
                expressions.Add(ByteExpr("(" + string.Join('+', sum) + ")/" + steps));
            }
            return RgbExpr(expressions);
        }
        if (id == "extract")
        {
            var decode = FloatExpr(Rgb.Select(c => Linear("floor(" + c + "(X,Y)*255+0.5)")).Append("alpha(X,Y)").ToArray(), true);
            var luma = "st(0,floor(clip(0.2126*r(X,Y)+0.7152*g(X,Y)+0.0722*b(X,Y),0,1)*4096)/4096);if(lte(ld(0),0.0031308),ld(0)*12.92,1.055*pow(ld(0),1/2.4)-0.055)";
            var encode = FloatExpr(Rgb.Select(_ => luma).Append("alpha(X,Y)").ToArray());
            var soft = V("softness", 0) <= 0 ? 0 : V("softness", 0) / 100 * .2 + .0001;
            var inside = soft == 0 ? "gt(r(X,Y)," + N(V("black", 0) / 255) + ")*lt(r(X,Y)," + N(V("white", 255) / 255) + ")" : "min(clip((r(X,Y)-" + N(V("black", 0) / 255) + ")/" + N(soft) + ",0,1),clip((" + N(V("white", 255) / 255) + "-r(X,Y))/" + N(soft) + ",0,1))";
            var mask = "st(0," + inside + ");" + ByteExpr("(" + (V("invert", 0) == 1 ? "1-ld(0)" : "ld(0)") + ")*255") + "/255";
            return decode + "," + encode + "," + FloatExpr(Rgb.Select(_ => mask).Append("alpha(X,Y)").ToArray()) + ",zscale=rangein=full:range=full:dither=none,format=gbrap";
        }
        if (id == "channel_blur" || id == "reduce_interlace_flicker") return BuildLinearBlur(id, p);
        return null;
    }

    private static string FloatExpr(IReadOnlyList<string> values, bool convert = false)
        => (convert ? "format=gbrap,zscale=rangein=full:range=full:dither=none,format=gbrapf32le," : "") + "geq=" + string.Join(':', new[] { "r", "g", "b", "a" }.Select((c, i) => c + "='" + values[i] + "'")) + ":interpolation=nearest";

    private static int[] BoxRadii(double sigma)
    {
        if (sigma <= .3) return new[] { 0, 0, 0 };
        var lower = (int)Math.Floor(Math.Sqrt(4 * sigma * sigma + 1));
        if (lower % 2 == 0) lower--;
        var count = Math.Floor((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4 * lower - 4) + .5);
        return Enumerable.Range(0, 3).Select(i => Math.Max(0, ((i < count ? lower : lower + 2) - 1) / 2)).ToArray();
    }

    private static string BuildLinearBlur(string id, Dictionary<string, double> p)
    {
        var flicker = id == "reduce_interlace_flicker";
        var channels = new[] { "r", "g", "b", "alpha" };
        var radii = new[] { "red", "green", "blue", "alpha" }.Select((key, i) => flicker ? (i < 3 ? p.GetValueOrDefault("softness", 0) : 0) : p.GetValueOrDefault(key, 0)).ToArray();
        if (radii.All(r => r <= .3)) return "";
        var dimension = flicker ? 2 : p.GetValueOrDefault("dimensions", 0);
        var repeat = flicker || p.GetValueOrDefault("repeat_edge", 0) == 1;
        var filters = new List<string> { FloatExpr(channels.Select((c, i) => radii[i] > .3 ? Linear("floor(" + c + "(X,Y)*255+0.5)") : c + "(X,Y)").ToArray(), true) };
        var kernels = radii.Select(BoxRadii).ToArray();
        for (var axis = 0; axis < 2; axis++)
        {
            if (axis == 0 && dimension == 2 || axis == 1 && dimension == 1) continue;
            for (var pass = 0; pass < 3; pass++)
            {
                if (kernels.All(kernel => kernel[pass] == 0)) continue;
                var expressions = new List<string>();
                for (var i = 0; i < 4; i++)
                {
                    var radius = kernels[i][pass]; var c = channels[i];
                    var terms = new List<string>();
                    for (var d = -radius; d <= radius; d++)
                    {
                        var pos = (axis == 0 ? "X" : "Y") + (d < 0 ? d.ToString(CultureInfo.InvariantCulture) : "+" + d);
                        var sample = c + "(" + (axis == 0 ? "clip(" + pos + ",0,W-1),Y" : "X,clip(" + pos + ",0,H-1)") + ")";
                        terms.Add(repeat ? sample : "if(between(" + pos + ",0," + (axis == 0 ? "W" : "H") + "-1)," + sample + ",0)");
                    }
                    expressions.Add("(" + string.Join('+', terms) + ")/" + (2 * radius + 1));
                }
                filters.Add(FloatExpr(expressions));
            }
        }
        filters.Add(FloatExpr(channels.Select((c, i) => radii[i] > .3 ? "(" + Encoded(c + "(X,Y)") + ")/255" : c + "(X,Y)").ToArray()));
        filters.Add("zscale=rangein=full:range=full:dither=none,format=gbrap");
        return string.Join(',', filters);
    }

    private static string BuildGeometry(CatalogFxInstance effect)
    {
        var p = effect.Params;
        string Point(string key, string fallback) => p.TryGetValue(key, out var value) ? N(value) : fallback;
        var wrap = effect.Id == "offset";
        string x, y;
        if (wrap)
        {
            x = "X-(" + Point("shift_x", "W/2") + "-W/2)";
            y = "Y-(" + Point("shift_y", "H/2") + "-H/2)";
        }
        else
        {
            var angle = p.GetValueOrDefault(effect.Id == "transform" ? "rotation" : "rot_z", 0) * Math.PI / 180;
            var c = N(Math.Cos(angle)); var s = N(Math.Sin(angle));
            var ax = Point("anchor_x", "W/2"); var ay = Point("anchor_y", "H/2");
            var px = Point("position_x", "W/2"); var py = Point("position_y", "H/2");
            x = "(" + c + "*(X+0.5-(" + px + "))+" + s + "*(Y+0.5-(" + py + ")))/" + N(p.GetValueOrDefault("scale_x", 100) / 100) + "+(" + ax + ")-0.5";
            y = "(-(" + s + ")*(X+0.5-(" + px + "))+" + c + "*(Y+0.5-(" + py + ")))/" + N(p.GetValueOrDefault("scale_y", 100) / 100) + "+(" + ay + ")-0.5";
        }
        // One inverse lookup keeps the original canvas, PTS and frame count. Intermediate
        // scale/rotate/crop stages would discard pixels before the final translation.
        var init = "st(0," + x + ");st(1," + y + ");st(2,floor(ld(0)));st(3,floor(ld(1)));st(4,ld(0)-ld(2));st(5,ld(1)-ld(3));";
        var taps = new[] { ("ld(2)", "ld(3)", "(1-ld(4))*(1-ld(5))"),
            ("ld(2)+1", "ld(3)", "ld(4)*(1-ld(5))"),
            ("ld(2)", "ld(3)+1", "(1-ld(4))*ld(5)"), ("ld(2)+1", "ld(3)+1", "ld(4)*ld(5)") };
        string Tap(string channel, string tx, string ty)
        {
            if (wrap) return channel + "(mod(mod(" + tx + ",W)+W,W),mod(mod(" + ty + ",H)+H,H))";
            return "if(between(" + tx + ",0,W-1)*between(" + ty + ",0,H-1)," + channel + "(" + tx + "," + ty + "),0)";
        }
        var blend = wrap ? p.GetValueOrDefault("blend", 0) / 100 : 0;
        var weight = N(1 - blend); var original = N(blend);
        string Sum(string channel) => string.Join('+', taps.Select(tap =>
        {
            var a = Tap("alpha", tap.Item1, tap.Item2);
            // Offset accumulates normalized-alpha taps in this order. Reassociating the
            // weights changes a .5 tie by one alpha byte, magnified by later unpremultiply.
            if (wrap)
            {
                var z = "(" + a + "/255)*(" + tap.Item3 + ")*" + weight;
                return channel == "alpha" ? z : Linear(Tap(channel, tap.Item1, tap.Item2)) + "*(" + z + ")";
            }
            // geo() copies its linear-premultiplied Float32 taps through Uint8ClampedArray.
            var value = channel == "alpha" ? a : GeometryByte(Linear(Tap(channel, tap.Item1, tap.Item2)) + "*(" + a + ")");
            return "(" + value + ")*(" + tap.Item3 + ")";
        }));
        var oldAlpha = "alpha(X,Y)/255*" + original;
        var alpha = wrap ? "(" + Sum("alpha") + ")+" + oldAlpha : Sum("alpha");
        var channels = new List<string>();
        foreach (var channel in new[] { "r", "g", "b" })
        {
            var color = wrap ? "(" + Sum(channel) + ")+" + Linear(channel + "(X,Y)") + "*(" + oldAlpha + ")" : Sum(channel);
            var linear = "if(gt(ld(6)," + (wrap ? "0" : "0.00000001") + "),(" + color + ")/ld(6),0)";
            channels.Add(channel + "='" + init + "st(6," + alpha + ");" + Encoded(linear) + "'");
        }
        channels.Add("a='" + init + GeometryByte(wrap ? "(" + alpha + ")*255" : alpha) + "'");
        return "format=gbrap,geq=" + string.Join(':', channels) + ":interpolation=nearest";
    }

    // Uint8ClampedArray uses ties-to-even, not FFmpeg's truncating integer-plane cast.
    private static string GeometryByte(string value)
        => "st(8,clip(" + value + ",0,255));if(eq(ld(8)-floor(ld(8)),0.5),2*floor(ld(8)/2+0.5),floor(ld(8)+0.5))";

    private static string Linear(string sample)
        => "(st(9," + sample + ");if(lte(ld(9),10.31475),ld(9)/3294.6,pow((ld(9)/255+0.055)/1.055,2.4)))";

    private static string Encoded(string linear)
    {
        // fromLinear() indexes the preview's 4096-entry sRGB LUT, then stores bytes.
        var q = "st(7,floor(clip(" + linear + ",0,1)*4096)/4096);";
        return q + "floor(255*if(lte(ld(7),0.0031308),12.92*ld(7),1.055*pow(ld(7),1/2.4)-0.055)+0.5)";
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
