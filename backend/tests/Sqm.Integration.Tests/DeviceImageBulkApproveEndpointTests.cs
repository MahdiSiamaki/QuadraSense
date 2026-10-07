using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Approving a selection of device images is the single approval, repeated: the same transaction per
/// candidate, one audit entry per candidate, and refused whole when two of them are for one model.
/// </summary>
/// <remarks>
/// The real host with the store stubbed. What the store guarantees - the transaction, the race with
/// a rejection - is proven against PostgreSQL in DeviceImageCandidateTests.
/// </remarks>
public sealed class DeviceImageBulkApproveEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<long> _approved = [];
    private readonly List<AuditEntry> _audit = [];
    private string[] _held = [Permissions.DeviceImageManage];

    /// <summary>Candidates 1-4 are for distinct models; 5 shares candidate 1's model. 3 was decided already.</summary>
    private static readonly Dictionary<long, string> Models = new()
    {
        [1] = "samsung|galaxy a12", [2] = "samsung|galaxy a13", [3] = "apple|iphone 13", [4] = "redmi|redmi 9t", [5] = "samsung|galaxy a12",
    };

    public DeviceImageBulkApproveEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(1, "admin", "Administrator", false, _held.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, args) =>
            {
                _audit.Add((AuditEntry)args[0]!);
                return Task.CompletedTask;
            }));
            services.AddSingleton(TestStubs.Create<IDeviceImageCandidateStore>((method, args) => method.Name switch
            {
                "GetModelsAsync" => Task.FromResult<IReadOnlyList<CandidateModel>>(
                    [.. ((IReadOnlyCollection<long>)args[0]!).Where(Models.ContainsKey).Order()
                        .Select(id => new CandidateModel(id, Models[id], "Brand", Models[id].Split('|')[1]))]),
                "ApproveAsync" => Approve((long)args[0]!),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private Task<bool> Approve(long id)
    {
        if (id == 3)
        {
            return Task.FromResult(false);
        }

        _approved.Add(id);
        return Task.FromResult(true);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/image-candidates/approve")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Cookie", "sqm_session=admin; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task Each_candidate_is_approved_on_its_own_and_audited_once()
    {
        var (status, body) = await PostAsync(new { ids = new long[] { 2, 3, 4 } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([2L, 4L], _approved);
        Assert.Equal([2L, 4L], body.GetProperty("approved").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal([3L], body.GetProperty("notAwaitingReview").EnumerateArray().Select(e => e.GetInt64()));

        // One entry per candidate, as three clicks would have written, each saying it was one of three.
        Assert.Equal(3, _audit.Count);
        Assert.All(_audit, e => Assert.Equal(3, Convert.ToInt32(e.Detail!["batch"], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(["promoted", "not awaiting review", "promoted"], _audit.Select(e => (string)e.Detail!["result"]!));
    }

    [Fact]
    public async Task Two_images_for_one_model_are_refused_and_nothing_is_approved()
    {
        var (status, body) = await PostAsync(new { ids = new long[] { 2, 1, 5 } });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("galaxy a12", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(_approved);
        Assert.Empty(_audit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task An_empty_or_oversized_batch_is_refused(int count)
    {
        var (status, _) = await PostAsync(new { ids = Enumerable.Range(1, count).Select(i => (long)i).ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_approved);
    }

    [Fact]
    public async Task A_batch_of_exactly_one_hundred_is_taken()
    {
        var (status, body) = await PostAsync(new { ids = Enumerable.Range(1, 100).Select(i => (long)i).Where(i => i != 5).Append(200).ToArray() });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(100, body.GetProperty("approved").GetArrayLength() + body.GetProperty("notAwaitingReview").GetArrayLength());
    }

    [Fact]
    public async Task A_candidate_named_twice_is_refused()
    {
        var (status, _) = await PostAsync(new { ids = new long[] { 2, 2 } });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_approved);
    }

    [Fact]
    public async Task Without_device_image_manage_it_is_refused()
    {
        _held = [Permissions.DeviceView];

        var (status, _) = await PostAsync(new { ids = new long[] { 2 } });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_approved);
    }
}
