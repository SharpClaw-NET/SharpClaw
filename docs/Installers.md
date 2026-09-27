# SharpClaw 0.5.0 preview installers

The Application installer contains the Uno client, Gateway, Runtime, and the
verified module contribution bundle. The Debian Server installer contains
Gateway and Runtime with the same modules and no graphical client. Packaging is
separate from publishing: creating either installer never installs the product,
changes certificate trust, or starts services.

## Publish all deployment targets

Use a clean `main` or `dev` checkout and a frozen BOM produced by
`build/BuildFrozenBom.ps1`. Retain its `feed`, `bundle`, `bom-manifest.json`, and
the independently verified BOM manifest SHA-256. The script verifies every
package and contribution file, uses an isolated NuGet cache/source map, publishes
all fifteen Application/Server/Runtime RID combinations, and retains individual
logs. It requires a fresh output directory; it never erases a previous run.

```powershell
./scripts/publish.ps1 -BomRoot C:/artifacts/frozen-bom `
  -BomManifestSha256 <verified-bom-manifest-sha256> -OutputDir C:/artifacts/publish-01
```

Every deployment has a `publish-manifest.json` containing its source commit,
version, BOM identity, and payload file hashes. `-InstallerRevision` selects the
preview revision: revision 1 is `0.5.0-preview.1` for binaries, `0.5.0.1` for
MSIX, and `0.5.0~preview.1` for Debian. Increment it for an upgrade candidate;
never reuse a delivered version for changed bytes. Installer scripts require
the same clean source commit as that manifest. Contributions retain all reviewed
native assets; each Runtime has its own platform-specific sidecar apphost and
bundled .NET runtime, with no system-wide .NET prerequisite.

## Windows Application MSIX

On Windows with the Windows SDK, package the verified `Application-win-x64`
directory using its manifest SHA-256 and the existing `CN=SharpClaw Dev`
code-signing certificate. The builder validates the package and signature,
exports only the public certificate, and writes an installer manifest recording
both hashes and whether the signing chain is trusted on that machine. It never
exports a private key, imports a certificate, or installs an application.

```powershell
./scripts/package-msix.ps1 -StageRoot C:/artifacts/publish-01/SharpClaw-Application-win-x64 `
  -PublishManifestSha256 <verified-publish-manifest-sha256> `
  -CertificateThumbprint <reviewed-signing-certificate-thumbprint> `
  -OutputDir C:/artifacts/msix-01
```

After personally reviewing the MSIX, its hashes, and certificate, double-click
the MSIX if the signer is already trusted. Otherwise run the delivered
`Install-SharpClaw.ps1` with the reviewed package/certificate paths and their
expected SHA-256 values. Its optional `-TrustDevelopmentCertificate` switch
requires administrator access and explicit confirmation before importing that
particular development signer into LocalMachine TrustedPeople; installation
also requires confirmation. Do not disable signature checks. The package uses
the stable `com.mkn8rn.SharpClaw` / `CN=SharpClaw Dev` identity; a higher numeric
version upgrades that identity. Instance configuration, keys, logs, and storage
are kept outside the read-only installed binaries. Keep a backup of instance
state before an upgrade; uninstalling/reinstalling is not a data rollback.

## Debian 13 Server

Transfer the verified `Server-linux-x64` directory to Debian, retaining hidden
configuration templates, then run the builder with its reviewed manifest hash.
The builder uses `dpkg-deb`, includes root-owned binaries, executable ELF hosts,
systemd units, AGPL source offer, and administrator conffiles. It supports amd64
and arm64 and never installs or starts a service.

```powershell
./scripts/package-debian.ps1 -StageRoot /artifacts/SharpClaw-Server-linux-x64 `
  -PublishManifestSha256 <verified-publish-manifest-sha256> -OutputDir /artifacts/debian-01
```

Only after the owner accepts the Windows package and authorizes Shitbox1's
installation, verify the Debian package hash and use
`sudo apt install ./sharpclaw-server_0.5.0~preview.1_amd64.deb`. Fresh installation
creates the restricted `sharpclaw` system account and private state directories
but does not start or enable services. Review `/etc/sharpclaw/runtime.env` and
`gateway.env`, then explicitly run
`sudo systemctl enable --now sharpclaw-api.service sharpclaw-gateway.service`.
Runtime readiness must pass before the Gateway starts; both bind to loopback by
default. Expose only the Gateway after arranging the required access controls
and TLS. Custom Runtime ports require a matching readiness-unit override.

Debian binaries live in `/usr/lib/sharpclaw/{backend,gateway}`; protected
configuration, keys, discovery, logs, and storage live in `/var/lib/sharpclaw`.
Administrator environment overrides take precedence over protected instance
files in installed mode. dpkg preserves edited `/etc` conffiles on upgrades;
existing protected instance files and data are never replaced. An upgrade
quiesces the old services and resumes only those previously running. Removal
stops services; even purge preserves `/var/lib/sharpclaw` and its account. For
rollback, stop both services and restore a compatible installer and an explicit
pre-upgrade state backup; do not run an older binary against migrated data
without compatibility evidence.
