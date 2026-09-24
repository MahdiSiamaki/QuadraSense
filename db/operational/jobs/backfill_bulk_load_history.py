#!/usr/bin/env python3
"""Record the pre-platform bulk load as real import history.

WHY THIS EXISTS. The 133 daily files and the initial dump were loaded by a standalone script
before the import platform existed. Without this backfill the Import Center shows an empty
history for data that is plainly there, and - worse - missing-day detection reports all 133
delivered days as missing, because it looks for effective import jobs and finds none.

WHAT IT DOES NOT DO. It does not pretend a worker ran these. Every row it writes is marked
`bulk-load (pre-platform)` and carries a timeline event saying exactly how the data arrived.
History that lies about its own provenance is worse than no history.

The content hashes are real, computed from the files on disk. That matters beyond tidiness: it
is what lets the platform recognise a re-upload of a file that was already loaded this way, and
answer "you already have this" instead of importing it twice.

Usage:
    backfill_bulk_load_history.py --container sqm-postgres \\
        --daily "D:/SQM/New CDR/New CDR" \\
        [--dump "D:/SQM/1 Month/dump_....csv"] [--tac "D:/TAC/DeviceDatabase_TAC1Sep2026.csv"] \\
        [--dates-only]
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import os
import re
import subprocess
import sys

FILENAME_DATE = re.compile(r'(\d{4}-\d{2}-\d{2})')
CHUNK = 1 << 22  # 4 MiB


def sha256_of(path: str) -> tuple[str, int]:
    """Hash a file, returning (hex digest, size in bytes)."""
    digest = hashlib.sha256()
    size = 0
    with open(path, 'rb') as handle:
        while True:
            block = handle.read(CHUNK)
            if not block:
                break
            digest.update(block)
            size += len(block)
    return digest.hexdigest(), size


def quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def clickhouse(container: str, query: str) -> str:
    """Ask the analytics store something, for figures the file itself cannot give."""
    result = subprocess.run(
        ['docker', 'exec', '-i', container, 'clickhouse-client', '--query', query],
        capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError((result.stderr or result.stdout).strip().splitlines()[-1])
    return result.stdout.strip()


def rows_for_day(container: str, business_date: datetime.date) -> int:
    """How many events that day actually holds, read from the event log.

    Recording zero would be a lie of a specific and damaging kind: the Import Center shows a
    row count per import, and a history where every historical day reads 0 rows teaches the
    operator to ignore the column exactly when a real zero would matter.

    So a failure to ask is an error, not a zero. It used to be caught and returned as 0 - and a
    re-run takes the UPDATE branch, so one run with the analytics store unreachable overwrote
    every real count already recorded with that zero.
    """
    return int(clickhouse(
        container,
        f"SELECT count() FROM sqm.binding_event WHERE data_date = '{business_date}'"))


def psql(container: str, sql: str) -> str:
    result = subprocess.run(
        ['docker', 'exec', '-i', container, 'psql', '-U', 'sqm', '-d', 'sqm',
         '-v', 'ON_ERROR_STOP=1', '-q'],
        input=sql, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError((result.stderr or result.stdout).strip())
    return result.stdout


def daily_files(directory: str) -> list[tuple[datetime.date, str]]:
    found = []
    for name in sorted(os.listdir(directory)):
        match = FILENAME_DATE.search(name)
        if match and name.lower().endswith('.csv'):
            found.append((datetime.date.fromisoformat(match.group(1)),
                          os.path.join(directory, name)))
    return sorted(found)


def seed_expected_dates(container: str, dates: list[datetime.date]) -> int:
    """Write the calendar of days that should have been delivered.

    A gap is only a gap against a statement of what was expected. Inferring it from the absence
    of a file cannot distinguish "the delivery failed" from "there was no delivery that day",
    and only the first is a problem worth showing anyone.

    The expected range is every calendar day between the first and last delivery. The seven days
    with no file are marked as such rather than left out, so the reason a day is missing is
    recorded at the point someone asks.
    """
    delivered = set(dates)
    start, end = min(dates), max(dates)

    rows = []
    day = start
    while day <= end:
        note = ('NULL' if day in delivered
                else quote('No file was delivered for this day.'))
        rows.append(f"('SQM', DATE {quote(day.isoformat())}, {note})")
        day += datetime.timedelta(days=1)

    sql = (
        "INSERT INTO imports.expected_business_date (source_code, business_date, note) VALUES\n"
        + ",\n".join(rows)
        + "\nON CONFLICT (source_code, business_date) DO UPDATE SET note = EXCLUDED.note;\n")

    psql(container, sql)
    return len(rows)


def backfill_file(container: str, source: str, path: str, business_date: datetime.date | None,
                  description: str, rows: int = 0) -> str:
    """Register one already-loaded file and the completed job that loaded it."""
    digest, size = sha256_of(path)
    name = os.path.basename(path)

    # The stored path is recorded as the file's real location. These files were never copied
    # into the platform's storage root, and writing a path that does not exist would make the
    # "download the original" affordance lie.
    stored = path.replace('\\', '/')

    date_literal = f"DATE {quote(business_date.isoformat())}" if business_date else 'NULL'

    sql = f"""
