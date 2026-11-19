using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LiveClipper.Installation
{
    public sealed class DownloadClient
    {
        readonly HttpClient client;
        public DownloadClient(HttpClient client){this.client=client;}
        public static bool Verify(string path,string expected,string algorithm)
        {
            using(var stream=File.OpenRead(path))using(var digest=algorithm=="SHA512"?(HashAlgorithm)SHA512.Create():SHA256.Create())
                return BitConverter.ToString(digest.ComputeHash(stream)).Replace("-","").Equals(expected,StringComparison.OrdinalIgnoreCase);
        }
        public async Task<string> Fetch(IEnumerable<string> sources,string path,string hash,string algorithm,CancellationToken token,Action<string,int,string> report)
        {
            if(hash==null||hash.Length!=(algorithm=="SHA512"?128:64)||!hash.All(Uri.IsHexDigit))throw new IOException("下载缺少可信校验值");
            Paths.NoLinks(path);Paths.NoLinks(path+".part");Directory.CreateDirectory(Path.GetDirectoryName(path));
            token.ThrowIfCancellationRequested();
            if(File.Exists(path)&&Verify(path,hash,algorithm)){report("复用已校验缓存",100,Path.GetFileName(path));return path;}
            Exception last=null;var partial=path+".part";
            foreach(var url in sources.Distinct())
            {
                var uri=new Uri(url);if(uri.Scheme!="https")throw new IOException("下载源必须使用 HTTPS");
                for(var attempt=0;attempt<2;attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        long existing=File.Exists(partial)?new FileInfo(partial).Length:0;
                        report(existing>0?"正在续传":"正在下载",0,uri.Host+" · "+Path.GetFileName(path));
                        using(var request=new HttpRequestMessage(HttpMethod.Get,uri))
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            if(existing>0)request.Headers.Range=new RangeHeaderValue(existing,null);
                            timeout.CancelAfter(TimeSpan.FromSeconds(20));
                            using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token))
                            {
                                if(response.RequestMessage.RequestUri.Scheme!="https")throw new IOException("下载源重定向到非 HTTPS 地址");
                                if(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable&&File.Exists(partial))
                                {
                                    if(Verify(partial,hash,algorithm)){Commit(partial,path);return path;}
                                    File.Delete(partial);throw new IOException("续传缓存需重新下载");
                                }
                                response.EnsureSuccessStatusCode();
                                if(response.Content.Headers.ContentType?.MediaType=="text/html")throw new IOException("源返回网页，已切换备用源");
                                var resume=response.StatusCode==HttpStatusCode.PartialContent;
                                if(resume&&(response.Content.Headers.ContentRange?.From!=existing||!response.Content.Headers.ContentRange.Length.HasValue))throw new IOException("源返回的续传范围无效");
                                if(!resume)existing=0; // Range ignored: replace rather than append a whole file.
                                var total=resume?response.Content.Headers.ContentRange.Length:response.Content.Headers.ContentLength;
                                var received=existing;var watch=Stopwatch.StartNew();var tick=Stopwatch.StartNew();
                                using(var source=await response.Content.ReadAsStreamAsync())
                                using(var target=new FileStream(partial,resume?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
                                {
                                    var buffer=new byte[131072];int count;
                                    while(true)
                                    {
                                        timeout.CancelAfter(TimeSpan.FromSeconds(25));
                                        count=await source.ReadAsync(buffer,0,buffer.Length,timeout.Token);if(count==0)break;
                                        await target.WriteAsync(buffer,0,count,token);received+=count;
                                        if(tick.ElapsedMilliseconds<150)continue;
                                        var detail=(received/1048576.0).ToString("0.0")+" MB"+(total.HasValue?" / "+(total.Value/1048576.0).ToString("0.0")+" MB":"")+" · "+((received-existing)/1048576.0/Math.Max(.1,watch.Elapsed.TotalSeconds)).ToString("0.0")+" MB/s · "+uri.Host;
                                        report("正在下载",total.HasValue?(int)Math.Min(99,received*100/Math.Max(1,total.Value)):-1,detail);tick.Restart();
                                    }
                                    if(total.HasValue&&received!=total.Value)throw new IOException("下载中断，保留进度以便续传");
                                }
                            }
                        }
                        token.ThrowIfCancellationRequested();report("正在校验",100,Path.GetFileName(path));
                        if(!Verify(partial,hash,algorithm)){File.Delete(partial);throw new IOException("下载文件校验失败，切换备用源");}
                        Commit(partial,path);return path;
                    }
                    catch(OperationCanceledException)when(!token.IsCancellationRequested){last=new IOException("下载源响应超时");}
                    catch(HttpRequestException ex){last=ex;}
                    catch(IOException ex){last=ex;}
                    report("准备重试或切换备用源",-1,uri.Host+" · "+last.Message);
                    if(attempt==0)await Task.Delay(300,token);
                }
            }
            throw new IOException("下载失败："+Path.GetFileName(path)+"。可切换下载线路并重试；已下载进度会保留。",last);
        }
        static void Commit(string partial,string path){if(File.Exists(path))File.Delete(path);File.Move(partial,path);}
    }
}
