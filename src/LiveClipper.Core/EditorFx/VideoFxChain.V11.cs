namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV11(string id) => id is "circle" or "grid" or "checkerboard" or "ellipse" or "paint_bucket"
        or "alpha_adjust" or "track_matte" or "color_key" or "luma_key" or "logo_cutout";

    private static void ValidateV11(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("circle" or "grid" or "checkerboard" or "paint_bucket" or "alpha_adjust", "opacity")
                    or ("ellipse", "softness") or ("luma_key" or "logo_cutout", "threshold")
                    or ("luma_key", "cutoff") or ("logo_cutout", "softness") => (0d, 100d),
                ("circle", "center_x" or "center_y") or ("checkerboard", "anchor_x" or "anchor_y")
                    or ("ellipse", "center_x" or "center_y" or "width" or "height")
                    or ("paint_bucket", "point_x" or "point_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("circle", "radius") or ("grid", "size" or "border")
                    or ("checkerboard", "width" or "height" or "feather") or ("ellipse", "thickness") => (0d, MaxTransformCoordinate),
                ("circle" or "grid" or "checkerboard" or "paint_bucket" or "color_key" or "logo_cutout", "color_r" or "color_g" or "color_b")
                    or ("ellipse", "inside_r" or "inside_g" or "inside_b" or "outside_r" or "outside_g" or "outside_b") => (0d, 1d),
                ("checkerboard", "square" or "replace") or ("ellipse", "composite") or ("paint_bucket", "invert")
                    or ("alpha_adjust", "ignore" or "invert" or "mask_only") or ("track_matte", "composite" or "reverse")
                    or ("logo_cutout", "unmultiply" or "invert") => (0d, 1d),
                ("checkerboard" or "paint_bucket", "mode") => (0d, 4d),
                ("paint_bucket", "selector") => (0d, 3d),
                ("paint_bucket", "tolerance") => (100d, 100d),
                ("track_matte", "matte") or ("logo_cutout", "background") => (0d, 2d),
                ("color_key", "tolerance" or "thin" or "feather") => (0d, 0d),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || (key is "square" or "replace" or "composite" or "invert" or "ignore" or "mask_only" or "reverse" or "unmultiply" or "mode" or "selector" or "matte" or "background") && value != Math.Truncate(value))
                throw new ArgumentException(InvalidParam);
        }
        foreach (var prefix in new[] { "center", "anchor", "point" })
            if (effect.Params.ContainsKey(prefix + "_x") != effect.Params.ContainsKey(prefix + "_y"))
                throw new ArgumentException(InvalidParam);
        // Only the whole-frame connected paint domain and the binary exact-color mask are proved.
        if (effect.Id == "paint_bucket" && effect.Params.GetValueOrDefault("tolerance", 10) != 100
            || effect.Id == "checkerboard" && effect.Params.GetValueOrDefault("replace", 1) == 1 && effect.Params.GetValueOrDefault("mode", 0) != 0)
            throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV11(string id, Dictionary<string, double> p)
    {
        if (!IsV11(id)) return null;
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        string Color(string prefix, string channel, double fallback) => N(V(prefix + "_" + channel, fallback));
        if (id == "alpha_adjust")
        {
            var a = V("ignore", 0) == 1 ? "1" : "alpha(X,Y)/255";
            if (V("invert", 0) == 1) a = "1-(" + a + ")";
            a = "clip((" + a + ")*" + N(V("opacity", 100) / 100) + ",0,1)";
            if (V("mask_only", 0) == 1)
            {
                // The preview passes the rounded alpha through both Float32 sRGB lookup tables.
                var gray = GeometryByte("(" + LumetriEncoded(PreviewFloat32(Linear("floor((" + a + ")*255+0.5)"))) + ")*255");
                return RgbExpr(Rgb.Select(_ => gray).ToArray(), "255");
            }
            return RgbExpr(Rgb.Select(c => c + "(X,Y)").ToArray(), GeometryByte("(" + a + ")*255"));
        }
        if (id == "track_matte")
        {
            var a = V("composite", 0) == 1 || V("matte", 0) == 2 ? "(0.2126*r(X,Y)+0.7152*g(X,Y)+0.0722*b(X,Y))/255" : "alpha(X,Y)/255";
            if (V("reverse", 0) == 1) a = "1-(" + a + ")";
            return RgbExpr(Rgb.Select(c => c + "(X,Y)").ToArray(), GeometryByte("alpha(X,Y)*(" + a + ")"));
        }
        if (id == "color_key")
        {
            var match = string.Join('*', Rgb.Select(c => "eq(" + c + "(X,Y)/255," + Color("color", c, 0) + ")"));
            return RgbExpr(Rgb.Select(c => c + "(X,Y)").ToArray(), "alpha(X,Y)*not(" + match + ")");
        }
        if (id == "luma_key")
        {
            var linear = "0.2126*" + PreviewFloat32(Linear("r(X,Y)")) + "+0.7152*" + PreviewFloat32(Linear("g(X,Y)")) + "+0.0722*" + PreviewFloat32(Linear("b(X,Y)"));
            var threshold = V("threshold", 50) / 100;
            var cut = V("cutoff", 0) / 100;
            var init = "st(0," + LumetriEncoded(linear) + ");st(1,if(lte(ld(0)," + N(cut) + "),0,if(gte(ld(0)," + N(Math.Max(threshold, cut + .001)) + "),1,(ld(0)-" + N(cut) + ")/" + N(Math.Max(threshold - cut, .001)) + ")));";
            return RgbExpr(Rgb.Select(c => "if(lte(alpha(X,Y),0)," + c + "(X,Y)," + init + GeometryByte(c + "(X,Y)*ld(1)") + ")").ToArray(), "if(lte(alpha(X,Y),0),0," + init + GeometryByte("alpha(X,Y)*ld(1)") + ")");
        }
        if (id == "logo_cutout")
        {
            var background = V("background", 0);
            var distance = "max(max(" + string.Join(",", Rgb.Select(c => "abs(" + c + "(X,Y)/255-" + (background == 2 ? Color("color", c, 1) : N(background == 0 ? 1 : 0)) + ")").Take(2)) + "),abs(b(X,Y)/255-" + (background == 2 ? Color("color", "b", 1) : N(background == 0 ? 1 : 0)) + "))";
            var init = "st(0,clip((" + distance + "-" + N(V("threshold", 5) / 100) + ")/" + N(Math.Max(1d / 255, V("softness", 10) / 100)) + ",0,1));st(0,ld(0)*ld(0)*(3-2*ld(0)));";
            var keep = V("invert", 0) == 1 ? "1-ld(0)" : "ld(0)";
            var remove = V("unmultiply", 1) == 1 && V("invert", 0) == 0;
            return RgbExpr(Rgb.Select(c => remove ? init + "if(gt(ld(0),0.001)," + GeometryByte("clip((" + c + "(X,Y)/255-" + (background == 2 ? Color("color", c, 1) : N(background == 0 ? 1 : 0)) + "*(1-ld(0)))/ld(0),0,1)*255") + "," + c + "(X,Y))" : c + "(X,Y)").ToArray(), init + GeometryByte("alpha(X,Y)*(" + keep + ")"));
        }
        if (id == "circle")
        {
            var cx = p.ContainsKey("center_x") ? N(V("center_x", 0)) : "W/2";
            var cy = p.ContainsKey("center_y") ? N(V("center_y", 0)) : "H/2";
            var a = "clip(" + N(V("radius", 75)) + "-hypot(X+0.5-(" + cx + "),Y+0.5-(" + cy + "))+0.5,0,1)*" + N(V("opacity", 100) / 100);
            return V11Over(p, a, false, false, "color");
        }
        if (id == "grid")
        {
            var size = N(Math.Max(V("size", 50), 1));
            var init = "st(0,mod(mod(X-W/2," + size + ")+" + size + "," + size + "));st(1,mod(mod(Y-H/2," + size + ")+" + size + "," + size + "));";
            var a = init + "clip(" + N(V("border", 2) * .5) + "-min(min(ld(0)," + size + "-ld(0)),min(ld(1)," + size + "-ld(1)))+0.5,0,1)*" + N(V("opacity", 100) / 100);
            return V11Over(p, a, false, false, "color");
        }
        if (id == "ellipse")
        {
            var cx = p.ContainsKey("center_x") ? N(V("center_x", 0)) : "W/2";
            var cy = p.ContainsKey("center_y") ? N(V("center_y", 0)) : "H/2";
            var rx = N(Math.Max(.01, Math.Abs(V("width", 200)) / 2)); var ry = N(Math.Max(.01, Math.Abs(V("height", 200)) / 2));
            var thick = V("thickness", 8); var soft = N(Math.Max(.5, thick * V("softness", 15) / 100));
            var init = "st(0,X+0.5-(" + cx + "));st(1,Y+0.5-(" + cy + "));st(2,hypot(ld(0)/" + rx + ",ld(1)/" + ry + "));st(3,if(gt(ld(2),0.00000001),hypot(ld(0)/(" + rx + "*" + rx + "),ld(1)/(" + ry + "*" + ry + "))/ld(2),1/min(" + rx + "," + ry + ")));st(4,(ld(2)-1)/ld(3));";
            var a = init + "clip((" + (thick > 0 ? N(thick / 2) + "-abs(ld(4))" : "-ld(4)") + ")/" + soft + "+0.5,0,1)";
            var composite = V("composite", 0) == 1;
            var background = composite ? "" : RgbExpr(Rgb.Select(c => GeometryByte(Color("outside", c, 0) + "*255")).ToArray(), "255") + ",";
            return background + V11Over(p, a, true, false, "inside");
        }
        if (id == "checkerboard")
        {
            var width = N(Math.Max(1, V("width", 64))); var height = N(Math.Max(1, V("square", 1) == 1 ? V("width", 64) : V("height", 64)));
            var ax = p.ContainsKey("anchor_x") ? N(V("anchor_x", 0)) : "W/2";
            var ay = p.ContainsKey("anchor_y") ? N(V("anchor_y", 0)) : "H/2";
            string Wave(string coordinate, string size) => V("feather", 0) == 0 ? "if(mod(floor((" + coordinate + ")/" + size + "),2),-1,1)" : "(st(2,mod(mod(" + coordinate + "," + size + ")+" + size + "," + size + "));if(lt(mod(mod(" + coordinate + ",2*" + size + ")+2*" + size + ",2*" + size + ")," + size + "),1,-1)*clip(min(ld(2)," + size + "-ld(2))/min(" + N(V("feather", 0)) + "," + size + "/2),0,1))";
            var a = "st(0," + Wave("X+0.5-(" + ax + ")", width) + ");st(1," + Wave("Y+0.5-(" + ay + ")", height) + ");(1+ld(0)*ld(1))*0.5*" + N(V("opacity", 100) / 100);
            return V11Over(p, a, true, V("replace", 1) == 1, "color");
        }
        return V11Over(p, V("invert", 0) == 1 ? "0" : N(V("opacity", 100) / 100), true, false, "color");
    }

    private static string V11Over(Dictionary<string, double> p, string amount, bool straight, bool replace, string prefix)
    {
        var init = "st(4,clip((" + amount + "),0,1));st(5," + (replace ? "0" : "alpha(X,Y)/255") + ");st(6,ld(4)+ld(5)*(1-ld(4)));";
        var mode = p.GetValueOrDefault("mode", 0);
        return RgbExpr(Rgb.Select(c =>
        {
            var color = N(p.GetValueOrDefault(prefix + "_" + c, 1));
            var value = replace ? "0" : c + "(X,Y)/255";
            var blend = mode == 1 ? "min(1," + value + "+" + color + ")" : mode == 2 ? "1-(1-" + value + ")*(1-" + color + ")" : mode == 3 ? value + "*" + color : mode == 4 ? "if(lt(" + value + ",0.5),2*" + value + "*" + color + ",1-2*(1-" + value + ")*(1-" + color + "))" : color;
            var outValue = straight ? "((1-ld(4))*ld(5)*" + value + "+ld(4)*((1-ld(5))*" + color + "+ld(5)*(" + blend + ")))/ld(6)*255" : "(" + color + "*ld(4)+" + value + "*(1-ld(4)))*255";
            return init + "if(lte(ld(4),0)," + (replace ? "0" : c + "(X,Y)") + "," + GeometryByte(outValue) + ")";
        }).ToArray(), init + "if(lte(ld(4),0)," + (replace ? "0" : "alpha(X,Y)") + "," + GeometryByte("ld(6)*255") + ")");
    }
}
