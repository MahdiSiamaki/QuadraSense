using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>A stored path cannot leave the storage root, not even into a sibling that shares its name.</summary>
public sealed class FileStoreRootTests
{
    [Fact]
    public async Task A_sibling_directory_whose_name_extends_the_root_is_outside_it()
    {
        var parent = Directory.CreateTempSubdirectory("sqm-root-");
        try
        {
            var root = Path.Combine(parent.FullName, "imports");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(root + "-evil");
            await File.WriteAllTextAsync(Path.Combine(root + "-evil", "secret.txt"), "x",
                TestContext.Current.CancellationToken);

            var store = new DirectoryImportFileStore(
                Options.Create(new ImportStorageOptions { RootPath = root }),
                NullLogger<DirectoryImportFileStore>.Instance);

            // "../imports-evil/secret.txt" resolves to a path that starts with the root's text.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ExistsAsync("../imports-evil/secret.txt", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ExistsAsync("../../etc/passwd", TestContext.Current.CancellationToken));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_root_that_has_left_reads_as_unreachable_and_is_not_made_again()
    {
        var parent = Directory.CreateTempSubdirectory("sqm-root-");
        try
        {
            var root = Path.Combine(parent.FullName, "imports");
            var store = new DirectoryImportFileStore(
                Options.Create(new ImportStorageOptions { RootPath = root }),
                NullLogger<DirectoryImportFileStore>.Instance);

            Assert.True(await store.IsReachableAsync(TestContext.Current.CancellationToken));

            Directory.Delete(root);

            Assert.False(await store.IsReachableAsync(TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_root_that_is_not_there_at_the_start_reads_as_unreachable()
    {
        // A file where a folder would have to be: no directory can be made under it, on any system.
        var blocker = Path.GetTempFileName();
        try
        {
            var store = new DirectoryImportFileStore(
                Options.Create(new ImportStorageOptions { RootPath = Path.Combine(blocker, "imports") }),
                NullLogger<DirectoryImportFileStore>.Instance);

            Assert.False(await store.IsReachableAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}

/// <summary>The API has dashboards to serve whether or not the disk of originals is attached.</summary>
/// <remarks>
/// On the laptop the store is a USB disk. On 2026-11-06 it was away, and the host could not start at all:
/// no dashboard, no Explorer, for a folder only an upload needs. A store that leaves while the API runs was
/// already a hold; one that is away at the start is the same thing.
/// </remarks>
public sealed class ApiWithoutTheStoreTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task The_api_starts_with_the_store_away()
    {
        var blocker = Path.GetTempFileName();
        try
        {
            using var host = factory.WithWebHostBuilder(builder =>
                builder.UseSetting("ImportStorage:RootPath", Path.Combine(blocker, "imports")));
            using var client = host.CreateClient();

            using var response = await client.GetAsync(
                new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
