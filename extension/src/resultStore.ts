import { ResultSetEvent } from './sidecarClient';

/**
 * Result sets from the last debug session, at full width - the sidecar caps
 * them. The Debug Console only shows a clipped text table; the command
 * "sqldbgr: Open last result set" opens these as a document.
 */
const MAX_KEPT = 20;
const results: ResultSetEvent[] = [];

export function clearResults(): void {
  results.length = 0;
}

export function addResult(r: ResultSetEvent): void {
  results.push(r);
  if (results.length > MAX_KEPT) results.shift();
}

export function getResults(): readonly ResultSetEvent[] {
  return results;
}

/** Markdown tables: readable as text, and rendered in the markdown preview. */
export function renderMarkdown(sets: readonly ResultSetEvent[]): string {
  const esc = (s: string) => s.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
  return sets.map((r, i) => {
    const header = `| ${r.columns.map(esc).join(' | ')} |`;
    const sep = `| ${r.columns.map(() => '---').join(' | ')} |`;
    const rows = r.rows.map(row => `| ${row.map(c => c === null ? 'NULL' : esc(c)).join(' | ')} |`);
    const note = r.total > r.rows.length ? `\n\n_${r.total} rows, first ${r.rows.length} shown_` : `\n\n_${r.total} rows_`;
    return `## Result set ${i + 1}\n\n${header}\n${sep}\n${rows.join('\n')}${note}`;
  }).join('\n\n');
}
