using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LiveClipper.Installation
{
    public sealed class SourcePolicy
    {
        public string Country {get;set;}="UNKNOWN";
        public bool China=>Country=="CN";
        public string Description=>China?"中国大陆 · 国内源优先":Country=="UNKNOWN"?"地区检测不可用 · 官方源，可手动切换":"非中国大陆 · 官方源优先";
        public const string PipChina="https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple";
        public const string PipOfficial="https://pypi.org/simple";
        public const string PythonChina="https://mirror.nju.edu.cn/github-release/astral-sh/python-build-standalone";
        public const string TorchChina="https://mirror.sjtu.edu.cn/pytorch-wheels/cpu";
        public const string TorchOfficial="https://download.pytorch.org/whl/cpu";
        public static async Task<SourcePolicy> Detect(CancellationToken token)
        {
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            // Only the country code is retained. The public IP is neither stored nor logged.
            using(var client=new HttpClient{Timeout=TimeSpan.FromSeconds(3)})
            foreach(var address in new[]{"https://ipwho.is/?fields=success,country_code","https://api.country.is/"})
            {
                try
                {
                    using(var response=await client.GetAsync(address,token))
                    {
                        response.EnsureSuccessStatusCode();
                        var country=ParseCountry(await response.Content.ReadAsStringAsync());
                        if(country!="UNKNOWN")return new SourcePolicy{Country=country};
                    }
                }
                catch(OperationCanceledException)when(!token.IsCancellationRequested){}
                catch(HttpRequestException){}
                catch(ArgumentException){}
                catch(InvalidOperationException){}
            }
            token.ThrowIfCancellationRequested();return new SourcePolicy();
        }
        public static string ParseCountry(string json)
        {
            var values=Paths.Json.Deserialize<Dictionary<string,object>>(json);
            object value;if(values==null||(values.TryGetValue("success",out value)&&!Equals(value,true)))return "UNKNOWN";
            if(!values.TryGetValue("country_code",out value))values.TryGetValue("country",out value);
            var country=(value as string??"").Trim().ToUpperInvariant();
            return country.Length==2&&country.All(c=>c>='A'&&c<='Z')?country:"UNKNOWN";
        }
        public IEnumerable<string> Candidates(string official,string mirror=null)
        {
            var uri=new Uri(official);if(uri.Scheme!="https")throw new ArgumentException("下载源必须使用 HTTPS");
            if(China)
            {
                if(mirror!=null)yield return mirror;
                if(uri.Host=="github.com")yield return "https://ghfast.top/"+official;
            }
            yield return official;
        }
    }
}
