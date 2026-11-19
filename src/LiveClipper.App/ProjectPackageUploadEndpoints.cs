using System.Collections.Concurrent;
using System.Text.Json;
using LiveClipper.Core;
using Microsoft.AspNetCore.Mvc;

public sealed record ProjectPackageUploadStart(long TotalBytes);

public static class ProjectPackageUploadEndpoints
{
    private const int ChunkBytes=4*1024*1024;
    private sealed record Upload(string Path,long Total,ProjectPackageService Service)
    {
        public SemaphoreSlim Gate {get;}=new(1,1);
        public long Offset;
        public bool Closed;
    }
    public static void Map(WebApplication app,StudioStore studio,Func<string> root)
    {
        var uploads=new ConcurrentDictionary<string,Upload>();
        app.MapPost("/api/studio/project-packages/uploads",(ProjectPackageUploadStart body)=>
        {
            if(body.TotalBytes is <1 or >ProjectPackageService.MaximumBytes)return Results.BadRequest(new {error="工程包大小无效"});
            var capturedRoot=root();var directory=Path.Combine(capturedRoot,"uploads");Directory.CreateDirectory(directory);
            var id=Guid.NewGuid().ToString("N");var path=Path.Combine(directory,id+".upload");
            using(File.Open(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}
            uploads[id]=new(path,body.TotalBytes,new ProjectPackageService(studio,capturedRoot));
            return Results.Ok(new {uploadId=id,offset=0,chunkBytes=ChunkBytes});
        });
        app.MapPost("/api/studio/project-packages/uploads/{id}/chunks",async (string id,long offset,HttpRequest request,CancellationToken token)=>
        {
            if(!uploads.TryGetValue(id,out var upload))return Results.NotFound();
            if(!string.Equals(request.ContentType,"application/octet-stream",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new {error="请上传二进制文件分块"});
            await upload.Gate.WaitAsync(token);
            try
            {
                if(upload.Closed)return Results.NotFound();
                if(offset!=upload.Offset)return Results.Conflict(new {error="上传位置不一致，请重新上传",offset=upload.Offset});
                var bytes=new byte[ChunkBytes+1];var count=0;
                while(count<bytes.Length)
                {
                    var read=await request.Body.ReadAsync(bytes.AsMemory(count),token);if(read==0)break;count+=read;
                }
                if(count==0||count>ChunkBytes||count>upload.Total-upload.Offset)return Results.BadRequest(new {error="文件分块大小无效"});
                await using var file=new FileStream(upload.Path,FileMode.Open,FileAccess.Write,FileShare.None,81920,true);
                file.Position=upload.Offset;
                try{await file.WriteAsync(bytes.AsMemory(0,count),token);await file.FlushAsync(token);}
                catch{file.SetLength(upload.Offset);throw;}
                upload.Offset+=count;return Results.Ok(new {offset=upload.Offset});
            }
            finally{upload.Gate.Release();}
        }).WithMetadata(new RequestSizeLimitAttribute(ChunkBytes+1));
        app.MapPost("/api/studio/project-packages/uploads/{id}/complete",async (string id,HttpResponse response,CancellationToken token)=>
        {
            if(!uploads.TryGetValue(id,out var upload))return Results.NotFound();
            await upload.Gate.WaitAsync(token);
            try
            {
                if(upload.Closed)return Results.NotFound();
                if(upload.Offset!=upload.Total||new FileInfo(upload.Path).Length!=upload.Total)return Results.Conflict(new {error="工程包尚未上传完整",offset=upload.Offset});
                response.Headers["X-Editor-Epoch"]=EditorRevisionStore.EpochFor(studio);
                await using(var file=new FileStream(upload.Path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))
                {
                    var result=await upload.Service.ImportAsync(file,token);
                    upload.Closed=true;uploads.TryRemove(id,out _);
                    return Results.Ok(result);
                }
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or JsonException or EditorProjectSchemaException or ReplicaExtensionException or InvalidDataException)
            {return Results.Json(new {error=ex.Message,code="project_package_invalid"},statusCode:422);}
            finally
            {
                if(upload.Closed)File.Delete(upload.Path);
                upload.Gate.Release();
            }
        });
        app.MapDelete("/api/studio/project-packages/uploads/{id}",async (string id,CancellationToken token)=>
        {
            if(!uploads.TryGetValue(id,out var upload))return Results.NoContent();
            await upload.Gate.WaitAsync(token);
            try
            {
                if(!upload.Closed){File.Delete(upload.Path);upload.Closed=true;uploads.TryRemove(id,out _);}
                return Results.NoContent();
            }
            finally{upload.Gate.Release();}
        });
    }
}
