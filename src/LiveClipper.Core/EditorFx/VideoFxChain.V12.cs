namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV12(string id) => id is "mirror" or "lens_distortion" or "twirl" or "wave_warp"
        or "spherize" or "warp_stabilizer" or "spacer" or "magnify";

    private static void ValidateV12(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("mirror" or "twirl" or "spherize" or "magnify", "center_x" or "center_y")
                    or ("warp_stabilizer", "motion_x" or "motion_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("mirror" or "twirl", "angle") or ("wave_warp", "direction")
                    or ("warp_stabilizer", "motion_rotation") => (-360d, 360d),
                ("lens_distortion", "curvature" or "h_decentering" or "v_decentering")
                    or ("warp_stabilizer", "motion_shear") => (-100d, 100d),
                ("twirl", "radius") or ("magnify", "opacity")
                    or ("warp_stabilizer", "smoothness" or "action_safe" or "crop_less") => (0d, 100d),
                ("wave_warp", "height" or "width") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("wave_warp", "speed") or ("spacer", "fill") => (0d, 0d),
                ("spherize" or "spacer", "radius") or ("spacer", "left" or "top" or "right" or "bottom")
                    or ("magnify", "size" or "feather") => (0d, MaxTransformCoordinate),
                ("magnify", "magnification") or ("warp_stabilizer", "max_scale" or "additional_scale" or "motion_scale") => (0d, MaxTransformScale),
                ("magnify", "shape") or ("spacer", "uniform")
                    or ("warp_stabilizer", "result" or "preserve_scale" or "detailed") => (0d, 1d),
                ("magnify", "mode") or ("warp_stabilizer", "method" or "framing") => (0d, 3d),
                ("spacer", "color_r" or "color_g" or "color_b") => (0d, 1d),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || (key is "shape" or "uniform" or "result" or "preserve_scale" or "detailed" or "mode" or "method" or "framing") && value != Math.Truncate(value))
                throw new ArgumentException(InvalidParam);
        }
        foreach (var prefix in new[] { "center", "motion" })
            if (effect.Params.ContainsKey(prefix + "_x") != effect.Params.ContainsKey(prefix + "_y"))
                throw new ArgumentException(InvalidParam);
        // The default wave is animated; only an explicit zero speed has a static oracle.
        if (effect.Id == "wave_warp" && effect.Params.GetValueOrDefault("speed", 1) != 0)
            throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV12(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        string P(string key, string fallback) => p.ContainsKey(key) ? N(p[key]) : fallback;
        if (id == "mirror")
        {
            var a = V("angle", 0) * Math.PI / 180;
            var nx = N(Math.Cos(a)); var ny = N(Math.Sin(a));
            var distance = "max(0,(X+0.5-(" + P("center_x", "W/2") + "))*" + nx + "+(Y+0.5-(" + P("center_y", "H/2") + "))*" + ny + ")";
            return V12Warp("(st(4," + distance + ");X+0.5-2*ld(4)*" + nx + ")", "Y+0.5-2*ld(4)*" + ny, true);
        }
        if (id == "lens_distortion")
        {
            var k = N(V("curvature", 0) / 100); var hx = N(V("h_decentering", 0) / 100); var hy = N(V("v_decentering", 0) / 100);
            // Coordinates are evaluated x then y before sampling; retain their common mapping terms.
            var init = "st(4,(X+0.5-W/2)/hypot(W/2,H/2));st(5,(Y+0.5-H/2)/hypot(W/2,H/2));st(6,ld(4)*ld(4)+ld(5)*ld(5));";
            var u = "ld(4)"; var v = "ld(5)"; var rr = "ld(6)";
            var radial = "(1+" + k + "*" + rr + ")";
            return V12Warp("(" + init + "W/2+(" + u + "*" + radial + "+2*" + hy + "*" + u + "*" + v + "+" + hx + "*(" + rr + "+2*" + u + "*" + u + "))*hypot(W/2,H/2))", "H/2+(" + v + "*" + radial + "+" + hy + "*(" + rr + "+2*" + v + "*" + v + ")+2*" + hx + "*" + u + "*" + v + ")*hypot(W/2,H/2)", true);
        }
        if (id == "twirl" || id == "spherize")
        {
            var cx = P("center_x", "W/2"); var cy = P("center_y", "H/2");
            var dx = "(X+0.5-(" + cx + "))"; var dy = "(Y+0.5-(" + cy + "))";
            var radius = id == "twirl" ? N(V("radius", 30) / 100) + "*min(W,H)" : N(V("radius", 0));
            var rho = "(hypot(" + dx + "," + dy + ")/(" + radius + "))";
            if (id == "spherize")
            {
                var gain = "if(gt(" + rho + ",0)*lt(" + rho + ",1),asin(" + rho + ")/(PI/2*" + rho + "),1)";
                if (V("radius", 0) == 0) return "";
                return V12Warp("(" + cx + ")+" + dx + "*(" + gain + ")", "(" + cy + ")+" + dy + "*(" + gain + ")", false);
            }
            var angle = N(-V("angle", 50) * Math.PI / 180) + "*pow(1-" + rho + ",2)";
            var inside = "gt(" + radius + ",0)*lt(hypot(" + dx + "," + dy + ")," + radius + ")";
            return V12Warp("if(" + inside + ",(" + cx + ")+" + dx + "*cos(" + angle + ")-" + dy + "*sin(" + angle + "),X+0.5)", "if(" + inside + ",(" + cy + ")+" + dx + "*sin(" + angle + ")+" + dy + "*cos(" + angle + "),Y+0.5)", true);
        }
        if (id == "wave_warp")
        {
            var a = V("direction", 90) * Math.PI / 180;
            var ux = N(Math.Cos(a)); var uy = N(Math.Sin(a));
            var displacement = N(V("height", 10)) + "*sin(((X+0.5)*" + ux + "+(Y+0.5)*" + uy + ")*2*PI/" + N(Math.Max(.1, Math.Abs(V("width", 40)))) + ")";
            return V12Warp("X+0.5+" + uy + "*(" + displacement + ")", "Y+0.5-" + ux + "*(" + displacement + ")", true);
        }
        if (id == "warp_stabilizer")
        {
            var strength = V("result", 0) == 1 ? 1 : V("smoothness", 50) / 100;
            var method = V("method", 0); var framing = V("framing", 0);
            var rotation = method > 0 ? V("motion_rotation", 0) * (Math.PI / 180) * strength : 0;
            var motionScale = method > 0 && V("preserve_scale", 0) == 0 ? Math.Max(.01, V("motion_scale", 100) / 100) : 1;
            var mx = V("motion_x", 0); var my = V("motion_y", 0);
            var crop = framing == 1 || framing == 2 ? "1+" + N(V("action_safe", 0) / 100) + "*2+(" + N(Math.Abs(mx)) + "/W+" + N(Math.Abs(my)) + "/H)*" + N(strength) + "*" + N(1 + V("crop_less", 50) / 100) : "1";
            var zoom = N(Math.Max(.01, V("additional_scale", 100) / 100)) + "*min(" + N(Math.Max(1, V("max_scale", 150) / 100)) + "," + crop + ")/" + N(1 + (motionScale - 1) * strength);
            var dx = "((X+0.5-W/2)/(" + zoom + "))"; var dy = "((Y+0.5-H/2)/(" + zoom + "))";
            var x = "W/2+" + N(Math.Cos(rotation)) + "*" + dx + "-" + N(Math.Sin(rotation)) + "*" + dy + "+" + N(mx * strength);
            if (V("detailed", 0) == 1 && method >= 2) x += "+" + dy + "*" + N(V("motion_shear", 0) / 100 * strength);
            var y = "H/2+" + N(Math.Sin(rotation)) + "*" + dx + "+" + N(Math.Cos(rotation)) + "*" + dy + "+" + N(my * strength);
            return V12Warp(x, y, false, framing == 3);
        }
        if (id == "spacer")
        {
            var left = V("left", 40); var top = V("uniform", 1) == 1 ? left : V("top", 40);
            var right = V("uniform", 1) == 1 ? left : V("right", 40); var bottom = V("uniform", 1) == 1 ? left : V("bottom", 40);
            var w = "(W-" + N(left) + "-" + N(right) + ")"; var h = "(H-" + N(top) + "-" + N(bottom) + ")";
            var guard = "gt(" + w + ",0)*gt(" + h + ",0)";
            var warp = V12Warp("if(" + guard + ",(X+0.5-" + N(left) + ")*W/" + w + ",-100000000)", "if(" + guard + ",(Y+0.5-" + N(top) + ")*H/" + h + ",-100000000)", true);
            var radius = "max(0,min(" + N(V("radius", 0)) + ",min(" + w + "/2," + h + "/2)))";
            var init = "st(0,abs(X+0.5-" + N(left) + "-" + w + "/2)-(" + w + "/2-" + radius + "));st(1,abs(Y+0.5-" + N(top) + "-" + h + "/2)-(" + h + "/2-" + radius + "));st(2,hypot(max(ld(0),0),max(ld(1),0))+min(max(ld(0),ld(1)),0)-" + radius + ");";
            return warp + "," + RgbExpr(Rgb.Select(c => c + "(X,Y)").ToArray(), init + GeometryByte("alpha(X,Y)*clip(0.5-ld(2),0,1)"));
        }
        if (id == "magnify")
        {
            var cx = P("center_x", "W/2"); var cy = P("center_y", "H/2");
            var dx = "(X+0.5-(" + cx + "))"; var dy = "(Y+0.5-(" + cy + "))";
            var zoom = N(Math.Max(.01, V("magnification", 200) / 100));
            var distance = V("shape", 0) == 1 ? "max(abs(" + dx + "),abs(" + dy + "))" : "hypot(" + dx + "," + dy + ")";
            var size = V("size", 100); var feather = V("feather", 0);
            var t = feather > 0 ? "(st(4,clip(((" + distance + ")-" + N(Math.Max(0, size - feather)) + ")/" + N(Math.Max(size - Math.Max(0, size - feather), 1e-6)) + ",0,1));1-ld(4)*ld(4)*(3-2*ld(4)))" : "lt(" + distance + "," + N(size) + ")";
            var init = "st(5,(" + t + ")*" + N(V("opacity", 100) / 100) + ");" + V12Coordinates("(" + cx + ")+" + dx + "/" + zoom, "(" + cy + ")+" + dy + "/" + zoom, false) + "st(6," + V12Sample("alpha", false) + "/255*ld(5));st(7,ld(6)+alpha(X,Y)/255*(1-ld(6)));";
            var mode = V("mode", 0);
            return RgbExpr(Rgb.Select(c =>
            {
                var q = "(" + V12Sample(c, false) + "/255)"; var b = c + "(X,Y)/255";
                var v = mode == 1 ? "1-(1-" + q + ")*(1-" + b + ")" : mode == 2 ? "min(1," + q + "+" + b + ")" : mode == 3 ? q + "*" + b : q;
                return init + "if(lte(ld(5),0)," + c + "(X,Y)," + GeometryByte("if(gt(ld(7),0),((" + v + ")*ld(6)+" + b + "*alpha(X,Y)/255*(1-ld(6)))/ld(7)*255,0)") + ")";
            }).ToArray(), init + "if(lte(ld(5),0),alpha(X,Y)," + GeometryByte("ld(7)*255") + ")");
        }
        return null;
    }

    private static string V12Coordinates(string x, string y, bool clamped)
    {
        var sx = "(" + x + ")-0.5"; var sy = "(" + y + ")-0.5";
        if (clamped) { sx = "clip(" + sx + ",0,W-1)"; sy = "clip(" + sy + ",0,H-1)"; }
        return "st(0," + sx + ");st(1," + sy + ");st(2,floor(ld(0)));st(3,floor(ld(1)));st(0,ld(0)-ld(2));st(1,ld(1)-ld(3));";
    }

    private static string V12Sample(string channel, bool linear, bool clamped = false)
    {
        string Tap(string x, string y)
        {
            var sx = clamped ? "clip(" + x + ",0,W-1)" : x; var sy = clamped ? "clip(" + y + ",0,H-1)" : y;
            var value = channel + "(" + sx + "," + sy + ")";
            return clamped ? value : "if(between(" + x + ",0,W-1)*between(" + y + ",0,H-1)," + value + ",0)";
        }
        var a = Tap("ld(2)", "ld(3)"); var b = Tap("ld(2)+1", "ld(3)");
        var c = Tap("ld(2)", "ld(3)+1"); var d = Tap("ld(2)+1", "ld(3)+1");
        // Cache each tap once; encoded samples must preserve magnify's 5-7.
        var top = linear ? "5" : "8"; var bot = linear ? "6" : "9";
        return "(st(" + top + "," + a + ");st(" + top + ",ld(" + top + ")+(" + b + "-ld(" + top + "))*ld(0));st(" + bot + "," + c + ");st(" + bot + ",ld(" + bot + ")+(" + d + "-ld(" + bot + "))*ld(0));ld(" + top + ")+(ld(" + bot + ")-ld(" + top + "))*ld(1))";
    }

    private static string V12Warp(string x, string y, bool linear, bool clamped = false)
    {
        var init = V12Coordinates(x, y, clamped);
        var alpha = "(" + V12Sample("alpha", false, clamped) + ")/255*255";
        if (linear) alpha = PreviewFloat32(alpha);
        // geo() materializes these byte taps before warping. Keep that boundary instead of
        // inlining the same decode/premultiply into every bilinear lookup and channel.
        var prefix = linear ? RgbExpr(Rgb.Select(c => GeometryByte(PreviewFloat32(PreviewFloat32(Linear(c + "(X,Y)")) + "*(alpha(X,Y)/255)*255"))).ToArray()) + "," : "";
        return prefix + RgbExpr(Rgb.Select(c =>
        {
            var sample = V12Sample(c, linear, clamped);
            // Keep the Float32 warp result before alpha byte rounding and unpremultiplication.
            var value = linear ? GeometryByte("(" + LumetriEncoded(PreviewFloat32("if(gt(ld(4),0.00000001)," + PreviewFloat32("(" + sample + ")/255*255") + "/ld(4),0)")) + ")*255") : GeometryByte("(" + sample + ")/255*255");
            return init + "st(4," + alpha + ");" + value;
        }).ToArray(), init + GeometryByte(alpha));
    }
}
