# Licensiering

All kod i detta repo är MIT-licensierad och fri att använda, modifiera
och distribuera.

**Lokal debugging (mot localhost/(localdb)) är helt gratis, utan licens.**

**Remote/attach-läge** (debugging mot en delad dev/QA-server) kräver en
licensnyckel utfärdad via vår hostade tjänst. Det är den enda delen som
pratar med en extern server – all kärnfunktionalitet (parsning,
instrumentering, DAP) körs och kan granskas helt lokalt.

Attach-mekaniken ligger **inte** i det här repot. Den är en separat
extension med egen sidecar, och det här repot exponerar bara en
extension-punkt som laddar den om den är installerad och licensierad – se
[docs/ATTACH-PROTOCOL.md](docs/ATTACH-PROTOCOL.md). Allt som finns här är
och förblir MIT; saknas attach-extensionen påverkas lokal debugging inte
alls.
