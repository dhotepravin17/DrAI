using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartQueue.Api.Tests.Infrastructure;

namespace SmartQueue.Api.Tests;

/// <summary>Production hosting: reverse-proxy headers, serving the web app, and persisted Data Protection keys.</summary>
[Collection(ApiCollection.Name)]
public class HostingTests(ApiFactory factory)
{
    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_gets_its_own_rate_limit()
    {
        await using var proxied = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Proxy:TrustAll", "true");
            b.UseSetting("RateLimits:OtpPerMinutePerIp", "2");
        });
        var client = proxied.CreateClient();

        Task<HttpResponseMessage> RequestFrom(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/otp/request")
            {
                Content = JsonContent.Create(new { mobileNumber = TestApi.NextMobile() }),
            };
            request.Headers.Add("X-Forwarded-For", ip);
            return client.SendAsync(request);
        }

        Assert.True((await RequestFrom("203.0.113.10")).IsSuccessStatusCode);
        Assert.True((await RequestFrom("203.0.113.10")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RequestFrom("203.0.113.10")).StatusCode);
        // A different user behind the same proxy is not locked out.
        Assert.True((await RequestFrom("198.51.100.20")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_built_web_app_is_served_with_deep_links_and_api_404s_stay_404()
    {
        var webRoot = Directory.CreateTempSubdirectory("valmedo-web-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Valmedo test app</title>");
            Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
            await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "index-abc123.js"), "console.log('app')");

            await using var hosted = factory.WithWebHostBuilder(b => b.UseWebRoot(webRoot));
            var client = hosted.CreateClient();

            var home = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.Contains("Valmedo test app", await home.Content.ReadAsStringAsync());

            var deepLink = await client.GetAsync("/clinic/schedule");
            Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
            Assert.Contains("Valmedo test app", await deepLink.Content.ReadAsStringAsync());
            Assert.Equal("no-cache", deepLink.Headers.CacheControl?.ToString());

            var asset = await client.GetAsync("/assets/index-abc123.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Contains("immutable", asset.Headers.CacheControl?.ToString());

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/does-not-exist")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/hubs/unknown")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/reference")).StatusCode);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Data_protection_keys_are_stored_in_the_database()
    {
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("hosting-test");
        var secret = protector.Protect("authenticator secret");
        Assert.Equal("authenticator secret", protector.Unprotect(secret));

        var keys = await factory.WithDbAsync(db => db.DataProtectionKeys.CountAsync());
        Assert.True(keys > 0);
    }
}
