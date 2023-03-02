using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
public class Startup
{
    public Startup(IConfiguration configuration, IHostingEnvironment env)
    {
    }
    public IServiceProvider ConfigureServices(IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });
        services.AddDistributedMemoryCache();
        services.AddMvc();
        services.AddRouting();
        services.AddSession();
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "LBS";
        });
        return services.BuildServiceProvider();
    }
    public void Configure(IApplicationBuilder app, IHostingEnvironment env)
    {
        app.UseForwardedHeaders();

        app.UseStaticFiles();

        app.UseSession();

        app.UseMiddleware<ReverseProxyMiddleware>();
    }
}