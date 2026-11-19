namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV9(string id) => id is "compound_blur" or "focus_blur";

    private static void ValidateV9(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("compound_blur", "max") or ("focus_blur", "amount") => (0d, MaxRadius),
                ("compound_blur", "layer") => (0d, 2d),
                ("compound_blur", "stretch" or "invert") or ("focus_blur", "shape") => (0d, 1d),
                ("focus_blur", "show") => (0d, 0d),
                ("focus_blur", "center_x" or "center_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("focus_blur", "angle") => (-360d, 360d),
                ("focus_blur", "size" or "feather") => (0d, MaxTransformCoordinate),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || key is "layer" or "stretch" or "invert" or "shape" or "show" && value != Math.Truncate(value))
                throw new ArgumentException(InvalidParam);
        }
        if (effect.Params.ContainsKey("center_x") != effect.Params.ContainsKey("center_y"))
            throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV9(string id, Dictionary<string, double> p)
    {
        if (!IsV9(id)) return null;
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        var radius = V(id == "compound_blur" ? "max" : "amount", 20);
        if (radius <= .3) return "";

        // The lower half retains encoded source bytes; only the upper half is blurred.
        // This single-input graph avoids fixed split labels when effects are chained.
        var channels = new[] { "r", "g", "b", "alpha" };
        var filters = new List<string> { "format=gbrap,pad=iw:ih*2:0:0:color=black", FloatExpr(channels.Select((c, i) => i == 3
            ? "alpha(X,mod(Y,H/2))" : "if(lt(Y,H/2)," + Linear("floor(" + c + "(X,Y)*255+0.5)") + "," + c + "(X,Y-H/2))").ToArray(), true) };
        for (var axis = 0; axis < 2; axis++)
            for (var pass = 0; pass < 3; pass++)
            {
                var coordinate = axis == 0 ? "X" : "Y";
                var dimension = axis == 0 ? "W" : "H/2";
                // At the integer cap sigma=2*max(dimension,8), the box radii are
                // [sigma-1,sigma-1,sigma]. Precompute the uncapped branch as well.
                var box = BoxRadii(radius)[pass];
                var capped = "max(" + dimension + ",8)*2";
                var init = "st(3,if(gt(" + N(radius) + "," + capped + ")," + capped + (pass < 2 ? "-1" : "") + "," + N(box) + "));"
                    + "st(0,max(0," + coordinate + "-ld(3)));st(1,min(" + dimension + "-1," + coordinate + "+ld(3)));";
                var values = Rgb.Select(c =>
                {
                    // Coordinates are nonnegative except the guarded previous row/column.
                    var sum = axis == 0
                        ? c + "sum(ld(1),Y)-if(gt(ld(0),0)," + c + "sum(ld(0)-1,Y),0)-if(gt(Y,0)," + c + "sum(ld(1),Y-1)-if(gt(ld(0),0)," + c + "sum(ld(0)-1,Y-1),0),0)"
                        : c + "sum(X,ld(1))-if(gt(ld(0),0)," + c + "sum(X,ld(0)-1),0)-if(gt(X,0)," + c + "sum(X-1,ld(1))-if(gt(ld(0),0)," + c + "sum(X-1,ld(0)-1),0),0)";
                    var edge = axis == 0 ? "max(0,ld(3)-X)*" + c + "(0,Y)+max(0,X+ld(3)-W+1)*" + c + "(W-1,Y)"
                        : "max(0,ld(3)-Y)*" + c + "(X,0)+max(0,Y+ld(3)-H/2+1)*" + c + "(X,H/2-1)";
                    return "if(lt(Y,H/2)," + init + "(" + sum + "+" + edge + ")/(2*ld(3)+1)," + c + "(X,Y))";
                }).ToArray();
                filters.Add(FloatExpr(new[] { values[0], values[1], values[2], "alpha(X,Y)" }));
            }
        var original = "st(0,floor(r(X,Y+H/2)*255+0.5));st(1,floor(g(X,Y+H/2)*255+0.5));st(2,floor(b(X,Y+H/2)*255+0.5));";
        string mask;
        if (id == "compound_blur")
        {
            mask = V("layer", 0) == 2 ? "floor(alpha(X,Y)*255+0.5)/255" : "(0.2126*ld(0)+0.7152*ld(1)+0.0722*ld(2))/255";
            if (V("invert", 0) == 1) mask = "1-(" + mask + ")";
        }
        else
        {
            var cx = p.ContainsKey("center_x") ? N(p["center_x"]) : "W/2";
            var cy = p.ContainsKey("center_y") ? N(p["center_y"]) : "H/4";
            var dx = "(X+0.5-(" + cx + "))"; var dy = "(Y+0.5-(" + cy + "))";
            var angle = V("angle", 0) * Math.PI / 180;
            var distance = V("shape", 0) == 1 ? "abs(-" + dx + "*" + N(Math.Sin(angle)) + "+" + dy + "*" + N(Math.Cos(angle)) + ")" : "hypot(" + dx + "," + dy + ")";
            mask = "st(4,clip((" + distance + "-" + N(V("size", 200) / 2) + ")/" + N(Math.Max(1, V("feather", 200))) + ",0,1));ld(4)*ld(4)*(3-2*ld(4))";
        }
        var prefix = original + "st(4," + mask + ");";
        var output = Rgb.Select((c, i) => "if(lt(Y,H/2),(" + prefix + GeometryByte("ld(" + i + ")+( (" + GeometryByte(LumetriEncoded(c + "(X,Y)") + "*255") + ")-ld(" + i + "))*ld(4)") + ")/255," + c + "(X,Y))").ToArray();
        filters.Add(FloatExpr(new[] { output[0], output[1], output[2], "alpha(X,Y)" }));
        filters.Add("zscale=rangein=full:range=full:dither=none,format=gbrap,crop=iw:ih/2:0:0");
        return string.Join(',', filters);
    }
}
