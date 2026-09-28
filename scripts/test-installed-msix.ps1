#requires -Version 5.1
<# Destructive ONLY to a dedicated clean test user's exact SharpClaw installation.
   Run elevated in that user's interactive, disposable Windows VM desktop.
   Temporarily trusts the exact signer in that GUEST's machine store, not on the host.
   Never run as the owner's normal account. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPackageSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$ExpectedTestUserSid,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,15}$')][string]$ExpectedGuestComputerName,
    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedCertificateSha256,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(10, 600)][int]$StartupTimeoutSeconds = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this UI Automation gate with stock Windows PowerShell 5.1 (powershell.exe).' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$account = ($identity.Name -split '\\')[-1]
if ($identity.User.Value -ne $ExpectedTestUserSid -or
    $account -notmatch '^(SharpClawMSIXTest[A-Za-z0-9_]*|WDAGUtilityAccount)$' -or
    [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
    throw 'Run only as the specified dedicated test user in an interactive desktop.'
}
if (-not [string]::Equals([Environment]::MachineName, $ExpectedGuestComputerName,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run only in the specified disposable Windows guest.'
}
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run in an elevated PowerShell desktop inside the disposable guest to trust this full MSIX.'
}
foreach ($expected in @(@($PackagePath, $ExpectedPackageSha256), @($CertificatePath, $ExpectedCertificateSha256))) {
    if ((Get-FileHash -LiteralPath $expected[0] -Algorithm SHA256).Hash -ne $expected[1]) { throw 'Test input hash mismatch.' }
}
$profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SharpClaw'
if (Test-Path -LiteralPath $profileRoot) { throw 'The test user already has SharpClaw state; use a clean user.' }
if (Get-AppxPackage -Name 'com.mkn8rn.SharpClaw') { throw 'The test user already has SharpClaw installed.' }
if (Get-Process -Name 'SharpClaw*' -ErrorAction SilentlyContinue) { throw 'SharpClaw processes already exist; do not disturb them.' }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Use a new evidence directory.' }
if ($ReportDirectory.StartsWith($profileRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence must be outside the test state directory that will be removed.'
}
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem, UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies 'UIAutomationClient', 'UIAutomationTypes', 'WindowsBase', 'System.Core' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
public static class SharpClawInstalledProbe {
    static Task<bool> bootUiProbe;
    static long bootUiProbeHandle;
    public delegate bool EnumCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    public struct Rect { public int Left, Top, Right, Bottom; }
    public sealed class WindowInfo { public long Handle; public uint ProcessId; public string Title; }
    public static WindowInfo[] VisibleWindows(uint processId) {
        var result = new List<WindowInfo>();
        EnumWindows((window, state) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == processId && IsWindowVisible(window)) {
                var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity);
                result.Add(new WindowInfo { Handle=window.ToInt64(), ProcessId=owner, Title=text.ToString() });
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint pid);
        [PreserveSig] int ActivateForFile(IntPtr item, IntPtr verb, out uint pid);
        [PreserveSig] int ActivateForProtocol(IntPtr item, out uint pid);
    }
    public static uint Activate(string aumid) {
        var manager = (IActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
        try { uint pid; Marshal.ThrowExceptionForHR(manager.ActivateApplication(aumid, "", 0, out pid)); return pid; }
        finally { Marshal.ReleaseComObject(manager); }
    }
    public static bool HasVisibleBootUi(long handle) {
        // A slow UIA provider must not end the startup test after one short
        // probe. Keep at most one outstanding COM call per visible window and
        // let the caller's overall startup deadline bound the observation.
        if (bootUiProbe == null || bootUiProbeHandle != handle) {
            bootUiProbeHandle = handle;
            bootUiProbe = Task.Run(() => FindVisibleBootUi(handle));
            return false;
        }
        if (!bootUiProbe.IsCompleted) return false;
        var visible = bootUiProbe.GetAwaiter().GetResult();
        if (!visible) bootUiProbe = null;
        return visible;
    }
    static bool FindVisibleBootUi(long handle) {
        var root = AutomationElement.FromHandle(new IntPtr(handle));
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "SharpClawBoot");
        var boot = root.FindFirst(TreeScope.Descendants, condition);
        return boot != null && !boot.Current.IsOffscreen;
    }
}
'@
$package = $null
$trustOwnedByTest = $false
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($CertificatePath))
if ($certificate.Subject -ne 'CN=SharpClaw Dev' -or $certificate.NotAfter -le (Get-Date)) {
    $certificate.Dispose()
    throw 'Unexpected or expired test signer.'
}
$guestTrustPath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
if (Test-Path -LiteralPath $guestTrustPath) {
    $certificate.Dispose()
    throw 'The guest already trusts this signer; use a clean VM so the gate can own and remove temporary trust.'
}
$result = [ordered]@{
    SourceCommit = $ExpectedSourceCommit; PackageSha256 = $ExpectedPackageSha256
    TestUserSid = $ExpectedTestUserSid; StartUtc = [DateTime]::UtcNow.ToString('O')
    Aumid = $null; ActivatedProcessId = 0; Window = $null; BootUiObserved = $false
    RuntimeObserved = $false; GatewayObserved = $false; ProcessSnapshotTimeouts = 0
    CleanupVerified = $false; Success = $false
}
function Get-TestPackageProcesses {
    param([switch]$AllowTransientTimeout)
    if ($null -eq $package) { return @() }
    try {
        return @(Get-CimInstance Win32_Process -Filter "Name LIKE 'SharpClaw%'" -OperationTimeoutSec 10 | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith($package.InstallLocation + '\', [StringComparison]::OrdinalIgnoreCase)
        })
    } catch {
        # A cold Windows guest can temporarily time out the process provider.
        # Only observation may retry; cleanup must still prove an exact snapshot.
        if ($AllowTransientTimeout -and $_.Exception.Message -match 'Timed out') {
            $result.ProcessSnapshotTimeouts++
            return @()
        }
        throw
    }
}
function Save-WindowCapture {
    param([long]$Handle)
    $rectangle = [SharpClawInstalledProbe+Rect]::new()
    if (-not [SharpClawInstalledProbe]::GetWindowRect([IntPtr]$Handle, [ref]$rectangle)) { throw 'Cannot read window geometry.' }
    $bitmap = [Drawing.Bitmap]::new($rectangle.Right - $rectangle.Left, $rectangle.Bottom - $rectangle.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try {
        if (-not [SharpClawInstalledProbe]::PrintWindow([IntPtr]$Handle, $dc, 2)) { throw 'Window capture failed.' }
    } finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $ReportDirectory 'boot-window.png'), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}
