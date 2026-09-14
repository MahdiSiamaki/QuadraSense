using System.Text;

namespace Sqm.Application.Sql;

/// <summary>
/// Splits a SQL script into individual statements.
/// </summary>
/// <remarks>
/// A semicolon separates statements, except inside a comment or a string literal, where it is
/// just a character. Splitting naively on <c>';'</c> has already broken this project once: a
/// semicolon inside a <c>--</c> comment cut a statement in half, and the resulting syntax error
/// pointed nowhere near the comment that caused it. Tracking comment and string state is the
/// minimum correct implementation.
///
/// PostgreSQL does not need this - Npgsql sends a whole multi-statement script as one command.
/// ClickHouse does: its HTTP interface takes one statement per request. The splitter also buys
/// per-statement timing and a failure that names the statement that failed.
///
/// It lives in the application layer because two things need it: the migrator, which applies
/// schema files, and the ingestion worker, which runs the mart refresh script after an import.
/// One parser, so a script that the migrator splits correctly cannot be split differently by
/// the worker.
/// </remarks>
public static class SqlScript
{
    public static IReadOnlyList<string> Split(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inQuotedIdentifier = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                {
                    inLineComment = false;
                }

                current.Append(ch);
            }
            else if (inBlockComment)
            {
                current.Append(ch);
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    current.Append(next);
                    i++;
                }
            }
            else if (inString || inQuotedIdentifier)
            {
                // Both ClickHouse and PostgreSQL accept a backslash escape inside a literal;
                // consume the escaped character so a trailing quote is not misread as the end.
                if (ch == '\\' && next != '\0')
                {
                    current.Append(ch).Append(next);
                    i++;
                    continue;
                }

                if (inString && ch == '\'')
                {
                    inString = false;
                }
                else if (inQuotedIdentifier && ch == '"')
                {
                    inQuotedIdentifier = false;
                }

                current.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '-' when next == '-':
                        inLineComment = true;
                        current.Append(ch);
                        break;
                    case '/' when next == '*':
                        inBlockComment = true;
                        current.Append(ch).Append(next);
                        i++;
                        break;
                    case '\'':
                        inString = true;
                        current.Append(ch);
                        break;
                    case '"':
                        inQuotedIdentifier = true;
                        current.Append(ch);
                        break;
                    case ';':
                        statements.Add(current.ToString());
                        current.Clear();
                        break;
                    default:
                        current.Append(ch);
                        break;
                }
            }
        }

        statements.Add(current.ToString());
        return [.. statements.Where(HasExecutableContent)];
    }

    /// <summary>True when the fragment holds SQL and not only whitespace and comments.</summary>
    private static bool HasExecutableContent(string statement)
    {
        var code = new StringBuilder();
        foreach (var line in statement.Split('\n'))
        {
            var commentStart = line.IndexOf("--", StringComparison.Ordinal);
            code.Append(commentStart >= 0 ? line[..commentStart] : line);
        }

        return !string.IsNullOrWhiteSpace(code.ToString());
    }

    /// <summary>A short label for progress output: the first line that is not a comment.</summary>
    public static string Label(string statement, int fallbackIndex)
    {
        foreach (var line in statement.Split('\n'))
        {
            var commentStart = line.IndexOf("--", StringComparison.Ordinal);
            var code = (commentStart >= 0 ? line[..commentStart] : line).Trim();
            if (code.Length > 0)
            {
                return code.Length > 72 ? code[..72] : code;
            }
        }

        return $"statement {fallbackIndex}";
    }
}
