import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import {
  architectureName,
  parseArguments,
  selectReleaseAssets,
  verifyPackageTrust,
  verifyChecksum,
} from '../src/installer.mjs';

test('maps supported Windows architectures', () => {
  assert.equal(architectureName('x64'), 'x64');
  assert.equal(architectureName('arm64'), 'arm64');
  assert.equal(architectureName('ia32'), 'x86');
  assert.throws(() => architectureName('riscv64'));
});

test('selects matching MSIX and checksum assets', () => {
  const assets = selectReleaseAssets(
    {
      tag_name: 'v1.0.0',
      assets: [
        { name: '7Record-win-x64.msix' },
        { name: '7Record-win-x64.msix.sha256' },
      ],
    },
    'x64');
  assert.equal(assets.msix.name, '7Record-win-x64.msix');
});

test('verifies SHA-256 checksums', async () => {
  const root = await mkdtemp(join(tmpdir(), '7record-cli-'));
  const file = join(root, 'app.msix');
  const checksum = join(root, 'app.msix.sha256');
  const bytes = Buffer.from('seven-record');
  await writeFile(file, bytes);
  await writeFile(
    checksum,
    `${createHash('sha256').update(bytes).digest('hex')}  app.msix\n`);
  await assert.doesNotReject(() => verifyChecksum(file, checksum));
});

test('requires the pinned Authenticode signer and package identity', () => {
  verifyPackageTrust('app.msix', () => ({
    status: 0,
    stdout: JSON.stringify({
      status: 'Valid',
      thumbprint: 'ABC123',
      identityName: '10E50AEA-A7C9-45B6-B2CB-5DCA37C626A8',
    }),
    stderr: '',
  }), ['ABC123']);
  assert.throws(() =>
    verifyPackageTrust('app.msix', () => ({
      status: 0,
      stdout: JSON.stringify({
        status: 'Valid',
        thumbprint: 'ATTACKER',
        identityName: '10E50AEA-A7C9-45B6-B2CB-5DCA37C626A8',
      }),
      stderr: '',
    }), ['ABC123']));
});

test('parses installer options', () => {
  assert.deepEqual(
    parseArguments(['--release', 'v1.2.3', '--dry-run']),
    { release: 'v1.2.3', dryRun: true, help: false });
});
