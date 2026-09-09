// Rena enhetstester, körda med node:test mot den kompilerade koden. Ingen
// vscode-import finns i breakpointMapper, så den går att köra utan en
// extension host - och den avgör var VARJE breakpoint hamnar, så den är värd
// att täcka innan något tyngre testramverk.
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

test('en rad som är ett statement träffar det', () => {
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 1), { line: 1, stmtId: 0 });
});

test('en rad inuti ett flerradigt statement träffar statementet', () => {
  // Rad 4 är BEGIN - inuti IF-satsen, inget eget statement.
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 4), { line: 3, stmtId: 1 });
});

test('vid nästling vinner det innersta statementet', () => {
  // Rad 5 ligger både i IF (3-6) och i SET (5-5). Att stanna på IF vore fel:
  // användaren satte breakpointen på tilldelningen.
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 5), { line: 5, stmtId: 2 });
});

test('en tom rad snappas nedåt till nästa statement', () => {
  // Rad 2 och 7 är tomma - VS/SSDT flyttar breakpointen nedåt, inte uppåt.
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 2), { line: 3, stmtId: 1 });
  assert.deepStrictEqual(mapper().snapToStatement('/tmp/x.sql', 7), { line: 8, stmtId: 3 });
});

test('en rad efter sista statementet är ingen giltig breakpoint', () => {
  assert.strictEqual(mapper().snapToStatement('/tmp/x.sql', 99), null);
});

test('en okänd fil ger null i stället för att kasta', () => {
  assert.strictEqual(mapper().snapToStatement('/tmp/annan.sql', 1), null);
});

test('sökvägar jämförs skiftlägesokänsligt och med / oavsett separator', () => {
  // VS Code och sidecaren är inte överens om vare sig separator eller
  // skiftläge på Windows; hade de jämförts rakt av hade inga breakpoints
  // fungerat där.
  const m = mapper('C:\\Work\\Proc.sql');
  assert.deepStrictEqual(m.snapToStatement('c:/work/proc.sql', 1), { line: 1, stmtId: 0 });
});

test('spans behöver inte komma sorterade', () => {
  const m = new BreakpointMapper();
  m.load('/tmp/y.sql', [...spans].reverse());
  assert.deepStrictEqual(m.snapToStatement('/tmp/y.sql', 5), { line: 5, stmtId: 2 });
  assert.deepStrictEqual(m.snapToStatement('/tmp/y.sql', 2), { line: 3, stmtId: 1 });
});

test('en fil utan statements ger null', () => {
  const m = new BreakpointMapper();
  m.load('/tmp/tom.sql', []);
  assert.strictEqual(m.snapToStatement('/tmp/tom.sql', 1), null);
});
