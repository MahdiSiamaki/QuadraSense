using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// A file that was registered but never queued can be uploaded again, and is queued then.
/// </summary>
/// <remarks>
/// Registration commits before the job is queued, and the queueing ran on the request's token.
/// A connection dropped between the two left a file with no job: every later upload of the same
/// content was refused as a duplicate of an import that did not exist, and reprocessing needs a
/// job to start from - so that day's file was importable by no route at all. It happened again on
/// 2026-11-03, to the file for 26 October, and nothing said so: a day with no job blocks nothing.
/// So the whole intake now runs off the request's token, and a file that still ends up with no job
/// is listed on the Import Center and queued from there.
/// </remarks>
public sealed class OrphanedUploadTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly UnqueuedFile Stranded = new(
        41, "SQM", "daily_subs_device_sim_info_2026-10-26.csv", "sqm/copy.csv", 10, "operator", DateTimeOffset.UtcNow);

    private readonly List<string> _calls = [];
    private readonly List<(string Method, CancellationToken Token)> _tokens = [];
    private readonly WebApplicationFactory<Program> _factory;
    private long? _existingJob;
    private DateOnly? _queuedFor;
    private bool _storeReachable = true;
    private bool _copyPresent = true;
    private IReadOnlyList<UnqueuedFile> _unqueued = [Stranded];

    public OrphanedUploadTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(9, "operator", "Operator", false,
                        new[] { Permissions.ImportView, Permissions.ImportUploadSqm }
                            .ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IImportFileStore>((method, args) =>
            {
                _calls.Add(method.Name);
                return method.Name switch
                {
                    "SaveAsync" => Task.FromResult(new StoredFile("sqm/copy.csv", 10, "abc")),
                    "DeleteAsync" => Noting(method.Name, args, Task.CompletedTask),
                    "ExistsAsync" => Task.FromResult(_copyPresent),
                    "IsReachableAsync" => Task.FromResult(_storeReachable),
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
            services.AddSingleton(TestStubs.Create<IImportJobRepository>((method, args) =>
            {
                _calls.Add(method.Name);
                return method.Name switch
                {
                    // Known content: registered once already, under file 41.
                    "RegisterFileAsync" => Noting(method.Name, args, Task.FromResult(
                        new RegisteredFile(41, "abc", IsNew: false, _existingJob, "daily.csv"))),
                    "EnqueueAsync" => Queued(args),
                    "WriteAuditAsync" => Noting(method.Name, args, Task.CompletedTask),
                    "GetUnqueuedFilesAsync" => Task.FromResult(_unqueued),
                    "GetWorkerHealthAsync" => Task.FromResult(new WorkerHealth(2, 0, 0, 0, null, 1, 0)),
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
        }));

    /// <summary>Every one of these calls ends in a token: keeps it, for the test that reads them.</summary>
    private T Noting<T>(string method, object?[] args, T result)
    {
        _tokens.Add((method, (CancellationToken)args[^1]!));
        return result;
    }

    private Task<long> Queued(object?[] args)
    {
        _queuedFor = (DateOnly?)args[2];
        return Noting("EnqueueAsync", args, Task.FromResult(77L));
    }

    [Fact]
    public async Task A_registered_file_with_no_job_is_queued_on_the_next_upload()
    {
        using var response = await UploadAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task A_file_that_already_has_a_job_is_still_a_duplicate()
    {
        _existingJob = 12;

        using var response = await UploadAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task Nothing_after_the_file_is_stored_can_be_stopped_by_the_request()
    {
        using var response = await UploadAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Every step from registering to the audit, the duplicate's delete included.
        Assert.Equal(
            ["RegisterFileAsync", "DeleteAsync", "EnqueueAsync", "WriteAuditAsync"],
            _tokens.Select(t => t.Method));
        Assert.All(_tokens, t => Assert.False(t.Token.CanBeCanceled, t.Method));
    }

    [Fact]
    public async Task A_stored_file_with_no_job_is_listed()
    {
        using var client = _factory.CreateClient();
        using var request = Authenticated(HttpMethod.Get, "/api/v1/imports/unqueued-files");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "daily_subs_device_sim_info_2026-10-26.csv",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listed_file_is_queued_from_its_stored_copy_for_the_day_in_its_name()
    {
        using var response = await QueueAsync("SQM", 41);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new DateOnly(2026, 10, 26), _queuedFor);
        Assert.Contains("WriteAuditAsync", _calls);
    }

    [Fact]
    public async Task A_file_no_longer_on_the_list_is_not_queued()
    {
        _unqueued = [];

        using var response = await QueueAsync("SQM", 41);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task A_listed_file_the_store_cannot_read_is_refused_rather_than_queued()
    {
        _copyPresent = false;

        using var response = await QueueAsync("SQM", 41);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task Queuing_a_file_takes_the_upload_permission_of_its_source()
    {
        // The session may upload SQM files and not TAC ones.
        using var response = await QueueAsync("TAC", 41);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task A_file_of_another_source_is_not_queued_under_this_one()
    {
        _unqueued = [Stranded with { SourceCode = "TAC" }];

        using var response = await QueueAsync("SQM", 41);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("EnqueueAsync", _calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Worker_health_says_whether_the_store_can_be_reached(bool reachable)
    {
        _storeReachable = reachable;

        using var client = _factory.CreateClient();
        using var request = Authenticated(HttpMethod.Get, "/api/v1/imports/worker-health");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var health = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reachable, health.RootElement.GetProperty("storeReachable").GetBoolean());
        Assert.Equal(2, health.RootElement.GetProperty("queued").GetInt32());
    }

    private async Task<HttpResponseMessage> QueueAsync(string source, long fileId)
    {
        using var client = _factory.CreateClient();
        using var request = Authenticated(HttpMethod.Post, $"/api/v1/imports/{source}/unqueued-files/{fileId}/queue");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", "sqm_session=operator; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        return request;
    }

    private async Task<HttpResponseMessage> UploadAsync()
    {
        using var client = _factory.CreateClient();
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent("msisdn,imsi,imei,label\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        body.Add(file, "file", "daily_subs_device_sim_info_2026-06-15.csv");

        using var request = Authenticated(HttpMethod.Post, "/api/v1/imports/SQM/upload");
        request.Content = body;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
