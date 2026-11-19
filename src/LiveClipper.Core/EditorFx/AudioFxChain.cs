using System.Globalization;
using System.Text.Json.Serialization;

namespace LiveClipper.Core.EditorFx;

public sealed record CatalogAudioFxInstance(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("params")] Dictionary<string, double> Params);

public static partial class AudioFxChain
{
    public const string InvalidChain = "catalog-audio-fx-invalid-chain";
    public const string InvalidVersion = "catalog-audio-fx-invalid-version";
    public const string UnsupportedTrack = "catalog-audio-fx-unsupported-track";
    public const string UnsupportedId = "catalog-audio-fx-unsupported-id";
    public const string UnsupportedParam = "catalog-audio-fx-unsupported-param";
    public const string InvalidParam = "catalog-audio-fx-invalid-param";
    public const string UnsupportedMapping = "catalog-audio-fx-unsupported-mapping";

    private readonly record struct Rule(double Default, double Min, double Max, bool Integer = false);
    private static readonly (string Prefix, double Frequency, double Q)[] EqBands =
    [ ("low", 100, .707), ("b1", 200, 1), ("b2", 500, 1), ("mid", 1000, 1),
      ("b4", 2000, 1), ("b5", 5000, 1), ("high", 8000, .707) ];
    private static readonly double[] Graphic10 = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private static readonly double[] Graphic20 = [31.5, 44, 63, 88, 125, 177, 250, 355, 500, 710, 1000, 1400, 2000, 2800, 4000, 5600, 8000, 11200, 16000, 22400];
    private static readonly double[] Graphic30 = [25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000];

