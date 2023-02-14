var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Welcome to TestSite2");
app.MapGet("/healthcheck", () => "site 2");

app.Run();
