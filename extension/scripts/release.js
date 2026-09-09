#!/usr/bin/env node
// One command for the whole release: bump the version, build, publish to the
// Marketplace and tag.
//
//   npm run release              # patch: 0.1.0 -> 0.1.1
//   npm run release -- minor
//   npm run release -- 1.0.0
//   npm run release -- patch --no-push
//
// Publishing happens as YOU, through `az login`: no app registration, no
// service principal, no Azure DevOps user. That is the whole point - this route
// needs none of the parts that are awkward to set up.
//
// CI does not collide with this. Its publish job skips the Marketplace when
// AZURE_CLIENT_ID is absent, so the tag pushed below builds the VSIX and creates
// the GitHub release while this run has already done the publishing.
'use strict';

const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const extensionDir = path.join(__dirname, '..');
const manifestPath = path.join(extensionDir, 'package.json');
const win = process.platform === 'win32';

// npx and az are batch files on Windows; execFile cannot run those directly.
const cmd = name => (win ? `${name}.cmd` : name);

function run(file, args, options = {}) {
  execFileSync(file, args, { stdio: 'inherit', cwd: extensionDir, ...options });
}

function capture(file, args) {
  const result = spawnSync(file, args, { cwd: extensionDir, encoding: 'utf8' });
  return { ok: result.status === 0, out: (result.stdout || '') + (result.stderr || '') };
}

function fail(message) {
  console.error(`\n✖ ${message}\n`);
  process.exit(1);
}

const args = process.argv.slice(2);
const push = !args.includes('--no-push');
const bump = args.find(a => !a.startsWith('--')) ?? 'patch';

// A dirty tree means the tag would not describe what was published.
const status = capture('git', ['status', '--porcelain']);
if (status.ok && status.out.trim()) {
  fail('the working tree has uncommitted changes - commit or stash them first, ' +
       'so the tag matches what is published.');
}

// Check the login before bumping anything, so a missing az login does not leave
// the version changed.
const account = capture(cmd('az'), ['account', 'show']);
if (!account.ok) {
  fail('not signed in to Azure. Run:\n\n    az login --allow-no-subscriptions\n\n' +
       'Use the Microsoft account that owns the Marketplace publisher.');
}

const before = JSON.parse(fs.readFileSync(manifestPath, 'utf8')).version;
run(cmd('npm'), ['version', '--no-git-tag-version', bump]);
const version = JSON.parse(fs.readFileSync(manifestPath, 'utf8')).version;
console.log(`\n▸ ${before} -> ${version}\n`);

const vsix = path.join(extensionDir, `sqldbgr-${version}.vsix`);

try {
  run(cmd('npm'), ['run', 'package']);
  run(cmd('npx'), ['-y', '@vscode/vsce', 'publish', '--azure-credential', '--packagePath', vsix]);
} catch (err) {
  // Roll the version back. The Marketplace refuses a version it has already
  // seen, so leaving the bump in place after a failure makes the next attempt
  // look like a duplicate of something that was never published.
  run(cmd('npm'), ['version', '--no-git-tag-version', '--allow-same-version', before]);
  fail(`publish failed (${err.message}) - version rolled back to ${before}.`);
}

run('git', ['add', 'package.json', 'package-lock.json'], { cwd: extensionDir });
run('git', ['commit', '-m', `Release v${version}`]);
run('git', ['tag', `v${version}`]);

if (push) {
  run('git', ['push']);
  run('git', ['push', 'origin', `v${version}`]);
}

console.log(`\n✓ sqldbgr ${version} published.`);
console.log(push
  ? '  Tagged and pushed; CI will build the VSIX and create the GitHub release.'
  : `  Not pushed - run: git push && git push origin v${version}`);
