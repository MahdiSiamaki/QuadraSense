"""
Real examples for every Data quality category, from the newest days of the feed.

Writes docs/discovery/07-data-quality-examples.md. Read-only: every statement is a SELECT, as the
API's user. Re-run it after new deliveries to refresh the examples:

    python tools/quality_examples.py --from 2026-10-04 --to 2026-10-10

How an example is chosen, so it is never hand-picked: candidates are the bindings with an event in
the window that could belong to the category, in the order of cityHash64(msisdn, imsi, imei); each
is checked against the category's own rule - the same conditions as the measures snapshot
(ClickHouseIngestionStore.RiskSnapshot.cs, QualityCurrentSql / QualityHistorySql) - on the binding's
whole history, and the first five that qualify are kept.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import subprocess
from collections import defaultdict

PER_CATEGORY = 5


def ch(sql: str) -> list[dict]:
    """One read-only query as the API's user, rows as dicts."""
    # The statement goes in on stdin: an IN list of a thousand numbers would pass Windows' 32 K
    # command-line limit.
    done = subprocess.run(
        ['docker', 'exec', '-i', 'sqm-clickhouse', 'clickhouse-client', '--user', 'sqm_app',
         '--password', 'sqm_dev', '--format', 'JSONEachRow',
         '--output_format_json_quote_64bit_integers', '0'],
        input=sql, capture_output=True, text=True, encoding='utf-8')
    if done.returncode != 0:
        raise RuntimeError(f'{done.stderr.strip()}\n--- in ---\n{sql}')
    return [json.loads(line) for line in done.stdout.splitlines() if line.strip()]


TACS = '(SELECT tac FROM sqm.tac)'
RESTORED = ' OR '.join(f"concat('{d}', substring(imei, 1, 7)) IN {TACS}" for d in range(10))
SHIFTED = (f"(match(imei, '^[0-9]{{14}}$') AND endsWith(imei, '0') "
           f"AND substring(imei, 1, 8) NOT IN {TACS} AND ({RESTORED}))")
COUNTABLE = (f"(match(imei, '^[0-9]{{14}}$') AND imei != '00000000000000' AND NOT "
             f"(endsWith(imei, '0') AND substring(imei, 1, 8) NOT IN {TACS} AND ({RESTORED})))")

# The identifier categories: (code, label, the snapshot's condition, sample the window or not).
IDENTIFIERS = [
    ('missing_imei', 'Missing IMEI', "imei IN ('000000', '')", False),
    ('invalid_imei', 'Invalid IMEI', "imei NOT IN ('000000', '') AND NOT match(imei, '^[0-9]{14}$')", False),
    ('zero_imei', 'IMEI of fourteen zeros', "imei = '00000000000000'", False),
    ('shifted_imei', 'Shifted IMEI', SHIFTED, True),
    ('unknown_tac', 'Unknown TAC', f"{COUNTABLE} AND substring(imei, 1, 8) NOT IN {TACS}", False),
    ('invalid_imsi', 'Invalid IMSI', 'length(toString(imsi)) != 15', False),
    # Written so the primary key, which starts with msisdn, can skip every 10-digit number.
    ('invalid_msisdn', 'Invalid number', '(msisdn < 1000000000 OR msisdn >= 10000000000)', False),
]


def key(r: dict) -> tuple:
    return (int(r['msisdn']), int(r['imsi']), r['imei'])


def details(keys: list[tuple]) -> dict:
    """Current state and the whole event history of each binding, by point reads on the number."""
    if not keys:
        return {}
    numbers = ', '.join(sorted({str(k[0]) for k in keys}))
    wanted = set(keys)
    now = {key(r): r for r in ch(f"""
        SELECT msisdn, imsi, imei, active, toString(last_change_date) AS last_change
        FROM sqm.binding_current FINAL WHERE msisdn IN ({numbers})""") if key(r) in wanted}
    hist = {key(r): r for r in ch(f"""
        SELECT msisdn, imsi, imei, max(in_dump) AS dump,
               arraySort(x -> (x.1, x.2), groupArrayArray(events)) AS ev
        FROM sqm.binding_history WHERE msisdn IN ({numbers})
        GROUP BY msisdn, imsi, imei""") if key(r) in wanted}
    out = {}
    for k in keys:
        h = hist.get(k, {'dump': 0, 'ev': []})
        ev = [(dt.date.fromisoformat(e[0]), int(e[1]), int(e[2])) for e in h['ev']]
        out[k] = {'now': now.get(k), 'dump': int(h['dump']), 'ev': ev}
    return out


