# Research: what limits the worker, and what more workers would buy

**Date:** 2026-11-05. **Status:** research for a decision; nothing here is built.
**Asked by:** the product owner: several workers, started according to the machine's resources, running
what does not conflict, with the number chosen automatically, on the laptop, the department VM and MCI's
OpenShift.

Measured on both machines' real data, delivery of 2026-10-31. Every figure on the VM is from
`system.query_log`, the import's own events, or a read-only test tagged in `log_comment`.

---

## 1. What the machines have

| | Laptop | Department VM | MCI OpenShift |
|---|---|---|---|
| CPU | 6 cores, 12 threads; Docker's VM 7.8 GiB | 8 vCPU; ClickHouse limited to 6 | Project limits; requests as low as possible (`03-…`, 1a) |
| Memory for ClickHouse | 5.2 GiB | 6.4 GiB of an 8 GiB limit | Not set yet |
| Disk | NVMe | A virtual disk that reports as rotational, about 117 MB/s while scanning | Central Ceph, not measured |

## 2. Where the time goes

**The worker process is not the resource.** On the VM, during a run: worker 0.03% of a core and 60 MiB;
ClickHouse 105% of a core. Everything heavy is a statement inside ClickHouse. A second worker process
would add a second client, not a second engine.

**One statement at a time.** Over 4.9 hours on the VM, statements above one second averaged a concurrency
of **1.01**, every one at `max_threads` 1. The VM's CPU was 86% idle.

**One day, 11.4 million rows, on the VM (2026-09-26, a late day):**

| Stage | Time |
|---|---:|
| Validate, deduplicate, write the events | 12 s |
| Fold into current state, 23 statements | 43 min 20 s |
| History, feed quality, risk day, models | 2 min 15 s |
| **Dashboard marts** | **2 h 12 min 31 s** |
| Risk and planning runs, 384 and 128 steps, afterwards | more than 2 hours and still going |

**The marts read the same table fourteen times.** Each of 14 statements reads 1,259 million row versions,
66 GiB, of `binding_current FINAL`, for about 9 minutes, to serve 116 million active rows. Together that
is 928 GiB read per refresh.

**Four of those passes compute what is already there.** `agg_dimension_daily` for vendor, manufacturer,
device type and OS takes about 9.5 minutes each. `agg_filter_population`, a merge of the per-model states
that took 66 s for all of them on the VM, holds the same figures. Compared value by value on both
machines: 6,224 values, 0 differences in bindings, subscribers or handsets. Only the two bucket rows of
each dimension, `(unknown device)` and `(unknown TAC)`, are not in it.

**Two independent runs share one lane.** Risk and planning take turns on the worker's idle loop. On the
laptop risk moved about 0.5 steps a minute beside planning and 4.75 a minute after planning finished:
a short risk step waits for a long planning step.

## 3. What more parallelism buys

The heaviest read shape, `binding_current FINAL` grouped by TAC, on the VM, read-only, while the risk and
planning runs went on:

| Threads | Time | Peak memory | CPU |
|---:|---:|---:|---:|
| 1 | 947 s | 2.07 GiB | 535 s |
| 2 | 815 s | 2.41 GiB | 561 s |
| 4 | 743 s | 2.54 GiB | 604 s |

**Four times the threads, 1.27 times the speed.** The CPU time barely moved, because the wait is the
disk: `vmstat` showed 117 MB/s and 21% iowait, and `/proc/pressure/io` 25-50%, against about 6% without
this test.
On this VM, several workers scanning at once would divide the same disk.

So the three environments have three different limits: memory on the laptop, disk on the VM, and on
OpenShift a shared Ceph and limits that are ceilings, not promises. No constant fits all three, which is
the sound part of the request. The limit has to be read where the system runs.

## 4. What the world does, and what applies here

