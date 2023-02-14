public class ConnectionSettingsModel
{
    public int Timeout { get; set; } = 600;
    public int HealthCheckTimer { get; set; } = 10000;
    public int HealthCheckTimeout { get; set; } = 2;
}