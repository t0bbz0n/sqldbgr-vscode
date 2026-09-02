# Roadmap – sqldbgr

Genomgång av kodbasen (extension + sidecar) med fokus på missade use cases,
buggar i flödet och användarupplevelse. Prioriterat i faser; S/M/L =
uppskattad insats (timmar / dag / flera dagar).

## Fas 1 – gör verktyget pålitligt (buggar och grundläggande UX) ✅

*Klar (commits `1b9a242`, `26e35df`). Punkterna nedan behålls som beskrivning av vad som gjordes.*

1. **Breakpoints satta före F5 försvinner.** `[M]` *Bugg.* Adaptern skickar
   `InitializedEvent` redan i `initializeRequest`, så VS Code skickar
   `setBreakpoints` innan `/session/start` svarat: mappern är tom (alla
   breakpoints blir ogiltiga) och `sessionId` saknas (inget pushas). Det
   enda som räddar en idag är att `entry`-läget alltid stannar på första
   statementet. Fix: sidecaren startar sessionen i vänteläge (parse +
   lineMap utan att köra), adaptern skickar `InitializedEvent` efter det,
   och `configurationDone` triggar körningen. Inför samtidigt `stopOnEntry`
   (default false, som andra debuggers) – idag stannar man alltid på rad 1.
2. **Paus-före-semantik.** `[M]` Idag pausas *efter* statementet: det
   highlightade statementet har redan körts och Locals visar läget efteråt.
   Alla andra debuggers highlightar det som ska köras *härnäst*. Byt till
   capture + pause före statementet. Förenklar också RETURN-hanteringen.
3. **PRINT och resultatmängder syns inte.** `[M]` Runnern kör `ExecuteAsync`
   och kastar bort både `InfoMessage` (PRINT/RAISERROR) och result sets.
   Streama till Debug Console: PRINT som stdout, result sets som texttabell
   (med radcap), rader påverkade när NOCOUNT är av.
4. **Stop kör klart scriptet.** `[S]` *Säkerhetsbugg.* `StopAsync` signalerar
   `continue` och avbryter sedan – statements hinner köras efter pausen
   innan cancel biter. Lägg till kommandot `abort` som får `__dbg.Pause`
   att `THROW`, så batchen dör exakt vid pauspunkten.
5. **SQL-fel pekar på fel rad.** `[M]` `SqlException.LineNumber` avser den
   instrumenterade batchen. Bygg en omvänd radkarta i `Splice`, mappa till
   originalraden och rapportera som "stopped on exception" med statementet
   highlightat och felet i Debug Console – i stället för bara terminated.
6. **Slutläget i modulläge försvinner.** `[S]` Efter sista statementet
   avslutas sessionen och Locals töms – returvärde och OUTPUT-parametrar
   hinner aldrig ses. Lägg in ett virtuellt stopp "slut på batch" och skriv
   `@__dbg_return`/OUTPUT-värden till Debug Console vid avslut.
7. **`USE` i scriptet bryter pausmekaniken.** `[S]` `__dbg.Pause` anropas
   utan databasnamn; efter `USE annanDb` finns den inte. Kvalificera
   anropen med initialdatabasen (`[Db].__dbg.Pause`) vid instrumentering.
8. **Gammal sidecar överlever uppgradering.** `[S]` En kvarlämnad sidecar
   svarar på `/health` och används trots att extensionen är nyare. Jämför
   `version` i `/health` med den buntade versionen och starta om vid
   mismatch. (Versionen finns redan i svaret.)

## Fas 2 – vardagsupplevelse

- **Breakpoint mitt i flerradigt statement.** `[S]` Snap-down hoppar till
  *nästa* statement om man klickar på rad 3 av en 5-raders SELECT. Mappa
  via spans (finns i `StmtToSpan`) så rader inuti ett statement träffar det.
- **Hover och Watch för variabler.** `[S]` `supportsEvaluateForHovers` +
  `evaluate` som slår upp namnet i fångade locals. Sätt även `type` på
  DAP-variablerna så typen syns i Variables-panelen.
- **Pause-knappen.** `[S]` `pauseRequest` = signalera `stepOver`; batchen
  stannar vid nästa statement. Idag är knappen död.