    private static Dictionary<string, Rule> Rules(string id)
    {
        var rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
        void Add(string key, double fallback, double min, double max, bool integer = false) => rules.Add(key, new(fallback, min, max, integer));
        switch (id)
        {
            case "amplify": Add("gain", 0, -96, 24); break;
            case "highpass": case "lowpass": Add("cutoff", id == "highpass" ? 80 : 8000, 20, 20000); break;
            case "bass": case "treble": Add("boost", 0, -30, 30); break;
            case "bandpass": case "simple_notch": case "simple_eq":
                // Mapping setBand(undefined) restores DSP b1 defaults, not catalogue UI defaults.
                Add("center", 30, 20, 20000); Add("q", .707, .1, 30);
                if (id == "simple_eq") Add("boost", 0, -30, 30);
                break;
            case "delay":
                Add("delay", .001, .001, 2); Add("feedback", 30, 0, 95); Add("mix", 30, 0, 100); break;
            case "analog_delay":
                Add("mode", 0, 0, 2, true); Add("dry", 100, 0, 100); Add("wet", 40, 0, 100);
                Add("delay", 250, 5, 8000); Add("feedback", 40, 0, 200); Add("trash", 0, 0, 200); Add("spread", 0, 0, 200); break;
            case "notch":
                Add("width", 0, 0, 2, true);
                for (var i = 1; i <= 6; i++)
                { Add($"n{i}_on", 0, 0, 1, true); Add($"n{i}_freq", 60 * i, 20, 20000); Add($"n{i}_gain", -30, -90, 0); }
                break;
            case "parametric_eq":
                Add("master_gain", 0, -30, 30);
                Add("hp_on", 0, 0, 1, true); Add("hp_freq", 30, 10, 20000); Add("hp_slope", 0, 0, 3, true);
                foreach (var band in EqBands)
                { Add(band.Prefix + "_on", 1, 0, 1, true); Add(band.Prefix + "_freq", band.Frequency, 20, 20000); Add(band.Prefix + "_gain", 0, -30, 30); Add(band.Prefix + "_q", band.Q, .1, 30); }
                Add("lp_on", 0, 0, 1, true); Add("lp_freq", 18000, 20, 22000); Add("lp_slope", 0, 0, 3, true);
                break;
            case "graphic_eq": case "graphic_eq_20": case "graphic_eq_30":
                Add("gain", 0, -24, 24);
                for (var i = 1; i <= GraphicFrequencies(id).Length; i++) Add($"b{i}", 0, -24, 24);
                break;
            case "dynamics": case "single_band_compressor":
                Add("threshold", -20, -60, 0); Add("ratio", 4, 1, 30); Add("attack", 10, .01, 300); Add("release", 100, 1, 3000);
                if (id == "single_band_compressor") Add("output", 0, -40, 40);
                break;
            case "multiband_compressor":
                Add("xo1", 120, 20, 1000); Add("xo2", 2000, 100, 8000); Add("xo3", 10000, 1000, 20000);
                for (var i = 1; i <= 4; i++)
                {
                    Add($"b{i}_threshold", -18, -60, 0); Add($"b{i}_ratio", 3, 1, 30);
                    Add($"b{i}_attack", 10, .1, 500); Add($"b{i}_release", 100, 1, 5000); Add($"b{i}_gain", 0, -18, 18);
                    Add($"b{i}_solo", 0, 0, 1, true); Add($"b{i}_bypass", 0, 0, 1, true);
                }
                Add("output", 0, -18, 18); Add("lim_on", 0, 0, 1, true); Add("lim_threshold", -.1, -30, 0);
                Add("lim_release", 50, 1, 1000); Add("link", 1, 0, 1, true); break;
            case "dynamics_rack":
                Add("gate_on", 0, 0, 1, true); Add("gate_threshold", -50, -80, 0); Add("gate_attack", 2, .1, 100);
                Add("gate_release", 100, 1, 3000); Add("gate_hold", 50, 0, 1000);
                Add("comp_on", 1, 0, 1, true); Add("comp_threshold", -20, -60, 0); Add("comp_ratio", 2, 1, 30);
                Add("comp_attack", 10, .1, 300); Add("comp_release", 100, 1, 3000); Add("comp_auto", 0, 0, 1, true); Add("comp_makeup", 0, 0, 30);
                Add("exp_on", 0, 0, 1, true); Add("exp_threshold", -60, -80, 0); Add("exp_ratio", 2, 1, 30);
                Add("lim_on", 0, 0, 1, true); Add("lim_threshold", -1, -30, 0); Add("lim_release", 50, 1, 1000);
                Add("soft_clip", 0, 0, 1, true); Add("output", 0, -30, 30); break;
            case "tube_compressor":
                Add("threshold", -20, -60, 0); Add("ratio", 4, 1, 30); Add("attack", 10, .1, 500);
                Add("release", 100, 1, 5000); Add("output", 0, -30, 30); break;
            case "deesser":
                Add("frequency", 6000, 2000, 12000); Add("threshold", -12, -40, 0); Add("reduction", 8, 0, 24); break;
            case "distortion":
                Add("drive", 12, 0, 48); Add("curve", 0, 0, 3, true); Add("symmetry", 0, -100, 100);
                Add("tone", 20000, 500, 20000); Add("output", 0, -48, 12); Add("mix", 100, 0, 100); break;
            case "fill_left": case "fill_right": case "invert_a": case "swap_channels": break;
            case "stereo_width": Add("width", 100, 0, 200); break;
            case "stereo_expander": Add("center_pan", 0, -100, 100); Add("expand", 100, 0, 300); break;
            case "panner_ambisonics":
                Add("pan", 0, -180, 180); Add("tilt", 0, -90, 90); Add("roll", 0, -180, 180); break;
            case "channel_mixer_a":
                Add("l_from_l", 100, -100, 100); Add("l_from_r", 0, -100, 100);
                Add("r_from_l", 0, -100, 100); Add("r_from_r", 100, -100, 100);
                Add("invert_l", 0, 0, 1, true); Add("invert_r", 0, 0, 1, true); break;
            case "channel_volume_a":
                foreach (var channel in new[] { "left", "right", "center", "lfe", "left_surround", "right_surround" }) Add(channel, 0, -96, 24);
                break;
            case "loudness_radar": Add("target", -23, -36, -5); break;
            case "scientific_filter":
                Add("type", 1, 0, 3, true); Add("mode", 0, 0, 3, true); Add("order", 6, 1, 12, true);
                Add("cutoff", 1000, 20, 20000); Add("high_cutoff", 4000, 20, 20000);
                Add("ripple", 1, .01, 6); Add("stop_atten", 60, 20, 120); Add("gain", 0, -30, 30); break;
            case "dehummer":
                // Project mapping defaults differ from the catalogue's display defaults.
                Add("freq", 1, 0, 1, true); Add("gain", 0, -96, 0); Add("harmonics", 5, 1, 10, true); Add("q", 30, 1, 100); break;
            case "speech_enhance": Add("mix", 100, 0, 100); Add("tone", 0, 0, 1, true); break;
            case "mastering":
                Add("eq_low", 0, -12, 12); Add("eq_mid_freq", 1000, 100, 10000); Add("eq_mid", 0, -12, 12); Add("eq_high", 0, -12, 12);
                Add("reverb", 0, 0, 100); Add("exciter", 0, 0, 100); Add("exciter_mode", 1, 0, 2, true);
                Add("widener", 100, 0, 200); Add("loudness", 0, 0, 100); Add("output", 0, -24, 12); break;
            case "vocal_enhancer": Add("mode", 0, 0, 2, true); break;
            case "multitap_delay":
                for (var i = 1; i <= 4; i++)
                { Add($"delay{i}", i * 250, 1, 4000); Add($"feedback{i}", 0, 0, 95); Add($"level{i}", -3 * (i + 1), -96, 0); }
                Add("mix", 50, 0, 100); break;
            default: throw new ArgumentException(UnsupportedId);
        }
        return rules;
    }

