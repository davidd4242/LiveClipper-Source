using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiveClipper.Installation
{
    static class DownloadChecks
    {
        sealed class Handler:HttpMessageHandler
        {
            readonly Func<HttpRequestMessage,HttpResponseMessage> action;
            public Handler(Func<HttpRequestMessage,HttpResponseMessage> action){this.action=action;}
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {token.ThrowIfCancellationRequested();var result=action(request);result.RequestMessage=request;return Task.FromResult(result);}
        }
        static HttpResponseMessage Response(string data,long? from=null,long total=6)
        {
            var response=new HttpResponseMessage(from.HasValue?HttpStatusCode.PartialContent:HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.ASCII.GetBytes(data))};
            if(from.HasValue)response.Content.Headers.ContentRange=new ContentRangeHeaderValue(from.Value,from.Value+data.Length-1,total);
            return response;
        }
        static void Expect(bool condition,string message){if(!condition)throw new Exception(message);}
        public static int Run(string root)
        {
            var checks=0;var china=new SourcePolicy{Country="CN"};
            Expect(SourcePolicy.ParseCountry("{\"country_code\":\"cn\",\"success\":true}")=="CN","国家代码解析失败");checks++;
            foreach(var country in new[]{"HK","MO","TW","US"}){Expect(!new SourcePolicy{Country=country}.China,"错误地将地区判定为中国大陆");checks++;}
            Expect(SourcePolicy.ParseCountry("{\"country_code\":\"CN\",\"success\":false}")=="UNKNOWN","忽略了地区接口失败状态");checks++;
            Expect(china.Candidates("https://github.com/owner/file.zip").First().StartsWith("https://ghfast.top/"),"国内线路未优先选择加速源");checks++;
            Expect(new SourcePolicy().Candidates("https://github.com/owner/file.zip").Count()==1,"官方线路被擅自改写");checks++;
            var path=Path.Combine(root,"transfer.bin");var bytes=Encoding.ASCII.GetBytes("abcdef");string hash;
            using(var digest=SHA256.Create())hash=BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-","");
            Action<Func<HttpRequestMessage,HttpResponseMessage>,string[]> fetch=(handler,urls)=>
            {
                using(var http=new HttpClient(new Handler(handler)))new DownloadClient(http).Fetch(urls,path,hash,"SHA256",CancellationToken.None,(a,b,c)=>{}).GetAwaiter().GetResult();
                Expect(File.ReadAllText(path)=="abcdef","下载文件内容不正确");File.Delete(path);
            };
            File.WriteAllText(path+".part","abc");
            fetch(request=>{Expect(request.Headers.Range.Ranges.Single().From==3,"续传偏移错误");return Response("def",3);},new[]{"https://official.test/file"});checks++;
            File.WriteAllText(path+".part","abc");fetch(request=>Response("abcdef"),new[]{"https://official.test/file"});checks++;
            var failures=0;File.WriteAllText(path+".part","abc");
            fetch(request=>{if(request.RequestUri.Host=="mirror.test"){failures++;return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);}return Response("def",3);},new[]{"https://mirror.test/file","https://official.test/file"});
            Expect(failures==2,"国内源失败时未按预期回退");checks++;
            fetch(request=>Response(request.RequestUri.Host=="mirror.test"?"wrong!":"abcdef"),new[]{"https://mirror.test/file","https://official.test/file"});checks++;
            File.WriteAllText(path+".part","abc");
            using(var cancellation=new CancellationTokenSource())using(var http=new HttpClient(new Handler(request=>Response("abcdef"))))
            {cancellation.Cancel();try{new DownloadClient(http).Fetch(new[]{"https://official.test/file"},path,hash,"SHA256",cancellation.Token,(a,b,c)=>{}).GetAwaiter().GetResult();throw new Exception("取消未生效");}catch(OperationCanceledException){}}
            Expect(File.ReadAllText(path+".part")=="abc","取消清除了续传进度");File.Delete(path+".part");checks++;
            File.WriteAllText(path,"abcdef");var requests=0;fetch(request=>{requests++;return Response("abcdef");},new[]{"https://official.test/file"});Expect(requests==0,"有效缓存仍重新下载");checks++;
            return checks;
        }
    }
}
