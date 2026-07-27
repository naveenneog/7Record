import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { spawnSync } from 'node:child_process';

const repository = 'naveenneog/7Record';
const packageIdentity = '10E50AEA-A7C9-45B6-B2CB-5DCA37C626A8';
// Populate only with the production code-signing certificate thumbprint.
const trustedPublisherThumbprints = [];

export function architectureName(architecture = process.arch) {
  const names = { x64: 'x64', arm64: 'arm64', ia32: 'x86' };
  const name = names[architecture];
  if (!name) {
    throw new Error(`Unsupported Windows architecture: ${architecture}`);
  }
  return name;
}

export function selectReleaseAssets(release, architecture) {
  const suffix = `win-${architecture}.msix`;
  const msix = release.assets?.find((asset) =>
    asset.name.toLowerCase().endsWith(suffix));
  if (!msix) {
    throw new Error(
      `Release ${release.tag_name ?? ''} has no ${suffix} asset.`);
  }
  const checksum = release.assets.find((asset) =>
    asset.name === `${msix.name}.sha256`);
  if (!checksum) {
    throw new Error(`Release asset ${msix.name} has no SHA-256 file.`);
  }
  return { msix, checksum };
}

export async function verifyChecksum(filePath, checksumPath) {
  const expectedText = await readFile(checksumPath, 'utf8');
  const expected = expectedText.trim().split(/\s+/)[0]?.toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(expected ?? '')) {
    throw new Error('The release checksum file is invalid.');
  }
  const actual = createHash('sha256')
    .update(await readFile(filePath))
    .digest('hex');
  if (actual !== expected) {
    throw new Error(
      `SHA-256 mismatch for ${basename(filePath)}.`);
  }
  return actual;
}

export function verifyPackageTrust(
  filePath,
  runner = spawnSync,
  trustedThumbprints = trustedPublisherThumbprints) {
  if (trustedThumbprints.length === 0) {
    throw new Error(
      'No trusted 7Record publisher certificate is configured.');
  }
  const script = [
    `$ErrorActionPreference='Stop'`,
    `$path=${quotePowerShell(filePath)}`,
    `$signature=Get-AuthenticodeSignature -LiteralPath $path`,
    `Add-Type -AssemblyName System.IO.Compression.FileSystem`,
    `$archive=[System.IO.Compression.ZipFile]::OpenRead($path)`,
    `try{$entry=$archive.GetEntry('AppxManifest.xml');if(-not $entry){throw 'MSIX manifest is missing.'};$reader=[IO.StreamReader]::new($entry.Open());try{[xml]$manifest=$reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$archive.Dispose()}`,
    `[pscustomobject]@{status=$signature.Status.ToString();thumbprint=$signature.SignerCertificate.Thumbprint;subject=$signature.SignerCertificate.Subject;identityName=$manifest.Package.Identity.Name;identityPublisher=$manifest.Package.Identity.Publisher}|ConvertTo-Json -Compress`,
  ].join(';');
  const result = runner(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      script,
    ],
    { encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) {
    throw new Error(
      result.stderr?.trim() || 'Authenticode verification could not run.');
  }
  let trust;
  try {
    trust = JSON.parse(result.stdout);
  } catch {
    throw new Error('Windows returned invalid package trust metadata.');
  }
  if (trust.status !== 'Valid') {
    throw new Error(
      `The MSIX Authenticode signature is ${trust.status || 'unknown'}, not Valid.`);
  }
  const normalizedThumbprint = String(trust.thumbprint ?? '')
    .replaceAll(' ', '')
    .toUpperCase();
  const trusted = trustedThumbprints
    .map((value) => value.replaceAll(' ', '').toUpperCase());
  if (!trusted.includes(normalizedThumbprint)) {
    throw new Error(
      `The MSIX signer ${normalizedThumbprint || 'unknown'} is not a trusted 7Record publisher.`);
  }
  if (trust.identityName !== packageIdentity) {
    throw new Error(
      `Unexpected MSIX identity: ${trust.identityName || 'missing'}.`);
  }
}

