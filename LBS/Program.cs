using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

const string appUnid = "{78e2a518-bf77-4773-9590-aa89ed811dd8}";
using (Mutex m = new(false, $"Global\\{appUnid}")) // 防止程式重複開啟
{
    if (!m.WaitOne(0, false)) return;
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
}