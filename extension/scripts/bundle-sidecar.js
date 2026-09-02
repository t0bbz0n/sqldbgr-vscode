#!/usr/bin/env node
// Publicerar sidecaren till sidecar-dist/ med extensionens version stämplad i
// DLL:n, så /health-versionen matchar extensionen och en lokalt byggd VSIX
// inte tolkar sin egen sidecar som en främmande, gammal instans.
'use strict';

const { execFileSync } = require('child_process');
const path = require('path');
const { version } = require('../package.json');

execFileSync('dotnet',
  ['publish', '../sidecar', '-c', 'Release', '-o', 'sidecar-dist', `-p:Version=${version}`],
  { stdio: 'inherit', cwd: path.join(__dirname, '..') });
