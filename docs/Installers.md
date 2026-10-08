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

Each stage also carries `legal/redistribution-inventory.json`. Publishing walks
every resolved dependency manifest and the exact third-party DLLs embedded in
contributions, retains package licences and notices, obtains hash-pinned
upstream texts when a package omits them, and attributes redistributed managed,
native, runtime-pack, and font files to their exact source archive entries within
their own dependency scope. SDK publish receipts capture selected input digests,
final copy-source digests, and non-composite ReadyToRun input/output mappings,
compiler identity/digest, SDK target hashes and compiler inputs. Unchanged copies
must byte-match their archive entry; transformed files must match their captured
output, verified original input, normalized managed metadata, method IL/exception
state, each RVA-backed field's token/validated length/initializer bytes, and
resources. Field addresses may relocate, but their stored values may not change.
Initializer sizes must be fixed-width primitives or locally declared, fieldless,
fixed-size value types with validated layout; unresolved, platform-dependent,
generic or otherwise unsupported layouts fail closed rather than estimating a
length from neighbouring addresses. Each publish invalidates stale intermediate ReadyToRun
images using its new input receipt. Historical contributions without dependency
manifests require explicit scoped archive-entry/digest pins in the notice policy;
a shared filename is never evidence of origin. Unknown licensing, missing
notices, wrong-version/replaced bytes, extra nested known-name files, unsupported
transformations and unattributed files fail the publish step. The stage audit
rechecks actual archives and receipts before either installer is built. The legal
inventory includes the Open Sans font's own OFL notice in addition to Uno's
package licence; the five Persistence written offers remain byte-for-byte from
their reviewed nupkgs. The publisher needs network access to NuGet.org and the
hash-pinned public licence sources on a cache miss.

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
`Install-SharpClaw.ps1` in Windows PowerShell 5.1 (the stock Windows shell) or
PowerShell 7 with the reviewed package/certificate paths and their
expected SHA-256 values. Its optional `-TrustDevelopmentCertificate` switch
requires administrator access and explicit confirmation before importing that
particular development signer into LocalMachine TrustedPeople; installation
also requires confirmation. Do not disable signature checks. The package uses
the stable `com.mkn8rn.SharpClaw` / `CN=SharpClaw Dev` identity; a higher numeric
version upgrades that identity. Instance configuration, keys, logs, and storage
are kept outside the read-only installed binaries. Keep a backup of instance
state before an upgrade; uninstalling/reinstalling is not a data rollback.

Before independent review or delivery, run `scripts/test-installed-msix.ps1` in
stock Windows PowerShell 5.1 (`powershell.exe`, for its UI Automation assemblies)
against the exact signed package in an elevated, interactive `SharpClawMSIXTest*`
account inside a disposable Windows VM. Pass its exact name through
`-ExpectedGuestComputerName`; the gate refuses a different machine, account,
existing installation, or SharpClaw profile. It checks the package, source, and
certificate identities and requires genuinely deployed templates marked
`Application Protected`, not merely files extracted from an MSIX. Before
writing any test configuration, it launches the registered AUMID from a clean
profile and verifies frontend/backend template bytes against their protected
package sources, without copying encryption or read-only attributes. Windows may independently protect newly created
files inside the package's own LocalCache: those targets must retain exact bytes
and actual write access, and encrypted targets are accepted only in that exact
LocalCache with `cipher` corroborating Application Protected. Journal evidence
is also exported by contents, not metadata, with sharing compatible with the
active journal writer. The gate waits for terminated test processes to disappear
before configured activation and uninstall. The negative
`File.Copy` control must reproduce Windows error 6000; content-only seeding must
succeed. Clean first launch must also reach `/echo`, authenticated `/readyz` and
`/ping`, report setup required and expose the visible provider setup controls.
Supply explicit `-TestProviderKey`, `-TestModel` and `-TestProviderEndpoint` for
an independently prepared real keyless test backend. The gate selects the enabled
provider through the product UI, saves its protected configuration, verifies a
different Runtime PID and configured chat UI, then sends one short message.
A nonempty assistant response with terminal-complete accessibility status is
required; a partial stream or HTTP success alone does not pass. Retain the real
backend's model identity and request execution evidence alongside this report;
the script does not certify that an arbitrary endpoint is genuine inference.
Only afterward does it enable Gateway, whose first startup must seed its own
untouched templates before the test proceeds; observing a newly spawned Gateway
process alone does not prove seeding. The gate requires one visible
window, a real UI Automation boot
element plus screenshot, and the configured Runtime/Gateway processes within a
deadline. Repeated activation must not leave duplicate windowless processes.
Full MSIX registration requires the signer in the machine's Trusted People store,
so the gate adds it **only inside the disposable VM**, removes that exact certificate
after testing, and checks its removal; it never changes the host's trust store.
The package is uninstalled and removal verified. This gate is not owner delivery
or approval. The startup journal is flushed independently of the window and DI
under the user's `SharpClaw/diagnostics/startup` directory; it records stages and
exception types/codes, never exception messages or configuration values.

