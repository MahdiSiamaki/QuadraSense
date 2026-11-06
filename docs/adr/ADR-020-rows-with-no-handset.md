# ADR-020: Rows with no handset stay stored and are left out of every count

- **Status:** Accepted
- **Date:** 2026-11-05
- **Decided by:** the product owner, on 2026-11-05, from the measurements below: remove `000000` from
  every result, keep the rows, and show them once under data quality.
- **Supersedes:** assumption 2 in `docs/discovery/02-open-questions.md` and the glossary's "never
  silently dropped".

## Context

`000000` is what the source writes for the IMEI when it has no handset. Since discovery it was treated
as a business category: real subscribers whose device the operator did not record. Everything followed
from that. It was shown as `(unknown device)`, the largest row in the Devices catalogue, and counted in
every total, on the argument that hiding it "would understate every total by about a twentieth".

The argument was never measured. It assumed these rows were people with an unknown phone.

## Measurements

The laptop's current state at delivery 247, 2026-11-05. Two samples of contiguous number blocks, about
1% of numbers each and sharing none, read from `binding_current FINAL`.

| | Sample 1 | Sample 2 |
|---|---:|---:|
| Active rows | 1,251,409 | 1,140,559 |
| Active rows with `000000` | 54,929 | 48,505 |
| ...from the initial dump, no daily file has touched them since | 54,891 (99.9%) | 48,483 (99.95%) |
| ...on a SIM with a real handset now | 51,122 (93.1%) | 44,847 (92.5%) |
| ...on a SIM whose real handset ended, none now | not split | 3,658 (7.5%) |
| ...on a SIM that never had a real handset | not split | **0** |

So the population is not what the code and the docs said. It is what is left of the dump, which lists
every binding seen over a 30-day window: a row written when the handset was not known, beside the row
written when it was, and never removed by any file since.

Across the whole store the marts agree. 5,398,819 active rows carry `000000` and 41,088 a malformed
IMEI. `000000` was 12.0% of January's rows and 5.9% of February's, then 0.004% from March to October.

What leaving them out does to the headline, at delivery 247:

| | With them | Without | Change |
|---|---:|---:|---:|
| Active bindings | 116,124,016 | 110,684,109 | -5,439,907 (-4.68%) |
| Subscribers (estimated) | 75,265,656 | 74,984,884 | -0.37% |
| SIM cards (estimated) | 76,688,436 | 76,314,146 | -0.49% |
| Handsets (estimated) | 82,066,166 | 82,066,166 | none |
| TAC coverage | 95.0% | 99.7% | |

## Options

| | What | For | Against |
|---|---|---|---|
| A. **Leave them out when reading** | The rows stay in `binding_current` and `binding_event`; counts skip them | Nothing moves or is lost; reversible with a switch; the marts already hold both populations | The rule lives in every read path, so it must be tested in each |
| B. Out of bindings and device charts only | Subscribers and SIMs keep counting them | A SIM stays a SIM | About 380 thousand SIMs count as live on the strength of one leftover row, and the figures stop agreeing with each other |
| C. A quarantine table | Move the rows out of current state | Keeps the state table clean | Quarantine is for a row someone will repair, and nothing here can be repaired. Every daily `remove` would have to look in two places, or a removed row would stay held forever. 2.1 billion events would need rewriting. |
| D. Delete them | | | The owner's rule: full history, no deletion. Everything must stay rebuildable from the originals |
| E. Change nothing | | | The headline counts rows that are not bindings |

The usual practice points the same way. Kimball's fact-table guidance keeps a row whose key is missing
and points it at a default "unknown" member, with a suspense file as the exception for rows held out to
be fixed. Quarantine patterns in pipelines exist for review and reprocessing. Layered pipelines keep
the raw data whole and filter in the layer people read. The archive this asks for already exists:
the original files, kept indefinitely, and the event log.

## Decision

