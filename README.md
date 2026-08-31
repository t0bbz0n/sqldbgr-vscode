# tsql-debugger

Breakpoint-debugging för T-SQL i VS Code – sätt breakpoints i `.sql`-filer,
stega igenom statements, inspektera variabler. Ingen Visual Studio, ingen SSDT.

**Lokal debugging är gratis och kräver ingen licens.** Se `NOTICE.md` för
gränsen mot betalfunktioner (remote/attach-läge, ej byggt än).

## Arkitektur

```
VS Code (extension, TS)  ──DAP──►  debugAdapter.ts
                                        │ HTTP + SSE
                                        ▼
                              sidecar (C#, ASP.NET minimal API)
                                        │ 1. ScriptDom-parse + instrumentering
                                        │ 2. exekverar batch (connection A)
                                        │ 3. övervakar __dbg.Control (connection B)
                                        ▼
                                   SQL Server
                              __dbg.Pause blockerar batchen
                              tills klienten signalerar
```

## Komma igång (utveckling)

Förutsättningar: .NET 8 SDK, Node 20+, en lokal SQL Server ((localdb) räcker).

```bash
cd extension
npm install
npm run compile
# Öppna extension/ i VS Code, F5 -> Extension Development Host
```

I dev-hosten: öppna en `.sql`-fil, skapa en launch-konfiguration av typen
`tsql` (snippet finns), sätt breakpoints, F5.

### Sidecaren startar automatiskt

Ingen backend behöver startas för hand: vid F5 probar extensionen
`sidecarUrl` (`/health`) och startar annars sidecaren själv via
`npx -y tsql-debugger-sidecar` (npm-paketet i `sidecar-npm/`, kräver
.NET 8-runtime). Processen ägs av extensionen och städas undan när den
avaktiveras; en sidecar man startat själv rörs aldrig. Loggarna hamnar i
output-kanalen **T-SQL Debugger Sidecar**.

Under utveckling (opublicerat paket / lokala ändringar) pekar man om
startkommandot i launch-konfigurationen:

```jsonc
{
  "type": "tsql",
  // ...
  "sidecarCommand": ["dotnet", "run", "--project", "${workspaceFolder}/sidecar", "--"],
  // eller stäng av helt och kör `dotnet run` själv:
  "autoStartSidecar": false
}
```

Publicering av sidecar-paketet: `cd sidecar-npm && npm publish`
(prepack kör `dotnet publish` till `dist/`).

## Installera i VS Code

För att installera extensionen på riktigt (utanför dev-hosten):

```bash
cd extension
npm install
npm run compile
npx @vscode/vsce package                            # -> tsql-debugger-0.0.1.vsix
code --install-extension tsql-debugger-0.0.1.vsix
```

(eller Extensions-panelen → `⋯` → *Install from VSIX…*)

OBS: autostarten kör `npx -y tsql-debugger-sidecar`, så tills det paketet
är publicerat på npm måste en installerad extension antingen peka om
`sidecarCommand` mot `dotnet run --project .../sidecar --` eller köra med
en manuellt startad sidecar.

För Marketplace-publicering: skapa en publisher på
marketplace.visualstudio.com, byt ut `publisher` i `extension/package.json`
(står som `your-publisher-id`), och kör `npx @vscode/vsce publish`.

## Status / roadmap

- [x] DAP-skelett: launch, breakpoints, continue/step, locals, SSE-events
- [x] ScriptDom-instrumentering, rekursivt ner i `BEGIN/END`, `IF`/`ELSE`,
      `WHILE` och `TRY/CATCH` (grenar utan `BEGIN/END` wrappas i syntetiska block)
- [x] `__dbg`-schema med pause/control-mekanism
- [x] Verklig source-mappning i paused-events (rad/kolumn + slutposition,
      exakt statement highlightas i editorn)
- [x] Locals: TABLE-variabler som expanderbart träd (variablesReference)
- [ ] Step-into i stored procedures (inline-expansion, virtuella source-filer)
- [ ] Invoke-läge med parameterpanel
- [ ] Conditional breakpoints
- [ ] Deploy/backup/restore av procs från fil (remote-förberedelse)
- [ ] Attach-läge + licens (betald del, separat repo)

## Kända begränsningar just nu

- `GO`-batchseparatorer hanteras av parsern men körs som en batch.
- Modul-definitioner (`CREATE PROCEDURE`/`VIEW`/`TRIGGER`) instrumenteras
  inte - de måste stå ensamma i sin batch. Kör dem, men det går inte att
  pausa på dem, och eftersom batcher slås ihop kan ett script som blandar
  moduldefinitioner med andra statements fortfarande faila.
- Locals-captures körs före `__dbg.Pause` – variabler visar värdet
  *efter* att statementet körts.

## Licens

MIT – se `LICENSE` och `NOTICE.md`.
