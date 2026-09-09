#!/usr/bin/env node
// Publishes the sidecar into sidecar-dist/ with the extension's version stamped
// into the DLL, so the version /health reports matches the extension and a
// locally built VSIX does not mistake its own sidecar for a stale foreign one.
'use strict';

const { execFileSync } = require('child_process');
const path = require('path');
const { version } = require('../package.json');

execFileSync('dotnet',
  ['publish', '../sidecar', '-c', 'Release', '-o', 'sidecar-dist', `-p:Version=${version}`],
  { stdio: 'inherit', cwd: path.join(__dirname, '..') });