Runtime can start and expose authenticated setup and health endpoints without
a chat provider or credentials. The first-run client opens Settings when its
default chat profile needs configuration. Choose an enabled module's provider,
its model, and any required endpoint/key; saving uses the existing protected
backend configuration document and restarts the frontend-owned Runtime as a
complete new process/graph. Unrelated settings and provider-scoped credentials
are preserved. The client never writes to or stops an external Runtime.
Installers do not choose a provider, ship credentials, or supply a model.
Unconfigured chat requests fail with HTTP 409 `provider_setup_required`, while
health/setup remain available; a module-owned chat profile can provide its own
selection. An explicit unknown default provider or invalid module graph still
fails closed. Exited bundled processes are not automatically relaunched with
the same inputs: the boot page clears its progress animation and reports the
actual stopped/exited state, leaving explicit retry or exit to the user. A
keyless provider still needs its actual backend and model to complete chat.

Runtime request ingress and execution use separate actions in the same compiled
graph: `runtime.request.receive` handles the short, repeat-safe input stage;
`runtime.request.handler.invoke` contains the actual buffered or streamed handler.
Both stages preserve the request's caller, features, trace and idempotency identity,
and modules can intercept, rewrite or cancel them under their existing grants.
Ingress does not hold its 30-second budget over a complete model response. Handler
execution uses its existing finite budget, and the handler terminal runs at most
once; a module-supplied result that skips a required terminal is rejected. Neither
the kernel's action budgets nor installer-test observation limits are extended.

Cold module-sidecar startup has its own bounded bootstrap budget rather than
an action-execution deadline: `Packages__OutOfProcessSidecarStartupTimeoutSeconds`
defaults to 120 and accepts integers from 1 to 600. Process exit and caller
cancellation terminate the readiness wait, and a failed probe does not grant readiness.
The bundled-client echo probe allows 120 seconds per existing bounded attempt;
external targets retain their five-second probe budget. A larger startup
allowance is not a startup-speed or reliability guarantee, and preserved inner
exceptions distinguish timeout, cancellation and process failure diagnostics.
Client action observers inherit the finite Core action deadline instead of
imposing a separate five-second limit on the wrapped terminal. Caller
cancellation and uncertainty safeguards remain in force; an uncertain process
start is not automatically retried.
Client terminals retain the caller's synchronization context when one exists,
so UI state and navigation commits run on the client UI thread even though Core
schedules generic kernel work independently. The client bridge preserves the
authorized terminal's execution context, not an earlier caller snapshot.
Cross-thread transfer with suppressed execution-context flow is rejected.
Cancellation before a queued terminal starts prevents any later mutation;
once started, its receipt waits for actual completion and Core retains ownership
of deadlines and uncertain effects. Background callers remain background callers.
The installed gate acknowledges physical text input by exact public readback
before Save or Send; incomplete input fails closed without replaying it.
Provider selection likewise uses physical keyboard navigation with read-only
public selection acknowledgement after each step; it never advances or saves
when the intended item is unconfirmed.
The provider combo binds its accessible name and ItemStatus to the actual
selected option's display name and provider key. The installed gate reads that
public key against the authenticated provider list; it does not infer selection
from accepted key events or unnamed platform item peers. These view bindings do
not select a provider or write configuration.

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
`gateway.env`; set `Provider__Key`, the model, and any required credentials
before explicitly running
`sudo systemctl enable --now sharpclaw-api.service sharpclaw-gateway.service`.
Runtime readiness must pass before the Gateway starts; both bind to loopback by
default. Expose only the Gateway after arranging the required access controls
and TLS. Custom Runtime ports require a matching readiness-unit override.

Debian binaries live in `/usr/lib/sharpclaw/{backend,gateway}`; protected
configuration, keys, discovery, logs, and storage live in `/var/lib/sharpclaw`.
Administrator environment overrides take precedence over protected instance
files in installed mode. dpkg preserves edited `/etc` conffiles on upgrades;
existing protected instance files and data are never replaced. An upgrade
quiesces the old services and resumes only those previously running, even if
they were manually started without boot enablement. Masking, administrator
`policy-rc.d` restrictions, or a failed restart prevent restoration: configuration
reports failure and retains the resume marker for an explicit retry; it never
enables a service or treats a skipped start as success. Removal
stops services; even purge preserves `/var/lib/sharpclaw` and its account. For
rollback, stop both services and restore a compatible installer and an explicit
pre-upgrade state backup; do not run an older binary against migrated data
without compatibility evidence.
