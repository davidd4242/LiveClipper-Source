namespace LiveClipper.Core.EditorFx;

public static partial class VideoFxChain
{
    private static bool IsV8(string id) => id is "four_color_gradient" or "gradient" or "rgb_split" or "clone" or "lens_flare";

    private static void ValidateV8(CatalogFxInstance effect)
    {
        foreach (var (key, value) in effect.Params)
        {
            var bounds = (effect.Id, key) switch
            {
                ("four_color_gradient", "opacity" or "blend") => (0d, 100d),
                ("four_color_gradient", "c1_r" or "c1_g" or "c1_b" or "c2_r" or "c2_g" or "c2_b" or "c3_r" or "c3_g" or "c3_b" or "c4_r" or "c4_g" or "c4_b") => (0d, 1d),
                ("gradient", "start_r" or "start_g" or "start_b" or "end_r" or "end_g" or "end_b") => (0d, 1d),
                ("gradient", "start_x" or "start_y" or "end_x" or "end_y")
                    or ("rgb_split" or "lens_flare", "center_x" or "center_y") => (-MaxTransformCoordinate, MaxTransformCoordinate),
                ("gradient", "shape") or ("rgb_split", "mode") or ("clone", "mirror") => (0d, 1d),
                ("gradient", "scatter") => (0d, 0d),
                ("gradient", "midpoint") or ("lens_flare", "blend") => (0d, 100d),
                ("rgb_split", "amount") => (-1024d, 1024d),
                ("rgb_split", "angle") => (-360d, 360d),
                ("clone", "columns" or "rows") => (1d, 128d),
                ("clone", "gap") => (0d, 4096d),
                ("lens_flare", "brightness") => (0d, 300d),
                _ => throw new ArgumentException(UnsupportedParam),
            };
            if (!double.IsFinite(value) || value < bounds.Item1 || value > bounds.Item2
                || key is "shape" or "mode" or "mirror" && value != Math.Truncate(value))
                throw new ArgumentException(InvalidParam);
        }
        foreach (var key in effect.Id == "gradient" ? new[] { "start", "end" } : new[] { "center" })
            if (effect.Params.ContainsKey(key + "_x") != effect.Params.ContainsKey(key + "_y"))
                throw new ArgumentException(InvalidParam);
    }

