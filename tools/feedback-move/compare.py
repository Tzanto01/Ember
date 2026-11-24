#!/usr/bin/env python3
"""Compares Ember's feedback with what Lookout holds. Prints ids, counts and statuses only, never titles or texts.

Usage: compare.py <ember.json> <notes.json> <lookout.json> [<accepted ids>]
  ember.json    the array export.sql prints
  notes.json    {"<report id>": ["<md5 of a trimmed note>", ...]} from emberops
  lookout.json  an array of board.sh show outputs: {"ticket": {...}, "comments": [...]}
  accepted ids  comma-separated reports whose problems the operator settled by hand; printed, not counted

Exit 0: Lookout holds everything. Exit 2: it does, but a status, title or text differs (Lookout's counts; look at each).
Exit 1: something is missing. The last line is always RESULT; without it the check didn't finish.
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


def check(report, notes, tickets, used, now):
    """The ticket that holds the report (None when there is none), what is missing and what differs."""
    if report["delivery"] in ("waiting", "parked"):
        missing = [f"{len(notes)} notes, only on Ember's board"] if notes else []
        differs = [f"arrives as open, Ember has {report['status']}"] if report["status"] not in (None, "open") else []
        return None, missing, differs

    candidates = tickets.get(identity(report), [])
    if len(candidates) > 1:
        candidates = [c for c in candidates if md5(c["ticket"]["title"]) == report["titleMd5"]]
    if len(candidates) != 1:
        return None, [f"the report: {len(candidates)} tickets match"], []
    ticket, comments = candidates[0]["ticket"], candidates[0]["comments"]
    if ticket["id"] in used:
        return None, ["the report: its ticket already matched another one"], []
    used.add(ticket["id"])

    missing, differs = [], []
    lost = Counter(report["pictures"]) - Counter(p["size"] for p in ticket["pictures"])
    if lost:
        line = f"{sum(lost.values())} of {len(report['pictures'])} pictures (sizes {sorted(lost.elements())})"
        if pictures_expired(ticket, now):
            differs.append(line + " not there; resolved 29 days ago or longer, so Lookout's retention deleted them if it had them")
        else:
            missing.append(line)
    lost = Counter(notes) - Counter(md5(c["body"].strip(" \t\r\n")) for c in comments)
    if lost:
        missing.append(f"{sum(lost.values())} of {len(notes)} notes")
    if report["status"] not in (None, ticket["status"]):
        differs.append(f"Ember has {report['status']}, Lookout {ticket['status']}")
    if not same_content(report, ticket):
        differs.append("title or text (edited on the board?)")
    return ticket, missing, differs


def same_content(report, ticket):
    return report["titleMd5"] == md5(ticket["title"]) and report["textMd5"] == md5(ticket["text"])


def compare(reports, notes, held, now, accepted=frozenset()):
    tickets = {}
    for entry in held:
        tickets.setdefault(identity(entry["ticket"]), []).append(entry)

    problems, differences, mark, unsure, used = 0, 0, [], [], set()
    for report in reports:
        own_notes = notes.get(str(report["id"]), [])
        ticket, missing, differs = check(report, own_notes, tickets, used, now)
        where = f"ticket {ticket['id']}" if ticket else "no ticket"
        print(f"report {report['id']}: {report['delivery'] or 'never queued'}, {where}, "
              f"{len(report['pictures'])} pictures (sizes {report['pictures']}), {len(own_notes)} notes")
        for line in missing:
            print(f"  {'ACCEPTED' if report['id'] in accepted else 'MISSING'} {line}")
        for line in differs:
            print(f"  DIFFERS {line}")
        if report["id"] in accepted:
            missing = []
        problems += len(missing)
        differences += len(differs)
        if report["delivery"] is None and ticket and not missing:
            (mark if same_content(report, ticket) else unsure).append(report["id"])

    for orphan in sorted(set(notes) - {str(r["id"]) for r in reports}, key=int):
        print(f"{len(notes[orphan])} notes of report {orphan}: the report is gone from Ember")
    if unsure:
        print("Held, but title or text differs; add to FEEDBACK_IDS only after looking: " + ",".join(map(str, unsure)))
    if mark:
        print("FEEDBACK_IDS for ops/sql/mark_imported_feedback.sql: " + ",".join(map(str, mark)))
    code = 1 if problems else 2 if differences else 0
    print(f"RESULT {code}: {len(reports)} reports, {problems} missing, {differences} differences")
    return code


def load(path):
    with open(path, encoding="utf-8") as file:
        return json.load(file)


if __name__ == "__main__":
    if len(sys.argv) not in (4, 5):
        sys.exit(__doc__)
    ids = frozenset(int(i) for i in sys.argv[4].split(",")) if len(sys.argv) == 5 else frozenset()
    sys.exit(compare(load(sys.argv[1]), load(sys.argv[2]), load(sys.argv[3]), datetime.now(timezone.utc), ids))
