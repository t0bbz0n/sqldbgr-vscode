#!/usr/bin/env node
// Startar sidecar-tjänsten (framework-dependent .NET-publish som ligger i dist/).
// Kräver .NET 8-runtime på maskinen - paketet bär ingen egen runtime, så det
// förblir litet och plattformsoberoende.
'use strict';

const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const dll = path.join(__dirname, '..', 'dist', 'SqlDebugger.Sidecar.dll');
if (!fs.existsSync(dll)) {
  console.error('sqldbgr-sidecar: dist/SqlDebugger.Sidecar.dll saknas - paketet är felbyggt.');
  console.error('Bygg med: npm run build (kräver .NET 8 SDK).');
  process.exit(1);
}

const probe = spawnSync('dotnet', ['--list-runtimes'], { encoding: 'utf8' });
if (probe.error || probe.status !== 0) {
  console.error('sqldbgr-sidecar: hittar inte `dotnet`. Installera .NET 8-runtime:');
  console.error('  https://dotnet.microsoft.com/download/dotnet/8.0');
  process.exit(1);
}
if (!/Microsoft\.AspNetCore\.App 8\./.test(probe.stdout)) {
  console.error('sqldbgr-sidecar: ASP.NET Core 8-runtime saknas. Installera "ASP.NET Core Runtime 8.0":');
  console.error('  https://dotnet.microsoft.com/download/dotnet/8.0');
  process.exit(1);
}

const child = spawn('dotnet', [dll, ...process.argv.slice(2)], { stdio: 'inherit' });
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => child.kill(signal));
}
child.on('exit', (code, signal) => process.exit(signal ? 1 : code ?? 0));