    private static string? BuildV8(string id, Dictionary<string, double> p)
    {
        double V(string key, double fallback) => p.GetValueOrDefault(key, fallback);
        string P(string key, string fallback) => p.ContainsKey(key) ? N(p[key]) : fallback;
        if (id == "four_color_gradient")
        {
            var init = "";
            for (var i = 0; i < 4; i++)
                init += "st(" + i + ",1/pow(max(hypot(X+0.5-W*" + N(i % 2 == 0 ? .25 : .75)
                    + ",Y+0.5-H*" + N(i < 2 ? .25 : .75) + "),1)," + N(1 + V("blend", 100) / 100) + "));";
            init += "st(4,ld(0)+ld(1)+ld(2)+ld(3));";
            var opacity = V("opacity", 100) / 100;
            var channels = new List<string>();
            foreach (var c in Rgb)
            {
                var sum = "";
                for (var i = 0; i < 4; i++)
                    sum += (i == 0 ? "" : "+") + N(V("c" + (i + 1) + "_" + c, i % 2 == 0 ? 1 : 0)) + "*ld(" + i + ")";
                channels.Add(init + GeometryByte("((" + sum + ")/ld(4)*" + N(opacity) + "+" + c + "(X,Y)/255*" + N(1 - opacity) + ")*255"));
            }
            return RgbExpr(channels, GeometryByte(N(opacity * 255) + "+alpha(X,Y)*" + N(1 - opacity)));
        }
        if (id == "gradient")
        {
            var sx = P("start_x", "W*0.25"); var sy = P("start_y", "H*0.5");
            var ex = P("end_x", "W*0.75"); var ey = P("end_y", "H*0.5");
            var init = "st(0,(" + ex + ")-(" + sx + "));st(1,(" + ey + ")-(" + sy + "));st(2,max(ld(0)*ld(0)+ld(1)*ld(1),0.000001));";
            var t = V("shape", 0) == 1 ? "hypot(X+0.5-(" + sx + "),Y+0.5-(" + sy + "))/sqrt(ld(2))"
                : "((X+0.5-(" + sx + "))*ld(0)+(Y+0.5-(" + sy + "))*ld(1))/ld(2)";
            var midpoint = Math.Max(.001, Math.Min(.999, V("midpoint", 50) / 100));
            init += "st(3,clip(" + t + ",0,1));st(4,if(lt(ld(3)," + N(midpoint) + "),ld(3)/" + N(midpoint)
                + "*0.5,0.5+(ld(3)-" + N(midpoint) + ")/" + N(1 - midpoint) + "*0.5));";
            return RgbExpr(Rgb.Select(c => init + GeometryByte("(" + N(V("start_" + c, 0)) + "+" + N(V("end_" + c, 1) - V("start_" + c, 0)) + "*ld(4))*255")).ToArray(), "255");
        }
        if (id == "rgb_split")
        {
            var angle = V("angle", 0) * Math.PI / 180; var amount = V("amount", 10);
            var cos = N(Math.Cos(angle)); var sin = N(Math.Sin(angle));
            var rx = "(X+0.5-(" + P("center_x", "W/2") + "))/max(1,min(W,H)/2)";
            var ry = "(Y+0.5-(" + P("center_y", "H/2") + "))/max(1,min(W,H)/2)";
            var dx = V("mode", 0) == 1 ? N(amount) + "*((" + rx + ")*" + cos + "-(" + ry + ")*" + sin + ")" : N(amount * Math.Cos(angle));
            var dy = V("mode", 0) == 1 ? N(amount) + "*((" + rx + ")*" + sin + "+(" + ry + ")*" + cos + ")" : N(amount * Math.Sin(angle));
            var red = V8Sample("r", "X+(" + dx + ")", "Y+(" + dy + ")");
            var blue = V8Sample("b", "X-(" + dx + ")", "Y-(" + dy + ")");
            var ra = V8Sample("alpha", "X+(" + dx + ")", "Y+(" + dy + ")");
            var ba = V8Sample("alpha", "X-(" + dx + ")", "Y-(" + dy + ")");
            var init = "st(5," + ra + ");st(6," + ba + ");st(7,max(max(ld(5),ld(6)),alpha(X,Y)));";
            return RgbExpr(new[] {
                init + GeometryByte("if(gt(ld(7),0),(" + red + ")*ld(5)/ld(7),0)"),
                init + GeometryByte("if(gt(ld(7),0),g(X,Y)*alpha(X,Y)/ld(7),0)"),
                init + GeometryByte("if(gt(ld(7),0),(" + blue + ")*ld(6)/ld(7),0)") }, init + GeometryByte("ld(7)"));
        }
        if (id == "clone")
        {
            var cols = Math.Floor(V("columns", 2) + .5); var rows = Math.Floor(V("rows", 1) + .5);
            var gap = N(V("gap", 0)); var mirror = V("mirror", 0) == 1;
            var cw = "W/" + N(cols); var ch = "H/" + N(rows);
            var u = "mod(X+0.5," + cw + ")-" + gap + "/2"; var v = "mod(Y+0.5," + ch + ")-" + gap + "/2";
            var guard = "gt(" + cw + "," + gap + ")*gt(" + ch + "," + gap + ")*gte((" + u + "),0)*gte((" + v + "),0)*lt((" + u + ")," + cw + "-" + gap + ")*lt((" + v + ")," + ch + "-" + gap + ")";
            var x = "(" + u + ")/(" + cw + "-" + gap + ")"; var y = "(" + v + ")/(" + ch + "-" + gap + ")";
            if (mirror) { x = "if(mod(floor((X+0.5)/(" + cw + ")),2),1-(" + x + ")," + x + ")"; y = "if(mod(floor((Y+0.5)/(" + ch + ")),2),1-(" + y + ")," + y + ")"; }
            return V8Geometry("if(" + guard + ",(" + x + ")*W-0.5,-100000000)", "if(" + guard + ",(" + y + ")*H-0.5,-100000000)");
        }
        if (id == "lens_flare")
        {
            var cx = P("center_x", "W/2"); var cy = P("center_y", "H/2");
            var gain = V("brightness", 100) / 100 * (1 - V("blend", 0) / 100);
            var init = "st(0,(X+0.5-(" + cx + "))/max(W,H));st(1,(Y+0.5-(" + cy + "))/max(W,H));st(2,ld(0)*ld(0)+ld(1)*ld(1));st(3,sqrt(ld(2)));";
            var core = "0.001/(ld(2)+0.0002)"; var glow = "0.16*exp(-ld(3)*12)";
            var halo = "exp(-pow((ld(3)-0.12)/0.012,2))*0.12";
            var streak = "(0.45*exp(-abs(ld(1))*1400)/(1+ld(0)*ld(0)*140)+0.13*exp(-abs(ld(0))*1200)/(1+ld(1)*ld(1)*200))";
            var energies = new[] { core + "+" + glow + "+" + halo + "+" + streak,
                core + "*0.9+" + glow + "*0.75+" + halo + "*0.55+" + streak + "*0.7",
                core + "*0.65+" + glow + "*0.5+" + halo + "*0.25+" + streak + "*0.45" };
            var ghosts = new[] { new[] { -.8, .038, .11, .7, .85, 1 }, new[] { -.3, .065, .08, .3, 1, .65 },
                new[] { .4, .023, .15, 1, .45, .2 }, new[] { .85, .095, .09, .5, .65, 1 }, new[] { 1.3, .045, .12, 1, .5, .25 } };
            foreach (var ghost in ghosts)
            {
                var distance = "hypot((X+0.5-W/2-(W/2-(" + cx + "))*" + N(ghost[0]) + ")/max(W,H),(Y+0.5-H/2-(H/2-(" + cy + "))*" + N(ghost[0]) + ")/max(W,H))";
                var energy = N(ghost[2]) + "*(pow(max(0,1-(" + distance + ")/" + N(ghost[1]) + "),2)+max(0,1-abs((" + distance + ")-" + N(ghost[1]) + ")/0.004)*0.7)";
                for (var k = 0; k < 3; k++) energies[k] += "+(" + energy + ")*" + N(ghost[k + 3]);
            }
            var channels = new List<string>();
            for (var k = 0; k < 3; k++) channels.Add(init + Encoded(Linear(Rgb[k] + "(X,Y)") + "+(" + energies[k] + ")*" + N(gain)));
            var alpha = init + GeometryByte("alpha(X,Y)+clip(max(max(" + energies[0] + "," + energies[1] + ")," + energies[2] + ")*" + N(gain) + ",0,1)*(255-alpha(X,Y))");
            return RgbExpr(channels, alpha);
        }
        return null;
    }