**A.** A binding is a number, a SIM and a handset, so a row with no handset is not one. Such a row is
any whose TAC is empty: `000000`, or an IMEI that is not 14 digits. It stays stored and is left out of
every count when the count is read.

`IncludeUnknownDevice` already existed as a switch on the dashboard, with marts behind it. Its default
turned from true to false, in the API and the lens, and `Show unknown devices` brings the rows back.

The malformed IMEIs go with them because the rule is the empty TAC, and because they name no handset
either: 41,088 active rows, 0.04%. Fourteen zeros, about 2,000 active rows, do not: they carry a TAC.
That is a smaller case left for the data-quality decision. (Decided the same day: see the update below.)

### Where each figure comes from now

| Figure | How it leaves them out | Follows Show unknown devices |
|---|---|---|
| KPI row, Key figures | The `knownDevice` row of `agg_filter_population`, already built | Yes |
| Top vendors, models, device types, operating systems | Skips the `(unknown device)` row; shares are of the counted population | Yes |
| Device class mix | Skips the `Unknown device` class | No: the card has no lens |
| Daily change and its cumulative line | Subtracts each day's empty-TAC events, and anchors on the counted population | No |
| Vendor movement | Skips the bucket, so shares and the network total follow | No |
| Devices catalogue, and a model's share of the network | Skips the empty TAC, unless someone searches for 000000 | No |
| Capability card | Its `unknown` column, in the mart | No. **Takes effect at the next mart refresh** |

### What stays as it was, on purpose

- **Data quality**, and the Enrichment breakdown on the Data operations dashboard, which accounts for
  every active row: these are the one place the rows are counted. A KPI card for Unknown device or
  Malformed IMEI still reads the true figure, because `KpiSummary` carries them whether counted or not.
- **Subscriber lookup and the binding timeline** show a person's rows as they are. They are evidence.
  (Changed the next day: see the update below.)
- **Explorer** reads raw rows. It is where someone goes to look at what was left out. (Changed the same
  day: see the update below.)
- **Planning's "Not assessable: an unknown device"**: 360,379 numbers at delivery 247. It says why
  those numbers cannot be planned for, and it is built per number in the run. (Changed the same day:
  see the update below.)
- **SIM and handset changes** before March. `agg_sim_change_daily` and `agg_device_change_daily` count
  a number's change between `000000` and a real IMEI as a change, and they hold counts per day, so
  only a rebuild of every day could separate them. From March such rows are 0.004% of the feed.

## Consequences

- The headline moves once, by 4.68%. Anyone comparing with a figure quoted before 2026-11-05 needs this
  ADR, and the Enrichment breakdown reconciles the two.
- Nothing was rebuilt or rewritten, so `Show unknown devices`, or reversing the default, restores the
  old figures at once. The capability card is the exception, and needs a refresh either way.
- Each new read path has to leave these rows out itself. The rule is one line, but it is not enforced
  in one place: the tests that hold it are named in the change.
- The measurements are a statement about this data. If a daily file starts carrying many `000000`
  rows again, as January did, they will be left out too, and Data quality is where it will show.

## Update: IMEIs that are not an IMEI (2026-11-05)

The owner widened the rule the same day: an IMEI that is zero or not acceptable leaves the counts too,
and Planning's row and Explorer follow, but a TAC merely missing from the catalogue is a different thing
and stays. This closes the "smaller case" left open above.

### What makes an IMEI unacceptable

GSMA TS.06 v27, Annex A, lists the reporting bodies, the first two digits of a TAC: current 00 (test),
01, 35, 86, 98 (reserved) and 99; former 10, 30, 33, 44, 45, 49, 50, 51, 52, 53, 54 and 91. A 14-digit
IMEI whose TAC is in the catalogue is always accepted. One whose TAC is not, is judged in this order:

