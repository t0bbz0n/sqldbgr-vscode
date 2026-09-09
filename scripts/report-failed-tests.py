#!/usr/bin/env python3
"""Prints the failed tests from a .trx file.

CI runs against a SQL Server service container, and stopping that container
dumps its entire startup log - some 700 lines - at the end of the job. A test
failure is therefore buried far above the end of the log, where neither a tail
nor a glance at the web UI finds it.

Printing the failures does not fix that on its own: this step still runs before
the container is stopped, so its output ends up buried too. The failures are
therefore also emitted as ::error:: annotations and written to the job summary,
both of which the run page shows without anyone reading the log at all.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def main(patterns: list[str]) -> int:
    files = [f for pattern in patterns for f in glob.glob(pattern)]
    if not files:
        print(f'no trx file matched {patterns}', file=sys.stderr)
        return 0

    failures = 0
    summary = []
    for path in files:
        for result in ET.parse(path).getroot().iter(f'{{{NS["t"]}}}UnitTestResult'):
            if result.get('outcome') != 'Failed':
                continue
            failures += 1
            name = result.get('testName') or '(unnamed)'
            parts = {}
            for tag in ('Message', 'StackTrace'):
                element = result.find(f'.//t:{tag}', NS)
                parts[tag] = element.text.strip() if element is not None and element.text else ''

            print('=' * 72)
            print(f'FAILED: {name}')
            for tag in ('Message', 'StackTrace'):
                if parts[tag]:
                    print(parts[tag])

            # One line, so it lands as an annotation on the run page.
            message = ' '.join(parts['Message'].split())[:800]
            print(f'::error title=Test failed::{name}: {message}')

            summary.append(f'### {name}\n\n```\n{parts["Message"]}\n\n{parts["StackTrace"]}\n```\n')

    if failures:
        print('=' * 72)
        print(f'{failures} test(s) failed')

    step_summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if step_summary and summary:
        with open(step_summary, 'a', encoding='utf-8') as handle:
            handle.write(f'## {failures} failing test(s)\n\n' + '\n'.join(summary))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:] or ['artifacts/*.trx']))
