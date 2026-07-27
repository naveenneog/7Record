# 7Record installer

Install the latest signed Windows release:

```powershell
npx 7record
```

The installer:

1. Selects the x64, ARM64, or x86 MSIX for the current machine.
2. Downloads the matching SHA-256 file.
3. Verifies the checksum.
4. Requires a valid Authenticode signature.
5. Requires the pinned 7Record production publisher certificate and package identity.
6. Installs the MSIX with `Add-AppxPackage`.
7. Launches 7Record.

The repository intentionally ships with no trusted production thumbprint until
the release certificate is provisioned. Installation fails closed until that
thumbprint is committed to the npm package.

Use `npx 7record -- --dry-run` to inspect the release asset without installing,
or `npx 7record -- --release v1.2.3` for a specific tag.
