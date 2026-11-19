using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiveClipper.Core;

public sealed partial record TimelineItem
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? GraphicKind {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? TitleFont {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? GraphicBoxColor {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? GraphicBoxOpacity {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? GraphicBoxX {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? GraphicBoxY {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? GraphicBoxW {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? GraphicBoxH {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public double? GraphicLineThickness {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? PreviewOnly {get;init;}
}

public static class EditorProjectGraphics
{
    public static bool IsSourceFree(TimelineItem item)=>item.Kind=="graphic";
    private static bool HasGraphics(TimelineItem item)=>IsSourceFree(item)||item.GraphicKind is not null||item.GraphicBoxColor is not null||item.GraphicBoxOpacity is not null||item.GraphicBoxX is not null||item.GraphicBoxY is not null||item.GraphicBoxW is not null||item.GraphicBoxH is not null||item.GraphicLineThickness is not null||item.PreviewOnly is not null;
    private static string N(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
    private static void Require(bool valid,string code){if(!valid)throw new ArgumentException(code);}
    private static bool Range(double value,double min,double max)=>double.IsFinite(value)&&value>=min&&value<=max;
    private static bool Color(string? value)=>value is {Length:7}&&Regex.IsMatch(value,"^#[0-9A-Fa-f]{6}$");

    public static void Validate(TimelineItem item)
    {
        Require(item.TitleFont is null or "Microsoft YaHei" or "Segoe UI" or "Arial","invalid_title_font");
        if(!HasGraphics(item))return;
        Require(item.GraphicKind is "title" or "box" or "lowerThirdBlock" or "line" or "callout-pointer" or "end-credits","invalid_graphic_kind");
        Require(item.Track==3,"invalid_graphic_track");
        if(IsSourceFree(item))
        {
            Require(item.ClipId==""&&string.IsNullOrEmpty(item.NestedSequenceId),"invalid_graphic_source");
            Require(item.Start==0&&Range(item.End,double.Epsilon,21600)&&item.Speed==1,"invalid_graphic_timing");
        }
        var preview=item.GraphicKind is "callout-pointer" or "end-credits";
        Require((item.PreviewOnly??false)==preview,"invalid_graphic_preview_only");
        Require(!preview,"invalid_graphic_preview_only"); // Archive belongs to replicaExtension, never the render timeline.
        Require((item.TitleText?.Length??0)<=120&&!(item.TitleText?.Contains('\r')??false)&&!(item.TitleText?.Contains('\n')??false),"invalid_graphic_title_text");
        Require(Range(item.TitleSize,12,160),"invalid_graphic_title_size");
        Require(Color(item.TitleColor),"invalid_graphic_title_color");
        Require(Range(item.TitleX,0,1)&&Range(item.TitleY,0,1),"invalid_graphic_title_position");
        Require(item.GraphicBoxColor is null||Color(item.GraphicBoxColor),"invalid_graphic_box_color");
        Require(Range(item.GraphicBoxOpacity??1,0,1),"invalid_graphic_box_opacity");
        Require(Range(item.GraphicBoxX??0,0,1)&&Range(item.GraphicBoxY??0,0,1),"invalid_graphic_box_position");
        Require(Range(item.GraphicBoxW??0,0,1)&&Range(item.GraphicBoxH??0,0,1),"invalid_graphic_box_size");
        Require((item.GraphicBoxX??0)+(item.GraphicBoxW??0)<=1&&(item.GraphicBoxY??0)+(item.GraphicBoxH??0)<=1,"invalid_graphic_box_bounds");
        Require(Range(item.GraphicLineThickness??8,0,40),"invalid_graphic_line_thickness");
    }

    internal static void ValidateProject(JsonElement root)
    {
        ValidateItems(root,"timeline");ValidateItems(root,"silenceTrimBackup");
        if(Find(root,"nestedSequences",out var nested)&&nested.ValueKind==JsonValueKind.Object)
            foreach(var entry in nested.EnumerateObject())ValidateItems(entry.Value,"timeline");
    }
    private static bool Find(JsonElement owner,string name,out JsonElement value)
    {
        value=default;var found=false;
        if(owner.ValueKind!=JsonValueKind.Object)return false;
        foreach(var property in owner.EnumerateObject())if(string.Equals(property.Name,name,StringComparison.OrdinalIgnoreCase)){value=property.Value;found=true;}
        return found;
    }
    private static void ValidateItems(JsonElement owner,string name)
    {
        if(!Find(owner,name,out var items)||items.ValueKind!=JsonValueKind.Array)return;
        foreach(var raw in items.EnumerateArray())
        {
            var explicitGraphic=Find(raw,"kind",out var kind)&&kind.ValueKind==JsonValueKind.String&&kind.GetString()=="graphic";
            if(raw.ValueKind!=JsonValueKind.Object)continue;
            var fields=raw.EnumerateObject().Where(p=>p.Name.StartsWith("graphic",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("previewOnly",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(!explicitGraphic&&fields.Length==0)continue;
            try
            {
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var field in raw.EnumerateObject().Where(p=>fields.Any(f=>f.Name==p.Name)||p.Name.StartsWith("title",StringComparison.OrdinalIgnoreCase)))
                {
                    Require(seen.Add(field.Name),"invalid_graphic_duplicate_field");
                    var key=field.Name.ToLowerInvariant();
                    if(key is "graphickind" or "graphicboxcolor" or "titletext" or "titlecolor")
                        Require(field.Value.ValueKind==JsonValueKind.String,"invalid_graphic_"+key+"_type");
                    else if(key=="previewonly")Require(field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,"invalid_graphic_preview_only");
                    else if(key=="titlefont")Require(field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,"invalid_graphic_titlefont_type");
                    else
                    {
                        Require(key is "graphicboxopacity" or "graphicboxx" or "graphicboxy" or "graphicboxw" or "graphicboxh" or "graphiclinethickness" or "titlesize" or "titlex" or "titley","invalid_graphic_field");
                        Require(field.Value.ValueKind==JsonValueKind.Number&&field.Value.TryGetDouble(out var number)&&double.IsFinite(number),"invalid_graphic_"+key+"_type");
                    }
                }
                var item=raw.Deserialize<TimelineItem>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Validate(ApplyDefaults(item,raw));
            }
            catch(ArgumentException ex){throw new EditorProjectSchemaException(ex.Message,ex.Message,null,ex);}
            catch(JsonException ex){throw new EditorProjectSchemaException("invalid_graphic_field_type","invalid_graphic_field_type",null,ex);}
        }
    }
    private static TimelineItem ApplyDefaults(TimelineItem item,JsonElement raw)
    {
        if(item.GraphicKind is null)return item;
        return item with
        {
            TitleText=Find(raw,"titleText",out _)?item.TitleText:"标题",
            TitleColor=Find(raw,"titleColor",out _)?item.TitleColor:"#E6E6E6",
            TitleY=Find(raw,"titleY",out _)?item.TitleY:.5
        };
    }
    internal static EditorProject ApplyDefaults(EditorProject project,JsonElement root)
    {
        TimelineItem[]? Items(TimelineItem[]? items,JsonElement owner,string key)
        {
            if(items is null||!Find(owner,key,out var raw)||raw.ValueKind!=JsonValueKind.Array)return items;
            return items.Select((item,index)=>ApplyDefaults(item,raw[index])).ToArray();
        }
        var nested=project.NestedSequences;
        if(nested is not null&&Find(root,"nestedSequences",out var rawNested)&&rawNested.ValueKind==JsonValueKind.Object)
            nested=nested.ToDictionary(pair=>pair.Key,pair=>Find(rawNested,pair.Key,out var raw)?pair.Value with {Timeline=Items(pair.Value.Timeline,raw,"timeline")!}:pair.Value);
        return project with {Timeline=Items(project.Timeline,root,"timeline"),SilenceTrimBackup=Items(project.SilenceTrimBackup,root,"silenceTrimBackup"),NestedSequences=nested};
    }

    internal static string TitleFontFilterPath(string? font)
    {
        var file=font switch{null or "Microsoft YaHei"=>"msyh.ttc","Segoe UI"=>"segoeui.ttf","Arial"=>"arial.ttf",_=>throw new ArgumentException("invalid_title_font")};
        var dir=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if(dir.Length==0)throw new DirectoryNotFoundException("title_font_directory_missing");
        var path=Path.Combine(dir,file);
        if(!File.Exists(path))throw new FileNotFoundException("title_font_file_missing",path);
        return EditorRenderService.FilterPath(path);
    }

    /// <summary>Draw on the composed frame: untouched pixels remain transparent to the timeline underneath.</summary>
    public static string Build(TimelineItem item,int width,int height,double start,double end,double opacity=1)
    {
        Validate(item);
        Require(width>0&&height>0&&Range(start,0,21600)&&double.IsFinite(end)&&end>start&&Range(opacity,0,1),"invalid_graphic_output");
        var filters=new List<string>();
        var enable=$":enable='gte(t,{N(start)})*lt(t,{N(end)})'";
        var color=$"{item.GraphicBoxColor??"#FFFFFF"}@{N((item.GraphicBoxOpacity??1)*opacity)}";
        var x=(item.GraphicBoxX??0)*width;var y=(item.GraphicBoxY??0)*height;
        var w=(item.GraphicBoxW??0)*width;var h=(item.GraphicBoxH??0)*height;
        var thickness=Math.Max(1,Math.Round((item.GraphicLineThickness??8)*height/720,MidpointRounding.AwayFromZero));
        void Box(double bx,double by,double bw,double bh)
        {
            if(bw>0&&bh>0)filters.Add($"drawbox=x={N(bx)}:y={N(by)}:w={N(bw)}:h={N(bh)}:color={color}:t=fill{enable}");
        }
        if(item.GraphicKind=="box")
        {
            // Disjoint INSIDE rectangles avoid double alpha at the corners.
            var tx=Math.Min(thickness,w/2);var ty=Math.Min(thickness,h/2);
            Box(x,y,w,ty);Box(x,y+h-ty,w,ty);Box(x,y+ty,tx,h-2*ty);Box(x+w-tx,y+ty,tx,h-2*ty);
        }
        else if(item.GraphicKind is "lowerThirdBlock" or "line")Box(x,y,w,h); // line width is GraphicBoxW, not thickness.
        var text=item.TitleText??"标题";
        if(!string.IsNullOrWhiteSpace(text))
        {
            var size=Math.Clamp((int)Math.Round(item.TitleSize*height/720,MidpointRounding.AwayFromZero),12,200);
            var escaped=text.Replace("\\","\\\\").Replace("'","\\'").Replace(":","\\:").Replace("%","\\%");
            filters.Add($"drawtext=fontfile='{TitleFontFilterPath(item.TitleFont)}':text='{escaped}':fontcolor={item.TitleColor}@{N(opacity)}:fontsize={size}:x=w*{N(item.TitleX)}-text_w/2:y=h*{N(item.TitleY)}-text_h/2:shadowcolor=black@{N(.65*opacity)}:shadowx=2:shadowy=2{enable}");
        }
        return filters.Count==0?"null":string.Join(',',filters);
    }
}
