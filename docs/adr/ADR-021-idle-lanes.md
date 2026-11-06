# ADR-021: The worker runs what does not conflict, in lanes

- **Status:** Accepted
- **Date:** 2026-11-06
- **Decided by:** the product owner, on 2026-11-05: one worker with slots rather than separate containers,
  capacity read at run time with a protective brake rather than a dynamic controller, and dashboards
  always ahead of imports.
- **Evidence:** `docs/research/2026-11-worker-capacity.md`, section 6.

## Context

After a day's import the worker builds two runs for the delivery, Risk (384 steps) and Planning (128).
They are independent, but they took turns on the claim loop: one step of each, then the poll interval.
A short Risk step waited behind a long Planning one, and the worker slept 5 s after every round, which is
32 minutes of a 384-step run. The owner asked for several workers, chosen by the machine's resources, on
the laptop, the department's VM and MCI's OpenShift.

**The worker process is not the resource.** It used 0.03% of a core during a run on the VM. Everything heavy
is a statement inside ClickHouse, so a second process would add a client, not an engine.

**The runs are CPU at one thread.** 80% of the VM's run statements is CPU, on a machine that keeps 86% of its
CPU idle, and 77 to 96% on the laptop. Measured on the laptop with both runs side by side, each statement
took 0.87 to 1.51 times as long as alone, and the two did **1.55 to 1.69 times** the work of one lane. A
whole run is 161 minutes of statements in one lane and about 100 in two. Nothing failed.

**The three machines differ.** `max_server_memory_usage` is 5.2 GiB on the laptop and 6.4 GiB on the VM, and
the same step peaks at 828 MiB on one and 1.59 GiB on the other. No constant fits, and OpenShift's limits
are not known yet.

## Options

1. **Separate worker containers.** Rejected, by the owner's decision: they would share the one engine that
   is the real limit, and would need locks between them that one process does not.
2. **Lanes in the background, drained when a job is claimed.** A job would be seen within the poll interval
   and no lane would wait for another's step. Rejected: a claimed job then waits for the drain outside its
   lease's renewal, and the worker gains states to get wrong, to save seconds on a file a day.
3. **Rounds on the claim loop (chosen).** The claim loop is untouched, so nothing can run beside a job: a
   round is over before the next claim.
4. **A controller**: PID, or AIMD as in Netflix's `concurrency-limits`. Rejected, by the owner's decision:
   built for thousands of requests a second, and a task here takes minutes, so it would learn too slowly.
5. **Several chunks of one run at once.** Not now. Chunks are free of each other, so it would go further, but
   a run hands out its chunks one at a time, and two lanes are what has been measured.

## Decision

**Idle work runs in rounds.** A task that must have nothing beside it says so (`IIdleTask.RunsAlone`) and goes
first. Today that is the dashboard rebuild, the conflict that matters: it reads what the others may be
planning from, and it is what people are looking at. The others run in lanes, each stepping until
`IdleRound` (a minute) is over. With one lane a round is a step each, in turn, as it always was.

**The worker does not sleep after work.** A task says whether there may be more, and only a round that found
nothing waits the poll interval.

**The number of lanes is read from the engine:**

```
lanes = max(1, min(cores / 2, 60% of max_server_memory_usage / the heaviest step))
```

Capped by `ImportWorker:MaxIdleLanes`, 2, which is what has been measured; 1 turns lanes off.

| | Memory | Cores | Heaviest step, last 14 days | Lanes |
|---|---:|---:|---:|---:|
| Laptop | 5.2 GiB | 12 | 1.10 GiB | 2 |
| Department VM | 6.4 GiB | 6 | 1.59 GiB | 2 |
| VM with no run to learn from | 6.4 GiB | 6 | its 2.5 GB limit | 1 |

- `max_server_memory_usage` and `max_threads`, not the cgroup metrics: ClickHouse has already worked them out
  from wherever it runs. `CGroupMemoryTotal` reads 0 on the laptop and `CGroupMaxCPU` does not exist there.
- The heaviest step comes from `system.query_log`, because nothing else knows it. Before there is a run to
  learn from it is the per-statement limit, the cautious answer.
- 60% leaves the rest to merges, dashboards and searches. Two lanes at their peak take 2.2 GiB of the laptop's
  5.2 and 3.2 GiB of the VM's 6.4.
- Half the cores, because each step holds one and merges and readers need the others.
- Read every ten minutes, 0.18 s on the VM. When it cannot be read the answer is one lane.

**A brake, not an engine.** A step that fails leaves one lane for `ImportWorker:IdleBrake`, 30 minutes. Whatever
failed it, its retries have the machine to themselves, and a run is failed only after three tries.

## Consequences

- A run costs about 100 minutes on the laptop where it cost 161, and the 32 minutes of sleep go in either case.
- **A job waits longer in the worst case.** A round and the steps in progress: about 2.5 minutes, where it was
  about 1.5. Files come about once a day.
- A lane that reaches the end of a round waits for the other's step, up to a minute and a half on the VM. A
  longer round wastes less and keeps a job waiting longer.
- **The VM is not proven.** Its log says the runs are mostly CPU, and only Planning's heaviest three and
  `quality_chunk` wait on the disk, 24 to 41% of their time. Two of those at once would share it. To read after
  the VM's first run in lanes: how long the two runs took, and `PSI_IO_some` beside dashboards being used.
- **Dashboards are not yet ahead of the lanes.** That is step 5, ClickHouse workloads.
- A pressure brake is not built. ClickHouse reports `PSI_CPU_*`, `PSI_MEM_*` and `PSI_IO_*` in
  `system.asynchronous_metrics`, so it needs no access to the host, but its threshold should come from the VM's
  first runs, not from a guess.
- Two worker replicas (step 6) would both step the same run. Each task checks for a job elsewhere, not for
  itself; that has to be closed before there is a second replica.
