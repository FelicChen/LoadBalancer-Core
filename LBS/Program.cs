using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build();
var builder = WebHost
    .CreateDefaultBuilder(args)
    .UseConfiguration(config)
    .UseStartup<Startup>()
    .UseKestrel(options =>
    {
        var listens = config.GetSection("ServerSettings:Listens").Get<List<ListenModel>>();
        foreach (var listen in listens)
        {
            if (listen.Https)
            {
                options.ListenAnyIP(listen.Port, o => o.UseHttps());
            }
            else
            {
                options.ListenAnyIP(listen.Port);
            }
        }
    });

var app = builder.Build();
app.Run();