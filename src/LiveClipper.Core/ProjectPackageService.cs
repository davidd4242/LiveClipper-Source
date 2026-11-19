using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace LiveClipper.Core;

public sealed record ProjectPackageExport(string Token,string FileName);
public sealed record ProjectPackageFile(string Path,long Bytes,string Sha256);
public sealed record ProjectPackageManifest(string Format,int Version,string Name,IReadOnlyList<ProjectPackageFile> Files);
internal sealed record PackageAsset(string Id,string Name,string Kind,string Style,string Path,string[][] Documents);
internal sealed record PackageAssociations(StudioStore.CopySnapshot Snapshot,PackageAsset[] Assets);

/// <summary>Portable .lcproj ZIP. Media is streamed and every declared member is verified before publication.</summary>
public sealed class ProjectPackageService(StudioStore studio,string packageRoot)
{
    public const long MaximumBytes=16L*1024*1024*1024;
    private const int MetadataLimit=64*1024*1024;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true};
    public string? DownloadPath(string token)
    {
        if(!Guid.TryParseExact(token,"N",out _))return null;
        var path=Path.Combine(packageRoot,"downloads",token+".lcproj");return File.Exists(path)?path:null;
    }
    public string DownloadFileName(string token)
    {
        var path=DownloadPath(token)??throw new FileNotFoundException("工程包不存在");
        return File.Exists(path+".name")?File.ReadAllText(path+".name"):"LiveClipper-"+token[..8]+".lcproj";
    }
    public async Task<ProjectPackageExport> ExportAsync(string sourceId,string name,EditorProject project,long expectedRevision,CancellationToken token=default)
    {
        name=name.Trim();if(name.Length is 0 or >100)throw new ArgumentException("工程包名称须为 1–100 个字符");
        ProjectCopyService.ValidateCopyProject(project,studio.GetIndependentProject(sourceId) is not null);
        var captured=studio.CapturePackage(sourceId,project,expectedRevision);
        if(captured.Snapshot.Catalog is {} catalog&&project.Fps!=catalog.Fps)throw new ArgumentException("Project frame rate must match its catalog.");
        var clipMap=captured.Snapshot.Sources.ToDictionary(source=>source.Id,source=>source.Id,StringComparer.Ordinal);
        var assetMap=captured.Assets.ToDictionary(asset=>asset.Id,asset=>asset.Id,StringComparer.Ordinal);
        var eventMap=captured.Snapshot.Sources.SelectMany(source=>source.Events).ToDictionary(row=>row[0],row=>row[0],StringComparer.Ordinal);
        var detached=StudioProjectMediaRefs.Remap(project,clipMap,eventMap,assetMap);
        foreach(var source in captured.Snapshot.Sources)
            foreach(var variant in source.Variants)
                EditorRenderService.Validate(StudioProjectMediaRefs.Remap(EditorProjectCodec.Deserialize(variant[2]),clipMap,eventMap,assetMap));
        var downloadRoot=Path.Combine(packageRoot,"downloads");Directory.CreateDirectory(downloadRoot);
        var id=Guid.NewGuid().ToString("N");var output=Path.Combine(downloadRoot,id+".lcproj");var partial=output+".partial";
        var files=new List<ProjectPackageFile>();long total=0;var index=0;
        await using(var stream=new FileStream(partial,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,81920,FileOptions.Asynchronous))
        {
            using(var archive=new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                async Task<string> Media(string path)
                {
                    var extension=Path.GetExtension(path);
                    if(extension.Length>12||extension.Skip(1).Any(c=>!char.IsAsciiLetterOrDigit(c)))throw new NotSupportedException("素材扩展名不能用于可移植工程包");
                    var member=$"media/{++index:D6}{extension.ToLowerInvariant()}";
                    var entry=archive.CreateEntry(member,CompressionLevel.NoCompression);
                    await using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous|FileOptions.SequentialScan);
                    await using var target=entry.Open();
                    var copied=await CopyHashed(source,target,MaximumBytes-total,token);total+=copied.Bytes;
                    files.Add(new(member,copied.Bytes,copied.Hash));return member;
                }
                var portableSources=new List<StudioStore.CopySource>();
                foreach(var source in captured.Snapshot.Sources)
                {
                    var media=await Media(source.Path);var srt=source.SrtPath is {} s?await Media(s):null;
                    if(source.TranscriptJson is not null&&srt is null)throw new NotSupportedException("源转录缺少字幕文件，工程包未完成。");
                    string[]? stems=source.Stems is {} pair?[await Media(pair[0]),await Media(pair[1])]:null;
                    portableSources.Add(source with {Path=media,SrtPath=srt,Stems=stems,OutputDirectory=null,ProjectJson=null});
                }
                var portableAssets=new List<PackageAsset>();
                foreach(var asset in captured.Assets)portableAssets.Add(asset with {Path=await Media(asset.Path)});
                var portable=new PackageAssociations(captured.Snapshot with {ProjectJson=null,Sources=portableSources.ToArray()},portableAssets.ToArray());
                await WriteJson(archive,"project.json",detached,files,token);
                await WriteJson(archive,"associations.json",portable,files,token);
                var manifest=archive.CreateEntry("manifest.json",CompressionLevel.Optimal);
                await using(var target=manifest.Open())await JsonSerializer.SerializeAsync(target,new ProjectPackageManifest("liveclipper-project",captured.Snapshot.Catalog is null?1:2,name,files),Json,token);
            }
            await stream.FlushAsync(token);
        }
        if(new FileInfo(partial).Length>MaximumBytes)throw new NotSupportedException("工程包超过 16 GiB 大小限制");
        studio.VerifyPackageSnapshot(captured);
        token.ThrowIfCancellationRequested();File.Move(partial,output,false);
        var safeName=string.Concat(name.Select(c=>c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ||char.IsControl(c)?'_':c)).TrimEnd(' ','.');
        var fileName=(safeName.Length==0?"LiveClipper":safeName)+".lcproj";
        File.WriteAllText(output+".name",fileName);
        return new(id,fileName);
    }

    public async Task<ProjectCopyResult> ImportAsync(Stream input,CancellationToken token=default)
    {
        var incomingRoot=Path.Combine(packageRoot,"imports");Directory.CreateDirectory(incomingRoot);
        var directory=Path.Combine(incomingRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var archivePath=Path.Combine(directory,"upload.lcproj");
        await using(var file=new FileStream(archivePath,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
            await CopyHashed(input,file,MaximumBytes,token);
        using var archive=ZipFile.OpenRead(archivePath);
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            ValidateMember(entry.FullName);
            var unixType=(entry.ExternalAttributes>>16)&0xf000;
            if(unixType==0xa000||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("工程包不能包含链接文件");
            if(!entries.TryAdd(entry.FullName,entry))throw new InvalidDataException("工程包存在大小写冲突或重复成员");
        }
        if(!entries.TryGetValue("manifest.json",out var manifestEntry))throw new InvalidDataException("不是 LiveClipper 工程包：缺少专属 manifest");
        var manifest=await ReadJson<ProjectPackageManifest>(manifestEntry,token);
        if(manifest.Format!="liveclipper-project"||manifest.Version is not (1 or 2))throw new NotSupportedException("不支持此工程包格式或版本");
        if(string.IsNullOrWhiteSpace(manifest.Name)||manifest.Name.Length>100)throw new InvalidDataException("工程包名称无效");
        if(manifest.Files.Any(file=>file is null))throw new InvalidDataException("工程包文件声明不能为空");
        var declarations=new Dictionary<string,ProjectPackageFile>(StringComparer.OrdinalIgnoreCase);long expanded=0;
        foreach(var file in manifest.Files)
        {
            ValidateMember(file.Path);
            if(file.Path is not ("project.json" or "associations.json")&&!file.Path.StartsWith("media/",StringComparison.Ordinal))throw new InvalidDataException("工程包包含未知类型的声明资源");
            if(file.Path=="manifest.json"||!declarations.TryAdd(file.Path,file))throw new InvalidDataException("重复的工程包文件声明");
            if(file.Bytes<0||file.Bytes>MaximumBytes-expanded)throw new InvalidDataException("工程包展开总量超过 16 GiB");
            expanded+=file.Bytes;
            if(file.Sha256.Length!=64||!file.Sha256.All(Uri.IsHexDigit))throw new InvalidDataException("工程包 SHA256 声明无效");
            if(!entries.TryGetValue(file.Path,out var entry)||entry.Length!=file.Bytes)throw new InvalidDataException("工程包文件缺失或长度不符");
        }
        if(entries.Count!=declarations.Count+1||!declarations.ContainsKey("project.json")||!declarations.ContainsKey("associations.json"))
            throw new InvalidDataException("工程包包含未声明成员或缺少工程数据");
        var extracted=new Dictionary<string,ProjectCopyMediaStager.StagedFile>(StringComparer.OrdinalIgnoreCase);var fileIndex=0;
        foreach(var declaration in declarations.Values)
        {
            token.ThrowIfCancellationRequested();var extension=Path.GetExtension(declaration.Path);
            var path=Path.Combine(directory,$"{++fileIndex:D6}"+extension);
            await using var source=entries[declaration.Path].Open();
            await using var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous);
            var actual=await CopyHashed(source,target,declaration.Bytes,token);
            if(actual.Bytes!=declaration.Bytes||!string.Equals(actual.Hash,declaration.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("工程包文件 SHA256 校验失败");
            extracted.Add(declaration.Path,new(path,actual.Bytes,actual.Hash));
        }
        async Task<T> Metadata<T>(string name)
        {
            if(declarations[name].Bytes>MetadataLimit)throw new InvalidDataException("工程包 JSON 数据超过 64 MiB");
            await using var file=File.OpenRead(extracted[name].Path);
            return await JsonSerializer.DeserializeAsync<T>(file,Json,token)??throw new InvalidDataException("工程包 JSON 为空");
        }
        var associations=await Metadata<PackageAssociations>("associations.json");
        if(declarations["project.json"].Bytes>MetadataLimit)throw new InvalidDataException("工程包 JSON 数据超过 64 MiB");
        var project=EditorProjectCodec.Deserialize(await File.ReadAllTextAsync(extracted["project.json"].Path,token));
        ProjectCopyService.ValidateCopyProject(project,associations.Snapshot.Catalog is not null);
        if(associations.Assets.Any(asset=>asset is null))throw new InvalidDataException("工程包素材不能为空");
        var snapshot=associations.Snapshot;
        if(snapshot.ProjectJson is not null||snapshot.Sources.Any(s=>s is null)||(snapshot.Catalog is null&&(snapshot.Sources.Length==0||!snapshot.Sources.Any(s=>s.Id==snapshot.PrimaryId)))||snapshot.Sources.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()!=snapshot.Sources.Length)
            throw new InvalidDataException("工程包源媒体身份无效");
        if((manifest.Version==2)!=(snapshot.Catalog is not null))throw new InvalidDataException("Package version and project identity disagree.");
        if(snapshot.Catalog is {} catalog)
        {
            if(catalog.Id!=snapshot.PrimaryId||snapshot.Sources.Any(s=>s.Id==catalog.Id)||catalog.Width is <16 or >16384||catalog.Height is <16 or >16384||catalog.Fps is not (24 or 25 or 30 or 50 or 60)||project.Fps!=catalog.Fps)
                throw new InvalidDataException("Invalid independent project catalog.");
        }
        var usedMedia=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ProjectCopyMediaStager.StagedFile FileFor(string member)
        {
            ValidateMember(member);
            if(!member.StartsWith("media/",StringComparison.Ordinal)||!extracted.TryGetValue(member,out var file))throw new InvalidDataException("工程包媒体引用未声明");
            if(!usedMedia.Add(member))throw new InvalidDataException("工程包中的独立资源不能共用可变文件");
            return file;
        }
        var clipMap=snapshot.Sources.ToDictionary(s=>s.Id,_=>Guid.NewGuid().ToString("N"),StringComparer.Ordinal);
        var eventMap=new Dictionary<string,string>(StringComparer.Ordinal);
        var staged=new Dictionary<string,ProjectCopyMediaStager.StagedClip>(StringComparer.Ordinal);
        foreach(var source in snapshot.Sources)
        {
            if(string.IsNullOrWhiteSpace(source.Id)||string.IsNullOrWhiteSpace(source.EventId)||source.OutputDirectory is not null)throw new InvalidDataException("工程包包含非便携源信息");
            staged.Add(source.Id,new(clipMap[source.Id],FileFor(source.Path),source.SrtPath is {} srt?FileFor(srt):null));
            if(source.Stems is {} stems)
            {
                if(stems.Length!=2)throw new InvalidDataException("工程包分轨信息无效");
                staged.Add("stems:"+source.Id,new(Guid.NewGuid().ToString("N"),FileFor(stems[0]),FileFor(stems[1])));
            }
            foreach(var item in source.Events)
            {
                if(item is null||item.Length!=7||!eventMap.TryAdd(item[0],Guid.NewGuid().ToString("N")))throw new InvalidDataException("工程包候选身份无效");
                if(!double.TryParse(item[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var start)||!double.IsFinite(start)||start<0||
                    !double.TryParse(item[2],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var end)||!double.IsFinite(end)||end<=start||string.IsNullOrWhiteSpace(item[3]))throw new InvalidDataException("工程包候选时间无效");
            }
            StudioStore.ValidatePackageDocuments(source.Documents);
        }
        var assetMap=associations.Assets.ToDictionary(a=>a.Id,_=>Guid.NewGuid().ToString("N"),StringComparer.Ordinal);
        var assets=associations.Assets.Select(asset=>asset with {Path=FileFor(asset.Path).Path}).ToArray();
        foreach(var asset in assets)StudioStore.ValidateAssetDocuments(asset.Documents);
        var copied=StudioProjectMediaRefs.Remap(project,clipMap,eventMap,assetMap);ProjectCopyService.ValidateCopyProject(copied,snapshot.Catalog is not null);
        foreach(var source in snapshot.Sources)
            foreach(var variant in source.Variants)
            {
                if(variant is null||variant.Length!=3)throw new InvalidDataException("工程包保存版本无效");
                EditorRenderService.Validate(StudioProjectMediaRefs.Remap(EditorProjectCodec.Deserialize(variant[2]),clipMap,eventMap,assetMap));
            }
        if(usedMedia.Count!=declarations.Keys.Count(key=>key.StartsWith("media/",StringComparison.Ordinal)))throw new InvalidDataException("工程包包含未关联媒体文件");
        token.ThrowIfCancellationRequested();
        return studio.PublishProjectCopy(snapshot,new(directory,staged),clipMap,eventMap,copied,manifest.Name,false,assets,assetMap);
    }
    private static void ValidateMember(string path)
    {
        if(string.IsNullOrEmpty(path)||path.StartsWith('/')||path.Contains('\\')||path.Contains(':')||path.Split('/').Any(p=>p.Length==0||p is "." or ".."||p.EndsWith(' ')||p.EndsWith('.')||p.Any(c=>char.IsControl(c)||c is '<' or '>' or '"' or '|' or '?' or '*')))
            throw new InvalidDataException("工程包成员路径不安全或不能跨平台打开");
    }
    private static async Task<T> ReadJson<T>(ZipArchiveEntry entry,CancellationToken token)
    {
        if(entry.Length>MetadataLimit)throw new InvalidDataException("工程包 JSON 数据超过 64 MiB");
        await using var stream=entry.Open();return await JsonSerializer.DeserializeAsync<T>(stream,Json,token)??throw new InvalidDataException("工程包 JSON 为空");
    }
    private static async Task WriteJson<T>(ZipArchive archive,string name,T value,List<ProjectPackageFile> files,CancellationToken token)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Json);
        if(bytes.Length>MetadataLimit)throw new NotSupportedException("工程包 JSON 数据超过 64 MiB");
        var entry=archive.CreateEntry(name,CompressionLevel.Optimal);await using var stream=entry.Open();await stream.WriteAsync(bytes,token);
        files.Add(new(name,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes))));
    }
    private static async Task<(long Bytes,string Hash)> CopyHashed(Stream source,Stream target,long limit,CancellationToken token)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[81920];long count=0;int read;
        while((read=await source.ReadAsync(buffer,token))>0)
        {
            count+=read;if(count>limit)throw new InvalidDataException("工程包文件超过声明大小或 16 GiB 限制");
            hash.AppendData(buffer,0,read);await target.WriteAsync(buffer.AsMemory(0,read),token);
        }
        await target.FlushAsync(token);return(count,Convert.ToHexString(hash.GetHashAndReset()));
    }
}

