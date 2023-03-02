//using Newtonsoft.Json;

public class ConnectionStateModel
{
    public string ID { get; set; }
    public string IPAddress { get; set; }
    public string SiteName { get; set; }
    public DateTime Timeout { get; set; }
    //[JsonIgnore]
    //public HttpClient Client { get; set; }
}