- **Upptäckbarhet.** `[M]` CodeLens "▷ Debugga med parametrar" ovanför
  `CREATE PROCEDURE/FUNCTION`, kommandon i paletten ("T-SQL: Debugga aktuell
  fil", "…kör igen med samma parametrar") och en setting
  `sqldbgr.moduleFiles: ask | debug | run` så QuickPicken inte dyker
  upp vid varje F5.
- **Parse-fel i Problems-panelen.** `[S]` Idag ett 400-fel i en dialog.
  Returnera rad/kolumn från sidecaren och publicera Diagnostics.
- **Anslutningar via mssql-extensionen.** `[M]` De flesta har redan
  profiler i ms-mssql.mssql; erbjud "välj anslutning" via dess API i
  stället för råa connection strings, och lägg lösenord i `SecretStorage`
  i stället för `settings.json`.
- **Typad parameterbindning.** `[S]` Parametrar skickas som strängar och
  konverteras implicit (datum är kulturberoende). Mappa AST-typen till
  `SqlDbType` och konvertera i sidecaren – panelen kan då också validera.
- **Locals-polish.** `[S]` Deklarationsordning i stället för alfabetisk,
  källtextens typnamn överallt (`NUMERIC(14,4)` i stället för `Numeric`),
  `@__dbg_return` visad som `(returvärde)`, och `TOP n` + antal rader för
  tabellvariabler så stora tabeller inte serialiseras varje statement.
- **Temp-tabeller i Locals.** `[M]` `#t` är vanligare än tabellvariabler i
  verkliga script. Spåra `CREATE TABLE #x` / `SELECT … INTO #x` och fånga
  dem på samma sätt.
- **Sidecar-loggen.** `[S]` ASP.NET:s info-loggning fyller output-kanalen;
  sätt Warning som default, verbose via setting.

## Fas 3 – nya use cases

- **Transaktionsläge ("dry run").** `[S/M]` Launch-option
  `transaction: rollback | commit | none`. Kör hela scriptet i en
  transaktion som rullas tillbaka vid slut/stop – gör det ofarligt att
  stega igenom datamodifierande script. Troligen den mest efterfrågade
  funktionen för vardagsanvändning.
- **Villkorliga breakpoints och logpoints.** `[M]` Locals fångas *innan*
  pausen, så sidecaren kan utvärdera villkoret själv på en egen connection
  (`SELECT CASE WHEN <villkor> THEN 1 END` med variablerna deklarerade och
  satta från fångade värden) och auto-fortsätta vid falskt. Ingen
  ominstrumentering, villkor kan ändras mitt i körningen. Logpoints på
  samma sätt.
- **Ändra variabelvärden under körning.** `[M]` `setVariable` via en
  `__dbg.Overrides`-tabell: efter varje paus injiceras
  `SELECT @x = CONVERT(typ, Value) FROM __dbg.Overrides WHERE …` per
  deklarerad variabel. Möjliggör "vad händer om @x = 5" utan omstart.
- **Step-into i stored procedures.** `[L]` Vid `EXEC dbo.Proc` i stegläge:
  hämta definitionen, instrumentera kroppen med samma mekanik som
  modulläget, visa som virtuell source-fil (`DocumentContentProvider`)
  och lägg en riktig stack-frame. Step Out = kör tills framen lämnas.
- **Heartbeat och föräldralösa sessioner.** `[S]` `LastHeartbeatUtc` finns
  i schemat men används inte. Dör sidecaren mitt i en paus loopar
  `__dbg.Pause` för evigt och håller lås. Sidecaren pingar, Pause ger upp
  efter t.ex. 60 s utan heartbeat. Städa gamla Control-rader vid start.
- **Debugschema i annan databas.** `[S]` Miljöer där användaren saknar
  DDL-rätt i måldatabasen: setting för var `__dbg` skapas (t.ex. en
  dedikerad debug-databas), 3-delade namn i instrumenteringen.

## Fas 4 – kvalitet och drift

- **Testprojekt för analysatorn.** `[M]` Det ad hoc-harness som använts
  under utvecklingen (instrumentera → re-parsa → kontrollera lineMap)
  bör bli ett xunit-projekt med fall för IF/WHILE/TRY, blocklösa grenar,
  GO-batchar, modulläge och RETURN-omskrivning, kört i CI.
- **Integrationstest mot riktig SQL Server.** `[M]` GitHub Actions kan
  köra `mcr.microsoft.com/mssql/server` som service-container: starta
  sidecaren, kör ett script med breakpoints via HTTP-API:t, verifiera
  pausordning och locals. Fångar det som enhetstesterna inte kan
  (Pause-proceduren, SESSION_CONTEXT, batchkörning).
- **Extension-tester.** `[S]` `@vscode/test-electron` för
  breakpoint-mappning och parameterpanelens meddelandeprotokoll.
- **Attach-läge (betaldel).** `[L]` Enligt NOTICE.md; bygger på
  deploy/backup/restore av instrumenterade procs och en inverterad
  SESSION_CONTEXT-spärr. Kräver fas 3:s heartbeat/timeout först – man
  får aldrig hänga främmande sessioner utan säkerhetsventil.

## Medvetna avgränsningar

- sqlcmd-läge (`:r`, `:setvar`, `GO n`) stöds inte.
- Pausa inne i *deployade* moduler går inte utan attach-läge; scalar-UDF:er
  aldrig (inga sidoeffekter tillåtna).
- Ad hoc-SQL i Debug Console mot den pausade sessionens temp-tabeller är
  inte möjligt (annan session); mot databasen i övrigt är det möjligt och
  kan läggas till som REPL senare.
