namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV14(string id) => id is "noise" or "basic_3d" or "turbulent_displace" or "spin"
        or "camera_shake" or "wiggle" or "wonder_glow" or "stroke";

    private static void ValidateV14(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("noise" or "turbulent_displace" or "spin" or "camera_shake" or "wiggle", "amount")
                    or ("basic_3d", "swivel" or "tilt" or "distance")
                    or ("camera_shake", "rotation" or "zoom" or "motion_blur")
                    or ("wiggle", "rotation" or "scale") or ("wonder_glow", "intensity") => (0d, 0d),
                ("noise", "color" or "clip") or ("turbulent_displace", "cycle" or "antialias")
                    or ("wonder_glow", "use_color") or ("stroke", "only") => (0d, 1d),
                ("spin", "scale") => (0d, MaxTransformScale),
                ("spin", "center_x" or "center_y") or ("turbulent_displace", "offset_x" or "offset_y" or "seed")
                    or ("camera_shake" or "wiggle", "seed") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("spin", "easing") or ("turbulent_displace", "pinning") => (0d, 3d),
                ("turbulent_displace", "displacement") => (0d, 5d),
                ("turbulent_displace", "size" or "cycle_revs") or ("camera_shake" or "wiggle", "frequency")
                    or ("wonder_glow", "radius") or ("stroke", "width") => (0d, MaxTransformCoordinate),
                ("turbulent_displace", "complexity") => (1d, 5d),
                ("turbulent_displace", "evolution") => (-360d, 360d),
                ("camera_shake", "complexity") => (1d, 6d),
                ("wiggle", "dimensions") or ("stroke", "position") => (0d, 2d),
                ("wonder_glow", "threshold") or ("stroke", "opacity") => (0d, 100d),
                ("wonder_glow", "saturation") => (0d, 200d),
                ("wonder_glow", "mode") => (0d, 4d),
                ("wonder_glow" or "stroke", "color_r" or "color_g" or "color_b") => (0d, 1d),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || (key is "color" or "clip" or "cycle" or "antialias" or "use_color" or "only" or "easing" or "pinning" or "displacement" or "dimensions" or "position" or "mode") && value != Math.Truncate(value))
                throw new ArgumentException(InvalidParam);
        }
        foreach (var prefix in new[] { "center", "offset" })
            if (effect.Params.ContainsKey(prefix + "_x") != effect.Params.ContainsKey(prefix + "_y"))
                throw new ArgumentException(InvalidParam);
        double V(string key, double fallback) => effect.Params.GetValueOrDefault(key, fallback);
        if ((effect.Id == "turbulent_displace" && V("amount", 50) != 0)
            || (effect.Id == "spin" && V("amount", 360) != 0)
            || (effect.Id == "camera_shake" && (V("amount", 20) != 0 || V("rotation", 1) != 0 || V("zoom", 5) != 0))
            || (effect.Id == "wiggle" && V("amount", 20) != 0)
            || (effect.Id == "wonder_glow" && V("intensity", 100) != 0)
            || (effect.Id == "stroke" && V("width", 4) != 0 && V("opacity", 100) != 0))
            throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV14(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        string P(string key, string fallback) => p.ContainsKey(key) ? N(p[key]) : fallback;
        if (id == "noise" || id == "basic_3d" || id == "turbulent_displace") return "";
        if (id == "stroke") return V("only", 0) == 1 ? RgbExpr(new[] { "0", "0", "0" }, "0") : "";
        if (id == "wonder_glow")
            return RgbExpr(Rgb.Select(c => GeometryByte("(" + LumetriEncoded(PreviewFloat32(Linear(c + "(X,Y)"))) + ")*255")).ToArray());
        if (id == "spin" || id == "camera_shake" || id == "wiggle")
        {
            var z = id == "spin" ? V("scale", 100) / 100 : 1;
            if (Math.Abs(z * z) < 1e-10) return RgbExpr(new[] { "0", "0", "0" }, "0");
            var cx = P("center_x", "W/2"); var cy = P("center_y", "H/2");
            return V12Warp("(" + cx + ")+(X+0.5-(" + cx + "))/" + N(z), "(" + cy + ")+(Y+0.5-(" + cy + "))/" + N(z), true);
        }
        return null;
    }
}