def models(imeis: set[str]) -> dict:
    tacs = {i[:8] for i in imeis if len(i) == 14}
    if not tacs:
        return {}
    rows = ch(f"""SELECT tac, marketingName, coalesce(nullIf(brandName, ''), manufacturer) AS brand
                  FROM sqm.tac WHERE tac IN ({', '.join(f"'{t}'" for t in tacs)})""")
    # The brand only where the marketing name does not already start with it ("Apple iPhone 11").
    return {r['tac']: (r['marketingName'] if r['marketingName'].lower().startswith(r['brand'].lower())
                       else f"{r['brand']} {r['marketingName']}").strip() for r in rows}


# ------------------------------------------------------------------ the history rules, as the SQL has them

def redundant_adds(ev):
    return [i for i in range(1, len(ev)) if ev[i][2] == 1 and ev[i - 1][2] == 1]


def orphan_removes(ev, dump):
    return [i for i in range(len(ev)) if ev[i][2] == 2 and ((i == 0 and dump == 0) or (i > 0 and ev[i - 1][2] == 2))]


def periods(ev):
    """(index of the remove, days from its add)."""
    return [(i, (ev[i][0] - ev[i - 1][0]).days) for i in range(1, len(ev)) if ev[i][2] == 2 and ev[i - 1][2] == 1]


BUCKETS = [
    ('period_same_day', 'Period: removed the day it was added', lambda d: d == 0),
    ('period_1_day', 'Period: one day', lambda d: d == 1),
    ('period_2_7_days', 'Period: 2 to 7 days', lambda d: 2 <= d <= 7),
    ('period_8_30_days', 'Period: 8 to 30 days', lambda d: 8 <= d <= 30),
    ('period_31_90_days', 'Period: 31 to 90 days', lambda d: 31 <= d <= 90),
    ('period_over_90_days', 'Period: over 90 days', lambda d: d > 90),
]


# ------------------------------------------------------------------ formatting

def fmt_ev(e, mark=False):
    label = 'add' if e[2] == 1 else 'remove'
    text = f"{e[0]:%Y-%m-%d} (delivery {e[1]}) {label}"
    return f"**{text}**" if mark else text


def history_text(d, marks=()):
    ev = d['ev']
    parts = []
    if d['dump']:
        parts.append('in the initial dump (2025-12-27 to 2026-01-25)')
    if len(ev) > 10:
        # The last eight, and every event that puts the binding in its category.
        keep = sorted(set(range(len(ev) - 8, len(ev))) | set(marks))
        parts.append(f'{len(ev) - len(keep)} earlier events not shown')
        parts += [fmt_ev(ev[i], i in marks) for i in keep]
    else:
        parts += [fmt_ev(e, i in marks) for i, e in enumerate(ev)]
    return '; '.join(parts) if parts else 'no event'


def state_text(d):
    n = d['now']
    if n is None:
        return 'not in current state'
    if int(n['active']) == 1:
        return 'held' + (f", last change {n['last_change']}" if n['last_change'] else ', never in a daily file')
    return f"ended {n['last_change']}"


def table(rows, model_of, extra_header=None):
    head = '| # | Number (MSISDN) | SIM (IMSI) | Handset (IMEI) | Model | Now | History (bold: the event that puts it here) |'
    sep = '|---|---|---|---|---|---|---|'
    lines = [head, sep]
    for n, (k, d, marks, note) in enumerate(rows, 1):
        imei = k[2] if k[2] != '' else "''"
        model = model_of.get(k[2][:8], '-') if len(k[2]) == 14 else '-'
        hist = history_text(d, marks)
        if note:
            hist = f'{note}. {hist}'
        lines.append(f'| {n} | {k[0]} | {k[1]} | `{imei}` | {model} | {state_text(d)} | {hist} |')
    return '\n'.join(lines)


