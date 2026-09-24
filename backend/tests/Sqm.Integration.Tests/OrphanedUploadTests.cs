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
/// job to start from - so that day's file was importable by no route at all.
/// </remarks>
public sealed class OrphanedUploadTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly List<string> _calls = [];
    private readonly WebApplicationFactory<Program> _factory;
    private long? _existingJob;

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
            services.AddSingleton(TestStubs.Create<IImportFileStore>((method, _) =>
            {
                _calls.Add(method.Name);
                return method.Name switch
                {
                    "SaveAsync" => Task.FromResult(new StoredFile("sqm/copy.csv", 10, "abc")),
                    "DeleteAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
            services.AddSingleton(TestStubs.Create<IImportJobRepository>((method, _) =>
            {
                _calls.Add(method.Name);
                return method.Name switch
                {
                    // Known content: registered once already, under file 41.
                    "RegisterFileAsync" => Task.FromResult(
                        new RegisteredFile(41, "abc", IsNew: false, _existingJob, "daily.csv")),
                    "EnqueueAsync" => Task.FromResult(77L),
                    "WriteAuditAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
        }));

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

    private async Task<HttpResponseMessage> UploadAsync()
    {
        using var client = _factory.CreateClient();
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent("msisdn,imsi,imei,label\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        body.Add(file, "file", "daily_subs_device_sim_info_2026-06-15.csv");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/imports/SQM/upload") { Content = body };
        request.Headers.Add("Cookie", "sqm_session=operator; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