    private static double[] GraphicFrequencies(string id) => id == "graphic_eq" ? Graphic10 : id == "graphic_eq_20" ? Graphic20 : Graphic30;
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static void Gain(List<string> filters, double db)
    { if (db != 0) filters.Add($"volume={N(db)}dB:precision=double"); }
    private static void Eq(List<string> filters, string filter, double frequency, double q, double gain)
    { if (gain != 0) filters.Add($"{filter}=f={N(frequency)}:t=q:w={N(q)}:g={N(gain)}:r=f64"); }

    // c=same and the mono guard preserve the preview's channel count and untouched channels above R.
    private static string Stereo(string left, string right, string mono = "val(0)")
        => $"aeval=exprs='if(lt(nb_in_channels,2),{mono},if(eq(ch,0),{left},if(eq(ch,1),{right},val(ch))))':c=same";

    private static (double Left, double Right) PannerGains(double azimuth, double pan, double tilt, double roll)
    {
        double Radians(double degrees) => degrees * Math.PI / 180;
        var x = Math.Cos(Radians(azimuth)); var y = Math.Sin(Radians(azimuth));
        var z = Math.Sin(Radians(roll)) * y; y = Math.Cos(Radians(roll)) * y;
        var rotatedX = Math.Cos(Radians(tilt)) * x + Math.Sin(Radians(tilt)) * z;
        z = -Math.Sin(Radians(tilt)) * x + Math.Cos(Radians(tilt)) * z; x = rotatedX;
        rotatedX = Math.Cos(Radians(pan)) * x - Math.Sin(Radians(pan)) * y;
        y = Math.Sin(Radians(pan)) * x + Math.Cos(Radians(pan)) * y; x = rotatedX;
        var position = Math.Clamp(y / Math.Sin(Radians(30)), -1, 1);
        var angle = (1 - position) * .5 * (Math.PI / 2);
        var attenuation = (1 - .3 * Math.Max(-x, 0)) * (1 - .2 * Math.Abs(z));
        double Snap(double value) => value > -1e-6 && value < 1e-6 ? 0 : value > 1 - 1e-6 && value < 1 + 1e-6 ? 1 : value;
        return (Snap(Math.Cos(angle) * attenuation), Snap(Math.Sin(angle) * attenuation));
    }

