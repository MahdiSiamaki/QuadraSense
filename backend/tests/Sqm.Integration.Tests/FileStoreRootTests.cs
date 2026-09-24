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
}
