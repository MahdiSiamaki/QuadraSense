#!/usr/bin/env python3
"""Load dated daily delta files into sqm.binding_event.

One file is one day. The date comes from the filename, which the source now supplies
(daily_subs_device_sim_info_YYYY-MM-DD.csv), so `seq` is derived from the date order rather
than from delivery order.

IDEMPOTENT. A day already present is deleted before being re-inserted, so re-running it
replaces rather than doubles.

The presence check matters. Issuing the DELETE unconditionally made a bulk load quadratic:
an ALTER..DELETE is a mutation that rewrites affected parts, so every file paid a cost
proportional to everything loaded before it - the first days took 11s and the rate kept
falling. Checking first turns 133 full-table mutations into 133 cheap counts.

Usage:
    load_daily_files.py <container> <source-dir-in-container> [--from YYYY-MM-DD] [--to ...]
"""

from __future__ import annotations

import argparse
import datetime
import re
import subprocess
import sys
import time

FILENAME_RE = re.compile(r'(\d{4}-\d{2}-\d{2})\.csv$')

# A per-query ceiling keeps each insert out of the server's overcommit arbitration; see
# run_refresh.py for the full reasoning.
SETTINGS = ['--max_memory_usage=3000000000', '--max_threads=4']


def run(container: str, query: str, params: list[str] | None = None) -> str:
    result = subprocess.run(
        ['docker', 'exec', '-i', container, 'clickhouse-client', *(params or []), '--query', query],
        capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip().splitlines()[-1] if result.stderr else 'failed')
    return result.stdout.strip()


def list_files(container: str, directory: str) -> list[tuple[datetime.date, str]]:
    listing = subprocess.run(
        ['docker', 'exec', container, 'sh', '-c', f'ls "{directory}"'],
        capture_output=True, text=True, check=True).stdout.splitlines()

    found = []
    for name in listing:
        match = FILENAME_RE.search(name.strip())
        if match:
            found.append((datetime.date.fromisoformat(match.group(1)), name.strip()))
    return sorted(found)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('container')
    parser.add_argument('directory')
    parser.add_argument('--from', dest='date_from')
    parser.add_argument('--to', dest='date_to')
    args = parser.parse_args()

    files = list_files(args.container, args.directory)
    if not files:
        print(f'no dated files found in {args.directory}', file=sys.stderr)
        return 1

    # seq is assigned over the full set so it stays stable when loading a subset: day N is
    # always day N, whether or not days 1..N-1 are being loaded in this run.
    seq_by_date = {date: index for index, (date, _) in enumerate(files, start=1)}

    selected = files
    if args.date_from:
        selected = [f for f in selected if f[0] >= datetime.date.fromisoformat(args.date_from)]
    if args.date_to:
        selected = [f for f in selected if f[0] <= datetime.date.fromisoformat(args.date_to)]

    print(f'{len(files)} dated files, {files[0][0]} .. {files[-1][0]}')
    print(f'loading {len(selected)}')

    started = time.time()
    total_rows = 0
    failures = 0

    for date, name in selected:
        seq = seq_by_date[date]
        began = time.time()

        try:
            # Only mutate when there is actually something to replace. A mutation rewrites
            # parts, so issuing one per file during a bulk load is quadratic.
            existing = int(run(
                args.container,
                f"SELECT count() FROM sqm.binding_event WHERE data_date = '{date}'"))
            if existing:
                run(args.container,
                    f"ALTER TABLE sqm.binding_event DELETE WHERE data_date = '{date}'",
                    ['--mutations_sync=2'])

            path = f'{args.directory}/{name}'
            insert = (
                f"INSERT INTO sqm.binding_event (seq, data_date, msisdn, imsi, imei, label) "
                f"SELECT {seq}, toDate('{date}'), msisdn, imsi, imei, label "
                f"FROM input('msisdn UInt64, imsi UInt64, imei String, label String') "
                f"FORMAT CSVWithNames")

            # The file is streamed in rather than read server-side: it lives outside
            # user_files_path, and piping avoids granting the server a wider read path.
            piped = subprocess.run(
                ['docker', 'exec', '-i', args.container, 'sh', '-c',
                 f'cat "{path}" | clickhouse-client {" ".join(SETTINGS)} --query "{insert}"'],
                capture_output=True, text=True, check=False)

            if piped.returncode != 0:
                detail = (piped.stderr or '').strip().splitlines()
                print(f'  FAILED  {date}  {detail[-1][:160] if detail else "unknown"}')
                failures += 1
                continue

            rows = int(run(args.container,
                           f"SELECT count() FROM sqm.binding_event WHERE data_date = '{date}'"))
            total_rows += rows
            print(f'  {time.time() - began:6.1f}s  seq {seq:>3}  {date}  {rows:>11,} rows')

        except Exception as error:  # noqa: BLE001 - report and continue to the next day
            print(f'  FAILED  {date}  {error}')
            failures += 1

    elapsed = time.time() - started
    print(f'\nloaded {total_rows:,} rows in {elapsed:.0f}s '
          f'({total_rows / elapsed:,.0f} rows/s), {failures} failed')
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
