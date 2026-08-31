#!/usr/bin/env node
// Publicerar sidecaren till dist/ med paketets version stämplad i DLL:n,
// så /health rapporterar samma version som npm-paketet. Körs via npm run
// build (cross-platform - shell-varianter av $npm_package_version funkar
// inte på Windows).
'use strict';

const { execFileSync } = require('child_process');
const path = require('path');
const { version } = require('../package.json');

execFileSync('dotnet',
  ['publish', '../sidecar', '-c', 'Release', '-o', 'dist', `-p:Version=${version}`],
  { stdio: 'inherit', cwd: path.join(__dirname, '..') });