| Class | Test | Active rows at delivery 247 | Decision |
|---|---|---:|---|
| No handset | `000000`, or not 14 digits | 5,439,907 | Left out, as above |
| Zero TAC | `00000000` | 2,118 | **Left out** |
| Shifted IMEI | The feed-quality test: ends in 0, and one digit put back in front makes a GSMA TAC | 176,031 | **Left out** |
| Reporting body GSMA never had | First two digits not in Annex A | 46,095 | **Left out** |
| Valid body, not in the catalogue | None of the above | 124,163 | Stays: a real handset the catalogue may not have yet |
| In the catalogue | | 110,335,702 | Stays |

### One test, on the row

The first measurement judged a whole TAC: a model is shifted if one leading digit makes it a GSMA TAC.
That put 198,519 rows in the class. But the feed-quality monitor, Risk and Planning all judge the IMEI,
which must also end in 0, and a TAC of that shape need not be a shifted one:

| Share of the TAC's IMEIs ending in 0 | TACs | Rows | Rows ending in 0 |
|---|---:|---:|---:|
| 100% | 23,585 | 108,866 | 108,866 |
| 90-99% | 216 | 13,890 | 13,541 |
| 50-89% | 1,748 | 69,884 | 53,071 |
| 20-49% | 206 | 1,068 | 351 |
| Under 20% | 1,711 | 4,811 | 202 |

An ordinary model ends in 0 about one time in ten, which is the last row. So the rule stays on the row,
and "shifted" means one thing everywhere. Of the 22,488 rows of those TACs that do not end in 0, 21,177
name a body GSMA never had and are refused for that, and 1,311 stay.

### How it is applied

**Section 1b of the refresh reads every row anyway, so the rule sits there.** A row it refuses joins the
empty TAC, the one place every mart already treats as "no handset". So the `knownDevice` row, the top
lists, the class mix, capability, the Devices catalogue and Planning's per-model pages all follow with no
rule of their own, and the rows stay in `binding_current` and the event log as they arrived.
`agg_kpi_daily.invalid_imei_bindings` keeps their count, so Enrichment accounts for every row with a
fifth line. It takes effect at the next refresh, and nothing is rebuilt.

| Read path | How |
|---|---|
| Everything built on the marts | Section 1b, as above |
| Raw reads under a number prefix | `AnalyticsSchema.NoHandset`, the same rule in C#. `MartRefreshScriptTests` compares every mart with it, so the two cannot drift apart |
| Explorer, Advanced | Leaves out every row with no handset unless the query names an IMEI or a TAC, and the plan says so |
| Explorer, Simple: a number or a SIM | Left out of Details, Relationships and History, as of 2026-11-06. Searching a handset still shows what it asks for |
| Planning | See below |
| Data quality | Unchanged: it counts them all |
| Risk, and the handset lifecycle | Unchanged: "countable" already leaves out shifted IMEIs and fourteen zeros. A zero TAC with a serial or an unknown body is still countable there, about 48,000 rows |
| Daily change charts | Unchanged: they subtract empty-TAC events only, as the event log is not rebuilt |

**Planning needed more than a filter.** "Not assessable: an unknown device" held 360,378 numbers, and
55,426 of them hold a real handset too: a 2G phone and a leftover `000000`. Leaving those out would have
dropped real customers from the plan. So a row with no handset now decides nothing beside a real one,
and only a number with nothing else keeps reason 1 or 2. The reader leaves those out of every class,
total, list and trend, at once and for every published run. **The numbers that move to their real class
do so from the next run**: until then, the 55,426 are simply out of the published one.

The next run came on 2026-11-06, on the laptop, and moved more than that estimate, which looked at one
reason only. The totals stayed exact at 74,971,692 numbers:

