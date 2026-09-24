using System.Collections.Frozen;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>A model key with a slash in it reaches the store intact.</summary>
/// <remarks>
/// GSMA names such as "redmi note 8/8t" put a slash in the key. The SPA escapes it, and routing
/// leaves %2F undecoded in a route value, so Verify, Remove and Current answered 404 for every
/// such model.
/// </remarks>
public sealed class ModelKeyRouteTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Key = "xiaomi|redmi note 8/8t";

    private readonly List<string> _keys = [];
    private readonly WebApplicationFactory<Program> _factory;

    public ModelKeyRouteTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(5, "curator", "Curator", false,
                        new[] { Permissions.DeviceImageManage }.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IDeviceImageStore>((method, args) =>
            {
                _keys.Add((string)args[0]!);
                return method.Name switch
                {
                    "VerifyAsync" or "DeleteAsync" => Task.FromResult(true),
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
        }));

    [Theory]
    [InlineData("POST", "/verify")]
    [InlineData("DELETE", "")]
    public async Task A_slash_in_the_model_key_survives_the_route(string method, string suffix)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(
            new HttpMethod(method), $"/api/v1/devices/images/{Uri.EscapeDataString(Key)}{suffix}");
        request.Headers.Add("Cookie", "sqm_session=curator; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.Equal(Key, Assert.Single(_keys));
    }
}