export async function run(
  args,
  {
    fetchImpl = globalThis.fetch,
    platform = process.platform,
    architecture = process.arch,
    runner = spawnSync,
  } = {}) {
  const options = parseArguments(args);
  if (options.help) {
    console.log(helpText());
    return;
  }
  if (platform !== 'win32') {
    throw new Error('7Record can currently be installed only on Windows.');
  }
  const architectureLabel = architectureName(architecture);
  const release = await fetchRelease(
    fetchImpl,
    options.release);
  const assets = selectReleaseAssets(release, architectureLabel);
  if (options.dryRun) {
    console.log(
      `7Record ${release.tag_name}: ${assets.msix.name} (${architectureLabel})`);
    return;
  }

  const directory = join(
    tmpdir(),
    '7record-installer',
    release.tag_name ?? 'latest',
    architectureLabel);
  await mkdir(directory, { recursive: true });
  const msixPath = join(directory, assets.msix.name);
  const checksumPath = join(directory, assets.checksum.name);
  await Promise.all([
    download(fetchImpl, assets.msix.browser_download_url, msixPath),
    download(fetchImpl, assets.checksum.browser_download_url, checksumPath),
  ]);
  await verifyChecksum(msixPath, checksumPath);
  verifyPackageTrust(msixPath, runner);
  installAndLaunch(msixPath, runner);
  console.log(`7Record ${release.tag_name} installed and launched.`);
}

export function parseArguments(args) {
  const options = { dryRun: false, help: false, release: null };
  for (let index = 0; index < args.length; index++) {
    const argument = args[index];
    if (argument === '--dry-run') {
      options.dryRun = true;
    } else if (argument === '--help' || argument === '-h') {
      options.help = true;
    } else if (argument === '--release') {
      options.release = args[++index];
      if (!options.release) {
        throw new Error('--release requires a tag.');
      }
    } else {
      throw new Error(`Unknown option: ${argument}`);
    }
  }
  return options;
}

async function fetchRelease(fetchImpl, tag) {
  const path = tag
    ? `releases/tags/${encodeURIComponent(tag)}`
    : 'releases/latest';
  const response = await fetchImpl(
    `https://api.github.com/repos/${repository}/${path}`,
    {
      headers: {
        Accept: 'application/vnd.github+json',
        'User-Agent': '7record-npx-installer',
      },
    });
  if (!response.ok) {
    throw new Error(
      `GitHub release lookup failed (${response.status}).`);
  }
  return response.json();
}

async function download(fetchImpl, url, outputPath) {
  const response = await fetchImpl(url, {
    headers: { 'User-Agent': '7record-npx-installer' },
  });
  if (!response.ok) {
    throw new Error(`Download failed (${response.status}): ${url}`);
  }
  await writeFile(outputPath, Buffer.from(await response.arrayBuffer()));
}

function installAndLaunch(filePath, runner) {
  const command = [
    `$ErrorActionPreference='Stop'`,
    `Add-AppxPackage -Path ${quotePowerShell(filePath)}`,
    `$package=Get-AppxPackage -Name ${quotePowerShell(packageIdentity)} | Sort-Object Version -Descending | Select-Object -First 1`,
    `if(-not $package){throw 'Installed 7Record package was not found.'}`,
    `Start-Process explorer.exe ("shell:AppsFolder\\"+$package.PackageFamilyName+"!App")`,
  ].join(';');
  const result = runner(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', command],
    { encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) {
    throw new Error(
      result.stderr?.trim() || result.stdout?.trim() ||
      'Windows could not install 7Record.');
  }
}

function quotePowerShell(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}

function helpText() {
  return `Usage: npx 7record [options]

Options:
  --release <tag>  Install a specific GitHub release
  --dry-run        Resolve the release asset without downloading
  -h, --help       Show this help`;
}
