# Discovery addendum — the daily files, dated

**Date:** 2026-09-12
**Source:** `D:\SQM\New CDR\New CDR`
**Status:** **Resolves Q2 and risk RK1**, the largest open unknown in the project.

---

## 1. What these files are

Despite the folder name, **these are not Call Detail Records.** A CDR carries call or session
records — start time, duration, called party, cell, bearer, bytes. None of that is present.

The content is byte-for-byte the same feed already profiled:

```
msisdn,imsi,imei,label
9102717369,432112989600875,35898507497726,remove
9103096358,432113949613545,35129790719989,add
```

Same header, same four columns, same `add` / `remove` domain (verified on files spread across
the whole range: 2026-01-26, 2026-03-01, 2026-05-01, 2026-06-14).

**One file = one day of changes to the device–SIM binding set.** Exactly what was already being
ingested — but now the day is stated in the filename instead of having to be inferred.

| | |
|---|---|
| Files | **133** |
| Range | **2026-01-26 → 2026-06-14** |
| Calendar span | 140 days |
| Missing days | **7** |
| Duplicate dates | 0 |
| Total size | ~47 GB |

---

## 2. These are the same files we already had

Every one of the 82 previously-delivered undated files (`dayli_01.csv`, `d01.csv`, …) matches a
dated file **by exact byte size**:

| Old name | Bytes | Date |
|---|---:|---|
| `dayli_01.csv` | 386,999,920 | 2026-01-26 |
| `dayli_02.csv` | 377,551,863 | 2026-01-27 |
| `dayli_03.csv` | 368,889,144 | 2026-01-28 |
| … | | |
| `dump-06/d10.csv` | 361,986,961 | 2026-04-15 |
| `dump-06/d11.csv` | 346,614,890 | 2026-04-16 |
| `dump-07/01.csv` | 337,869,580 | 2026-04-17 |

**82 matched, 0 unmatched.**

The old delivery was therefore 2026-01-26 → 2026-04-17: **82 consecutive days with no gaps.**
The new set is a superset, extending 58 further days to 2026-06-14.

---

## 3. Two earlier conclusions were wrong, and one was right

### Right: the assumed ordering

Phase 0 ordered the undated files by `(batch folder timestamp, filename)` and validated that
ordering indirectly, via the TAC allocation-date frontier advancing monotonically. **That
ordering is now confirmed exactly correct** — `dayli_01..34` then `dump-04/d01..18` then
`dump-05`, `dump-06`, `dump-07` maps precisely onto 2026-01-26 … 2026-04-17 with no
transpositions.

The inference method worked. It is worth noting that it *only* established order, never dates,
and the report said so at the time.

### Wrong: the suspected gap after the initial dump

Phase 0 flagged a probable **~27-day gap** between the end of the initial dump (2026-01-25) and
the first delta file, reasoning from 82 files counted back from the delivery date.

There is **no gap**. The initial dump ends 2026-01-25 and the first daily file is **2026-01-26** —
the very next day. The arithmetic was wrong because it assumed the last file corresponded to the
delivery date, when deliveries were in fact running about four weeks behind the data.

### Wrong: the estimated date range

Phase 0 guessed the 82 files spanned roughly 2026-02-23 → 2026-05-16. The true span is
**2026-01-26 → 2026-04-17**, about four weeks earlier throughout.

### What this means for the anomaly rates

The 19.18% redundant-add and 1.77% orphan-remove rates were attributed, tentatively, to a
missing window of deltas. **That explanation is now ruled out** — there is no missing window at
the start. The rates must instead come from the initial dump being a month-union rather than a
point-in-time snapshot (Q1), which the 1.59-bindings-per-MSISDN measurement already pointed to.

**Q1 is now the sole remaining explanation and matters more than before.**

---

## 4. The 7 missing days

| Date | Weekday |
|---|---|
| 2026-05-08 | Friday |
| 2026-05-09 | Saturday |
| 2026-05-11 | Monday |
| 2026-05-12 | Tuesday |
| 2026-05-13 | Wednesday |
| 2026-05-17 | Sunday |
| 2026-05-18 | Monday |

All seven fall in a single 11-day window in May, and the cluster overlaps the dates the old
batch exports were generated (folders `dump-02..06` were stamped 2026-05-13, `dump-07`
2026-05-16). The likely reading is that the catch-up export disrupted the normal daily
generation — worth confirming with the source team, and worth requesting the seven files.

Surrounding days are all normal size (~330–370 MB), so nothing suggests the data was merged into
an adjacent file.

---

## 5. Consequences for the system

| Before | Now |
|---|---|
| No dates anywhere in the feed | Every file carries its date |
| Time axis labelled "delivery sequence" | **Real calendar axis** |
| Missing days undetectable | Detectable and enumerable — 7 found immediately |
| Re-sent day undetectable from content | Detectable by date, before hashing |
| `data_date` column nullable and unpopulated | Can be populated at ingest |

The schema already anticipated this: `import_batch.data_date` exists and is nullable precisely so
that dates could be backfilled without migration. No schema change is required.

`seq` remains as the ordering key — it is still what guarantees the fold is applied in order —
but it now has a date beside it rather than standing in for one.

---

## 6. Questions for the source team

1. Can the **7 missing May files** be re-sent?
2. Was the May gap caused by the catch-up export, and can that recur?
3. Deliveries ran roughly four weeks behind the data (a file for 2026-04-17 arrived in a batch
   stamped 2026-05-16). **What is the expected delivery lag going forward?** It determines how
   fresh the dashboard can honestly claim to be.
4. Is `daily_subs_device_sim_info_YYYY-MM-DD.csv` the permanent naming convention?
