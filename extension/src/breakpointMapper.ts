export interface StatementLocation { line: number; stmtId: number; }

/**
 * Mappar radnummer i en källfil till statement-ID:n från sidecarens parse.
 * Klick på en icke-exekverbar rad snappas till närmaste statement NEDÅT
 * (samma beteende som VS/SSDT).
 */
export class BreakpointMapper {
  // sourcePath -> sorterad lista av statement-startrader
  private maps = new Map<string, StatementLocation[]>();

  load(sourcePath: string, lineMap: StatementLocation[]): void {
    const sorted = [...lineMap].sort((a, b) => a.line - b.line);
    this.maps.set(this.normalize(sourcePath), sorted);
  }

  snapToStatement(sourcePath: string, line: number): StatementLocation | null {
    const map = this.maps.get(this.normalize(sourcePath));
    if (!map || map.length === 0) return null;

    // första statement vars startrad >= klickad rad
    for (const loc of map) {
      if (loc.line >= line) return loc;
    }
    return null; // klick efter sista statement - ogiltig breakpoint
  }

  private normalize(p: string): string {
    return p.replace(/\\/g, '/').toLowerCase();
  }
}