| Sunset class | Before | After | Change |
|---|---:|---:|---:|
| Ready: LTE or 5G | 64,882,932 | 64,882,932 | 0 |
| Ready: LTE-M / NB-IoT only | 15,198 | 15,198 | 0 |
| **Exposed: 2G/3G only** | 9,625,291 | 9,699,289 | **+73,998** |
| Not assessable: a shifted IMEI | 28,116 | 10,753 | -17,363 |
| Not assessable: no handset otherwise | 360,378 | 311,525 | -48,853 |
| Not assessable: a TAC the catalogue lacks | 42,841 | 34,773 | -8,068 |
| Not assessable: a model with no band | 16,936 | 17,222 | +286 |

So 73,998 numbers on 2G or 3G were out of the sunset's count, each behind a row that is not a handset. eSIM
moved further: **4,111,349 numbers** went from "not stated" (13,253,371 to 9,142,022) to none, because a
leftover row no longer decides for the real handset beside it. Capable stayed at 6,459,035.

### What this changes

The counted population at delivery 247, read directly from `binding_current FINAL`:

| | Before | After | Change |
|---|---:|---:|---:|
| Active bindings | 110,684,109 | 110,459,865 | -224,244 (-0.20%) |
| Subscribers (estimated) | 74,984,884 | 74,960,816 | -0.03% |
| SIM cards (estimated) | 76,314,146 | 76,282,821 | -0.04% |
| Handsets (estimated) | 82,066,166 | 81,891,587 | -0.21% |

**A person's lookup follows the rule too** (the owner, 2026-11-06). This ADR first kept it as evidence, and
the widening above only reached Advanced. The owner looked a number up, saw `000000` beside five real
handsets, and that was not what was agreed: Explorer means both of its modes. A row with no handset is left
out of a number's and a SIM's Details, Relationships and History, and the page says when that leaves
nothing: "This number holds no handset" rather than "no bindings found", which would claim the number is
not on the network.
**A mart has no IMEI to ask.** The rule went into `FilterBuilder`, which also writes the filter for the
per-model mart, and that table has one row per model and no IMEI. With any dashboard filter set, every
breakdown beside it failed, on both machines, until the owner saw it on 2026-11-06. The mart now has its own
filter, `ForTacMart`, which leaves out the empty model: section 1b has made that the same rows. No test
saw it because every filtered test set the unknown-device switch, and the page leaves it at its default.
They now send a filter as the page does, and a test with no database holds the mart's filter to its own
columns, because the pipeline skips the real-data tests.

The rule cost section 1b nothing that can be told from noise: 316 to 402 s with it, 350 to 361 s
without. (One run took 901 s, on the night the host disk filled; it is not counted.)

**Its side effect was not free.** Moving 70,000 tiny rows off the per-model mart left each granule
heavier, and the first full test run failed: the NTN classes reached 480 MiB against the API's 476 MiB cap
over a partition in six parts. The answer is a merge of the partition in the refresh, made affordable by
table settings (migration 036, `docs/architecture/11-clickhouse-memory.md` section 8). The NTN classes
now take 325 MiB, and the KPI statement 160 where it took 691.

One doubt stays open. Among the rows that stay, bodies 50 to 54 look suspicious, about four rows per TAC,
and may be shifted in a way the test does not see. Nothing proves it, so by the owner's rule they stay.

## Sources

- GSMA, [TS.06 IMEI Allocation and Approval Process v27](https://imeidb.gsma.com/imei/resources/documents/TS.06%20v27.0.pdf), Annex A
- Kimball Group, [Design Tip #43: Dealing with nulls in the dimensional model](https://www.kimballgroup.com/2003/02/design-tip-43-dealing-with-nulls-in-the-dimensional-model/)
- Kimball Group, [Fact table null](https://www.kimballgroup.com/data-warehouse-business-intelligence-resources/kimball-techniques/dimensional-modeling-techniques/fact-table-null/)
- Databricks community, [Designing an effective quarantine pattern](https://community.databricks.com/t5/data-engineering/designing-an-effective-quot-quarantine-quot-pattern-for-failed/td-p/168345): practitioners' discussion, not vendor guidance
