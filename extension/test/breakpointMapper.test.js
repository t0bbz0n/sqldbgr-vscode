// Plain unit tests, run with node:test against the compiled code.
// breakpointMapper imports nothing from vscode, so it can run without an
// extension host - and it decides where EVERY breakpoint lands, so it is worth
// covering before reaching for a heavier test framework.
const test = require('node:test');
const assert = require('node:assert');
const { BreakpointMapper } = require('../out/breakpointMapper');

/**
 *  1  DECLARE @x INT = 1;              stmt 0
 *  2  (tom rad)
 *  3  IF @x = 1                        stmt 1, rad 3-6 (flerradig)
 *  4  BEGIN
 *  5      SET @x = 2;                  stmt 2, rad 5
 *  6  END
 *  7  (tom rad)
 *  8  SELECT @x;                       stmt 3
 */
const spans = [
  { stmtId: 0, line: 1, endLine: 1 },
  { stmtId: 1, line: 3, endLine: 6 },
  { stmtId: 2, line: 5, endLine: 5 },
  { stmtId: 3, line: 8, endLine: 8 }
];

function mapper(path = '/tmp/x.sql') {
  const m = new BreakpointMapper();
  m.load(path, spans);
  return m;
}

test('a line that is a statement hits it', () => {
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 1), { line: 1, stmtId: 0 });
});

test('a line inside a multi-line statement hits that statement', () => {
  // Line 4 is BEGIN: inside the IF, not a statement of its own.
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 4), { line: 3, stmtId: 1 });
});

test('where they nest, the innermost statement wins', () => {
  // Line 5 is in both the IF (3-6) and the SET (5-5). Stopping on the IF would
  // be wrong: the user put the breakpoint on the assignment.
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 5), { line: 5, stmtId: 2 });
});

test('a blank line snaps down to the next statement', () => {
  // Lines 2 and 7 are blank. Visual Studio and SSDT move the breakpoint down, not up.
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 2), { line: 3, stmtId: 1 });
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 7), { line: 8, stmtId: 3 });
});

test('a line past the last statement is not a valid breakpoint', () => {
  assert.strictEqual(mapper().snapToStatement('/tmp/x.sql', 99), null);
});

test('an unknown file returns null rather than throwing', () => {
  assert.strictEqual(mapper().snapToStatement('/tmp/annan.sql', 1), null);
});

test('paths compare case-insensitively, and with / whatever the separator', () => {
  // VS Code and the sidecar agree on neither the separator nor the case on
  // Windows. Compared as they come, no breakpoint would work there.
  const m = mapper('C:\\Work\\Proc.sql');
  assert.deepStrictEqual(m.snapToStatement('c:/work/proc.sql', 1), { line: 1, stmtId: 0 });
});

test('spans need not arrive sorted', () => {
  const m = new BreakpointMapper();
  m.load('/tmp/y.sql', [...spans].reverse());
  assert.deepStrictEqual(m.snapToStatement('/tmp/y.sql', 5), { line: 5, stmtId: 2 });
  assert.deepStrictEqual(m.snapToStatement('/tmp/y.sql', 2), { line: 3, stmtId: 1 });
});

test('a file with no statements returns null', () => {
  const m = new BreakpointMapper();
  m.load('/tmp/tom.sql', []);
  assert.strictEqual(m.snapToStatement('/tmp/tom.sql', 1), null);
});
