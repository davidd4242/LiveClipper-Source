namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV13(string id) => id is "ultra_key" or "drop_shadow" or "long_shadow" or "grow" or "shrink" or "move";

    private static void ValidateV13(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("ultra_key", "key_color_r" or "key_color_g" or "key_color_b")
                    or ("drop_shadow" or "long_shadow", "color_r" or "color_g" or "color_b") => (0d, 1d),
                ("ultra_key", "tolerance" or "transparency" or "highlight" or "shadow" or "pedestal" or "spill")
                    or ("drop_shadow" or "long_shadow", "opacity") => (0d, 100d),
                ("ultra_key", "choke" or "soften") or ("drop_shadow", "softness") or ("move", "motion_blur") => (0d, 0d),
                ("ultra_key", "output") => (0d, 2d),
                ("ultra_key", "setting") or ("grow" or "shrink" or "move", "easing") => (0d, 3d),
                ("drop_shadow" or "long_shadow", "only") or ("long_shadow", "fade" or "length") => (0d, 1d),
                ("drop_shadow", "direction") or ("long_shadow", "angle") => (-360d, 360d),
                ("drop_shadow", "distance") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("grow" or "shrink", "from" or "to") => (0d, MaxTransformScale),
                ("grow" or "shrink", "center_x" or "center_y")
                    or ("move", "from_x" or "from_y" or "to_x" or "to_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || (key is "output" or "setting" or "easing" or "only" or "fade") && value != Math.Truncate(value))
                throw new ArgumentException(InvalidParam);
        }
        foreach (var prefix in new[] { "center", "from", "to" })
            if (effect.Params.ContainsKey(prefix + "_x") != effect.Params.ContainsKey(prefix + "_y"))
                throw new ArgumentException(InvalidParam);
        if (effect.Id is "grow" or "shrink")
        {
            if (effect.Params.GetValueOrDefault("from", effect.Id == "grow" ? 100 : 120)
                != effect.Params.GetValueOrDefault("to", effect.Id == "grow" ? 120 : 100))
                throw new ArgumentException(InvalidParam);
        }
        if (effect.Id == "move")
            foreach (var axis in new[] { "x", "y" })
                if (!effect.Params.ContainsKey("from_" + axis) || !effect.Params.ContainsKey("to_" + axis)
                    || effect.Params["from_" + axis] != effect.Params["to_" + axis])
                    throw new ArgumentException(InvalidParam);
        if (effect.Id == "long_shadow" && effect.Params.GetValueOrDefault("length", 100) > 1)
            throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV13(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        string P(string key, string fallback) => p.ContainsKey(key) ? N(p[key]) : fallback;
        if (id == "grow" || id == "shrink")
        {
            var z = V("from", id == "grow" ? 100 : 120) / 100;
            if (Math.Abs(z * z) < 1e-10) return RgbExpr(new[] { "0", "0", "0" }, "0");
            var cx = P("center_x", "W/2"); var cy = P("center_y", "H/2");
            return V12Warp("(" + cx + ")+(X+0.5-(" + cx + "))/" + N(z), "(" + cy + ")+(Y+0.5-(" + cy + "))/" + N(z), true);
        }
        if (id == "move")
        {
            return V12Warp("W/2+(X+0.5-(W/2+" + N(V("from_x", 0)) + "))", "H/2+(Y+0.5-(H/2+" + N(V("from_y", 0)) + "))", true);
        }
        if (id == "ultra_key")
        {
            var kr = V("key_color_r", 0); var kg = V("key_color_g", 0); var kb = V("key_color_b", 0);
            var kl = kr * .2126 + kg * .7152 + kb * .0722;
            var strength = V("setting", 0) == 1 ? .75 : V("setting", 0) == 2 ? 1.25 : 1;
            var lo = V("tolerance", 50) / 100 * .35 * strength;
            var hi = V("tolerance", 50) / 100 * (.6 + V("transparency", 45) / 100) * strength + .001;
            var pedestal = V("pedestal", 10) / 100;
            var init = "st(0,r(X,Y)/255);st(1,g(X,Y)/255);st(2,b(X,Y)/255);st(3,ld(0)*0.2126+ld(1)*0.7152+ld(2)*0.0722);"
                + "st(4,sqrt(pow((ld(0)-ld(3))-(" + N(kr - kl) + "),2)+pow((ld(1)-ld(3))-(" + N(kg - kl) + "),2)+pow((ld(2)-ld(3))-(" + N(kb - kl) + "),2)+pow((ld(3)-" + N(kl) + ")*0.35,2)));"
                + "st(4,clip((ld(4)-" + N(lo) + ")/" + N(Math.Max(hi - lo, 1e-6)) + ",0,1));st(4,ld(4)*ld(4)*(3-2*ld(4)));"
                + "st(5," + PreviewFloat32("clip((ld(4)-" + N(pedestal * .2) + "-(1-ld(3))*" + N(V("shadow", 50) / 100) + "*0.05+ld(3)*" + N(V("highlight", 10) / 100) + "*0.05)/" + N(Math.Max(.01, 1 - pedestal * .2)) + ",0,1)") + ");";
            var dominant = kg > kr && kg > kb ? 1 : kb > kr ? 2 : 0;
            var output = V("output", 0);
            return RgbExpr(Rgb.Select((c, i) => init + (output == 1 ? GeometryByte("ld(5)*255") : i == dominant
                ? GeometryByte(c + "(X,Y)-max(0," + c + "(X,Y)-(" + Rgb[(dominant + 1) % 3] + "(X,Y)+" + Rgb[(dominant + 2) % 3] + "(X,Y))/2)*" + N(V("spill", 50) / 100) + "*(1-ld(5))") : c + "(X,Y)")).ToArray(), init + (output == 1 ? "255" : output == 2 ? "alpha(X,Y)" : GeometryByte("alpha(X,Y)*ld(5)")));
        }
        if (id == "drop_shadow" || id == "long_shadow")
        {
            var angle = V(id == "drop_shadow" ? "direction" : "angle", 135) * Math.PI / 180;
            var dx = Math.Sin(angle); var dy = -Math.Cos(angle);
            var length = V("length", 100);
            var distance = V("distance", 5);
            if (id == "long_shadow") { var dominant = Math.Max(Math.Abs(dx), Math.Abs(dy)); dx /= dominant; dy /= dominant; }
            else { dx *= distance; dy *= distance; }
            var sample = "(st(0,X-(" + N(dx) + "));st(1,Y-(" + N(dy) + "));st(2,floor(ld(0)));st(3,floor(ld(1)));st(0,ld(0)-ld(2));st(1,ld(1)-ld(3));" + V13MaskSample() + ")";
            var mask = id == "long_shadow" ? length == 0 ? "0" : "max(" + PreviewFloat32("alpha(X,Y)/255") + "," + sample + "*" + N(V("fade", 1) == 1 ? Math.Exp(-3) : 1) + ")" : sample;
            var init = "st(4,clip(" + PreviewFloat32(mask) + "*" + N(V("opacity", 50) / 100) + ",0,1));st(5," + (V("only", 0) == 1 ? "0" : "alpha(X,Y)/255") + ");st(6,ld(5)+ld(4)*(1-ld(5)));";
            return RgbExpr(Rgb.Select(c => init + GeometryByte("if(gt(ld(6),0),(" + c + "(X,Y)/255*ld(5)+" + N(V("color_" + c, 0)) + "*ld(4)*(1-ld(5)))/ld(6)*255,0)")).ToArray(), init + GeometryByte("ld(6)*255"));
        }
        return null;
    }

    // Keep the bilinear accumulators (8/9) and coordinates (0..3) intact.
    private static string V13MaskFloat32(string value)
    {
        return PreviewFloat32(value).Replace("st(7,", "st(5,").Replace("ld(7)", "ld(5)").Replace("st(8,", "st(6,").Replace("ld(8)", "ld(6)").Replace("st(9,", "st(7,").Replace("ld(9)", "ld(7)");
    }

    private static string V13MaskSample()
    {
        string Tap(string x, string y) => "if(between(" + x + ",0,W-1)*between(" + y + ",0,H-1)," + V13MaskFloat32("alpha(" + x + "," + y + ")/255") + ",0)";
        var a = Tap("ld(2)", "ld(3)"); var b = Tap("ld(2)+1", "ld(3)");
        var c = Tap("ld(2)", "ld(3)+1"); var d = Tap("ld(2)+1", "ld(3)+1");
        return "(st(8," + a + ");st(8,ld(8)+(" + b + "-ld(8))*ld(0));st(9," + c + ");st(9,ld(9)+(" + d + "-ld(9))*ld(0));ld(8)+(ld(9)-ld(8))*ld(1))";
    }
}
