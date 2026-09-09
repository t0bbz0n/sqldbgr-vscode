#!/usr/bin/env node
// Publishes the sidecar into dist/ with the package's version stamped into the
// DLL, so /health reports the same version as the npm package. Run through npm
// run build, cross-platform: shell expansions of $npm_package_version do not
// work on Windows.
'use strict';

const { execFileSync } = require('child_process');
const path = require('path');
const { version } = require('../package.json');

execFileSync('dotnet',
  ['publish', '../sidecar', '-c', 'Release', '-o', 'dist', `-p:Version=${version}`],
  { stdio: 'inherit', cwd: path.join(__dirname, '..') });