# ------------------------------------------------------------------ the categories

def candidates(cond: str, frm: str, to: str, sample: int | None, limit: int, label: str | None = None) -> list[tuple]:
    where = [f"data_date BETWEEN '{frm}' AND '{to}'", f'({cond})']
    if label:
        where.append(f"label = '{label}'")
    if sample:
        where.append(f'cityHash64(msisdn, imsi, imei) % {sample} = 0')
    rows = ch(f"""
        SELECT msisdn, imsi, imei FROM sqm.binding_event WHERE {' AND '.join(where)}
        GROUP BY msisdn, imsi, imei ORDER BY cityHash64(msisdn, imsi, imei) LIMIT {limit}""")
    return [key(r) for r in rows]


def in_window(e, frm, to):
    return frm <= e[0] <= to


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--from', dest='frm', default='2026-10-04')
    ap.add_argument('--to', dest='to', default='2026-10-10')
    ap.add_argument('--out', default='docs/discovery/07-data-quality-examples.md')
    a = ap.parse_args()
    frm, to = dt.date.fromisoformat(a.frm), dt.date.fromisoformat(a.to)

    run = ch("""SELECT run_id, toString(as_of) AS as_of FROM sqm.risk_run FINAL
                WHERE state = 'published' ORDER BY as_of DESC, run_id DESC LIMIT 1""")[0]
    counts = {r['category']: r for r in ch(f"""
        SELECT category, sum(bindings) AS bindings, sum(numbers) AS numbers, sum(periods) AS events
        FROM sqm.quality_chunk WHERE run_id = {run['run_id']} GROUP BY category""")}

    sections = []

    # Identifiers: any binding with an event in the window and the identifier the rule names.
    ident = []
    for code, label, cond, sample in IDENTIFIERS:
        window = ch(f"""SELECT count() AS rows, uniqExact(msisdn, imsi, imei) AS bindings
                        FROM sqm.binding_event WHERE data_date BETWEEN '{a.frm}' AND '{a.to}' AND ({cond})""")[0]
        values = []
        if code == 'invalid_imei':
            # One value is 83% of these rows: five hash-first examples would all be '000100'. One
            # example for each of the five commonest values instead - still the hash-first binding.
            values = ch(f"""
                SELECT imei, length(imei) AS len, count() AS rows, uniqExact(msisdn) AS numbers
                FROM sqm.binding_event WHERE data_date BETWEEN '{a.frm}' AND '{a.to}' AND ({cond})
                GROUP BY imei ORDER BY rows DESC LIMIT 10""")
            keys = []
            for v in values[:PER_CATEGORY]:
                keys += candidates(f"imei = '{v['imei']}'", a.frm, a.to, None, 1)
        else:
            keys = candidates(cond, a.frm, a.to, 500 if sample else None, PER_CATEGORY)
        older = []
        if len(keys) < PER_CATEGORY:
            # Too few in the window: the newest before it, said so on each row.
            older = [key(r) for r in ch(f"""
                SELECT msisdn, imsi, imei, max(data_date) AS last FROM sqm.binding_event
                WHERE data_date < '{a.frm}' AND ({cond})
                GROUP BY msisdn, imsi, imei
                ORDER BY last DESC, cityHash64(msisdn, imsi, imei) LIMIT {PER_CATEGORY - len(keys)}""")]
        d = details(keys + older)
        rows = []
        for k in keys:
            marks = [i for i, e in enumerate(d[k]['ev']) if in_window(e, frm, to)]
            rows.append((k, d[k], marks, ''))
        for k in older:
            ev = d[k]['ev']
            last = max((e[0] for e in ev), default=None)
            marks = [i for i, e in enumerate(ev) if e[0] == last]
            rows.append((k, d[k], marks, f'Before the window - newest available, {last}'))
        ident.append((code, label, cond, window, rows, values))

    # Sequences and lifetimes: removes (or adds) in the window, judged on the whole history.
    removes = candidates('1', a.frm, a.to, 50, 1500, 'remove')
    adds = candidates('1', a.frm, a.to, 50, 800, 'add')
    dr, da = details(removes), details(adds)

    found = defaultdict(list)
    for k in removes:
        d = dr[k]
        ev = d['ev']
        orph = [i for i in orphan_removes(ev, d['dump']) if in_window(ev[i], frm, to)]
        if orph and len(found['orphan_remove']) < PER_CATEGORY:
            prev = 'the binding was not held: ' + ('its first event, and it was not in the dump' if orph[0] == 0
                                                     else 'the event before it was also a remove')
            found['orphan_remove'].append((k, d, orph[:1], prev))
        for i, days in periods(ev):
            if not in_window(ev[i], frm, to):
                continue
            for code, _, test in BUCKETS:
                if test(days) and len(found[code]) < PER_CATEGORY:
                    found[code].append((k, d, [i - 1, i], f'{days} day{"s" if days != 1 else ""} from add to remove'))
    # Added while already held: rare in the newest files - in numbers 9120000000-9120999999, 83,833
    # of 4,598,327 bindings had one ever and 281 in 2026-10-04..10 (measured 2026-10-21) - so a sample
    # of the window's adds finds none. Taken from that slice instead, with the snapshot's own SQL.
    red_rows = ch(f"""
        SELECT msisdn, imsi, imei FROM (
            SELECT msisdn, imsi, imei, arraySort(x -> (x.1, x.2), groupArrayArray(events)) AS ev
            FROM sqm.binding_history WHERE msisdn BETWEEN 9120000000 AND 9120999999
            GROUP BY msisdn, imsi, imei)
        WHERE arrayCount(i -> ev[i].3 = 1 AND i > 1 AND ev[i - 1].3 = 1
                              AND ev[i].1 BETWEEN '{a.frm}' AND '{a.to}', arrayEnumerate(ev)) > 0
        ORDER BY cityHash64(msisdn, imsi, imei) LIMIT {PER_CATEGORY}""")
    rk = [key(r) for r in red_rows]
    drd = details(rk)
    for k in rk:
        ev = drd[k]['ev']
        red = [i for i in redundant_adds(ev) if in_window(ev[i], frm, to)]
        found['redundant_add'].append((k, drd[k], [red[0] - 1, red[0]], 'the event before it was also an add'))
    for k in adds:
        d = da[k]
        ev = d['ev']
        if ev and ev[-1][2] == 1 and in_window(ev[-1], frm, to) and len(found['still_held']) < PER_CATEGORY:
            found['still_held'].append((k, d, [len(ev) - 1], 'last event an add, in the window'))

    # Held since the dump, never changed: no dated event by definition, so not from the window -
    # from one slice of numbers, by the same hash order.
    untouched = ch("""
        SELECT msisdn, imsi, imei FROM sqm.binding_current FINAL
        WHERE msisdn BETWEEN 9120000000 AND 9120099999 AND active = 1 AND last_change_date IS NULL
        ORDER BY cityHash64(msisdn, imsi, imei) LIMIT 5""")
    uk = [key(r) for r in untouched]
    du = details(uk)
    found['untouched_since_dump'] = [(k, du[k], [], '') for k in uk]

    imeis = {k[2] for _, _, _, _, rows, _ in ident for (k, _, _, _) in rows}
    imeis |= {k[2] for v in found.values() for (k, _, _, _) in v}
    model_of = models(imeis)

    # Day by day: the four judged checks, as rows of the window's files.
    day_checks = [
        ('UnknownDevice', 'Unknown-device rows', "imei = '000000'"),
        ('MalformedImei', 'Malformed IMEIs', "length(imei) != 14 AND imei != '000000'"),
        ('ShiftedImei', 'IMEIs shifted by one digit',
         f"length(imei) = 14 AND endsWith(imei, '0') AND imei != '00000000000000' AND tac NOT IN {TACS} AND ({RESTORED})"),
    ]
    day_rows = {}
    for code, _, cond in day_checks:
        day_rows[code] = ch(f"""
            SELECT toString(data_date) AS day, seq, msisdn, imsi, imei, toString(label) AS label
            FROM sqm.binding_event WHERE data_date BETWEEN '{a.frm}' AND '{a.to}' AND ({cond})
              AND cityHash64(seq, msisdn, imei) % 7 = 3
            ORDER BY cityHash64(seq, msisdn, imsi, imei) LIMIT {PER_CATEGORY}""")
    multi = ch(f"""
        SELECT toString(data_date) AS day, imsi, numbers, rows FROM sqm.dq_multi_number_sim_day
        WHERE data_date BETWEEN '{a.frm}' AND '{a.to}'
        ORDER BY cityHash64(data_date, imsi) LIMIT {PER_CATEGORY}""")
    multi_rows = {}
    for m in multi:
        multi_rows[(m['day'], m['imsi'])] = ch(f"""
            SELECT msisdn, imei, toString(label) AS label FROM sqm.binding_event
            WHERE data_date = '{m['day']}' AND imsi = {m['imsi']} ORDER BY msisdn, label""")
    daily = ch(f"""
        SELECT toString(data_date) AS day, rows_total, unknown_device_rows, malformed_imei_rows,
               shifted_imei_rows, unknown_tac_rows, multi_number_sims, sims_total
        FROM sqm.dq_daily FINAL WHERE data_date BETWEEN '{a.frm}' AND '{a.to}' ORDER BY data_date""")

    write(a, run, counts, ident, found, model_of, daily, day_rows, multi, multi_rows)


