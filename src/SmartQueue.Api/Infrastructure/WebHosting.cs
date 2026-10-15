using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Net.Http.Headers;

namespace SmartQueue.Api.Infrastructure;

/// <summary>
/// Which reverse proxies may set X-Forwarded-For/Proto. By default only loopback is trusted (ASP.NET Core default).
/// </summary>
public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>
    /// Trust forwarded headers from any address. Use only when the app is reachable solely through the proxy, for
    /// example in Docker Compose where the API port is not published and the proxy's IP changes.
    /// </summary>
    public bool TrustAll { get; set; }

    /// <summary>Proxy IP addresses to trust, for example the load balancer's private address.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        if (TrustAll)
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            return;
        }
        foreach (var proxy in TrustedProxies)
            if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
    }
}

/// <summary>
/// Serves the built web app (web/dist, copied to wwwroot by the Docker build) from the same origin as the API,
/// because the app calls /api and /hubs with relative URLs. In development wwwroot is empty and Vite serves the app.
/// </summary>
public static class WebHosting
{
    private static bool HasWebApp(IWebHostEnvironment env) => env.WebRootFileProvider.GetFileInfo("index.html").Exists;

    public static WebApplication UseWebApp(this WebApplication app)
    {
        if (!HasWebApp(app.Environment)) return app;
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                // Vite puts content-hashed files in /assets; everything else (index.html, sw.js, manifest) must be
                // revalidated so a new deployment is picked up at once.
                context.Context.Response.Headers[HeaderNames.CacheControl] =
                    context.Context.Request.Path.StartsWithSegments("/assets")
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        });
        return app;
    }

    /// <summary>
    /// Deep links such as /clinic/schedule return index.html so the app's router can show the page.
    /// Unknown /api and /hubs paths still return 404 instead of the app.
    /// </summary>
    public static WebApplication MapWebAppFallback(this WebApplication app)
    {
        if (!HasWebApp(app.Environment)) return app;
        app.Map("/api/{**rest}", () => Results.NotFound()).ExcludeFromDescription();
        app.Map("/hubs/{**rest}", () => Results.NotFound()).ExcludeFromDescription();
        app.MapFallbackToFile("index.html", new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
        });
        return app;
    }
}
