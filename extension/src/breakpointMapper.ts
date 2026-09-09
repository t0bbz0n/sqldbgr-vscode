import { StatementSpan } from './sidecarClient';

export interface StatementLocation { line: number; stmtId: number; }

/**
 * Maps a line number in a source file to a statement id from the sidecar's
 * parse. A line inside a multi-line statement hits that statement - the
 * innermost one, where they nest. A line between statements snaps DOWN to the
 * next statement, which is what Visual Studio and SSDT do.
 */
export class BreakpointMapper {
  // sourcePath -> spans sorted by start line
  private maps = new Map<string, StatementSpan[]>();

  load(sourcePath: string, statements: StatementSpan[]): void {
    const sorted = [...statements].sort((a, b) => a.line - b.line || b.endLine - a.endLine);
    this.maps.set(this.normalize(sourcePath), sorted);
  }

  snapToStatement(sourcePath: string, line: number): StatementLocation | null {
    const map = this.maps.get(this.normalize(sourcePath));
    if (!map || map.length === 0) return null;

    // The innermost statement enclosing the line: the greatest start line <= it.
    let containing: StatementSpan | null = null;
    for (const s of map) {
      if (s.line <= line && line <= s.endLine) containing = s;
      if (s.line > line) break;
    }
    if (containing) return { line: containing.line, stmtId: containing.stmtId };

    // Otherwise the first statement starting at or below the clicked line.
    for (const s of map) {
      if (s.line >= line) return { line: s.line, stmtId: s.stmtId };
    }
    return null; // clicked past the last statement - not a valid breakpoint
  }

  private normalize(p: string): string {
    return p.replace(/\\/g, '/').toLowerCase();
  }
}