    private static string V8Sample(string channel, string x, string y)
    {
        var init = "st(0," + x + ");st(1," + y + ");st(2,floor(ld(0)));st(3,floor(ld(1)));st(4,ld(0)-ld(2));st(9,ld(1)-ld(3));";
        string Tap(string tx, string ty) => "if(between(" + tx + ",0,W-1)*between(" + ty + ",0,H-1)," + channel + "(" + tx + "," + ty + "),0)";
        var a = Tap("ld(2)", "ld(3)"); var b = Tap("ld(2)+1", "ld(3)");
        var c = Tap("ld(2)", "ld(3)+1"); var d = Tap("ld(2)+1", "ld(3)+1");
        var top = "(" + a + "+(" + b + "-" + a + ")*ld(4))"; var bottom = "(" + c + "+(" + d + "-" + c + ")*ld(4))";
        return "(" + init + top + "+(" + bottom + "-" + top + ")*ld(9))";
    }

    private static string V8Geometry(string x, string y)
    {
        var init = "st(0," + x + ");st(1," + y + ");st(2,floor(ld(0)));st(3,floor(ld(1)));st(4,ld(0)-ld(2));st(5,ld(1)-ld(3));";
        var xs = new[] { "ld(2)", "ld(2)+1", "ld(2)", "ld(2)+1" };
        var ys = new[] { "ld(3)", "ld(3)", "ld(3)+1", "ld(3)+1" };
        var weights = new[] { "(1-ld(4))*(1-ld(5))", "ld(4)*(1-ld(5))", "(1-ld(4))*ld(5)", "ld(4)*ld(5)" };
        var channels = new List<string>(); var sums = new List<string>();
        foreach (var channel in new[] { "r", "g", "b", "alpha" })
        {
            var terms = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var guard = "between(" + xs[i] + ",0,W-1)*between(" + ys[i] + ",0,H-1)";
                var alpha = "if(" + guard + ",alpha(" + xs[i] + "," + ys[i] + "),0)";
                var sample = "if(" + guard + "," + channel + "(" + xs[i] + "," + ys[i] + "),0)";
                var value = channel == "alpha" ? alpha : GeometryByte(Linear(sample) + "*(" + alpha + ")");
                terms.Add("(" + value + ")*(" + weights[i] + ")");
            }
            sums.Add(string.Join('+', terms));
        }
        for (var i = 0; i < 3; i++) channels.Add(init + "st(6," + sums[3] + ");" + Encoded("if(gt(ld(6),0.00000001),(" + sums[i] + ")/ld(6),0)"));
        return RgbExpr(channels, init + GeometryByte(sums[3]));
    }
}