    private static double Coef(double ms) => Math.Exp(-1 / (ms * .001 * 48000));
    private static string Flush(string value, double epsilon)
        => $"if(lt(abs({value}),{N(epsilon)}),0,{value})";
    private static string Smooth(int state, string target, double attack, string release, double epsilon = 1e-7)
    {
        var coefficient = $"if(lt({target},ld({state})),{N(attack)},{release})";
        return $"st({state},{coefficient}*ld({state})+(1-{coefficient})*({target}));st({state},{Flush($"ld({state})", epsilon)})";
    }
    private static string Curve(string level, double threshold, double ratio, double knee)
    {
        var delta = $"({level}-({N(threshold)}))";
        var hard = $"if(gt({delta},0),({N(threshold)}+{delta}/{N(ratio)})-({level}),0)";
        return knee == 0 ? hard : $"if(lte(2*abs({delta}),{N(knee)}),(1/{N(ratio)}-1)*pow({delta}+{N(knee / 2)},2)/{N(2 * knee)},{hard})";
    }
    private const string LinkedPeak = "st(8,0);st(9,0);while(lt(ld(9),nb_in_channels),st(8,max(ld(8),abs(val(ld(9)))));st(9,ld(9)+1));";
    private const string PeakDb = "20*log(max(ld(8),1e-10))/log(10)";

    private static string StatefulDynamics(string id, Func<string, double> p)
    {
        // aeval clones registers per channel; each clone computes the same linked detector.
        var expression = LinkedPeak;
        string output;
        if (id == "dynamics_rack")
        {
            expression += $"st(5,{PeakDb});st(6,0);";
            if (p("gate_on") == 1)
            {
                var open = Smooth(0, "0", Coef(p("gate_release")), N(Coef(p("gate_attack"))));
                var close = Smooth(0, "-80", Coef(p("gate_release")), N(Coef(p("gate_release"))));
                expression += $"if(gte(ld(5),{N(p("gate_threshold"))}),st(1,{N(Math.Floor(p("gate_hold") * .001 * 48000))});{open},if(gt(ld(1),0),st(1,ld(1)-1),{close}));st(6,ld(6)+ld(0));st(5,ld(5)+ld(0));";
            }
            if (p("exp_on") == 1)
            {
                expression += $"st(7,if(lt(ld(5),{N(p("exp_threshold"))}),max((ld(5)-({N(p("exp_threshold"))}))*{N(p("exp_ratio") - 1)},-60),0));";
                expression += Smooth(2, "ld(7)", Coef(1), N(Coef(80))) + ";st(6,ld(6)+ld(2));st(5,ld(5)+ld(2));";
            }
            if (p("comp_on") == 1)
            {
                expression += $"st(7,{Curve("ld(5)", p("comp_threshold"), p("comp_ratio"), 0)});";
                expression += Smooth(3, "ld(7)", Coef(p("comp_attack")), N(Coef(p("comp_release")))) + ";st(6,ld(6)+ld(3));st(5,ld(5)+ld(3));";
            }
            if (p("lim_on") == 1)
            {
                expression += $"st(7,min({N(p("lim_threshold"))}-ld(5),0));";
                expression += $"if(lt(ld(7),ld(4)),st(4,ld(7)),{Smooth(4, "ld(7)", 0, N(Coef(p("lim_release"))))});st(6,ld(6)+ld(4));";
            }
            output = "val(ch)*pow(10,ld(6)/20)";
            if (p("lim_on") == 1)
            {
                var ceiling = Math.Pow(10, p("lim_threshold") / 20);
                expression += $"st(7,{output});";
                if (p("soft_clip") == 1)
                {
                    expression += $"st(8,min((abs(ld(7))-{N(ceiling / 2)})/{N(ceiling)},1));";
                    output = $"if(lte(abs(ld(7)),{N(ceiling / 2)}),ld(7),sgn(ld(7))*({N(ceiling / 2)}+{N(ceiling / 2)}*(2*ld(8)-ld(8)*ld(8))))";
                }
                else output = $"clip(ld(7),{N(-ceiling)},{N(ceiling)})";
            }
            var makeup = p("comp_on") == 0 ? 0 : p("comp_makeup") + (p("comp_auto") == 1 ? -.5 * p("comp_threshold") * (1 - 1 / p("comp_ratio")) : 0);
            output = $"({output})*{N(Math.Pow(10, (p("output") + makeup) / 20))}";
        }
        else
        {
            var tube = id == "tube_compressor";
            if (tube)
            {
                var rms = Coef(5); var slow = Coef(1500);
                expression += $"st(4,{N(rms)}*ld(4)+{N(1 - rms)}*ld(8)*ld(8));st(4,{Flush("ld(4)", 1e-30)});st(2,10*log(max(ld(4),1e-20))/log(10));";
                expression += $"st(5,{N(slow)}*ld(5)+{N(1 - slow)}*ld(0));st(5,{Flush("ld(5)", 1e-7)});";
            }
            else expression += $"st(2,{PeakDb});";
            expression += $"st(3,{Curve("ld(2)", p("threshold"), p("ratio"), tube ? 10 : id == "dynamics" ? 6 : 0)});";
            var release = tube ? $"min({N(Coef(p("release")))}+clip(-ld(5)/20,0,1)*{N((1 - Coef(p("release"))) * .5)},0.999999)" : N(Coef(p("release")));
            expression += Smooth(0, "ld(3)", Coef(p("attack")), release, tube ? 1e-7 : 1e-9) + ";";
            var makeup = id == "dynamics" ? 1 : Math.Pow(10, p("output") / 20);
            output = tube ? $"2*tanh(0.5*val(ch)*pow(10,ld(0)/20))*{N(makeup)}" : $"val(ch)*pow(10,ld(0)/20)*{N(makeup)}";
        }
        // Every preview effect writes Float32, including high makeup gains and effect chains.
        return $"aformat=sample_fmts=flt,aeval=exprs='{expression}{output}':c=same,aformat=sample_fmts=flt";
    }

