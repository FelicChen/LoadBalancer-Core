using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

public class ReverseProxyMiddleware
{
    static IConfiguration Configs { get; set; }
    static Dictionary<string, ConnectionStateModel> ConnectionState { get; set; }
    static ConnectionSettingsModel ConnectionSettings { get; set; }
    static Dictionary<string, ProxySiteModel> ProxySites { get; set; }
    /// <summary>紀錄連線數</summary>
    static Dictionary<string, int> SiteConnectionCount { get; set; }
    private readonly HttpClient _httpClient;
    private readonly Timer HealthCheckTimer;
    //private readonly RequestDelegate _nextMiddleware;
    public ReverseProxyMiddleware(RequestDelegate nextMiddleware)
    {
        Configs = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build();
        ConnectionState = new();
        SiteConnectionCount = new();
        ConnectionSettings = Configs.GetSection("ServerSettings:ConnectionSettings").Get<ConnectionSettingsModel>();
        ProxySites = Configs.GetSection("ProxySite").Get<Dictionary<string, ProxySiteModel>>();

        _httpClient = new(new HttpClientHandler()
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; },
            AllowAutoRedirect = false,

        })
        {
            Timeout = TimeSpan.FromSeconds(ConnectionSettings.HealthCheckTimeout)
        };

        //_nextMiddleware = nextMiddleware; // 這個沒用到
        HealthCheckTimer = new(new TimerCallback(HealthCheck), null, 0, ConnectionSettings.HealthCheckTimer);
    }
    public async Task Invoke(HttpContext context)
    {
        context.Session.SetString("Timeout", "0"); // Keep Session ID
        ConnectionStateCheck();
        if (context.Request.Path.HasValue && new List<string> { "/sesslinlist", "/connectioncount" }.Contains(context.Request.Path.Value.ToLower()))
        {
            await context.Response.WriteAsync(JsonConvert.SerializeObject(context.Request.Path.Value.ToLower() switch
            {
                "/connectioncount" => ConnectionState, // 顯示所有連線
                _ => SiteConnectionCount // 顯示各網站連線數
            }));
            return;
        }
        if (SiteConnectionCount.Count == 0)
        {
            await context.Response.WriteAsync("伺服器維護中...");
            return;
        }
        //var cports = SiteConnectionCount.Where(x => ProxySites.Where(y => y.Value.ListenPort == context.Request.Host.Port).Select(y => y.Key).Contains(x.Key));
        string siteName = SiteConnectionCount.MinBy(x => x.Value).Key;
        string userIp = context.Connection.RemoteIpAddress.ToString();
        DateTime timeout = DateTime.Now.AddSeconds(ConnectionSettings.Timeout);
        if (ConnectionState.ContainsKey(userIp))
        {
            if (!SiteConnectionCount.ContainsKey(ConnectionState[userIp].SiteName)) // 伺服器離線 重新分配
            {
                ConnectionState[userIp].SiteName = siteName;
            }
            ConnectionState[userIp].Timeout = timeout;
            ConnectionState[userIp].ID = context.Session.Id;
        }
        else
        {
            var handler = new SocketsHttpHandler()
            {
                UseCookies = true,
                AllowAutoRedirect = false,
                SslOptions = new SslClientAuthenticationOptions()
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                }
            };
            var client = new HttpClient(handler, true);

            ConnectionState.TryAdd(userIp, new()
            {
                ID = context.Session.Id,
                IPAddress = userIp,
                Timeout = timeout,
                SiteName = siteName,
                Client = client
            });
        }

        await HandleHttpRequest(context, ProxySites[ConnectionState[userIp].SiteName], ConnectionState[userIp]);
        //await _nextMiddleware(context); // 這個會掛掉
    }
    private async Task HandleHttpRequest(HttpContext context, ProxySiteModel site, ConnectionStateModel conn)
    {
        string method = context.Request.Method;
        var target = GetUri(context, site);
        var requireMessage = new HttpRequestMessage(new HttpMethod(method), target);

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsDelete(method) && !HttpMethods.IsTrace(method))
        {
            requireMessage.Content = new StreamContent(context.Request.Body);
        }
        foreach (var header in context.Request.Headers)
        {
            if (!requireMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && requireMessage.Content != null)
            {
                requireMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
        requireMessage.Headers.Host = context.Request.Host.Host;

        // 將Client端的IP寫到Header
        var clientIps = context.Connection.RemoteIpAddress;
        var clientIp = clientIps.ToString();
        if (clientIps.MapToIPv4() != null)
        {
            clientIp = clientIps.MapToIPv4().ToString();
        }
        requireMessage.Headers.Add("X-Forwarded-For", clientIp);

        var responseMessage = await conn.Client.SendAsync(requireMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)responseMessage.StatusCode;
        if (target != responseMessage.RequestMessage?.RequestUri)
        {
            Console.WriteLine($"Request was redirected to {responseMessage.RequestMessage?.RequestUri}");
        }
        foreach (var header in responseMessage.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        context.Response.Headers.Remove("transfer-encoding");
        await responseMessage.Content.CopyToAsync(context.Response.Body);
    }
    /// <summary>
    /// 組合網址
    /// </summary>
    /// <param name="context"></param>
    /// <param name="site"></param>
    /// <returns></returns>
    private Uri GetUri(HttpContext context, ProxySiteModel site)
    {
        int? port = context.Request.Host.Port;
        if (site.Port.HasValue && site.Port > 0) port = site.Port.Value;
        return new Uri($"{context.Request.Scheme}://{site.Url}{(port == null ? "" : $":{port}")}{context.Request.Path}{context.Request.QueryString.Value}");
    }
    /// <summary>
    /// 檢查Client連線狀態
    /// </summary>
    private void ConnectionStateCheck()
    {
        DateTime now = DateTime.Now;
        if (SiteConnectionCount.Count > 0)
        {
            foreach (var conn in ConnectionState)
            {
                if (conn.Value != null && conn.Value.Timeout < now)
                {
                    // 逾時就刪除
                    ConnectionState.Remove(conn.Key);
                }
            }
            foreach (var sc in SiteConnectionCount)
            {
                SiteConnectionCount[sc.Key] = ConnectionState.Count(x => x.Value.SiteName == sc.Key);
            }
        }
    }
    private void HealthCheck(object? stateInfo)
    {
        Dictionary<string, int> dic = new();
        foreach (var site in ProxySites)
        {
            var requireMessage = new HttpRequestMessage
            {
                RequestUri = new Uri(site.Value.HealthCheck),
                Method = HttpMethod.Get
            };
            requireMessage.Headers.Host = site.Value.Url;
            var response = _httpClient.SendAsync(requireMessage, HttpCompletionOption.ResponseHeadersRead, new CancellationToken(false)).Result;

            if (response.StatusCode == HttpStatusCode.OK) dic.Add(site.Key, 0);
            response.Dispose();
        }
        SiteConnectionCount = dic;
    }
}