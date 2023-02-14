var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Welcome to TestSite1");
app.MapGet("/healthcheck", () => "site 1");

app.Run();
