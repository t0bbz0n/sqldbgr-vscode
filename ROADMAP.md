# Roadmap – sqldbgr

Andra genomgången av kodbasen (efter fas 1). Prioriterat i faser; S/M/L =
uppskattad insats (timmar / dag / flera dagar). Punkter markerade *Bugg*
är fel i dagens beteende, övriga är förbättringar.

## Fas 1 – tillförlitlighet ✅

Klar (commits `1b9a242`, `26e35df`): breakpoint-handskakning + `stopOnEntry`,
abort som dödar batchen, paus-före-semantik, PRINT/resultat i Debug Console,
SQL-fel mappade till originalrad med exception-stopp, slutläge i modulläge,
`USE`-säker schemakvalificering, versionskontroll av kvarlämnad sidecar.

## Fas 2 – buggar hittade i andra genomgången ✅

*Klar. Punkterna behålls som beskrivning av vad som gjordes.*

1. **Upprepade stopp på samma statement missas.** `[S]` *Bugg.* Monitorn
   känner igen en ny paus på att `PausedAtStmt` ändrats. En `WHILE` vars
   kropp är ett enda statement pausar på samma id varje varv; blir gapet
   mellan resume och nästa paus kortare än pollintervallet (50 ms) ser
   monitorn aldrig `NULL` emellan och skickar inget nytt paused-event –
   VS Code visar "kör" medan SQL står stilla. Fix: en räknare
   `PauseSeq` i `Control` som `__dbg.Pause` stegar upp; monitorn jämför
   den i stället för statement-id.
2. **Modulparametrar binds som NVARCHAR.** `[S]` *Bugg.* Panelens värden
   skickas som strängar och `AddWithValue` gör dem till `NVARCHAR`. I en
   proc med `@a INT, @b INT` blir `@a + @b` då strängkonkatenering
   (`'5' + '10' = '510'`), och `@d DATETIME` jämförs som text. Fix utan
   typmappning i C#: bind som `@__p_<namn>` och låt preludet deklarera
   `DECLARE @a INT = @__p_a;` med typtexten från signaturen – SQL Server
   konverterar, och konverteringsfel syns som vanliga SQL-fel på rätt
   rad. Lägg `SET DATEFORMAT ymd` i preludet så ISO-datum är entydiga.
3. **Multi-statement TVF:er kraschar i modulläge.** `[S]` *Bugg.*
   `RETURNS @t TABLE (...)` ger `ReturnType` av tabelltyp; vi deklarerar
   `@__dbg_return SQL_VARIANT` och kroppens `INSERT INTO @t` får
   kompileringsfel. Fix: deklarera `@t` från return-definitionen,
   registrera den som tabellvariabel i Locals och rapportera den som
   resultat.
4. **Teckenkodning.** `[S]` *Bugg.* Filen läses som UTF-8. Äldre `.sql`
   i Windows-1252 (vanligt i svenska kodbaser) ger trasiga åäö i
   strängliteraler och PRINT – och det exekveras så mot databasen. Fix:
   strikt UTF-8 med fallback till 1252 (`System.Text.Encoding.CodePages`),
   alternativt låt extensionen skicka `files.encoding`.
5. **En sidecar delas av alla VS Code-fönster.** `[M]` Fast port 5199:
   fönster B med nyare extension byter ut sidecaren som fönster A
   debuggar mot (versionskontrollen), och när A stängs dödar `dispose`
   B:s sidecar. Fix: varje fönster startar sin egen sidecar på `--port 0`
   (slumpport), sidecaren skriver vald port på stdout och managern
   läser den. `sidecarUrl` blir ren override för egenstartad sidecar.
   Tar bort hela klassen av "stale sidecar"-problem.
6. **Capture per statement kostar även utan paus.** `[M]` Varje
   statement kör `DELETE` + en `INSERT` per variabel + `FOR JSON` per
   tabellvariabel + `EXEC Pause`, även med Continue och inga breakpoints.
   En loop med tiotusen varv blir mycket långsam. Fix: en `INSERT ...
   VALUES (…),(…)` för alla skalärer (billigt, behövs för exception-
   stoppet) och tabellvariabler bara när det faktiskt pausas:
   `IF [Db].__dbg.ShouldPause(@stmt_id) = 1 BEGIN <tabellcapture>; EXEC
   Pause END` där `ShouldPause` är en scalar function som läser `Control`.

## Fas 3 – vardagsupplevelse

