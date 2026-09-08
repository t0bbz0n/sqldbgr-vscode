# sqldbgr

Breakpoint-debugging för T-SQL i VS Code – sätt breakpoints i `.sql`-filer,
stega igenom statements, inspektera variabler. Ingen Visual Studio, ingen SSDT.

**Lokal debugging är gratis och kräver ingen licens.** Se `NOTICE.md` för
gränsen mot betalfunktioner: attach-mekaniken ligger i en separat, licensierad
extension - det här repot har bara extension-punkten som laddar den.

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

I dev-hosten: öppna en `.sql`-fil, sätt breakpoints, F5. Körningen stannar
på breakpoints (eller på första statementet med `stopOnEntry: true`); det
highlightade statementet är det som körs härnäst och Locals visar läget
innan det körs. `PRINT`, resultatmängder och SQL-fel hamnar i Debug Console;
ett SQL-fel stannar på den felande raden så Locals kan inspekteras. Efter
sista statementet finns ett virtuellt stopp (vid stegning) som visar
slutläget. Ingen launch.json behövs - saknas `connectionString` hämtas den från settingen
`sqldbgr.connectionString`, och finns inte den heller frågar
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
3. `npx -y sqldbgr-sidecar` (npm-paketet i `sidecar-npm/`) - fallback
   när ingen buntad sidecar finns. Kör `npm run bundle-sidecar` i
   `extension/` för att slippa den i dev-hosten.

Processen ägs av extensionen och städas undan när den avaktiveras; en
sidecar man startat själv rörs aldrig. Loggarna hamnar i output-kanalen
**sqldbgr Sidecar**.

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

## Tester

```bash
cd tests/SqlDebugger.Sidecar.Tests
dotnet test                                    # enhetstester (analysatorn)
SQLDBGR_TEST_CONNECTION="Server=localhost;User Id=sa;Password=...;TrustServerCertificate=true" \
  dotnet test                                  # + integrationstester mot SQL Server
```

Integrationstesterna skapar databasen `sqldbgr_test` och kör hela
pausmekaniken (breakpoints, stegning, loopar, abort, exception-stopp,
modulläge) mot servern. CI kör dem mot `mcr.microsoft.com/mssql/server`
som service-container på varje push.

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
npm run package                                     # bundlar sidecaren + vsce package
code --install-extension sqldbgr-0.1.0.vsix
```

(eller Extensions-panelen → `⋯` → *Install from VSIX…*)

### Publicera till Marketplace

CI publicerar automatiskt när en `v*`-tagg pushas. Två saker måste finnas på
plats en gång, och de går bara att göra manuellt:

1. **En publisher** på <https://marketplace.visualstudio.com/manage>, vars id
   är exakt `tobias-trunehag` - samma som `publisher` i
   `extension/package.json`. Skapandet kräver ett Microsoft-konto och en
   Azure DevOps-organisation (den skapas på köpet om du saknar en).
2. **En Personal Access Token** från Azure DevOps
   (<https://dev.azure.com> → User settings → Personal access tokens) med
   *Organization* satt till **All accessible organizations** och scopet
   **Marketplace → Manage**. Ingenting mindre räcker, och en token som är
   scopead till en enda organisation ger ett svårtolkat 401.
   Lägg den som repo-hemligheten `VSCE_PAT`
   (Settings → Secrets and variables → Actions).

Sedan räcker det med:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Taggen bygger VSIX:en, publicerar den och skapar GitHub-releasen. Saknas
`VSCE_PAT` byggs och releasas den ändå - steget säger bara ifrån att det inte
publicerade, i stället för att fälla releasen.

Att publicera från din egen maskin i stället går lika bra:

```bash
cd extension
npm run package
npx @vscode/vsce publish --packagePath sqldbgr-0.2.0.vsix   # frågar efter PAT
```

Första publiceringen tar några minuter innan tillägget syns i Marketplace;
därefter går uppdateringar igenom på under en minut. Versionen måste alltid
vara högre än den senast publicerade - Marketplace tar inte emot samma version
två gånger, ens efter att man tagit bort den.

## Funktioner i korthet

- Breakpoints (även villkorliga, med träffräkning och logpoints), stegning,
  Pause, Restart utan omfrågning
- Locals: skalärer, tabellvariabler och temp-tabeller som expanderbara träd;
  hover och Watch; ändra variabelvärden under paus (`setVariable`)
- Modulläge: F5 på `CREATE PROCEDURE/FUNCTION` debuggar kroppen med
  parameterpanel; returvärde/OUTPUT rapporteras; CodeLens ovanför definitionen
- Transaktionsläge `transaction: rollback|commit` ("dry run")
- `PRINT`, resultatmängder och SQL-fel i Debug Console; kommandot *Open last
  result set* visar resultat i full bredd; parse-fel i Problems-panelen
- Anslutning via mssql-extensionens profiler eller inputruta, sparad säkert i
  SecretStorage; `debugDatabase` för miljöer utan DDL-rätt
- Sidecaren buntad i VSIX:en, egen per fönster på slumpport, token-autentiserad;
  .NET-runtime hämtas automatiskt
- UI på engelska med svensk översättning (`l10n/`)

## Status / roadmap

Detaljerad plan: se [ROADMAP.md](ROADMAP.md). Kvar: step-into i stored
procedures, attach-mekaniken i sitt separata repo, extension-tester med
`@vscode/test-electron`.

## Kända begränsningar just nu

- Attach-läget (pausa i en deployad modul som annan trafik kör) ligger i en
  separat, licensierad extension - se [docs/ATTACH-PROTOCOL.md](docs/ATTACH-PROTOCOL.md).
  Utan den fungerar allt annat oförändrat.
- Pausa inne i en *deployad* modul går inte utan attach-läge; använd
  modulläget (F5 på filen) för att debugga kroppen. Scalar-funktioner kan
  aldrig pausas (UDF:er tillåter inga sidoeffekter).
- Modulläget kör kroppen som en batch: `RETURN` skrivs om och avslutar
  batchen. Refererar kroppen sig själv rekursivt måste modulen redan finnas.
- sqlcmd-läge (`:r`, `:setvar`, `GO n`) stöds inte. "(n rows affected)"
  visas inte (räknarna förorenas av instrumenteringens egna INSERT/DELETE).

## Licens

MIT – se `LICENSE` och `NOTICE.md`.