| Approach | What it is | Here |
|---|---|---|
| Competing consumers | Workers claim from a shared queue with `FOR UPDATE SKIP LOCKED` and leases | **Already built**, with `MaxConcurrentJobs` |
| Slots per task type | A fixed number of slots a worker offers, per kind of work | The plain core of what is needed |
| Resource-based slots | Temporal's tuner: PID controllers hold CPU and memory near a target. Temporal itself recommends fixed slots as more predictable, and resource-based ones for fluctuating, light tasks, because what a task will use cannot be known in advance | Our tasks are few, long and heavy, the case it advises against |
| Adaptive concurrency | AIMD, Vegas, gradient: Netflix's `concurrency-limits`, GitLab's Gitaly. Raise the limit slowly, cut it quickly on a bad signal | Good as a brake. Built for thousands of requests a second; a task here takes minutes, so the loop learns slowly |
| Pressure signals | Linux PSI: the share of time work stalled on CPU, memory or IO; beta in Kubernetes 1.34 | The right signal: it saw the VM's disk where CPU figures showed idle. Linux only |
| Server-side scheduling | ClickHouse workloads: `CREATE WORKLOAD`, priorities, weights, `max_concurrent_threads`, and CPU preemption | Keeps dashboards ahead of background work wherever it runs. To be proven on 25.8 |
| Autoscaling replicas | KEDA, Red Hat's Custom Metrics Autoscaler on OpenShift | Scales pods on queue depth. Here the queue is a file a day and the shared limit is ClickHouse, so more pods add nothing. The Red Hat docs found list Prometheus, CPU, memory and Kafka triggers, with no PostgreSQL |
| A workflow engine | Temporal, Airflow, Dagster | A server and a database to run and approve for what a Postgres table and one loop already do |

## 5. Recommendation

In this order, each measured before the next:

1. **Stop repeating work.** Build the marts from the per-model states: 14 passes become about 2. Four of
   the 12 are proven above; the class mix, capability and KPI passes need the same comparison. Estimated,
   not measured: 2 h 12 min to about 25 minutes on the VM, and 928 GiB read to about 130. This also serves
   the laptop, and leaves the disk free for everything else.
2. **Read the capacity at run time.** Threads, memory per statement and the merge cap come from what the
   environment reports (`CGroupMaxCPU`, `CGroupMemoryTotal`), replacing the laptop's constants. The merge
   cap matters most: at 2 GiB, `binding_current` holds 1.26 billion versions, and every `FINAL` pays for
   them (`dedicated-hardware.md`, section 3).
3. **One conductor with slots.** The worker stays one process and runs several tasks at once. Each kind of
   task declares what it touches, and two run together only if a table of conflicts allows it. Risk and
   planning first: independent, and the clearest gain.
4. **Admission by evidence.** A task starts only if its usual peak memory, learned from `query_log`, fits
   the budget, and if pressure is low. A rising signal or a memory error halves the slots; a quiet spell
   adds one. A brake, not an engine.
5. **Dashboards first.** ClickHouse workloads put API reads ahead of all background work.
6. **OpenShift.** The same image and the same logic. Two worker replicas for failover, which the leases
   already allow. No autoscaler until something needs one.

| Conflicts | Rule |
|---|---|
| Fold, day against day | One at a time, in date order (the claim query) |
| Marts against fold | Never together: a mart is the state of one delivery |
| Risk or planning against fold | Yield: they are built for one delivery, and restart on a new one |
| Risk against planning | Free |
| Chunks of one run | Free, up to the slots |
| Validate and write of the next file | Free, but only 12 s of a day: not worth building |

## 6. Not known yet

- Whether two run steps beside each other go twice as fast. The steps read little, so they should, unlike
  the scans of section 3; it needs a test.
- The same scaling test on the laptop's NVMe, and on Ceph.
- Whether ClickHouse workloads behave on 25.8 under our limits.
- What raising the merge cap costs on the VM.

## Sources

- [ClickHouse: workload scheduling](https://clickhouse.com/docs/operations/workload-scheduling)
- [Temporal: worker performance](https://docs.temporal.io/develop/worker-performance/)
- [Kubernetes: PSI metrics graduate to beta](https://www.kubernetes.io/blog/2025/09/04/kubernetes-v1-34-introducing-psi-metrics-beta/)
- [Red Hat: Custom Metrics Autoscaler overview](https://docs.redhat.com/en/documentation/openshift_container_platform/4.16/html/nodes/automatically-scaling-pods-with-the-custom-metrics-autoscaler-operator)
- [Netflix: performance under load](https://netflixtechblog.medium.com/performance-under-load-3e6fa9a60581)
- [GitLab: Gitaly adaptive concurrency limit](https://handbook.gitlab.com/handbook/engineering/architecture/design-documents/gitaly_adaptive_concurrency_limit)
- [PostgreSQL locking for .NET developers](https://milanjovanovic.tech/blog/pessimistic-locking-ef-core-postgresql)
