#!/usr/bin/env python3
"""Run refresh_marts.sql one statement at a time.

WHY NOT JUST PIPE THE FILE IN. Feeding the whole script to clickhouse-client as a single
--multiquery session made heavy aggregations fail non-deterministically with
MEMORY_LIMIT_EXCEEDED, reporting several gigabytes committed against a process using under
1.5 GB. Running each statement as its own query gives each one clean accounting, and it is
what a production job wants anyway: per-statement timing, and a failure that names the
statement that failed instead of aborting an opaque blob.

WHY PYTHON RATHER THAN A SHELL LOOP. Statements are separated by ';', but a ';' also
appears inside SQL comments -- and a naive split silently cut a statement in half, producing
a syntax error that looked nothing like its cause. Splitting correctly means tracking
comments and string literals, which is a parser, not a one-liner.

Usage: run_refresh.py <container> <seq> [sql-file]
"""

from __future__ import annotations

import os
import subprocess
import sys
import time

# A per-query ceiling is what makes this job reliable. Without one, queries enter the
# server-level overcommit arbitration, which kills victims non-deterministically. With it,
# a heavy GROUP BY spills to disk instead: slower, and always correct.
#
# The spill threshold sits below the ceiling so aggregation starts writing to disk well
# before the query approaches being killed.
QUERY_SETTINGS = [
    '--max_memory_usage=3000000000',
    '--max_bytes_before_external_group_by=800000000',
    '--max_threads=3',
]


def split_statements(sql: str) -> list[str]:
    """Split on ';', ignoring separators inside line comments, block comments and strings."""
    statements: list[str] = []
    current: list[str] = []
    i, n = 0, len(sql)
    in_line_comment = in_block_comment = in_string = False

    while i < n:
        ch = sql[i]
        nxt = sql[i + 1] if i + 1 < n else ''

        if in_line_comment:
            if ch == '\n':
                in_line_comment = False
            current.append(ch)
        elif in_block_comment:
            if ch == '*' and nxt == '/':
                in_block_comment = False
                current.append(ch)
                i += 1
                current.append(nxt)
            else:
                current.append(ch)
        elif in_string:
            # ClickHouse escapes a quote inside a string by doubling or backslash.
            if ch == '\\' and nxt:
                current.append(ch)
                i += 1
                current.append(nxt)
            else:
                if ch == "'":
                    in_string = False
                current.append(ch)
        elif ch == '-' and nxt == '-':
            in_line_comment = True
            current.append(ch)
        elif ch == '/' and nxt == '*':
            in_block_comment = True
            current.append(ch)
            i += 1
            current.append(nxt)
        elif ch == "'":
            in_string = True
            current.append(ch)
        elif ch == ';':
            statements.append(''.join(current))
            current = []
        else:
            current.append(ch)
        i += 1

    if ''.join(current).strip():
        statements.append(''.join(current))
    return statements


def is_executable(statement: str) -> bool:
    """True when the fragment contains SQL, not only comments and whitespace."""
    body = []
    for line in statement.splitlines():
        stripped = line.split('--', 1)[0]
        body.append(stripped)
    return bool(''.join(body).strip())


def label_for(statement: str, index: int) -> str:
    for line in statement.splitlines():
        code = line.split('--', 1)[0].strip()
        if code:
            return code[:64]
    return f'statement {index}'


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    container, seq = sys.argv[1], sys.argv[2]
    sql_file = sys.argv[3] if len(sys.argv) > 3 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), 'refresh_marts.sql')

    if not os.path.isfile(sql_file):
        print(f'no such file: {sql_file}', file=sys.stderr)
        return 1

    with open(sql_file, encoding='utf-8') as handle:
        statements = [s for s in split_statements(handle.read()) if is_executable(s)]

    print(f'refreshing marts from {os.path.basename(sql_file)} for seq={seq}')
    print(f'{len(statements)} statements')

    started = time.time()
    failures: list[str] = []

    for index, statement in enumerate(statements, start=1):
        label = label_for(statement, index)
        began = time.time()
        result = subprocess.run(
            ['docker', 'exec', '-i', container, 'clickhouse-client',
             f'--param_seq={seq}', *QUERY_SETTINGS],
            input=statement + ';', capture_output=True, text=True, check=False)
        elapsed = time.time() - began

        if result.returncode == 0:
            print(f'  {elapsed:6.1f}s  {label}')
        else:
            first_error = (result.stderr or '').strip().splitlines()
            detail = first_error[1] if len(first_error) > 1 else (
                first_error[0] if first_error else 'unknown error')
            print(f'  FAILED    {label}')
            print(f'            {detail[:200]}')
            failures.append(label)

    print(f'total {time.time() - started:.0f}s, {len(statements)} statements, '
          f'{len(failures)} failed')
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