- **Restart utan omfrågning.** `[S]` Ctrl+Shift+F5 går via
  `resolveDebugConfiguration` igen: QuickPick och parameterpanel visas
  på nytt. Implementera `supportsRestartRequest` i adaptern (samma args)
  och en setting `sqldbgr.moduleFiles: ask | debug | run`.
- **Breakpoint mitt i flerradigt statement.** `[S]` Snap-down hoppar
  till *nästa* statement; mappa via spans så rader inuti träffar det.
- **Hover och Watch.** `[S]` `evaluate` som slår upp variabelnamn i
  fångade locals; `type` på DAP-variablerna.
- **Pause-knappen.** `[S]` `pauseRequest` = signalera `stepOver`.
- **Upptäckbarhet.** `[M]` CodeLens "▷ Debugga med parametrar" ovanför
  `CREATE PROCEDURE/FUNCTION` (kräver `onLanguage:sql`-aktivering),
  kommandon i paletten, `configurationSnippets`.
- **Parse-fel i Problems-panelen.** `[S]` Diagnostics i stället för dialog.
- **Locals-polish.** `[S]` Deklarationsordning; datum via
  `CONVERT(…, 126)` (idag språkberoende "Jan 31 2024"), `varbinary` som
  hex (stil 1), float med full precision (stil 3); `@__dbg_return` som
  `(returvärde)`; `TOP n` + antal för tabellvariabler.
- **Resultatmängder i full bredd.** `[M]` Debug Console klipper vid 40
  tecken och 100 rader. Kommando "Öppna senaste resultat" som virtuellt
  dokument (CSV/markdown-tabell) eller en enkel grid-webview.
- **Anslutningar via mssql-extensionen + SecretStorage.** `[M]`
- **Temp-tabeller (`#t`) i Locals.** `[M]`
- **Sidecar-loggen.** `[S]` Warning som default.

## Fas 4 – nya use cases

- **Transaktionsläge ("dry run").** `[S/M]` `transaction: rollback |
  commit | none`; rullar tillbaka vid slut/stop.
- **Villkorliga breakpoints och logpoints.** `[M]` Sidecaren utvärderar
  villkoret på egen connection mot fångade locals och auto-fortsätter.
- **Ändra variabelvärden.** `[M]` `setVariable` via `__dbg.Overrides`.
- **Step-into i stored procedures.** `[L]` Virtuella source-filer +
  riktig stack.
- **Heartbeat och föräldralösa sessioner.** `[S]` `LastHeartbeatUtc`
  används inte; `Pause` ger upp efter t.ex. 60 s utan heartbeat.
- **Debugschema i annan databas.** `[S]` För miljöer utan DDL-rätt.

## Fas 5 – säkerhet, publicering, kvalitet

- **Auth mellan extension och sidecar.** `[M]` Sidecaren lyssnar på
  127.0.0.1 men utan autentisering: varje lokal process kan läsa
  godtyckliga filer via `/inspect` och starta sessioner med användarens
  Windows-inloggning. Extensionen genererar en token per start, ger den
  som miljövariabel, och alla anrop kräver `Authorization: Bearer`.
- **Publiceringspaket.** `[M]` `extension/README.md` (det som visas i
  Marketplace – finns inte idag), `CHANGELOG.md`, skärmdumpar/GIF av
  F5 → paus → Locals; riktigt `publisher`-id. Beslut om UI-språk:
  strängarna är svenska rakt igenom, Marketplace-publik förväntar sig
  engelska (ev. med svensk `package.nls.sv.json`).
- **Tester.** ✅ xunit-projekt med analysatortester och integrationstester
  mot `mcr.microsoft.com/mssql/server` som service-container i CI
  (pausmekanik, loopar, abort, exception-stopp, modulläge, TVF).
  Återstår: `@vscode/test-electron` för mappning och panel. `[S]`
- **Attach-läge (betaldel).** `[L]` Enligt NOTICE.md; kräver fas 4:s
  heartbeat först.

## Medvetna avgränsningar

- sqlcmd-läge (`:r`, `:setvar`, `GO n`) stöds inte.
- "(n rows affected)" visas inte – räknarna förorenas av
  instrumenteringens egna INSERT/DELETE.
- Pausa inne i *deployade* moduler går inte utan attach-läge;
  scalar-UDF:er aldrig.
- Ad hoc-SQL mot den pausade sessionens temp-tabeller är inte möjligt
  (annan session).
