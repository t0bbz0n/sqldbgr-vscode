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

I dev-hosten: öppna en `.sql`-fil, sätt breakpoints, F5. Ingen launch.json
behövs - saknas `connectionString` hämtas den från settingen
`tsql-debugger.connectionString`, och finns inte den heller frågar
extensionen efter en vid start (med erbjudande att spara i settings).
Vill man styra mer skapar man en launch-konfiguration av typen `tsql`
(snippet finns).

### Sidecaren startar automatiskt

Ingen backend behöver startas för hand, och ingen .NET behöver vara
installerad: vid F5 probar extensionen `sidecarUrl` (`/health`) och startar
annars sidecaren själv. Startkommandot väljs i ordning:

1. `sidecarCommand` från launch-konfigurationen (dev-override, se nedan)
2. Den **buntade sidecaren i VSIX:en** (`extension/sidecar-dist/`), körd med
   en ASP.NET Core 8-runtime som [.NET Install Tool-extensionen]
   (ms-dotnettools.vscode-dotnet-runtime, ett extension-beroende) laddar ner
   automatiskt vid första körningen. Finns runtimen redan svarar den direkt.
   Utan Install Tool (t.ex. i dev-hosten) provas systemets `dotnet`.
3. `npx -y tsql-debugger-sidecar` (npm-paketet i `sidecar-npm/`) - fallback
   när ingen buntad sidecar finns. Kör `npm run bundle-sidecar` i
   `extension/` för att slippa den i dev-hosten.

Processen ägs av extensionen och städas undan när den avaktiveras; en
sidecar man startat själv rörs aldrig. Loggarna hamnar i output-kanalen
**T-SQL Debugger Sidecar**.

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

CI (`.github/workflows/build.yml`) bygger VSIX:en på varje push - ladda ner
den under **Actions → körningen → Artifacts**. Taggas en `v*`-tagg skapas en
GitHub-release med VSIX:en och sidecar-npm-tarballen bifogade.

Versionering: varje CI-bygge får `major.minor` från `extension/package.json`
plus run-numret som patch (t.ex. `0.1.7`), stämplat i VSIX, npm-paket och
sidecar-DLL - `GET /health` svarar med versionen, så det syns exakt vilket
bygge som kör. Vid `v*`-taggar används taggens version rakt av.

För att bygga och installera lokalt:

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
- [x] Modulläge: F5 på ett `CREATE FUNCTION`/`PROCEDURE`-script debuggar
      kroppen som script - parametervärden matas in vid start, `RETURN`
      fångas i `@__dbg_return` (syns i Locals)
- [x] Parameterpanel: webview-formulär med alla parametrar, typinfo,
      NULL-kryss, deklarerade defaults och minne av senast använda värden
- [ ] Step-into i stored procedures (inline-expansion, virtuella source-filer)
- [ ] Conditional breakpoints
- [ ] Deploy/backup/restore av procs från fil (remote-förberedelse)
- [ ] Attach-läge + licens (betald del, separat repo)

## Kända begränsningar just nu

- Modul-definitioner (`CREATE PROCEDURE`/`FUNCTION`/`VIEW`/`TRIGGER`)
  som körs som script instrumenteras inte - använd modulläget (F5 på
  filen -> "Debugga funktionen/proceduren") för att debugga kroppen.
  Att pausa i den *deployade* modulen går inte: scalar-funktioner
  tillåter inga sidoeffekter, och step-into i procs är roadmap.
- Modulläget kör kroppen som en batch: `RETURN` mitt i skrivs om och
  avslutar batchen, men beteendeskillnader kan finnas (t.ex. refererar
  kroppen sig själv rekursivt krävs att modulen redan är deployad).
- Parametervärden binds som `NVARCHAR`-parametrar och konverteras
  implicit - ange datum som ISO (`2024-01-31`) för säkerhets skull.
- Locals-captures körs före `__dbg.Pause` – variabler visar värdet
  *efter* att statementet körts.

## Licens

MIT – se `LICENSE` och `NOTICE.md`.
