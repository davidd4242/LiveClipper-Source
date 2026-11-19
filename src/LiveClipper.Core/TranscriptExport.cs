using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace LiveClipper.Core;

public sealed record TranscriptSegment(double Start,double End,string Text);
public sealed record TranscriptPreview(string Language,IReadOnlyList<TranscriptSegment> Segments,string Text);

public static class TranscriptExport
{
    public static TranscriptPreview Parse(string json)
    {
        using var document=JsonDocument.Parse(json);
        var root=document.RootElement;
        var language=root.TryGetProperty("language",out var lang)?lang.GetString()??"":"";
        var segments=root.GetProperty("segments").EnumerateArray()
            .Select(x=>new TranscriptSegment(x.GetProperty("start").GetDouble(),x.GetProperty("end").GetDouble(),x.GetProperty("text").GetString()?.Trim()??""))
            .Where(x=>x.Text.Length>0).ToArray();
        return new(language,segments,string.Join(Environment.NewLine,segments.Select(x=>x.Text)));
    }

    public static byte[] ToDocx(TranscriptPreview preview)
    {
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            static void Add(ZipArchive archive,string name,string content)
            {
                using var stream=archive.CreateEntry(name,CompressionLevel.Optimal).Open();
                using var writer=new StreamWriter(stream,new UTF8Encoding(false));
                writer.Write(content);
            }
            Add(zip,"[Content_Types].xml","""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
                """);
            Add(zip,"_rels/.rels","""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
                """);
            XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var paragraphs=preview.Segments.Select(x=>new XElement(w+"p",new XElement(w+"r",new XElement(w+"t",new XAttribute(XNamespace.Xml+"space","preserve"),x.Text))));
            var document=new XDocument(new XDeclaration("1.0","UTF-8","yes"),new XElement(w+"document",new XElement(w+"body",paragraphs,new XElement(w+"sectPr"))));
            Add(zip,"word/document.xml",document.ToString(SaveOptions.DisableFormatting));
        }
        return output.ToArray();
    }
}
