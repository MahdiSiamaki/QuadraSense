using System.Net.Sockets;
using Npgsql;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion;

/// <summary>Decides whether a failure is worth trying again.</summary>
/// <remarks>
/// <para>
/// The distinction is the difference between a queue that recovers on its own and one that
/// hammers a broken file three times before telling anybody. A file whose columns are wrong will
/// have the same columns in five minutes; a connection that was refused may not be refused again.
/// </para>
/// <para>
/// The default for an unrecognised exception is <em>retryable</em>. Getting it wrong in that
/// direction costs two more attempts and a few minutes; getting it wrong in the other direction
/// means a transient blip permanently fails a day's data and someone has to notice and re-run it
/// by hand.
/// </para>
/// </remarks>
internal static class ImportFailure
{
    public static (bool Retryable, string Summary) Classify(Exception exception) => exception switch
    {
        // The file itself is wrong. Trying again changes nothing.
        ImportRejectedException => (false, Summarise(exception)),

        // The original bytes are gone or unreadable.
        FileNotFoundException or DirectoryNotFoundException => (false, Summarise(exception)),
        UnauthorizedAccessException => (false, Summarise(exception)),

        // Transport-level trouble reaching either store.
        HttpRequestException or SocketException or TimeoutException => (true, Summarise(exception)),
        TaskCanceledException => (true, "The operation timed out."),

        // PostgreSQL: transient classes are retryable, everything else is a bug or a constraint
        // violation that will reproduce exactly.
        PostgresException postgres => (IsTransient(postgres.SqlState), Summarise(exception)),
        NpgsqlException => (true, Summarise(exception)),

        IOException => (true, Summarise(exception)),

        _ => (true, Summarise(exception)),
    };

    /// <summary>
    /// PostgreSQL SQLSTATE classes worth a second attempt.
    /// </summary>
    /// <remarks>
    /// Class 40 is transaction rollback (serialization failure, deadlock); class 53 is
    /// insufficient resources; class 57 is operator intervention, which includes the server
    /// shutting down under a running statement; 08 is connection exceptions.
    /// </remarks>
    private static bool IsTransient(string? sqlState) =>
        sqlState is { Length: >= 2 }
        && sqlState[..2] is "08" or "40" or "53" or "57";

    private static string Summarise(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length > 500 ? message[..500] : message;
    }
}