DO $backfill$
DECLARE
    v_file_id bigint;
    v_job_id  bigint;
BEGIN
    INSERT INTO imports.import_file
        (source_code, original_file_name, stored_path, file_bytes, sha256, uploaded_by, uploaded_at)
    VALUES ({quote(source)}, {quote(name)}, {quote(stored)}, {size}, {quote(digest)},
            'bulk-load (pre-platform)', now())
    ON CONFLICT (source_code, sha256) DO NOTHING
    RETURNING id INTO v_file_id;

    IF v_file_id IS NULL THEN
        SELECT id INTO v_file_id FROM imports.import_file
         WHERE source_code = {quote(source)} AND sha256 = {quote(digest)};
    END IF;

    SELECT id INTO v_job_id FROM imports.import_job
     WHERE file_id = v_file_id LIMIT 1;

    IF v_job_id IS NULL THEN
        -- started_at and finished_at are left NULL on purpose. Setting them to now() would
        -- make the history report a duration of 0 ms for every historical import, which is not
        -- "fast" - it is "unknown", and the UI renders NULL as an em dash that says so.
        INSERT INTO imports.import_job
            (source_code, file_id, status, business_date, revision, is_effective,
             created_by, created_at, started_at, finished_at, rows_input, rows_inserted,
             rows_valid, rows_committed)
        VALUES ({quote(source)}, v_file_id, 'COMPLETED', {date_literal}, 1, true,
                'bulk-load (pre-platform)', now(), NULL, NULL, {rows}, {rows}, {rows}, {rows})
        RETURNING id INTO v_job_id;

        INSERT INTO imports.import_event (job_id, severity, stage, message, detail)
        VALUES (v_job_id, 'info', NULL, {quote(description)},
                jsonb_build_object('backfilled', true, 'sha256', {quote(digest)}));
    ELSE
        -- Re-running the backfill refreshes the figures rather than doing nothing. The row
        -- counts come from the analytics store, which is the only place that knows them.
        UPDATE imports.import_job
           SET rows_input = {rows}, rows_inserted = {rows},
               rows_valid = {rows}, rows_committed = {rows},
               started_at = NULL, finished_at = NULL
         WHERE id = v_job_id;
    END IF;
END
$backfill$;
"""
    psql(container, sql)
    return digest


def register_active_tac_version(container: str, tac_path: str, rows: int) -> None:
    """Record the snapshot already in use as TAC version 1, ACTIVE.

    Migration 014 made this snapshot analytics version 1 and pointed sqm.tac at it, so the
    product has been resolving manufacturers against it all along. Without a matching row here
    the Import Center reports "no TAC versions recorded" while the dashboard is plainly using
    one - two screens in the same product disagreeing about whether a thing exists.

    There is no diff, because there is nothing to diff against. Those columns stay NULL rather
    than zero: "no previous version" and "nothing changed" are different statements.
    """
    label = tac_version_label(os.path.basename(tac_path))

    sql = f"""
DO $tac$
DECLARE
    v_job_id bigint;
BEGIN
    SELECT j.id INTO v_job_id
      FROM imports.import_job j
      JOIN imports.import_file f ON f.id = j.file_id
     WHERE j.source_code = 'TAC'
     ORDER BY j.created_at DESC LIMIT 1;

    IF v_job_id IS NULL THEN RETURN; END IF;

    INSERT INTO imports.tac_version
        (job_id, analytics_version_id, version_label, dataset_date, status, row_count,
         created_at, activated_at, activated_by)
    VALUES (v_job_id, 1, {quote(label)}, NULL, 'ACTIVE', {rows},
            now(), now(), 'bulk-load (pre-platform)')
    ON CONFLICT (version_label) DO UPDATE
       SET analytics_version_id = 1, row_count = EXCLUDED.row_count;
