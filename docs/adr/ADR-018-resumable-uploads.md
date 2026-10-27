# ADR-018: Uploads resume after a broken connection, with the tus protocol

- **Status:** Accepted
- **Date:** 2026-10-27
- **Decided by:** the product owner chose resumable uploads, with tus as the standard way to build
  them, on 2026-10-26 - as the step after progress and cancel (`09-import-platform.md` D11-D13) -
  and asked for it on 2026-10-27.

## Context

A daily file is ~370 MB and was sent as one multipart request. On the laptop's own network that is
seconds. Over the department's VPN, where the files are uploaded to the department VM, it measured
(`docs/operations/deployment/02-department-vm.md` §9):

| | |
|---|---|
| throughput | 80-330 KB/s |
| the tunnel | drops about every 15 minutes |
| browser uploads of one day | **2 of 2 failed**, at 12.6 and 16.5 minutes, each in the minute the tunnel dropped (nginx 400, the API 499) |

A single request that breaks is lost whole, and the next try starts from byte zero into the same
fifteen-minute window. The days were carried instead as 5 MB compressed pieces over `scp`, retried
one by one and queued from the shell (`--import-file`) - which works, and is not something an
operator should have to do.

## Options

| | What | Against it |
|---|---|---|
| **A. tus** | The open protocol for resumable uploads: create an upload, send bytes, after a break ask how many arrived (HEAD gives the offset) and send the rest; terminate to give up; expire what is abandoned. `tusdotnet` on the API, `tus-js-client` in the page | Two dependencies |
| B. Our own chunked endpoints | Upload in numbered pieces to endpoints written here | The offset bookkeeping, concurrency, expiry and termination tus already defines and both libraries already test - written and tested again, for a protocol only this system speaks |
| C. Leave it | One request; carry large files by hand | The failure above, for every day uploaded from outside |

## Decision

**A.** `tusdotnet` 2.12.0 (MIT, its .NET 6 build has no dependencies; NuGet through Nexus's
`nuget-group`) and `tus-js-client` 4.3.1 (MIT; npm through Nexus's `npm-public`).

- **tus carries the bytes and nothing else.** `POST /api/v1/imports/uploads` creates an upload named
  by its source and file; who may create one is decided there, by the same check and audit entry as
  the single upload's (`ImportEndpoints.RefuseUploadAsync`), before a byte is accepted. PATCH sends
  bytes, HEAD reports how many arrived, DELETE gives up.
- **What the file becomes is decided where it always was.** When every byte is in, the page calls
  `POST /api/v1/imports/uploads/{id}/complete`: the file is hashed (read once - a hash cannot be
  carried across requests), moved into the store by rename (the pieces gather in `_resumable` under
  the storage root, the same volume), and handed to the same intake as any upload, which answers the
  same way: 201 with the queued import, or 409 with the one it duplicates. The permission is asked
  again at completion.
- **An upload is its creator's.** Its id is unguessable, but an id is not a permission: the creator
  is recorded beside it, and nobody else can add to it, ask about it, finish it or delete it - they
  are answered 404, so an id reveals nothing about anyone else's uploads.
- **Abandoned uploads go.** One untouched for 24 hours is removed by the API every hour, finished
  ones never completed included.
- **In the page,** after a break the client retries by itself - quickly at first, then a minute apart
  for about forty minutes, longer than the VPN takes to come back - and the queue says "Connection
  lost - resuming where it stopped". The same file dropped again after a reload continues the earlier
  upload (tus remembers it in the browser by name, size and date). Cancel terminates it on the server.
  The client library is loaded only when an upload starts: in the page's entry chunk it measured
  15.5 KB gzipped (30.3 -> 45.8 KB), on every page.
- **nginx** streams the tus path as it does the single upload: `client_max_body_size 11g`, no
  request buffering, 900 s timeouts.

The single-request endpoint stays: the tests of the intake use it, and it costs nothing to keep.

## Consequences

- Breaks no longer cost the bytes already sent. A file still has to be read once more on the server
  to be hashed (~370 MB, seconds), which a single upload did while it arrived.
- `_resumable` under the storage root holds the pieces of uploads in progress; they are not
  originals and are not retained.
- Verified: `ResumableUploadTests` speak the protocol by hand against the real API and file store -
  a file in two requests with a "how much arrived?" between, the store's copy whole, read-only and
  hashed right; completing early, another user's requests, a forbidden source, a duplicate, a cancel
  and the day-old sweep - each made to fail first with its check removed. The page's side has its
  own tests (cancel, resume, retry, completion, duplicate), likewise made to fail first.
