#!/usr/bin/env python3
"""Prints the failed tests from a .trx file.

CI runs against a SQL Server service container, and stopping that container
dumps its entire startup log - some 700 lines - at the end of the job. A test
failure is therefore buried far above the end of the log, where neither a tail
nor a glance at the web UI finds it. This puts the name, message and stack of
every failure at the very bottom instead.
"""
import glob
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def main(patterns: list[str]) -> int:
    files = [f for pattern in patterns for f in glob.glob(pattern)]
    if not files:
        print(f'no trx file matched {patterns}', file=sys.stderr)
        return 0

    failures = 0
    for path in files:
        for result in ET.parse(path).getroot().iter(f'{{{NS["t"]}}}UnitTestResult'):
            if result.get('outcome') != 'Failed':
                continue
            failures += 1
            print('=' * 72)
            print(f'FAILED: {result.get("testName")}')
            for tag in ('Message', 'StackTrace'):
                element = result.find(f'.//t:{tag}', NS)
                if element is not None and element.text:
                    print(element.text.strip())
    if failures:
        print('=' * 72)
        print(f'{failures} test(s) failed')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:] or ['artifacts/*.trx']))
