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

    /// <summary>How much of an authored rejection is kept.</summary>
    /// <remarks>
    /// A rejection written by a processor is the product's explanation of itself: it names the
    /// rule, the count and what to do next, and the operator has nothing else to go on. The TAC
    /// structural scan's message runs to about 900 characters and was being cut at 500, mid-word,
    /// losing the sentence that said the file was a damaged transfer and should be downloaded
    /// again - the only part that told anyone what to do.
    /// </remarks>
    private const int AuthoredLimit = 2_000;

    /// <summary>How much of an incidental exception message is kept.</summary>
    /// <remarks>
    /// Driver and engine messages are not written for anyone. ClickHouse in particular quotes the
    /// offending input back, so the message carries a row of the file; keeping 500 characters of
    /// that is enough to recognise it and little enough to read.
    /// </remarks>
    private const int IncidentalLimit = 500;

    private static string Summarise(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();

        return Clip(
            message,
            exception is ImportRejectedException ? AuthoredLimit : IncidentalLimit);
    }

    /// <summary>Shortens a message without leaving it looking like a different failure.</summary>
    /// <remarks>
    /// Cutting at a fixed offset stops mid-word, and a message that stops mid-word reads as
    /// corruption rather than as a message that was shortened. So the cut lands on a word
    /// boundary and says that it happened.
    /// </remarks>
    private static string Clip(string message, int limit)
    {
        if (message.Length <= limit)
        {
            return message;
        }

        const string Ellipsis = " [...]";
        var room = limit - Ellipsis.Length;
        var boundary = message.LastIndexOf(' ', room - 1);

        // A message with no spaces near the limit - a single enormous token - has no boundary
        // worth finding, so take the fixed cut rather than throwing most of it away.
        var cut = boundary > room / 2 ? boundary : room;

        return string.Concat(message.AsSpan(0, cut).TrimEnd(), Ellipsis);
    }
}
