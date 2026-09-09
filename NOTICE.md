# Licensing

All code in this repository is MIT licensed and free to use, modify and
distribute.

**Local debugging, against localhost or (localdb), is entirely free and needs
no licence.**

**Remote and attach mode**, debugging against a shared dev or QA server,
requires a licence key issued by our hosted service. That is the only part
that talks to an external server. Everything at the core, parsing,
instrumentation and the debug adapter protocol, runs locally and can be
inspected here.

The attach mechanism is **not** in this repository. It is a separate extension
with its own sidecar, and this repository only exposes an extension point that
loads it when it is installed and licensed. See
[docs/ATTACH-PROTOCOL.md](docs/ATTACH-PROTOCOL.md). Everything here is MIT and
stays MIT; without the attach extension, local debugging is unaffected.
