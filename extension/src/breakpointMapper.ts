import { StatementSpan } from './sidecarClient';

export interface StatementLocation { line: number; stmtId: number; }

/**
 * Mappar radnummer i en källfil till statement-ID:n från sidecarens parse.
 * En rad inuti ett flerradigt statement träffar det statementet (innersta
 * vid nästling); en rad mellan statements snappas till nästa statement NEDÅT
 * (samma beteende som VS/SSDT).
 */
export class BreakpointMapper {
  // sourcePath -> spans sorterade på startrad
  private maps = new Map<string, StatementSpan[]>();

  load(sourcePath: string, statements: StatementSpan[]): void {
    const sorted = [...statements].sort((a, b) => a.line - b.line || b.endLine - a.endLine);
    this.maps.set(this.normalize(sourcePath), sorted);
  }

  snapToStatement(sourcePath: string, line: number): StatementLocation | null {
    const map = this.maps.get(this.normalize(sourcePath));
    if (!map || map.length === 0) return null;

    // innersta statement som omsluter raden (störst startrad <= raden)
    let containing: StatementSpan | null = null;
    for (const s of map) {
      if (s.line <= line && line <= s.endLine) containing = s;
      if (s.line > line) break;
    }
    if (containing) return { line: containing.line, stmtId: containing.stmtId };

    // annars första statement vars startrad >= klickad rad
    for (const s of map) {
      if (s.line >= line) return { line: s.line, stmtId: s.stmtId };
    }
    return null; // klick efter sista statement - ogiltig breakpoint
  }

  private normalize(p: string): string {
    return p.replace(/\\/g, '/').toLowerCase();
  }
}