try {
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('publish-manifest.json').Open())
        try { $published = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($published.SourceCommit -ne $ExpectedSourceCommit -or [version]$published.InstallerVersion -le [version]'0.5.0.1') {
            throw 'Reject obsolete or mismatched installer identity.'
        }
    } finally { $zip.Dispose() }
    # Full MSIX deployment checks the machine store. This is the disposable
    # guest's TrustedPeople store, never the owner/host or a broad Root store.
    # Own cleanup even when Import-Certificate fails after partially writing.
    $trustOwnedByTest = $true
    $null = Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
    if (-not (Test-Path -LiteralPath $guestTrustPath)) {
        throw 'The exact signer was not added to the disposable guest TrustedPeople store.'
    }
    Add-AppxPackage -Path $PackagePath
    $package = Get-AppxPackage -Name 'com.mkn8rn.SharpClaw'
    if ($null -eq $package -or $package.Status -ne 'Ok' -or $package.Version.ToString() -ne $published.InstallerVersion) {
        throw 'Exact package registration failed.'
    }
    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName
    if ($manifest.Package.Identity.Publisher -ne $certificate.Subject) { throw 'Signer and installed identity differ.' }
    $applications = @($manifest.Package.Applications.Application)
    if ($applications.Count -ne 1) { throw 'Expected exactly one registered application.' }
    $result.Aumid = "$($package.PackageFamilyName)!$($applications[0].Id)"
    # Explicit non-secret configuration, not an implicit production default.
    # This tests the configured local stack without contacting an LLM backend.
    $frontend = Join-Path $profileRoot 'installed/com.mkn8rn.SharpClaw/frontend'
    $configs = @(
        @('Environment/.env.template', (Join-Path $frontend 'config'), 'Gateway__Enabled="true"'),
        @('backend/Environment/.env.template', (Join-Path $frontend 'stack/backend/config'), 'Provider__Key="ollama"'),
        @('gateway/Environment/.env.template', (Join-Path $frontend 'stack/gateway/config'), '')
    )
    foreach ($config in $configs) {
        New-Item -ItemType Directory -Path $config[1] -Force | Out-Null
        $text = [IO.File]::ReadAllText((Join-Path $package.InstallLocation $config[0]))
        if ($config[2]) {
            $key = ($config[2] -split '=')[0]
            $text = [regex]::Replace($text, "(?m)^$key=.*$", '') + "`n" + $config[2] + "`n"
        }
        [IO.File]::WriteAllText((Join-Path $config[1] '.env.template'), $text, [Text.UTF8Encoding]::new($false))
    }
    $result.ActivatedProcessId = [SharpClawInstalledProbe]::Activate($result.Aumid)
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        $processes = @(Get-TestPackageProcesses -AllowTransientTimeout)
        $clients = @($processes | Where-Object Name -eq 'SharpClaw.Client.Uno.exe')
        if ($clients.Count -gt 1) { throw "Expected one client process, observed $($clients.Count)." }
        if ($clients.Count -eq 0) {
            Start-Sleep -Milliseconds 200
            continue
        }
        $windows = @([SharpClawInstalledProbe]::VisibleWindows([uint32]$clients[0].ProcessId))
        if ($windows.Count -gt 1) { throw 'Duplicate top-level application windows.' }
        if ($windows.Count -eq 1 -and $windows[0].Handle -ne 0) {
            $client = Get-Process -Id $clients[0].ProcessId
            if ($client.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Visible window lacks the expected main window handle.' }
            $result.Window = $windows[0]
            if (-not $result.BootUiObserved -and [SharpClawInstalledProbe]::HasVisibleBootUi($windows[0].Handle)) {
                $result.BootUiObserved = $true
                Save-WindowCapture $windows[0].Handle
            }
        }
        $result.RuntimeObserved = @($processes | Where-Object {
            $_.Name -eq 'SharpClaw.Runtime.Host.exe' -and $_.ParentProcessId -eq $clients[0].ProcessId
        }).Count -eq 1
        $result.GatewayObserved = @($processes | Where-Object {
            $_.Name -eq 'SharpClaw.Gateway.exe' -and $_.ParentProcessId -eq $clients[0].ProcessId
        }).Count -eq 1
        if ($result.BootUiObserved -and $result.RuntimeObserved -and $result.GatewayObserved) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not ($result.BootUiObserved -and $result.RuntimeObserved -and $result.GatewayObserved)) {
        throw 'Installed activation did not expose boot UI and start the configured Runtime/Gateway within the deadline.'
    }
    $journalDirectory = Join-Path $profileRoot 'diagnostics/startup'
    $stages = @(Get-ChildItem -LiteralPath $journalDirectory -Filter '*.jsonl' | ForEach-Object {
        Get-Content -LiteralPath $_.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Stage }
    })
    if ('BootLoaded' -notin $stages -or 'WindowActivated' -notin $stages -or 'StartupFailed' -in $stages) {
        throw 'Durable startup journal does not corroborate installed window and boot initialization.'
    }
    # A second AUMID activation must not leave an extra invisible client behind.
    $null = [SharpClawInstalledProbe]::Activate($result.Aumid)
    Start-Sleep -Seconds 2
    $clients = @(Get-TestPackageProcesses | Where-Object Name -eq 'SharpClaw.Client.Uno.exe')
    if ($clients.Count -ne 1 -or @([SharpClawInstalledProbe]::VisibleWindows([uint32]$clients[0].ProcessId)).Count -ne 1) {
        throw 'Repeated activation produced duplicate or windowless client processes.'
    }
    $result.Success = $true
} catch {
    $result['Failure'] = $_.Exception.Message
    $result['FailureType'] = $_.Exception.GetType().FullName
    $result['FailurePosition'] = $_.InvocationInfo.ScriptLineNumber
    throw
} finally {
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    try {
        $diagnostics = Join-Path $profileRoot 'diagnostics/startup'
        try {
            if (Test-Path -LiteralPath $diagnostics) { Copy-Item -LiteralPath $diagnostics -Destination $ReportDirectory -Recurse }
            Get-TestPackageProcesses | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'processes.json')
        } catch { $cleanupFailures.Add('Evidence capture failed: ' + $_.Exception.Message) }
        foreach ($process in Get-TestPackageProcesses) {
            try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop }
            catch { $cleanupFailures.Add('Test process cleanup failed: ' + $_.Exception.Message) }
        }
        # Also recover a successful registration if a later metadata query failed.
        $registered = @(Get-AppxPackage -Name 'com.mkn8rn.SharpClaw')
        foreach ($installed in $registered) {
            if ($installed.Version.ToString() -ne $published.InstallerVersion) {
                $cleanupFailures.Add('Unexpected package version during cleanup; not removing it.')
                continue
            }
            try { Remove-AppxPackage -Package $installed.PackageFullName }
            catch { $cleanupFailures.Add('Test uninstall failed: ' + $_.Exception.Message) }
        }
        $removalDeadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $registrationRemains = $null -ne (Get-AppxPackage -Name 'com.mkn8rn.SharpClaw')
            $startEntryRemains = $result.Aumid -and @(Get-StartApps | Where-Object AppID -eq $result.Aumid).Count -ne 0
            if (-not ($registrationRemains -or $startEntryRemains)) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $removalDeadline)
        if ($registrationRemains) { $cleanupFailures.Add('Package registration survived uninstall.') }
        if ($result.Aumid -and @(Get-StartApps | Where-Object AppID -eq $result.Aumid).Count -ne 0) {
            $cleanupFailures.Add('Registered Start entry survived uninstall.')
        }
        if (@(Get-TestPackageProcesses).Count -ne 0) { $cleanupFailures.Add('Installed processes survived cleanup.') }
        if ($null -ne $package) {
            $packageState = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages/$($package.PackageFamilyName)"
            if (Test-Path -LiteralPath $packageState) { $cleanupFailures.Add('Package-local state survived uninstall.') }
        }
        # Exact validated dedicated-user state only; never a broad profile or home.
        try { if (Test-Path -LiteralPath $profileRoot) { Remove-Item -LiteralPath $profileRoot -Recurse -Force } }
        catch { $cleanupFailures.Add('Test state cleanup failed: ' + $_.Exception.Message) }
        try {
            if ($trustOwnedByTest -and (Test-Path -LiteralPath $guestTrustPath)) {
                Remove-Item -LiteralPath $guestTrustPath -Force -Confirm:$false
            }
            if ($trustOwnedByTest -and (Test-Path -LiteralPath $guestTrustPath)) {
                $cleanupFailures.Add('Temporary guest signer trust survived cleanup.')
            }
        } catch { $cleanupFailures.Add('Temporary guest signer trust removal failed: ' + $_.Exception.Message) }
        $result.CleanupVerified = $cleanupFailures.Count -eq 0
        if (-not $result.CleanupVerified) {
            $result.Success = $false
            $result['CleanupFailures'] = @($cleanupFailures)
            throw 'Installed activation cleanup did not complete; see evidence.'
        }
    } finally {
        $certificate.Dispose()
        $result['EndUtc'] = [DateTime]::UtcNow.ToString('O')
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'installed-activation.json')
    }
}
