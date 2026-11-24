"""python3 -m unittest discover tools/feedback-move"""
import contextlib
import io
import os
import pathlib
import subprocess
import tempfile
import unittest
from datetime import datetime, timezone

from compare import compare, md5

NOW = datetime(2026, 11, 24, tzinfo=timezone.utc)


def report(i, **changes):
    return {"id": i, "platform": "web", "version": "1", "createdAt": f"2026-11-0{i}T00:00:00.123Z", "anonymous": True, "status": "open",
            "titleMd5": md5(f"title {i}"), "textMd5": md5("text"), "pictures": [10, 10], "delivery": None, **changes}


def ticket(i, notes=(), **changes):
    return {"ticket": {"id": 100 + i, "platform": "web", "version": "1", "createdAt": f"2026-11-0{i}T00:00:00.123Z", "anonymous": True,
                       "status": "open", "title": f"title {i}", "text": "text", "pictures": [{"size": 10}, {"size": 10}], "resolvedAt": None, **changes},
            "comments": [{"imported": False, "body": n} for n in notes]}


def run(reports, notes, tickets, accepted=frozenset()):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = compare(reports, notes, tickets, NOW, accepted)
    return code, out.getvalue()


class Compare(unittest.TestCase):
    def test_held_also_with_a_note_copied_by_hand(self):
        code, out = run([report(1)], {"1": [md5("a note")]}, [ticket(1, ["a note\n", "another"])])
        self.assertEqual(code, 0)
        self.assertIn("FEEDBACK_IDS for ops/sql/mark_imported_feedback.sql: 1\n", out)
        self.assertNotIn("title", out)

    def test_missing(self):
        for name, args in {
            "ticket": ([report(1)], {}, []),
            "one of two pictures of one size": ([report(1)], {}, [ticket(1, pictures=[{"size": 10}])]),
            "picture of that size": ([report(1)], {}, [ticket(1, pictures=[{"size": 10}, {"size": 11}])]),
            "one of two equal notes": ([report(1)], {"1": [md5("n"), md5("n")]}, [ticket(1, ["n", "other"])]),
            "note of a parked report": ([report(1, delivery="parked")], {"1": [md5("n")]}, []),
            "note of a waiting report": ([report(1, delivery="waiting")], {"1": [md5("n")]}, []),
            "linked report, anonymous ticket": ([report(1, anonymous=False)], {}, [ticket(1)]),
            "two tickets alike": ([report(1)], {}, [ticket(1), ticket(1)]),
            "one ticket for two reports": ([report(1), report(1, id=2)], {}, [ticket(1)]),
            "pictures of a report resolved lately": ([report(1, status="released")], {},
                                                     [ticket(1, status="released", pictures=[], resolvedAt="2026-11-20T00:00:00.000Z")]),
        }.items():
            with self.subTest(name):
                self.assertEqual(run(*args)[0], 1)

    def test_differs(self):
        for name, args in {
            "status": ([report(1)], {}, [ticket(1, status="planned")]),
            "title": ([report(1)], {}, [ticket(1, title="x")]),
            "text": ([report(1)], {}, [ticket(1, text="x")]),
            "status of a parked report": ([report(1, delivery="parked", status="planned")], {}, []),
            "pictures of a report resolved long ago": ([report(1, status="released")], {},
                                                       [ticket(1, status="released", pictures=[], resolvedAt="2026-10-01T00:00:00.000Z")]),
        }.items():
            with self.subTest(name):
                self.assertEqual(run(*args)[0], 2)

    def test_the_title_decides_between_tickets_alike_and_the_new_schema_has_no_status(self):
        self.assertEqual(run([report(1, status=None)], {}, [ticket(1, status="planned"), ticket(1, id=7, title="other")])[0], 0)

    def test_a_problem_of_one_report_keeps_the_ids_of_the_others(self):
        code, out = run([report(1), report(2), report(3)], {}, [ticket(1), ticket(3, text="edited")])
        self.assertEqual(code, 1)
        self.assertIn("mark_imported_feedback.sql: 1\n", out)
        self.assertIn("only after looking: 3\n", out)

    def test_an_accepted_report_is_no_problem_and_marked_only_with_a_ticket(self):
        code, out = run([report(1), report(2)], {"2": [md5("n")]}, [ticket(2)], {1, 2})
        self.assertEqual(code, 0)
        self.assertIn("mark_imported_feedback.sql: 2\n", out)


class Check(unittest.TestCase):
    def test_the_check_finishes_only_when_both_databases_and_the_board_answer(self):
        for failing in ("emberdb", "emberops", "list", "nothing"):
            with self.subTest(failing), tempfile.TemporaryDirectory() as folder:
                root = pathlib.Path(folder)
                (root / "tools").mkdir()
                for path, body in {
                    root / "docker": f'case "$*" in *{failing}*) exit 2;; *emberdb*) echo "[]";; *) echo "{{}}";; esac',
                    root / "tools" / "board.sh": f'[ "$1" = {failing} ] && {{ echo down >&2; exit 1; }}; echo "[]"',
                }.items():
                    path.write_text("#!/bin/sh\n" + body + "\n")
                    path.chmod(0o755)
                check = pathlib.Path(__file__).with_name("check.sh")
                result = subprocess.run([check, root], env={**os.environ, "PATH": f"{root}:{os.environ['PATH']}"}, capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, failing == "nothing")
                self.assertEqual("RESULT" in result.stdout, failing == "nothing")
