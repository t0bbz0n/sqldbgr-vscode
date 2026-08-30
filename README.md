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
# Sidecar
cd sidecar
dotnet run            # lyssnar på http://localhost:5199

# Extension
cd extension
npm install
npm run compile
# Öppna extension/ i VS Code, F5 -> Extension Development Host
```

I dev-hosten: öppna en `.sql`-fil, skapa en launch-konfiguration av typen
`tsql` (snippet finns), sätt breakpoints, F5.

## Status / roadmap

- [x] DAP-skelett: launch, breakpoints, continue/step, locals, SSE-events
- [x] ScriptDom-instrumentering av topp-nivå-statements
- [x] `__dbg`-schema med pause/control-mekanism
- [ ] Verklig radmappning i paused-events (stack med korrekt source)
- [ ] Locals: TABLE-variabler som expanderbart träd (variablesReference)
- [ ] Step-into i stored procedures (inline-expansion, virtuella source-filer)
- [ ] Invoke-läge med parameterpanel
- [ ] Conditional breakpoints
- [ ] Deploy/backup/restore av procs från fil (remote-förberedelse)
- [ ] Attach-läge + licens (betald del, separat repo)

## Kända begränsningar just nu

- Instrumenteringen är per topp-nivå-statement; statements inne i
  `IF`/`WHILE`-block pausas inte ännu (kräver rekursiv besökare).
- `GO`-batchseparatorer hanteras av parsern men körs som en batch.
- Locals-captures körs före `__dbg.Pause` – variabler visar värdet
  *efter* att statementet körts.

## Licens

MIT – se `LICENSE` och `NOTICE.md`.
