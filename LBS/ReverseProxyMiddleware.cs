using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Net;
using System.Net.Security;

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

        // 只使用一個HttpClient
        var handler = new SocketsHttpHandler()
        {
            UseCookies = true,
            AllowAutoRedirect = false, // 確保每次站台發生Redirerct都會回傳回來，所以要設定false，如果設定成true，則會發生Redirect之後網址列還是錯的
            SslOptions = new SslClientAuthenticationOptions()
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true // 略過站台的SSL
            }
        };
        _httpClient = new(handler, true);

        //_nextMiddleware = nextMiddleware; // 這個沒用到
        HealthCheckTimer = new(new TimerCallback(HealthCheck), null, 0, ConnectionSettings.HealthCheckTimer);
    }
    public async Task Invoke(HttpContext context)
    {
        context.Session.SetString("Timeout", "0"); // Keep Session ID
        ConnectionStateCheck(); // 計算各站台連線數，以及刪除Timeout的連線
        if (context.Request.Path.HasValue && new List<string> { "/sesslinlist", "/connectioncount" }.Contains(context.Request.Path.Value.ToLower()))
        {
            // 用來debug的頁面
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
        string siteName = SiteConnectionCount.MinBy(x => x.Value).Key; // 從站台連線數判斷，決定要讓Client進入哪一個站台
        string userIp = context.Connection.RemoteIpAddress.ToString(); // 取得Client IP Address
        DateTime timeout = DateTime.Now.AddSeconds(ConnectionSettings.Timeout);
        if (ConnectionState.ContainsKey(userIp)) // 已經有連線記錄就更新
        {
            if (!SiteConnectionCount.ContainsKey(ConnectionState[userIp].SiteName)) // 伺服器離線 重新分配
            {
                ConnectionState[userIp].SiteName = siteName;
            }
            ConnectionState[userIp].Timeout = timeout; // 因為有保持連線，所以更新Timeout時間
            ConnectionState[userIp].ID = context.Session.Id;
        }
        else // 首次連線要建立連線記錄
        {
            //var handler = new SocketsHttpHandler()
            //{
            //    UseCookies = true,
            //    AllowAutoRedirect = false, // 確保每次站台發生Redirerct都會回傳回來，所以要設定false，如果設定成true，則會發生Redirect之後網址列還是錯的
            //    SslOptions = new SslClientAuthenticationOptions()
            //    {
            //        RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true // 略過站台的SSL
            //    }
            //};
            //var client = new HttpClient(handler, true);

            ConnectionState.TryAdd(userIp, new()
            {
                ID = context.Session.Id,
                IPAddress = userIp,
                Timeout = timeout,
                SiteName = siteName,
                //Client = client
            });
        }

        await HandleHttpRequest(context, ProxySites[ConnectionState[userIp].SiteName]); // 開始向站台連線
        //await _nextMiddleware(context); // 這個會掛掉
    }
    private async Task HandleHttpRequest(HttpContext context, ProxySiteModel site)
    {
        string method = context.Request.Method;
        var target = GetUri(context, site);
        var requireMessage = new HttpRequestMessage(new HttpMethod(method), target);

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsDelete(method) && !HttpMethods.IsTrace(method))
        {
            requireMessage.Content = new StreamContent(context.Request.Body);
        }
        // 轉送請求標頭
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
        if (clientIps.MapToIPv4() != null) // 取得IPv4
        {
            clientIp = clientIps.MapToIPv4().ToString();
        }
        requireMessage.Headers.Add("X-Forwarded-For", clientIp);

        var responseMessage = await _httpClient.SendAsync(requireMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)responseMessage.StatusCode;
        if (target != responseMessage.RequestMessage?.RequestUri)
        {
            Console.WriteLine($"Request was redirected to {responseMessage.RequestMessage?.RequestUri}");
        }
        // 轉送回應標頭
        foreach (var header in responseMessage.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        context.Response.Headers.Remove("transfer-encoding");
        await responseMessage.Content.CopyToAsync(context.Response.Body); // 回傳內容
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
            foreach (var conn in ConnectionState) // 檢查連線逾時
            {
                if (conn.Value != null && conn.Value.Timeout < now)
                {
                    // 逾時就刪除
                    ConnectionState.Remove(conn.Key);
                }
            }
            foreach (var sc in SiteConnectionCount) // 計算連線數
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
            if (string.IsNullOrEmpty(site.Value.HealthCheck)) continue;
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