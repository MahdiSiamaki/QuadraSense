using System.Collections.Frozen;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Api.Infrastructure;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// An upload that survives a broken connection, and becomes an import only when every byte is in.
/// </summary>
/// <remarks>
/// Over the department VPN two of two browser uploads of a day's file broke partway and had to
/// start again (ADR-018). Here the tus protocol is spoken by hand against the real API and the real
/// file store, in a temporary directory: the bytes go in two requests with a "how much arrived?" in
/// between - what the browser does after a drop - and the finished file must be the one hashed,
/// stored and queued. No database: the repository is a stand-in recording what intake asked of it.
/// </remarks>
public sealed class ResumableUploadTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Endpoint = "/api/v1/imports/uploads";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes(
        string.Concat(Enumerable.Repeat("9121234567,432110123456789,35085748000001,add\n", 2_000)));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqm-resumable-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private readonly WebApplicationFactory<Program> _factory;
    private long? _existingJob;
    private bool _alreadyKnown;

    public ResumableUploadTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host =>
        {
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ImportStorage:RootPath"] = _root }));
            host.ConfigureTestServices(services =>
            {
                services.AddSingleton(TestStubs.Create<ISessionStore>((method, args) => method.Name switch
                {
                    "ResolveAsync" => Task.FromResult<ResolvedSession?>(Session((string)args[0]!)),
                    "TouchAsync" => Task.CompletedTask,
                    "DeleteExpiredAsync" => Task.FromResult(0),
                    _ => throw new NotSupportedException(method.Name),
                }));
                services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
                services.AddSingleton(TestStubs.Create<IImportJobRepository>((method, args) =>
                {
                    _calls.Add(method.Name);
                    return method.Name switch
                    {
                        "RegisterFileAsync" => Task.FromResult(new RegisteredFile(
                            41, ((StoredFile)args[2]!).Sha256, IsNew: !_alreadyKnown, _existingJob, "daily.csv")),
                        "EnqueueAsync" => Task.FromResult(77L),
                        "WriteAuditAsync" => Task.CompletedTask,
                        _ => throw new NotSupportedException(method.Name),
                    };
                }));
            });
        });

    /// <summary>"operator" may upload daily files; "intruder" may too, but did not create the upload.</summary>
    private static ResolvedSession Session(string token) => new(
        Guid.NewGuid(),
        new AuthenticatedUser(
            token == "intruder" ? 10 : 9, token, token, false,
            new[] { Permissions.ImportView, Permissions.ImportUploadSqm }.ToFrozenSet(StringComparer.Ordinal)),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task A_file_sent_in_two_parts_with_a_break_between_is_stored_whole_and_queued()
    {
        using var client = _factory.CreateClient();
        var upload = await CreateAsync(client, "SQM", "daily_subs_device_sim_info_2026-06-15.csv");

        var half = Content.Length / 2;
        Assert.Equal(half, await SendAsync(client, upload, 0, Content[..half]));

        // The connection broke: the browser asks how much arrived, and sends the rest from there.
        Assert.Equal(half, await OffsetAsync(client, upload));
        Assert.Equal(Content.Length, await SendAsync(client, upload, half, Content[half..]));

        using var completed = await CompleteAsync(client, upload);
        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
        var body = await completed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"jobId\":77", body, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Content)), body, StringComparison.Ordinal);

        // In the store, whole, read-only - and nothing of the upload left behind.
        var stored = Assert.Single(Directory.GetFiles(Path.Combine(_root, "SQM"), "*.csv", SearchOption.AllDirectories));
        Assert.Equal(Content, await File.ReadAllBytesAsync(stored, TestContext.Current.CancellationToken));
        Assert.True(File.GetAttributes(stored).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "_resumable")));
        Assert.Contains("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task Completing_before_every_byte_has_arrived_is_refused()
    {
        using var client = _factory.CreateClient();
        var upload = await CreateAsync(client, "SQM", "daily_subs_device_sim_info_2026-06-15.csv");
        await SendAsync(client, upload, 0, Content[..100]);

        using var completed = await CompleteAsync(client, upload);

        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
        Assert.DoesNotContain("RegisterFileAsync", _calls);
    }

    [Fact]
    public async Task Nobody_else_can_add_to_ask_about_or_complete_an_upload()
    {
        using var client = _factory.CreateClient();
        var upload = await CreateAsync(client, "SQM", "daily_subs_device_sim_info_2026-06-15.csv");
        await SendAsync(client, upload, 0, Content);

        using var patch = Patch(upload, 0, Content[..10], "intruder");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(patch, TestContext.Current.CancellationToken)).StatusCode);

        using var head = Request(HttpMethod.Head, upload, "intruder");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(head, TestContext.Current.CancellationToken)).StatusCode);

        using var complete = Request(HttpMethod.Post, upload + "/complete", "intruder");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(complete, TestContext.Current.CancellationToken)).StatusCode);

        using var delete = Request(HttpMethod.Delete, upload, "intruder");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(delete, TestContext.Current.CancellationToken)).StatusCode);
        Assert.DoesNotContain("RegisterFileAsync", _calls);
    }

    [Fact]
    public async Task A_source_the_user_may_not_upload_is_refused_before_any_byte()
    {
        using var client = _factory.CreateClient();
        using var create = Create("TAC", "tac.csv", Content.Length);

        using var response = await client.SendAsync(create, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "_resumable"))
            && Directory.GetFiles(Path.Combine(_root, "_resumable")).Length > 0);
    }

    [Fact]
    public async Task The_same_content_again_is_reported_as_already_imported()
    {
        _alreadyKnown = true;
        _existingJob = 12;
        using var client = _factory.CreateClient();
        var upload = await CreateAsync(client, "SQM", "daily_subs_device_sim_info_2026-06-15.csv");
        await SendAsync(client, upload, 0, Content);

        using var completed = await CompleteAsync(client, upload);

        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
        Assert.Contains("\"existingJobId\":12", await completed.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueAsync", _calls);
    }

    [Fact]
    public async Task A_cancelled_upload_leaves_nothing_behind()
    {
        using var client = _factory.CreateClient();
        var upload = await CreateAsync(client, "SQM", "daily_subs_device_sim_info_2026-06-15.csv");
        await SendAsync(client, upload, 0, Content[..100]);

        using var delete = Request(HttpMethod.Delete, upload, "operator");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete, TestContext.Current.CancellationToken)).StatusCode);

        // The owner too: tus deletes only its own files, and on the VM, 2026-10-27, a cancel left
        // the upload's owner behind for the day-old sweep.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "_resumable")));
        using var complete = Request(HttpMethod.Post, upload + "/complete", "operator");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(complete, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task An_upload_left_alone_for_a_day_is_removed()
    {
        using var client = _factory.CreateClient();
        var upload = await CreateAsync(client, "SQM", "daily_subs_device_sim_info_2026-06-15.csv");
        await SendAsync(client, upload, 0, Content[..100]);

        var id = upload[(upload.LastIndexOf('/') + 1)..];
        var directory = Path.Combine(_root, "_resumable");
        foreach (var file in Directory.GetFiles(directory, id + "*"))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        }

        var uploads = _factory.Services.GetRequiredService<ResumableUploads>();
        Assert.True(await uploads.RemoveAbandonedAsync(TestContext.Current.CancellationToken) >= 1);
        Assert.Empty(Directory.GetFiles(directory, id + "*"));
    }

    // ---------------------------------------------------------------------- the protocol, by hand

    private static async Task<string> CreateAsync(HttpClient client, string source, string fileName)
    {
        using var create = Create(source, fileName, Content.Length);
        using var response = await client.SendAsync(create, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
    }

    private static HttpRequestMessage Create(string source, string fileName, long length)
    {
        var request = Request(HttpMethod.Post, Endpoint, "operator");
        request.Headers.Add("Upload-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("Upload-Metadata",
            $"source {Base64(source)},filename {Base64(fileName)}");
        return request;
    }

    private static async Task<long> SendAsync(HttpClient client, string upload, long offset, byte[] bytes)
    {
        using var patch = Patch(upload, offset, bytes, "operator");
        using var response = await client.SendAsync(patch, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return long.Parse(response.Headers.GetValues("Upload-Offset").Single(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HttpRequestMessage Patch(string upload, long offset, byte[] bytes, string user)
    {
        var request = Request(HttpMethod.Patch, upload, user);
        request.Headers.Add("Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/offset+octet-stream");
        return request;
    }

    private static async Task<long> OffsetAsync(HttpClient client, string upload)
    {
        using var head = Request(HttpMethod.Head, upload, "operator");
        using var response = await client.SendAsync(head, TestContext.Current.CancellationToken);
        return long.Parse(response.Headers.GetValues("Upload-Offset").Single(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient client, string upload)
    {
        using var request = Request(HttpMethod.Post, upload + "/complete", "operator");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string user)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Cookie", $"sqm_session={user}; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        return request;
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
