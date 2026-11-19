namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV10(string id) => id is "strobe" or "bokeh_blur";

    private static void ValidateV10(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("strobe", "period" or "duration") => (0d, MaxTransformCoordinate),
                ("strobe", "blend") or ("bokeh_blur", "highlight" or "threshold") => (0d, 100d),
                ("strobe", "color_r" or "color_g" or "color_b") => (0d, 1d),
                ("bokeh_blur", "amount") => (0d, 0d),
                ("bokeh_blur", "shape") => (0d, 8d),
                ("bokeh_blur", "rotation") => (-360d, 360d),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || effect.Id == "bokeh_blur" && key == "shape" && value is not (0 or 3 or 4 or 5 or 6 or 8))
                throw new ArgumentException(InvalidParam);
        }
        // Only the always-on strobe is independent of preview clip time and export PTS.
        if (effect.Id == "strobe" && effect.Params.GetValueOrDefault("duration", .1) < Math.Max(effect.Params.GetValueOrDefault("period", 1), .001)
            || effect.Id == "bokeh_blur" && effect.Params.GetValueOrDefault("amount", 10) != 0)
            throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV10(string id, Dictionary<string, double> p)
    {
        if (!IsV10(id)) return null;
        if (id == "bokeh_blur") return "";
        var blend = p.GetValueOrDefault("blend", 100) / 100;
        return RgbExpr(Rgb.Select(c =>
        {
            var color = N(p.GetValueOrDefault("color_" + c, 1));
            return GeometryByte("(" + color + "+(" + c + "(X,Y)/255-" + color + ")*" + N(blend) + ")*255");
        }).ToArray());
    }
}
