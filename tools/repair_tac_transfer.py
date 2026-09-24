"""
Rebuild a GSMA TAC export that arrived through a damaged transfer, and refuse to if it cannot be
done without losing data.

WHY THIS IS A TOOL AND NOT A FEATURE. The import platform's job is to refuse a file it cannot
vouch for, and it does: `TacFileScanner` counts every defect before anything is loaded. A product
that quietly repaired its own input would be a product that quietly accepted corruption nobody
had measured. So repair lives out here, is run by a person, and produces a file that then goes
through the ordinary import path and is validated by the ordinary rules. The scanner that refused
the original is what certifies the repair.

WHAT IT REPAIRS. One specific failure: a transfer that replayed blocks it had already sent and
lost bytes where it resumed. The observed instance, DeviceDatabase_TAC16Sep2026.csv, had
482,047 records for 270,885 distinct TACs - 1,224 replayed blocks of roughly 165 records - plus
27 records with their beginning missing.

WHAT IT REFUSES. Repair is only honest when it discards nothing, so both defects are proven
lossless before a byte is written:

  1. Every repeated TAC must carry byte-identical records. Two DIFFERENT descriptions of one TAC
     is a different problem - a genuine content conflict - and this tool has no business guessing
     which is right.
  2. Every malformed fragment must be the tail of a complete record that also survives in the
     file. A fragment with no intact counterpart is a device that only exists in damaged form,
     and dropping it would silently remove a model from the catalogue.

If either check fails the tool writes nothing and says which records are at stake.

Every output line is a verbatim copy of an input line. Nothing is reformatted, re-quoted or
re-serialised - the repair only chooses which lines to keep - so a field this script does not
understand cannot be damaged by it.

Usage:
    python tools/repair_tac_transfer.py <source.csv> <destination.csv>
"""

import csv
import hashlib
import sys
from collections import defaultdict

csv.field_size_limit(50_000_000)


def load(path):
    """Read the file as physical lines, preserving each one exactly."""
    with open(path, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()

    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.split(newline)
    while lines and lines[-1] == "":
        lines.pop()

    return lines, newline


def main(argv):
    if len(argv) != 3:
        print(__doc__.strip().splitlines()[-2].strip())
        return 2

    source, destination = argv[1], argv[2]

    lines, newline = load(source)
    if not lines:
        print("The file is empty.")
        return 1

    header = lines[0]
    width = len(next(csv.reader([header])))

    wellformed = []   # (line number, raw text, tac)
    fragments = []    # (line number, raw text, field count)

    for number, text in enumerate(lines[1:], start=2):
        if text == "":
            continue

        row = next(csv.reader([text]))
        if len(row) == width:
            wellformed.append((number, text, row[0]))
        else:
            fragments.append((number, text, len(row)))

    print(f"header fields:       {width}")
    print(f"records read:        {len(wellformed) + len(fragments):,}")
    print(f"  well-formed:       {len(wellformed):,}")
    print(f"  malformed:         {len(fragments):,}")

    # ---- check 1: repeats must be byte-identical -----------------------------------------
    by_tac = defaultdict(list)
    for number, text, tac in wellformed:
        by_tac[tac].append((number, text))

    conflicting = {
        tac: group for tac, group in by_tac.items()
        if len({text for _, text in group}) > 1
    }

    repeated = sum(len(group) - 1 for group in by_tac.values() if len(group) > 1)
    print(f"distinct TACs:       {len(by_tac):,}")
    print(f"redundant copies:    {repeated:,}")

    if conflicting:
        print(f"\nREFUSED. {len(conflicting):,} TAC(s) appear more than once with DIFFERENT "
              "content, so this is a content conflict rather than a replayed transfer. "
              "Choosing between them is not this tool's decision.")
        for tac, group in list(conflicting.items())[:5]:
            print(f"  {tac}: lines {[n for n, _ in group]}")
        return 1

    # ---- check 2: every fragment must have an intact counterpart -------------------------
    by_suffix = defaultdict(list)
    for number, text, tac in wellformed:
        by_suffix[text[-80:]].append(text)

    # A fragment counts as recovered only when its tail names exactly one device. "Some intact
    # record ends this way" was the test before, and a short tail - ',Smartphone,Android' - ends
    # hundreds of records: it passed while proving nothing about which device the fragment was,
    # so a model that survived only as that fragment would have been dropped unreported.
    orphaned = []
    ambiguous = []
    for number, text, nfields in fragments:
        candidates = by_suffix.get(text[-80:], []) if len(text) >= 80 else []
        if not candidates:
            candidates = [t for _, t, _ in wellformed if t.endswith(text)]
        else:
            candidates = [t for t in candidates if t.endswith(text)]

        tacs = {next(csv.reader([t]))[0] for t in candidates}
        if not tacs:
            orphaned.append((number, nfields, text[:70]))
        elif len(tacs) > 1:
            ambiguous.append((number, nfields, text[:70], len(tacs)))

    print(f"fragments recoverable elsewhere in the file: "
          f"{len(fragments) - len(orphaned) - len(ambiguous)} of {len(fragments)}")

    if orphaned:
        print(f"\nREFUSED. {len(orphaned)} malformed record(s) have no intact counterpart, so "
              "dropping them would remove device models from the catalogue rather than remove "
              "debris. Obtain a sound export instead.")
        for number, nfields, head in orphaned[:10]:
            print(f"  line {number}: {nfields} fields, starts {head!r}")
        return 1

    if ambiguous:
        print(f"\nREFUSED. {len(ambiguous)} malformed record(s) are too short to say which "
              "device they were: each ends the same way as several intact records with "
              "different TACs, so whether its own device survives cannot be shown. Obtain a "
              "sound export instead.")
        for number, nfields, head, count in ambiguous[:10]:
            print(f"  line {number}: {nfields} fields, {head!r} ends {count} different TACs")
        return 1

    # ---- write ---------------------------------------------------------------------------
    kept = [header]
    seen = set()
    for number, text, tac in wellformed:
        if tac in seen:
            continue
        seen.add(tac)
        kept.append(text)

    source_lines = set(lines)
    if not all(k in source_lines for k in kept):
        print("REFUSED. An output line is not verbatim from the input; this is a bug.")
        return 1

    out = newline.join(kept) + newline
    with open(destination, "w", encoding="utf-8", newline="") as fh:
        fh.write(out)

    encoded = out.encode("utf-8")
    print(f"\nrecords written:     {len(kept) - 1:,}")
    print(f"distinct TACs:       {len(seen):,}")
    print(f"bytes:               {len(encoded):,}")
    print(f"sha256:              {hashlib.sha256(encoded).hexdigest()}")
    print(f"written to:          {destination}")
    print("\nEvery output line is verbatim from the input. Import it through the Import Center; "
          "the platform's own structural scan is what should certify it, not this script.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