END
$tac$;
"""
    psql(container, sql)


def tac_version_label(file_name: str) -> str:
    """v2026.09.01 from DeviceDatabase_TAC1Sep2026.csv, or a fallback that is obviously one."""
    match = re.search(r'(\d{1,2})([A-Za-z]{3})(\d{4})', file_name)
    if not match:
        return 'v1'

    months = {m: i for i, m in enumerate(
        ['jan', 'feb', 'mar', 'apr', 'may', 'jun',
         'jul', 'aug', 'sep', 'oct', 'nov', 'dec'], start=1)}
    month = months.get(match.group(2).lower())
    if month is None:
        return 'v1'

    return f'v{match.group(3)}.{month:02d}.{int(match.group(1)):02d}'


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--container', default='sqm-postgres')
    # The stack's container (infra/docker-compose.yml), not the benchmark's 'bench-ch'.
    parser.add_argument('--clickhouse', default='sqm-clickhouse',
                        help='analytics container, for reading the real per-day row counts')
    parser.add_argument('--daily', required=True, help='directory of dated daily files')
    parser.add_argument('--dump', help='the initial one-month dump')
    parser.add_argument('--tac', help='the GSMA TAC snapshot')
    parser.add_argument('--dates-only', action='store_true',
                        help='seed the expected-day calendar and stop (no hashing)')
    args = parser.parse_args()

    files = daily_files(args.daily)
    if not files:
        print(f'no dated daily files in {args.daily}', file=sys.stderr)
        return 1

    dates = [d for d, _ in files]
    expected = seed_expected_dates(args.container, dates)
    print(f'expected-day calendar: {expected} days, '
          f'{dates[0]} .. {dates[-1]} ({expected - len(dates)} with no file)')

    if args.dates_only:
        return 0

    # Every count below comes from the analytics store. Checked once, before anything is written,
    # so an unreachable store stops the run instead of half-recording it.
    try:
        clickhouse(args.clickhouse, 'SELECT 1')
    except Exception as exc:  # noqa: BLE001 - reported, then stop
        print(f'cannot reach the analytics store in container {args.clickhouse!r}: {exc}\n'
              'The row counts come from there, and recording 0 instead would be false. '
              'Pass --clickhouse <container>, or --dates-only to seed the calendar alone.',
              file=sys.stderr)
        return 1

    if args.dump:
        # No business date: the dump covers a 30-day window, not a day. Recording one would be
        # the first of many small lies about what this data is.
        backfill_file(args.container, 'SQM', args.dump, None,
                      'Initial one-month dump, loaded directly into the analytics store before '
                      'the import platform existed. Covers 2025-12-27 to 2026-01-25, so it has '
                      'no single business date.')
        print(f'  registered the initial dump: {os.path.basename(args.dump)}')

    if args.tac:
        rows = int(clickhouse(args.clickhouse,
                              'SELECT count() FROM sqm.tac_all WHERE version_id = 1'))

        backfill_file(args.container, 'TAC', args.tac, None,
                      'GSMA TAC snapshot, loaded directly into the analytics store before the '
                      'import platform existed. Registered here as version 1, already active.',
                      rows=rows)
        register_active_tac_version(args.container, args.tac, rows)
        print(f'  registered the TAC snapshot as active version 1: '
              f'{os.path.basename(args.tac)} ({rows:,} TACs)')

    print(f'hashing and registering {len(files)} daily files')
    for index, (business_date, path) in enumerate(files, start=1):
        backfill_file(
            args.container, 'SQM', path, business_date,
            f'Loaded by the bulk loader before the import platform existed '
            f'(day {index} of {len(files)}).',
            rows=rows_for_day(args.clickhouse, business_date))
        if index % 10 == 0 or index == len(files):
            print(f'  {index}/{len(files)}  {business_date}')

    counts = psql(args.container, """
        SELECT source_code, count(*), count(*) FILTER (WHERE is_effective)
          FROM imports.import_job GROUP BY source_code ORDER BY source_code;
        """)
    print('\nimport_job now holds:')
    print(counts.strip())
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