    private static string DistortionShape(double curve, string x)
    {
        if (curve == 0) return $"tanh({x})";
        if (curve == 1) return $"clip({x},-1,1)";
        if (curve == 2) return $"if(gte({x},0),tanh({x}),tanh(1.6*({x}))/1.6)";
        return $"sin({x})";
    }
    private static string StatefulDistortion(Func<string, double> p)
    {
        var bias = N(.5 * p("symmetry") / 100);
        var shaped = DistortionShape(p("curve"), $"({N(Math.Pow(10, p("drive") / 20))}*val(ch)+({bias}))");
        var expression = $"st(3,{shaped}-({DistortionShape(p("curve"), bias)}));st(4,ld(3)-ld(0)+{N(1 - 2 * Math.PI * 20 / 48000)}*ld(1));st(0,ld(3));st(1,{Flush("ld(4)", 1e-30)});";
        if (p("tone") < 19999)
        {
            expression += $"st(2,ld(2)+{N(1 - Math.Exp(-2 * Math.PI * p("tone") / 48000))}*(ld(4)-ld(2)));st(2,{Flush("ld(2)", 1e-30)});st(4,ld(2));";
        }
        return $"aformat=sample_fmts=flt,aeval=exprs='{expression}val(ch)+{N(p("mix") / 100)}*(ld(4)*{N(Math.Pow(10, p("output") / 20))}-val(ch))':c=same,aformat=sample_fmts=flt";
    }

    private static void ScientificButterworth(List<string> filters, int order, int mode, double cutoff, double gain)
    {
        // Preview snaps log2(cutoff), then warps the reconstructed frequency.
        var hz = Math.Pow(2, Math.Log(cutoff) / Math.Log(2));
        if (order % 2 != 0)
        {
            var k = Math.Tan(Math.PI * hz / 48000);
            var b0 = (mode == 0 ? k : 1) / (1 + k);
            filters.Add($"biquad=b0={N(b0)}:b1={N(mode == 0 ? b0 : -b0)}:b2=0:a0=1:a1={N((k - 1) / (k + 1))}:a2=0:r=f64");
        }
        for (var i = 0; i < order / 2; i++)
        {
            var q = 1 / (2 * Math.Sin(Math.PI * (2 * i + 1) / (2 * order)));
            filters.Add($"{(mode == 0 ? "lowpass" : "highpass")}=f={N(hz)}:p=2:t=q:w={N(q)}:r=f64");
        }
        Gain(filters, gain);
    }

