"""Run the rhino3dm regressions; report each failure as a GitHub ::error annotation (logs are not always readable)."""
import pathlib
import sys
import unittest


def one_line(text, limit=900):
    lines = [line.strip() for line in text.strip().splitlines() if line.strip()]
    tail = ' | '.join(lines[-3:])
    return tail.replace('%', '%25').replace('\r', '').replace('\n', ' ')[:limit]


def main():
    here = pathlib.Path(__file__).resolve().parent
    suite = unittest.defaultTestLoader.discover(str(here), pattern='test_*.py', top_level_dir=str(here))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    problems = result.failures + result.errors
    for test, trace in problems[:40]:
        print(f'::error title=rhino {test.id()}::{one_line(trace)}')
    print(f'::notice title=rhino tests::ran={result.testsRun} failures={len(result.failures)} errors={len(result.errors)} skipped={len(result.skipped)}')
    return 0 if result.wasSuccessful() and result.testsRun > 0 else 1


if __name__ == '__main__':
    sys.exit(main())