def n(x):
    return f'{int(x):,}'


def write(a, run, counts, ident, found, model_of, daily, day_rows, multi, multi_rows):
    c = lambda code: counts.get(code, {'bindings': 0, 'numbers': 0, 'events': 0})
    L = []
    L.append('# Data quality — every category, with real examples from the newest days')
    L.append('')
    L.append(f"**Date:** {dt.date.today():%Y-%m-%d}, on the data through {run['as_of']}. Examples from the "
             f"files of **{a.frm} to {a.to}**.")
    L.append(f"**Counts:** the published measures snapshot the Data quality page shows - run "
             f"{run['run_id']}, as of {run['as_of']}.")
    L.append('**Status:** documentation, read-only. Full identifiers, by the product owner\'s decision '
             '(2026-10-21). Generated by `tools/quality_examples.py`; re-run it to refresh the examples.')
    L.append('**Read with:** `docs/architecture/15-feed-quality.md` (the day-by-day checks), '
             '`docs/discovery/05-shifted-imei-verification.md` (the shifted shape), ADR-014 (the snapshot).')
    L.append('')
    L.append('---')
    L.append('')
    L.append('## How the examples were chosen')
    L.append('')
    L.append('Never by hand. For each category the candidates are the bindings with an event in the window '
             'that could belong to it, in the order of `cityHash64(msisdn, imsi, imei)`; each is checked '
             'against the category\'s own rule - the same conditions as the snapshot '
             '(`ClickHouseIngestionStore.RiskSnapshot.cs`, `QualityCurrentSql` and `QualityHistorySql`) - on '
             'the binding\'s **whole** history, and the first five that qualify are kept. A history is every '
             'add and remove of that number + SIM + handset, oldest first, with the delivery it came in; the '
             'initial dump is not in the event log, only a flag. "Now" is current state after delivery 246.')
    L.append('')
    L.append('**Delivery numbers are not in date order.** A delivery is numbered when it is imported, and '
             'days imported late have later numbers - 2026-09-11 is delivery 203, after delivery 230 for 2026-09-07. '
             'Events are ordered by date first, then delivery, as the snapshot orders them. The history '
             'shows the last eight events and every event that puts the binding in its category.')
    L.append('')
    L.append('These are facts about the **feed**, not about the subscribers: a binding the feed adds twice '
             'or removes while not holding it says how the operator\'s files are written.')
    L.append('')

    L.append('## Identifiers — current state')
    L.append('')
    L.append('Bindings whose number, SIM or handset identifier is missing, malformed or unknown. The '
             'snapshot counts every binding in current state, held or ended; the examples are bindings '
             'with an event in the window.')
    L.append('')
    meanings = {
        'missing_imei': "The handset's identity is the feed's `000000`: the network did not know the device.",
        'invalid_imei': "The IMEI is neither 14 digits nor the `000000` sentinel - cut short, padded or not numeric.",
        'zero_imei': 'Fourteen zeros: a placeholder, not a handset.',
        'shifted_imei': 'The first digit dropped and a `0` added, so the TAC is not a real one (`05-…`).',
        'unknown_tac': 'A well-formed IMEI whose TAC the active GSMA version does not know. Shifted IMEIs are not counted here.',
        'invalid_imsi': "The SIM's IMSI is not 15 digits.",
        'invalid_msisdn': 'The phone number (MSISDN) is not 10 digits.',
    }
    for code, label, cond, window, rows, values in ident:
        L.append(f'### {label} (`{code}`)')
        L.append('')
        L.append(meanings[code])
        L.append('')
        shown = cond if len(cond) < 160 else cond.split(' AND (')[0] + ' AND <one leading digit makes a GSMA TAC>'
        L.append(f'- **Rule:** `{shown}`')
        L.append(f"- **Snapshot:** {n(c(code)['bindings'])} bindings, {n(c(code)['numbers'])} numbers.")
        L.append(f"- **In the window:** {n(window['rows'])} rows, {n(window['bindings'])} bindings.")
        L.append('')
        if values:
            total = int(window['rows'])
            L.append('The commonest values in the window - one example of each of the first five below:')
            L.append('')
            L.append('| IMEI | Digits | Rows | Share | Numbers |')
            L.append('|---|---:|---:|---:|---:|')
            for v in values:
                L.append(f"| `{v['imei']}` | {v['len']} | {n(v['rows'])} | {100 * int(v['rows']) / total:.2f}% | {n(v['numbers'])} |")
            L.append('')
        L.append(table(rows, model_of) if rows else '_No binding of this category had an event in the window._')
        L.append('')

    L.append('## Sequences — events the feed\'s own history contradicts')
    L.append('')
    for code, label, meaning in [
        ('redundant_add', 'Added while already held',
         'The feed added the binding when it already held it: an add with no remove since the last add.'),
        ('orphan_remove', 'Removed while not held',
         'The feed removed the binding when it did not hold it: a remove with no add before it, outside the '
         'initial dump - a binding in the dump starts held, so its first remove is not an orphan.'),
    ]:
        L.append(f'### {label} (`{code}`)')
        L.append('')
        L.append(meaning)
        L.append('')
        L.append(f"- **Snapshot:** {n(c(code)['bindings'])} bindings, {n(c(code)['events'])} such events.")
        if code == 'redundant_add':
            L.append('- **In the newest files it is rare.** In the numbers 9120000000 to 9120999999, 83,833 of '
                     '4,598,327 bindings were ever added while already held, and 281 in this window. The '
                     'examples are from that slice, chosen by the same hash order with the SQL of the snapshot.')
        L.append('')
        L.append(table(found[code], model_of))
        L.append('')

    L.append('## Lifetimes — how long the feed held a binding')
    L.append('')
    L.append('A period is the whole days from an add to the remove that ends it. Shown as a distribution, '
             'not a flag: a period of a day or less is how most bindings behave in this feed.')
    L.append('')
    untouched_meaning = ('Not yet removed, and no daily file has mentioned it: held since before 2026-01-26. '
                         'It has **no dated event**, so the window cannot apply: these five are from one slice '
                         'of numbers (9120000000 to 9120099999), in the same hash order.')
    for code, label in [('untouched_since_dump', 'Held since the initial dump, never changed')] + \
            [(b[0], b[1]) for b in BUCKETS] + [('still_held', 'Still held')]:
        L.append(f'### {label} (`{code}`)')
        L.append('')
        if code == 'untouched_since_dump':
            L.append(untouched_meaning)
        elif code == 'still_held':
            L.append("The binding's last event is an add (here: an add in the window), or it came from the "
                     'initial dump and was never removed.')
        L.append('')
        L.append(f"- **Snapshot:** {n(c(code)['bindings'])} bindings" +
                 (f", {n(c(code)['events'])} periods." if code.startswith('period') else '.'))
        L.append('')
        if code == 'period_same_day' and not found[code]:
            L.append('_None: the snapshot counts no such period anywhere in the history, and none ended in '
                     'the window. The feed never adds and removes the same binding on the same day '
                     '(`04-…`, "What was ruled out")._')
        else:
            L.append(table(found[code], model_of))
        L.append('')

    L.append('## Day by day — each daily file against the ordinary days')
    L.append('')
    L.append('Counted per file by the feed-quality monitor (`dq_daily`), judged against the reference days '
             '2026-01-26 to 2026-07-26: flagged above max(5 × their median, 0.01%) (`15-feed-quality.md`).')
    L.append('')
    L.append('| Day | Rows | Unknown device | Malformed IMEI | Shifted IMEI | Unknown TAC | SIMs with several numbers |')
    L.append('|---|---:|---:|---:|---:|---:|---:|')
    for d in daily:
        rows = int(d['rows_total'])
        pct = lambda x: f"{n(x)} ({100 * int(x) / rows:.3f}%)"
        L.append(f"| {d['day']} | {n(rows)} | {pct(d['unknown_device_rows'])} | {pct(d['malformed_imei_rows'])} | "
                 f"{pct(d['shifted_imei_rows'])} | {pct(d['unknown_tac_rows'])} | "
                 f"{n(d['multi_number_sims'])} of {n(d['sims_total'])} SIMs ({100 * int(d['multi_number_sims']) / int(d['sims_total']):.2f}%) |")
    L.append('')
    # Thresholds: 5 x the reference median, floor 0.01% - medians from 15-feed-quality.md.
    thresholds = [('shifted_imei_rows', 'rows_total', 0.445, 'shifted IMEIs', 'rows'),
                  ('multi_number_sims', 'sims_total', 0.055, 'SIMs with several numbers', 'SIMs'),
                  ('malformed_imei_rows', 'rows_total', 0.14, 'malformed IMEIs', 'rows'),
                  ('unknown_device_rows', 'rows_total', 0.025, 'unknown-device rows', 'rows')]
    verdicts = []
    for col, base, limit, name, unit in thresholds:
        flagged = [d['day'] for d in daily if 100 * int(d[col]) / int(d[base]) > limit]
        verdicts.append(f"**{name}** - threshold {limit}% of {unit}: "
                        + (f"flagged on {len(flagged)} of {len(daily)} days" if flagged else 'flagged on no day'))
    L.append('Judged (thresholds: 5 × the reference median, `15-feed-quality.md`): ' + '; '.join(verdicts) + '.')
    L.append('')
    L.append('"Unknown TAC" here counts every 14-digit IMEI whose TAC GSMA does not know, the shifted ones '
             'included - nearly all of them. The snapshot’s Unknown TAC category leaves the shifted ones out.')
    L.append('')
    for code, label in [('UnknownDevice', 'Unknown-device rows'), ('MalformedImei', 'Malformed IMEIs'),
                        ('ShiftedImei', 'IMEIs shifted by one digit')]:
        L.append(f'### {label}')
        L.append('')
        if code == 'MalformedImei':
            L.append('`000100` is 83% of these rows; the other shapes are in the Invalid IMEI table above.')
            L.append('')
        L.append('| # | Day | Delivery | Number | SIM | IMEI | Label |')
        L.append('|---|---|---:|---|---|---|---|')
        for i, r in enumerate(day_rows[code], 1):
            L.append(f"| {i} | {r['day']} | {r['seq']} | {r['msisdn']} | {r['imsi']} | `{r['imei']}` | {r['label']} |")
        L.append('')
    L.append('### SIMs with several numbers')
    L.append('')
    L.append('A SIM that the day\'s file puts under more than one phone number (`dq_multi_number_sim_day`).')
    L.append('')
    for i, m in enumerate(multi, 1):
        rows = multi_rows[(m['day'], m['imsi'])]
        L.append(f"{i}. **{m['day']}, SIM {m['imsi']}** - {m['numbers']} numbers, {m['rows']} rows:")
        for r in rows:
            L.append(f"   - {r['msisdn']} · `{r['imei']}` · {r['label']}")
    L.append('')
    open(a.out, 'w', encoding='utf-8', newline='\n').write('\n'.join(L) + '\n')
    print('wrote', a.out)


if __name__ == '__main__':
    main()