public sealed partial class StudioStore
{
    private static readonly HashSet<string> AssetDocumentScopes=new(StringComparer.Ordinal){"music-analysis","audio-attribution"};
    internal static void ValidatePackageDocuments(string[][] documents)
    {
        if(documents.Any(row=>row is null||row.Length!=2||!CopyDocumentScopes.Contains(row[0])))throw new NotSupportedException("工程包含暂不支持打包的分析关联");
        foreach(var document in documents){using var parsed=JsonDocument.Parse(document[1]);}
    }
    internal static void ValidateAssetDocuments(string[][] documents)
    {
        if(documents.Any(row=>row is null||row.Length!=2||!AssetDocumentScopes.Contains(row[0])))throw new NotSupportedException("素材包含暂不支持打包的关联");
        foreach(var document in documents){using var parsed=JsonDocument.Parse(document[1]);}
    }
    internal PackageAssociations CapturePackage(string primaryId,EditorProject project,long expectedRevision)
        =>EditorRevisionStore.Serialized(this,primaryId,()=>
        {
            using var registry=MediaRegistryGate.EnterRead();using var db=Open();using var tx=db.BeginTransaction();
            var sourceIds=new HashSet<string>(StringComparer.Ordinal);
            using(var identity=db.CreateCommand())
            {
                identity.Transaction=tx;identity.CommandText="SELECT COUNT(*) FROM editor_project_catalog WHERE id=$id";identity.Parameters.AddWithValue("$id",primaryId);
                if(Convert.ToInt64(identity.ExecuteScalar())==0)sourceIds.Add(primaryId);
            }
            var assetIds=new HashSet<string>(StringComparer.Ordinal);
            void Collect(EditorProject plan)=>StudioProjectMediaRefs.FindMissing(plan,primaryId,id=>{sourceIds.Add(id);return true;},(id,kind)=>{assetIds.Add(id);return true;});
            Collect(project);CopySnapshot snapshot;
            while(true)
            {
                snapshot=ReadCopySnapshot(db,tx,primaryId,sourceIds.Order(StringComparer.Ordinal).ToArray());var before=sourceIds.Count;
                foreach(var source in snapshot.Sources)foreach(var variant in source.Variants)Collect(EditorProjectCodec.Deserialize(variant[2]));
                if(sourceIds.Count==before)break;
            }
            if(snapshot.Revision!=expectedRevision)throw new ProjectCopyConflictException("原工程已改变，请重新保存工程包。");
            foreach(var source in snapshot.Sources)ValidatePackageDocuments(source.Documents);
            var assets=ReadPackageAssets(db,tx,assetIds.Order(StringComparer.Ordinal).ToArray());
            foreach(var asset in assets)ValidateAssetDocuments(asset.Documents);
            tx.Commit();return new PackageAssociations(snapshot,assets);
        });
    private static PackageAsset[] ReadPackageAssets(SqliteConnection db,SqliteTransaction tx,string[] ids)
        =>ids.Select(id=>
        {
            using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="SELECT name,kind,style,path FROM audio_assets WHERE id=$id";command.Parameters.AddWithValue("$id",id);
            string name,kind,style,path;
            using(var reader=command.ExecuteReader())
            {
                if(!reader.Read())throw new ArgumentException("工程素材不存在："+id);
                name=reader.GetString(0);kind=reader.GetString(1);style=reader.GetString(2);path=reader.GetString(3);
            }
            command.CommandText="SELECT scope,json FROM studio_documents WHERE key=$id ORDER BY scope";var docs=new List<string[]>();
            using(var reader=command.ExecuteReader())while(reader.Read())docs.Add([reader.GetString(0),reader.GetString(1)]);
            return new PackageAsset(id,name,kind,style,path,docs.ToArray());
        }).ToArray();
    internal void VerifyPackageSnapshot(PackageAssociations captured)
        =>EditorRevisionStore.Serialized(this,captured.Snapshot.PrimaryId,()=>
        {
            using var registry=MediaRegistryGate.EnterRead();using var db=Open();using var tx=db.BeginTransaction();
            var current=new PackageAssociations(ReadCopySnapshot(db,tx,captured.Snapshot.PrimaryId,captured.Snapshot.Sources.Select(s=>s.Id).ToArray()),
                ReadPackageAssets(db,tx,captured.Assets.Select(a=>a.Id).ToArray()));
            if(JsonSerializer.Serialize(current)!=JsonSerializer.Serialize(captured))throw new ProjectCopyConflictException("工程或关联数据在打包期间改变，工程包未发布。");
            tx.Commit();return true;
        });
}