    public static string Build(IReadOnlyList<CatalogAudioFxInstance> effects)
    {
        if (effects is null) throw new ArgumentException(InvalidChain);
        var filters = new List<string>();
        var tapIndex = 0;
        foreach (var effect in effects)
        {
            if (effect is null || effect.Params is null) throw new ArgumentException(InvalidChain);
            var rules = Rules(effect.Id);
            foreach (var (key, value) in effect.Params)
            {
                if (!rules.TryGetValue(key, out var rule)) throw new ArgumentException(UnsupportedParam);
                if (!double.IsFinite(value) || value < rule.Min || value > rule.Max || rule.Integer && value != Math.Truncate(value))
                    throw new ArgumentException(InvalidParam);
            }
            double P(string key) => effect.Params.GetValueOrDefault(key, rules[key].Default);
            // Finite aecho cannot represent recursive feedback or fractional interpolated taps.
            var delay = effect.Id == "delay" ? P("delay") * 48000 : effect.Id == "analog_delay" ? P("delay") * 48 : 0;
            var wet = effect.Id == "delay" ? P("mix") / 100 : effect.Id == "analog_delay" ? P("wet") / 100 : 0;
            if (wet != 0 && (P("feedback") != 0 || effect.Id == "analog_delay" && P("spread") != 0 || Math.Abs(delay - Math.Round(delay)) > 1e-7))
                throw new ArgumentException(UnsupportedMapping);
            // The adaptive deesser shelf is not a static filter; only its zero-reduction subset is exact.
            if (effect.Id == "deesser" && P("reduction") != 0) throw new ArgumentException(UnsupportedMapping);
            if (effect.Id == "mastering" && P("reverb") != 0) throw new ArgumentException(UnsupportedMapping);
            // High-order low-cutoff preview SOS flushes its first-stage state; normalized aliases diverge.
            if (effect.Id == "scientific_filter" && (P("type") != 1 || P("mode") > 1 || P("order") > 6)
                || effect.Id == "vocal_enhancer" && P("mode") != 2)
                throw new ArgumentException(UnsupportedMapping);
            if (effect.Id == "multitap_delay" && P("mix") != 0)
                for (var i = 1; i <= 4; i++)
                {
                    // The preview's tap scratch buffers round sample positions and levels to Float32.
                    var samples = (double)(float)(P($"delay{i}") * .001 * 48000);
                    if (P($"feedback{i}") != 0 || samples != Math.Truncate(samples)) throw new ArgumentException(UnsupportedMapping);
                }
            // Validate before skipping, including unsupported semantic subsets on disabled DTOs.
            if (!effect.Enabled) continue;
            switch (effect.Id)
            {
                case "amplify":
                    if (P("gain") == -96) filters.Add("volume=0:precision=double");
                    else Gain(filters, P("gain"));
                    break;
                case "highpass": case "lowpass":
                    filters.Add($"{effect.Id}=f={N(P("cutoff"))}:p=2:t=q:w=0.707:r=f64"); break;
                case "bass": case "treble":
                    if (P("boost") != 0) filters.Add($"{(effect.Id == "bass" ? "lowshelf" : "highshelf")}=g={N(P("boost"))}:f={(effect.Id == "bass" ? 200 : 4000)}:p=2:t=q:w=0.707:r=f64");
                    break;
                case "deesser": break;
                case "speech_enhance":
                    if (P("mix") != 0) filters.Add(A7Speech(P, $"a7s{tapIndex++}")); break;
                case "mastering": filters.Add(A7Mastering(P)); break;
                case "multiband_compressor": filters.Add(A8Multiband(P, $"a8m{tapIndex++}")); break;
                case "dynamics": case "single_band_compressor": case "dynamics_rack": case "tube_compressor":
                    filters.Add(StatefulDynamics(effect.Id, P)); break;
                case "distortion":
                    filters.Add(StatefulDistortion(P)); break;
                case "simple_eq": Eq(filters, "equalizer", P("center"), P("q"), P("boost")); break;
                case "simple_notch": case "bandpass":
                    filters.Add($"{(effect.Id == "simple_notch" ? "bandreject" : "bandpass")}=f={N(P("center"))}:t=q:w={N(P("q"))}:r=f64"); break;
                case "delay": case "analog_delay":
                    var dry = effect.Id == "delay" ? 1 - wet : P("dry") / 100;
                    if (wet == 0)
                    { if (dry != 1) filters.Add($"volume={N(dry)}:precision=double"); }
                    else if (dry == 0)
                    {
                        filters.Add($"adelay={N(Math.Round(delay))}S:all=1");
                        if (wet != 1) filters.Add($"volume={N(wet)}:precision=double");
                    }
                    else
                    {
                        // aecho clips to [-1,1] and truncates float millisecond taps. Keep headroom and sample-exact timing.
                        var tag = "catalogAudioTap" + tapIndex++;
                        filters.Add($"aformat=sample_fmts=dbl,asplit=2[{tag}d][{tag}w];[{tag}d]volume={N(dry)}:precision=double[{tag}dv];[{tag}w]adelay={N(Math.Round(delay))}S:all=1,volume={N(wet)}:precision=double[{tag}wv];[{tag}dv][{tag}wv]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0");
                    }
                    break;
                case "scientific_filter":
                    ScientificButterworth(filters, (int)P("order"), (int)P("mode"), P("cutoff"), P("gain")); break;
                case "vocal_enhancer":
                    filters.Add("highpass=f=40:p=2:t=q:w=0.707:r=f64");
                    Eq(filters, "equalizer", 1500, .7, -4); Eq(filters, "equalizer", 3500, 1, -2); break;
                case "dehummer":
                    if (P("gain") != 0)
                    {
                        var residual = Math.Pow(10, P("gain") / 20);
                        var amount = 1 - residual;
                        var hum = new List<string>();
                        for (var i = 1; i <= (int)P("harmonics"); i++)
                            hum.Add($"bandreject=f={N((P("freq") == 1 ? 60 : 50) * i)}:t=q:w={N(P("q"))}:r=f64");
                        // Amount belongs after the whole notch cascade, not to each individual notch.
                        var humTag = "catalogAudioHum" + tapIndex++;
                        filters.Add($"aformat=sample_fmts=dbl,asplit=2[{humTag}d][{humTag}w];[{humTag}d]volume={N(residual)}:precision=double[{humTag}dv];[{humTag}w]{string.Join(',', hum)},volume={N(amount)}:precision=double[{humTag}wv];[{humTag}dv][{humTag}wv]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0");
                    }
                    break;
                case "multitap_delay":
                    var multiMix = P("mix") / 100;
                    if (multiMix != 0)
                    {
                        var multiTag = "catalogAudioMulti" + tapIndex++;
                        var outputs = string.Concat(Enumerable.Range(0, 5).Select(i => $"[{multiTag}t{i}]"));
                        var branches = new List<string> { $"[{multiTag}t0]volume={N(1 - multiMix)}:precision=double[{multiTag}v0]" };
                        for (var i = 1; i <= 4; i++)
                        {
                            var samples = (double)(float)(P($"delay{i}") * .001 * 48000);
                            var level = P($"level{i}");
                            var tapGain = level <= -96 ? 0 : (double)(float)Math.Pow(10, level / 20);
                            branches.Add($"[{multiTag}t{i}]adelay={N(samples)}S:all=1,volume={N(multiMix * tapGain)}:precision=double[{multiTag}v{i}]");
                        }
                        var inputs = string.Concat(Enumerable.Range(0, 5).Select(i => $"[{multiTag}v{i}]"));
                        filters.Add($"aformat=sample_fmts=dbl,asplit=5{outputs};{string.Join(';', branches)};{inputs}amix=inputs=5:duration=longest:dropout_transition=0:normalize=0");
                    }
                    break;
                case "notch":
                    var q = new[] { 8d, 20d, 50d }[(int)P("width")];
                    for (var i = 1; i <= 6; i++)
                        if (P($"n{i}_on") == 1 && P($"n{i}_gain") != 0)
                            // Cut numerator = (1-g)*notch + g*identity, not an infinite notch.
                            filters.Add($"bandreject=f={N(P($"n{i}_freq"))}:t=q:w={N(q)}:m={N(1 - Math.Pow(10, P($"n{i}_gain") / 20))}:r=f64");
                    break;
                case "parametric_eq":
                    void Edge(string edge, string filter)
                    {
                        if (P(edge + "_on") == 0) return;
                        var count = (int)P(edge + "_slope") + 1;
                        for (var k = 0; k < count; k++)
                            filters.Add($"{filter}=f={N(P(edge + "_freq"))}:p=2:t=q:w={N(1 / (2 * Math.Cos(Math.PI * (2 * k + 1) / (4 * count))))}:r=f64");
                    }
                    Edge("hp", "highpass");
                    foreach (var band in EqBands)
                        if (P(band.Prefix + "_on") == 1)
                            Eq(filters, band.Prefix == "low" ? "lowshelf" : band.Prefix == "high" ? "highshelf" : "equalizer", P(band.Prefix + "_freq"), P(band.Prefix + "_q"), P(band.Prefix + "_gain"));
                    Edge("lp", "lowpass"); Gain(filters, P("master_gain")); break;
                case "graphic_eq": case "graphic_eq_20": case "graphic_eq_30":
                    var frequencies = GraphicFrequencies(effect.Id);
                    var ratio = Math.Pow(2, frequencies.Length == 10 ? 1 : frequencies.Length == 20 ? .5 : 1d / 3);
                    for (var i = 0; i < frequencies.Length; i++) Eq(filters, "equalizer", frequencies[i], Math.Sqrt(ratio) / (ratio - 1), P($"b{i + 1}"));
                    Gain(filters, P("gain")); break;
                case "fill_left": filters.Add(Stereo("val(1)", "val(1)")); break;
                case "fill_right": filters.Add(Stereo("val(0)", "val(0)")); break;
                case "swap_channels": filters.Add(Stereo("val(1)", "val(0)")); break;
                case "invert_a": filters.Add("aeval=exprs='-val(ch)':c=same"); break;
                case "stereo_width": case "stereo_expander":
                    var width = P(effect.Id == "stereo_width" ? "width" : "expand") / 100;
                    var pan = effect.Id == "stereo_expander" ? P("center_pan") / 100 : 0;
                    var mid = "((val(0)+val(1))*0.5)";
                    var side = $"((val(0)-val(1))*0.5*{N(width)})";
                    filters.Add(Stereo($"{mid}*{N(Math.Min(1 - pan, 1))}+{side}", $"{mid}*{N(Math.Min(1 + pan, 1))}-{side}")); break;
                case "channel_mixer_a":
                    var ll = P("l_from_l") / 100 * (P("invert_l") == 1 ? -1 : 1);
                    var lr = P("l_from_r") / 100 * (P("invert_l") == 1 ? -1 : 1);
                    var rl = P("r_from_l") / 100 * (P("invert_r") == 1 ? -1 : 1);
                    var rr = P("r_from_r") / 100 * (P("invert_r") == 1 ? -1 : 1);
                    filters.Add(Stereo($"{N(ll)}*val(0)+{N(lr)}*val(1)", $"{N(rl)}*val(0)+{N(rr)}*val(1)", $"val(0)*{N(ll + lr)}")); break;
                case "channel_volume_a":
                    var expression = "val(ch)";
                    var channels = new[] { "left", "right", "center", "lfe", "left_surround", "right_surround" };
                    for (var i = channels.Length - 1; i >= 0; i--)
                    {
                        var db = P(channels[i]); var gain = db == -96 ? 0 : Math.Pow(10, db / 20);
                        expression = $"if(eq(ch,{i}),val(ch)*{N(gain)},{expression})";
                    }
                    filters.Add($"aeval=exprs='{expression}':c=same"); break;
                case "panner_ambisonics":
                    var fromLeft = PannerGains(30, P("pan"), P("tilt"), P("roll"));
                    var fromRight = PannerGains(-30, P("pan"), P("tilt"), P("roll"));
                    filters.Add(Stereo($"{N(fromLeft.Left)}*val(0)+{N(fromRight.Left)}*val(1)", $"{N(fromLeft.Right)}*val(0)+{N(fromRight.Right)}*val(1)")); break;
                case "loudness_radar": break; // The preview meter reads PCM without changing it; no normalization or measurement metadata.
            }
        }
        // Static 48 kHz filters omit preview ramps/live state and f32 sample rounding.
        // No denoise admission: preview's minimum-statistics binary spectral gate is not afftdn.
        return string.Join(',', filters);
    }
}
