#!/usr/bin/env python3
"""Compares Ember's feedback with what Lookout holds. Prints ids, counts and statuses only, never titles or texts.

Usage: compare.py <ember.json> <notes.json> <lookout.json>
  ember.json    the array export.sql prints
  notes.json    {"<report id>": <note count>} from emberops, or {} once its tables are dropped
  lookout.json  an array of board.sh show outputs: {"ticket": {...}, "comments": [...]}

Exit 0: Lookout holds everything. Exit 2: it does, but a status or text differs (Lookout's counts; look at each).
Exit 1: something is missing.
"""
import hashlib
import json
import sys
from collections import Counter
from datetime import datetime, timedelta, timezone


def moment(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(timezone.utc)


def identity(item):
    created = moment(item["createdAt"])
    return (created.replace(microsecond=created.microsecond // 1000 * 1000), item["platform"], item["version"], item["anonymous"])


def md5(text):
    return hashlib.md5(text.encode(), usedforsecurity=False).hexdigest()


def pictures_expired(ticket, now):
    return (ticket["status"] in ("released", "declined") and ticket.get("resolvedAt")
            and moment(ticket["resolvedAt"]) < now - timedelta(days=29))


def compare(reports, notes, held, now):
    tickets = {}
    for entry in held:
        tickets.setdefault(identity(entry["ticket"]), []).append(entry)

    missing, differs, unqueued, used = [], [], [], set()
    for report in reports:
        name, delivery = f"report {report['id']}", report["delivery"] or "never queued"
        if delivery in ("waiting", "parked"):
            print(f"{name}: {delivery}, Lookout doesn't have it; Ember keeps it")
            continue
        candidates = tickets.get(identity(report), [])
        if len(candidates) > 1:
            candidates = [c for c in candidates if md5(c["ticket"]["title"]) == report["titleMd5"]]
        if len(candidates) != 1:
            missing.append(f"{name}: {delivery}, but {len(candidates)} tickets match")
            continue
        if candidates[0]["ticket"]["id"] in used:
            missing.append(f"{name}: {delivery}, but its ticket already matched another report")
            continue
        used.add(candidates[0]["ticket"]["id"])
        ticket, comments = candidates[0]["ticket"], candidates[0]["comments"]
        where = f"{name} (ticket {ticket['id']})"

        lost = Counter(report["pictures"]) - Counter(p["size"] for p in ticket["pictures"])
        if lost and pictures_expired(ticket, now):
            print(f"{where}: Lookout's retention deleted {sum(lost.values())} pictures")
        elif lost:
            missing.append(f"{where}: {sum(lost.values())} of {len(report['pictures'])} pictures missing (sizes {sorted(lost.elements())})")

        expected = notes.get(str(report["id"]), 0)
        imported = sum(1 for c in comments if c["imported"])
        if imported < expected:
            missing.append(f"{where}: {imported} of {expected} notes")

        if report["status"] is not None and report["status"] != ticket["status"]:
            differs.append(f"{where}: Ember {report['status']}, Lookout {ticket['status']}")
        if report["textMd5"] != md5(ticket["text"]):
            differs.append(f"{where}: the text in Lookout is another one (edited on the board?)")
        if report["delivery"] is None:
            unqueued.append(report["id"])
        print(f"{where}: held, {len(report['pictures'])} pictures, {expected} notes")

    known = {str(r["id"]) for r in reports}
    for orphan in sorted(set(notes) - known, key=int):
        print(f"notes of report {orphan}: the report is gone from Ember")
    for line in differs:
        print(f"DIFFERS {line}")
    for line in missing:
        print(f"MISSING {line}")
    print(f"{len(reports)} reports, {len(missing)} problems, {len(differs)} differences")
    if unqueued and not missing:
        print("FEEDBACK_IDS for ops/sql/mark_imported_feedback.sql: " + ",".join(map(str, unqueued)))
    return 1 if missing else 2 if differs else 0


def load(path):
    with open(path, encoding="utf-8") as file:
        return json.load(file)


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    sys.exit(compare(load(sys.argv[1]), load(sys.argv[2]), load(sys.argv[3]), datetime.now(timezone.utc)))
