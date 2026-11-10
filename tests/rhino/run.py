"""Run the rhino3dm regressions; report each failure as a GitHub ::error annotation (logs are not always readable)."""
import os
import pathlib
import subprocess
import sys
import unittest

# Guards against a discovery change silently running fewer regressions. Raise it when tests are added.
MINIMUM_TESTS = 209


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
    for test, reason in result.skipped[:20]:
        print(f'::notice title=rhino skipped {test.id()}::{one_line(reason, 300)}')
    print(f'::notice title=rhino tests::ran={result.testsRun} failures={len(result.failures)} errors={len(result.errors)} skipped={len(result.skipped)}')
    if result.testsRun < MINIMUM_TESTS:
        print(f'::error title=rhino tests::ran {result.testsRun} tests, expected at least {MINIMUM_TESTS}')
        return 1
    return 0 if result.wasSuccessful() else 1


def supervise():
    """Run main() in a child with faulthandler, so a native crash in rhino3dm still names the test that died."""
    child = subprocess.run([sys.executable, '-I', '-X', 'faulthandler', __file__], env={**os.environ, 'BIM_RHINO_TESTS_CHILD': '1'},
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, errors='replace')
    sys.stdout.write(child.stdout)
    sys.stderr.write(child.stderr)
    if child.returncode < 0 or child.returncode > 128:
        # unittest verbosity=2 writes "test_x (module.Class...) ... " to stderr before running each test.
        started = [line for line in child.stderr.splitlines() if ' ... ' in line or line.startswith('test_')]
        last = started[-1] if started else '(no test started)'
        fault = [line.strip() for line in child.stderr.splitlines() if line.strip().startswith('File ')]
        print(f'::error title=rhino crashed::exit {child.returncode} during {one_line(last, 300)} | {one_line(chr(10).join(fault[:6]), 600)}')
        return 1
    return child.returncode


if __name__ == '__main__':
    sys.exit(main() if os.environ.get('BIM_RHINO_TESTS_CHILD') == '1' else supervise())
