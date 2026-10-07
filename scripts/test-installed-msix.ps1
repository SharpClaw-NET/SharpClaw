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
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TestProviderKey,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TestModel,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TestProviderEndpoint,
    [ValidateRange(10, 600)][int]$StartupTimeoutSeconds = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this UI Automation gate with stock Windows PowerShell 5.1 (powershell.exe).' }
$testEndpoint = [Uri]$TestProviderEndpoint
if (-not $testEndpoint.IsAbsoluteUri -or $testEndpoint.Scheme -notin @('http', 'https') -or $testEndpoint.UserInfo) {
    throw 'Use an explicit test provider HTTP(S) endpoint without credentials.'
}
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
Add-Type -ReferencedAssemblies 'UIAutomationClient', 'UIAutomationTypes', 'WindowsBase', 'System', 'System.Core' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
public static class SharpClawInstalledProbe {
    static Task<bool> bootUiProbe;
    static long bootUiProbeHandle;
    static readonly Dictionary<string, Task<bool>> visibleProbes = new Dictionary<string, Task<bool>>();
    public static bool HasVisibleElement(long handle, string id) {
        var key = handle.ToString() + ":" + id;
        Task<bool> probe;
        if (!visibleProbes.TryGetValue(key, out probe)) {
            visibleProbes[key] = Task.Run(() => {
                var element = FindElement(handle, id);
                return element != null && !element.Current.IsOffscreen;
            });
            return false;
        }
        if (!probe.IsCompleted) return false;
        var visible = probe.GetAwaiter().GetResult();
        if (!visible) visibleProbes.Remove(key);
        return visible;
    }
    static AutomationElement FindElement(long handle, string id) {
        return AutomationElement.FromHandle(new IntPtr(handle)).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    }
    static void RunUiAction(Action action) {
        // A timed-out background writer could otherwise click after cleanup
        // starts. The executor bounds the whole gate; writes stay synchronous.
        action();
    }
    public static void SetValue(long handle, string id, string value) {
        RunUiAction(() => {
            var inputs = CreateTextInputs(value);
            var window = new IntPtr(handle);
            RequireForegroundWindow(window);
            var element = FindElement(handle, id);
            if (element == null) throw new InvalidOperationException("Missing UI input: " + id);
            // The shipped Uno peer exposes ValuePattern but rejects SetValue.
            // Click the real visible input, select its contents and type through
            // the foreground keyboard stream; never mutate a private UI/model.
            ClickVisibleElement(element);
            Thread.Sleep(75);
            SendKeyboardInputs(window, new[] {
                KeyboardInput(0x11, 0, 0), KeyboardInput(0x41, 0, 0),
                KeyboardInput(0x41, 0, 0x0002), KeyboardInput(0x11, 0, 0x0002)
            }); // Ctrl+A, with both keys released.
            if (inputs.Length > 0) SendKeyboardInputs(window, inputs);
            Thread.Sleep(75);
        });
    }
    static Input[] CreateTextInputs(string value) {
        if (value == null || value.Length == 0 || value.Length > 4096)
            throw new InvalidOperationException("Invalid bounded test UI text.");
        var inputs = new Input[value.Length * 2];
        for (var index = 0; index < value.Length; index++) {
            if (char.IsControl(value[index]))
                throw new InvalidOperationException("Test UI text cannot contain control keys.");
            inputs[index * 2] = KeyboardInput(0, value[index], 0x0004); // KEYEVENTF_UNICODE.
            inputs[index * 2 + 1] = KeyboardInput(0, value[index], 0x0004 | 0x0002);
        }
        return inputs;
    }
    static Input KeyboardInput(ushort key, ushort scan, uint flags) {
        return new Input { Type = 1, Data = new InputUnion {
            Keyboard = new KeyboardInputData { Key = key, Scan = scan, Flags = flags }
        }};
    }
    static void SendKeyboardInputs(IntPtr window, Input[] inputs) {
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Test window lost keyboard input focus.");
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != (uint)inputs.Length)
            throw new InvalidOperationException("Keyboard input was not completely accepted.");
    }
    static void RequireForegroundWindow(IntPtr window) {
        if (!SetForegroundWindow(window) || GetForegroundWindow() != window)
            throw new InvalidOperationException("Test window could not receive visible input.");
    }
    public static void SelectProvider(long handle, int providerIndex, int providerCount) {
        RunUiAction(() => {
            // Uno's Win32 UIA ExpandCollapse provider can expose the pattern
            // but reject Expand. Exercise the same visible pointer interaction
            // as a user, not a private setter or a runtime configuration API.
            var window = new IntPtr(handle);
            if (providerCount < 1 || providerCount > 256 || providerIndex < 0 || providerIndex >= providerCount)
                throw new InvalidOperationException("Invalid enabled-provider UI index.");
            if (!SetForegroundWindow(window) || GetForegroundWindow() != window)
                throw new InvalidOperationException("Provider setup window could not receive pointer input.");
            var combo = FindElement(handle, "ProviderSetupProvider");
            if (combo == null) throw new InvalidOperationException("Provider selection is missing.");
            ClickVisibleElement(combo);
            // Popup items may be virtualized or absent from the window UIA
            // subtree. Establish a selection using Down, saturate Up to the
            // first item, then move to the actual ordered setup-list index.
            // Do not assume Home was processed when selection started at -1.
            // Later Runtime assertions independently verify the actual key.
            PressProviderKey(window, 0x1B, false); // Escape: close popup, retain combo focus.
            foreach (var key in ProviderNavigationKeys(providerIndex, providerCount))
                PressProviderKey(window, key, true);
            PressProviderKey(window, 0x09, false); // Tab: leave provider selection.
        });
    }
    public static byte[] ProviderNavigationKeys(int providerIndex, int providerCount) {
        if (providerCount < 1 || providerCount > 256 || providerIndex < 0 || providerIndex >= providerCount)
            throw new InvalidOperationException("Invalid enabled-provider UI index.");
        var keys = new byte[1 + providerCount + providerIndex];
        keys[0] = 0x28; // Down establishes selection even from -1.
        for (var index = 1; index <= providerCount; index++) keys[index] = 0x26; // Up saturates at zero.
        for (var index = 1 + providerCount; index < keys.Length; index++) keys[index] = 0x28;
        return keys;
    }
    static void PressProviderKey(IntPtr window, byte key, bool extended) {
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Provider window lost input focus.");
        var scan = MapVirtualKey(key, 0);
        if (scan == 0 || scan > 0xFF) throw new InvalidOperationException("Navigation key has no supported scan code.");
        uint flags = 0x0008u | (extended ? 0x0001u : 0u); // KEYEVENTF_SCANCODE.
        SendKeyboardInputs(window, new[] {
            KeyboardInput(0, (ushort)scan, flags), KeyboardInput(0, (ushort)scan, flags | 0x0002u)
        });
        Thread.Sleep(125);
    }
    static void ClickVisibleElement(AutomationElement element) {
        if (element.Current.IsOffscreen || !element.Current.IsEnabled)
            throw new InvalidOperationException("Cannot click a hidden or disabled provider control.");
        System.Windows.Point point;
        if (!element.TryGetClickablePoint(out point) ||
            double.IsNaN(point.X) || double.IsNaN(point.Y) ||
            double.IsInfinity(point.X) || double.IsInfinity(point.Y) ||
            point.X < int.MinValue || point.X > int.MaxValue ||
            point.Y < int.MinValue || point.Y > int.MaxValue ||
            !SetCursorPos((int)point.X, (int)point.Y))
            throw new InvalidOperationException("Provider control has no usable visible click point.");
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // Left button down.
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // Left button up.
    }
    public static void Invoke(long handle, string id) {
        RunUiAction(() => {
            RequireForegroundWindow(new IntPtr(handle));
            var element = FindElement(handle, id);
            if (element == null) throw new InvalidOperationException("Missing UI command: " + id);
            ClickVisibleElement(element);
        });
    }
    public static string CompletedResponse(long handle) {
        var task = Task.Run(() => {
            var response = FindElement(handle, "ChatAssistantResponse");
            if (response == null || response.Current.ItemStatus != "complete") return null;
            return response.Current.Name;
        });
        if (!task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Response observation did not finish.");
        return task.GetAwaiter().GetResult();
    }
    // Read fresh native store snapshots. Certificate-provider path checks can
    // retain a negative result across Import-Certificate in Windows PowerShell.
    static bool SameCertificate(X509Certificate2 left, X509Certificate2 right) {
        return Convert.ToBase64String(left.RawData) == Convert.ToBase64String(right.RawData);
    }
    public static bool HasGuestTrust(X509Certificate2 certificate) {
        using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine)) {
            store.Open(OpenFlags.ReadOnly);
            foreach (var candidate in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false))
                if (SameCertificate(candidate, certificate)) return true;
            return false;
        }
    }
    public static void AddGuestTrust(X509Certificate2 certificate) {
        using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine)) {
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
        }
    }
    public static void RemoveGuestTrust(X509Certificate2 certificate) {
        using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine)) {
            store.Open(OpenFlags.ReadWrite);
            foreach (var candidate in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false))
                if (SameCertificate(candidate, certificate)) store.Remove(candidate);
        }
    }
    public delegate bool EnumCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint key, uint mapType);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    // INPUT includes the largest union member (MOUSEINPUT), even for keyboard
    // events. Sequential pointer alignment yields 40 bytes on x64 / 28 on x86.
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public MouseInputData Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInputData {
        public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] struct MouseInputData {
        public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo;
    }
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
$virtualProfileRoot = $null
$trustOwnedByTest = $false
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($CertificatePath))
if ($certificate.Subject -ne 'CN=SharpClaw Dev' -or $certificate.NotAfter -le (Get-Date)) {
    $certificate.Dispose()
    throw 'Unexpected or expired test signer.'
}
# Only the disposable guest's Cert:\LocalMachine\TrustedPeople store.
if ([SharpClawInstalledProbe]::HasGuestTrust($certificate)) {
    $certificate.Dispose()
    throw 'The guest already trusts this signer; use a clean VM so the gate can own and remove temporary trust.'
}
$result = [ordered]@{
    SourceCommit = $ExpectedSourceCommit; PackageSha256 = $ExpectedPackageSha256
    TestUserSid = $ExpectedTestUserSid; StartUtc = [DateTime]::UtcNow.ToString('O')
    Aumid = $null; ActivatedProcessId = 0; Window = $null; BootUiObserved = $false
    RuntimeObserved = $false; GatewayObserved = $false; ProcessSnapshotTimeouts = 0
    GatewayTemplatesObserved = $false
    CleanFirstLaunchVerified = $false; ProtectedTemplateSources = @(); SeededTemplates = @()
    CleanRuntimeReady = $false; CleanSetupObserved = $false; ConfiguredByProductUi = $false
    RealRequestCompleted = $false; TestProviderKey = $TestProviderKey; TestModel = $TestModel
    CleanupVerified = $false; Success = $false
}
function Get-TestRuntimeConnection {
    $backendRoot = Join-Path $frontend 'stack/backend'
    $keyFile = Join-Path $backendRoot 'runtime/.api-key'
    if (-not (Test-Path -LiteralPath $keyFile)) { return $null }
    foreach ($root in @($profileRoot, $virtualProfileRoot)) {
        $directory = Join-Path $root 'discovery/instances'
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $directory -Filter 'backend-*.json') {
            $entry = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
            $runtime = @(Get-TestPackageProcesses -AllowTransientTimeout | Where-Object {
                $_.Name -eq 'SharpClaw.Runtime.Host.exe' -and $_.ProcessId -eq $entry.processId
            })
            $base = [Uri]$entry.baseUrl
            if ($runtime.Count -eq 1 -and $base.IsAbsoluteUri -and $base.IsLoopback -and
                $base.Scheme -in @('http', 'https')) {
                # Credentials are used in memory only, never in the exported report.
                return [pscustomobject]@{ BaseUrl = $base.AbsoluteUri.TrimEnd('/'); ProcessId = $entry.processId
                    Headers = @{ 'X-Api-Key' = [IO.File]::ReadAllText($keyFile).Trim() } }
            }
        }
    }
    return $null
}
function Get-TestRuntimeSetup {
    param($Connection)
    if ($null -eq $Connection) { return $null }
    try {
        foreach ($path in @('/echo', '/readyz', '/ping')) {
            $probe = Invoke-WebRequest -UseBasicParsing -Uri ($Connection.BaseUrl + $path) -Headers $Connection.Headers -TimeoutSec 5
            if ($probe.StatusCode -ne 200) { return $null }
        }
        return Invoke-RestMethod -Uri ($Connection.BaseUrl + '/setup/provider') -Headers $Connection.Headers -TimeoutSec 5
    } catch { return $null }
}
function Get-TestProviderUiIndex {
    param([object[]]$Options, [string]$ProviderKey)
    if ($Options.Count -lt 1 -or $Options.Count -gt 256 -or [string]::IsNullOrWhiteSpace($ProviderKey)) {
        throw 'Invalid enabled-provider UI list.'
    }
    $selected = -1
    for ($index = 0; $index -lt $Options.Count; $index++) {
        if ([string]::Equals($Options[$index].key, $ProviderKey, [StringComparison]::OrdinalIgnoreCase)) {
            if ($selected -ge 0) { throw 'Ambiguous enabled-provider UI key.' }
            $selected = $index
        }
    }
    if ($selected -lt 0) { throw 'Enabled-provider UI key is missing.' }
    return $selected
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
function Wait-TestPackageProcessesStopped {
    param([ValidateRange(0, 20)][int]$TimeoutSeconds = 20)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (@(Get-TestPackageProcesses).Count -eq 0) { return }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Milliseconds 200
    } while ($true)
    throw 'Test package processes survived the bounded exit wait.'
}
function Copy-SharedJournalContents {
    param([string]$Source, [string]$Destination)
    # The startup writer allows ReadWrite sharing. A ReadAllBytes reader uses
    # only Read sharing and therefore conflicts while that writer is open.
    $journalStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $contents = [IO.MemoryStream]::new()
    try {
        $journalStream.CopyTo($contents)
        [IO.File]::WriteAllBytes($Destination, $contents.ToArray())
    } finally { $contents.Dispose(); $journalStream.Dispose() }
}
function Save-WindowCapture {
    param([long]$Handle, [string]$FileName = 'boot-window.png')
    $rectangle = [SharpClawInstalledProbe+Rect]::new()
    if (-not [SharpClawInstalledProbe]::GetWindowRect([IntPtr]$Handle, [ref]$rectangle)) { throw 'Cannot read window geometry.' }
    $bitmap = [Drawing.Bitmap]::new($rectangle.Right - $rectangle.Left, $rectangle.Bottom - $rectangle.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try {
        if (-not [SharpClawInstalledProbe]::PrintWindow([IntPtr]$Handle, $dc, 2)) { throw 'Window capture failed.' }
    } finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $ReportDirectory $FileName), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}
function Assert-SeededTemplate {
    param([string]$SourceRelativePath, [string]$DestinationDirectory)
    $source = Join-Path $package.InstallLocation $SourceRelativePath
    $destination = Join-Path $DestinationDirectory ([IO.Path]::GetFileName($source))
    if (-not (Test-Path -LiteralPath $destination) -or
        (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash -or
        ([IO.File]::GetAttributes($destination) -band [IO.FileAttributes]::ReadOnly)) {
        throw "Product did not seed writable, byte-identical template contents: $SourceRelativePath"
    }
    $applicationProtected = $false
    if ([IO.File]::GetAttributes($destination) -band [IO.FileAttributes]::Encrypted) {
        # Windows may independently protect files newly created inside this
        # package's LocalCache. That is not inherited source-file metadata.
        if (-not $virtualProfileRoot -or -not $destination.StartsWith(
            $virtualProfileRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'An encrypted template outside the exact package LocalCache is unsupported.'
        }
        $cipher = @(& cipher.exe /c $destination 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($cipher -join "`n") -notmatch 'Application Protected') {
            throw 'Only independently applied package LocalCache protection is supported.'
        }
        $applicationProtected = $true
    }
    $writable = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writable.Dispose()
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        throw 'Write-access verification changed the seeded template.'
    }
    [pscustomobject]@{
        Source = $SourceRelativePath; Destination = $destination
        Sha256 = (Get-FileHash -LiteralPath $destination).Hash
        Length = (Get-Item -LiteralPath $destination).Length
        Attributes = [IO.File]::GetAttributes($destination).ToString()
        Writable = $true; ApplicationProtectedLocalCache = $applicationProtected
    }
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
    # Own cleanup even when a native store write fails after partially writing.
    $trustOwnedByTest = $true
    [SharpClawInstalledProbe]::AddGuestTrust($certificate)
    if (-not [SharpClawInstalledProbe]::HasGuestTrust($certificate)) {
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
    $virtualProfileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages/$($package.PackageFamilyName)/LocalCache/Local/SharpClaw"
    if (Test-Path -LiteralPath $virtualProfileRoot) { throw 'The test user already has package-virtualized SharpClaw state.' }
    $frontend = Join-Path $profileRoot 'installed/com.mkn8rn.SharpClaw/frontend'
    $frontendCandidates = @($frontend, (Join-Path $virtualProfileRoot 'installed/com.mkn8rn.SharpClaw/frontend'))
    $templateScopes = @(
        @('Environment', (Join-Path $frontend 'config')),
        @('backend/Environment', (Join-Path $frontend 'stack/backend/config')),
        @('gateway/Environment', (Join-Path $frontend 'stack/gateway/config'))
    )
    # A plain extracted archive cannot reproduce AppX protection. Require the
    # genuinely deployed inputs and retain cipher's Application Protected proof.
    foreach ($scope in $templateScopes) {
        foreach ($name in @('.env.template', '.dev.env.template')) {
            $relative = $scope[0] + '/' + $name
            $source = Join-Path $package.InstallLocation $relative
            $attributes = [IO.File]::GetAttributes($source)
            $cipher = @(& cipher.exe /c $source 2>&1)
            if ($LASTEXITCODE -ne 0 -or -not ($attributes -band [IO.FileAttributes]::Encrypted) -or
                ($cipher -join "`n") -notmatch 'Application Protected') {
                throw "Require deployed Application Protected template input: $relative"
            }
            $cipher | Set-Content -LiteralPath (Join-Path $ReportDirectory ('cipher-' + $relative.Replace('/', '_') + '.txt'))
            $result.ProtectedTemplateSources += [pscustomobject]@{
                Path = $relative; Sha256 = (Get-FileHash -LiteralPath $source).Hash
                Attributes = $attributes.ToString(); ApplicationProtected = $true
            }
        }
    }
    # Negative control: the former metadata-copy operation must fail on this
    # real deployment, rather than silently testing an unprotected fixture.
    $copyProbe = Join-Path $ReportDirectory 'metadata-copy-negative.template'
    try {
        [IO.File]::Copy((Join-Path $package.InstallLocation 'Environment/.env.template'), $copyProbe, $false)
        throw 'The old File.Copy operation unexpectedly succeeded; regression fixture is not representative.'
    } catch [IO.IOException] {
        if ($_.Exception.HResult -ne -2147018896) { throw }
        $result['MetadataCopyFailureHResult'] = '0x80071770'
    } finally {
        if (Test-Path -LiteralPath $copyProbe) { Remove-Item -LiteralPath $copyProbe -Force }
    }
    # First activation must happen BEFORE any test configuration/template is
    # created. Pre-seeding templates hid the owner's first-launch failure.
    $result['CleanFirstLaunchProcessId'] = [SharpClawInstalledProbe]::Activate($result.Aumid)
    $cleanDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        $cleanClients = @(Get-TestPackageProcesses -AllowTransientTimeout | Where-Object Name -eq 'SharpClaw.Client.Uno.exe')
        if ($cleanClients.Count -gt 1) { throw 'Duplicate clients during clean first launch.' }
        if ($cleanClients.Count -eq 1) {
            $cleanWindows = @([SharpClawInstalledProbe]::VisibleWindows([uint32]$cleanClients[0].ProcessId))
            $seededRoots = @($frontendCandidates | Where-Object {
                (Test-Path -LiteralPath (Join-Path $_ 'config/.env.template')) -and
                (Test-Path -LiteralPath (Join-Path $_ 'stack/backend/config/.env.template'))
            })
            if ($seededRoots.Count -gt 1) { throw 'Ambiguous physical/package-virtualized test configuration roots.' }
            if ($seededRoots.Count -eq 1) {
                $frontend = $seededRoots[0]
                $templateScopes = @(
                    @('Environment', (Join-Path $frontend 'config')),
                    @('backend/Environment', (Join-Path $frontend 'stack/backend/config')),
                    @('gateway/Environment', (Join-Path $frontend 'stack/gateway/config'))
                )
            }
            $seeded = @($templateScopes[0..1] | ForEach-Object {
                Test-Path -LiteralPath (Join-Path $_[1] '.env.template')
                Test-Path -LiteralPath (Join-Path $_[1] '.dev.env.template')
            })
            if ($cleanWindows.Count -eq 1 -and $false -notin $seeded) {
                if (-not $result.BootUiObserved -and [SharpClawInstalledProbe]::HasVisibleBootUi($cleanWindows[0].Handle)) {
                    $result.BootUiObserved = $true
                    Save-WindowCapture $cleanWindows[0].Handle 'boot-window.png'
                }
                $cleanConnection = Get-TestRuntimeConnection
                $cleanSetup = Get-TestRuntimeSetup $cleanConnection
                if ($null -eq $cleanSetup -or -not $cleanSetup.setupRequired -or
                    -not [SharpClawInstalledProbe]::HasVisibleElement($cleanWindows[0].Handle, 'ProviderSetupApply')) {
                    Start-Sleep -Milliseconds 200
                    continue
                }
                $result.CleanRuntimeReady = $true
                $result.CleanSetupObserved = $true
                $result.CleanFirstLaunchVerified = $true
                $result['ActualFrontendRoot'] = $frontend
                Save-WindowCapture $cleanWindows[0].Handle 'clean-first-launch.png'
                break
            }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $cleanDeadline)
    if (-not $result.CleanFirstLaunchVerified) { throw 'Clean first launch did not reach an authenticated ready Runtime and visible provider setup UI.' }
    foreach ($scope in $templateScopes[0..1]) {
        foreach ($name in @('.env.template', '.dev.env.template')) {
            $result.SeededTemplates += Assert-SeededTemplate ($scope[0] + '/' + $name) $scope[1]
        }
    }
    # Exercise the product's protected configuration writer and full Runtime
    # replacement through the visible UI. No provider template is pre-seeded.
    $selection = @($cleanSetup.providers | Where-Object key -eq $TestProviderKey)
    if ($selection.Count -ne 1 -or $selection[0].requiresApiKey) {
        throw 'Use an explicitly prepared enabled keyless provider for this gate; it never harvests owner credentials.'
    }
    $originalRuntimeProcessId = $cleanConnection.ProcessId
    $providerOptions = @($cleanSetup.providers)
    $providerIndex = Get-TestProviderUiIndex $providerOptions $TestProviderKey
    $result['ProviderUiSelectionIndex'] = $providerIndex
    $result['ProviderUiSelectionDisplayName'] = $selection[0].displayName
    [SharpClawInstalledProbe]::SelectProvider($cleanWindows[0].Handle, $providerIndex, $providerOptions.Count)
    [SharpClawInstalledProbe]::SetValue($cleanWindows[0].Handle, 'ProviderSetupModel', $TestModel)
    [SharpClawInstalledProbe]::SetValue($cleanWindows[0].Handle, 'ProviderSetupEndpoint', $TestProviderEndpoint)
    [SharpClawInstalledProbe]::Invoke($cleanWindows[0].Handle, 'ProviderSetupApply')
    $configuredDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        $configuredConnection = Get-TestRuntimeConnection
        $configuredSetup = Get-TestRuntimeSetup $configuredConnection
        if ($null -ne $configuredSetup -and -not $configuredSetup.setupRequired -and
            $configuredSetup.providerKey -eq $TestProviderKey -and $configuredSetup.model -eq $TestModel -and
            $configuredConnection.ProcessId -ne $originalRuntimeProcessId -and
            [SharpClawInstalledProbe]::HasVisibleElement($cleanWindows[0].Handle, 'ChatMessageInput')) {
            $result.ConfiguredByProductUi = $true
            Save-WindowCapture $cleanWindows[0].Handle 'configured-main.png'
            break
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $configuredDeadline)
    if (-not $result.ConfiguredByProductUi) { throw 'Provider setup UI did not restart Runtime and expose configured chat.' }
    # ONE model request via the actual client streaming path. A partial response,
    # HTTP success alone or enabled Send button is not terminal completion.
    [SharpClawInstalledProbe]::SetValue($cleanWindows[0].Handle, 'ChatMessageInput', 'Reply with a short greeting.')
    [SharpClawInstalledProbe]::Invoke($cleanWindows[0].Handle, 'ChatSend')
    $requestDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        $reply = [SharpClawInstalledProbe]::CompletedResponse($cleanWindows[0].Handle)
        if (-not [string]::IsNullOrWhiteSpace($reply)) {
            $result.RealRequestCompleted = $true
            $result['CompletedResponse'] = $reply
            Save-WindowCapture $cleanWindows[0].Handle 'completed-request.png'
            break
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $requestDeadline)
    if (-not $result.RealRequestCompleted) { throw 'The actual client request did not receive a nonempty terminal-completed response.' }
    $null = (Get-Process -Id $cleanClients[0].ProcessId).CloseMainWindow()
    $stopDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (@(Get-TestPackageProcesses).Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $stopDeadline)
    foreach ($process in Get-TestPackageProcesses) {
        try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop }
        catch { if (Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) { throw } }
    }
    # Stop-Process requests termination; it does not prove all process handles
    # and the next CIM snapshot have observed that exit yet.
    Wait-TestPackageProcessesStopped
    # Only AFTER proving clean setup and a completed request, enable Gateway.
    # The protected provider document written by the product must remain intact.
    # Leave Gateway templates absent so its first startup also exercises seeding.
    $configs = @(
        @('Environment/.env.template', (Join-Path $frontend 'config'), 'Gateway__Enabled="true"')
    )
    foreach ($config in $configs) {
        New-Item -ItemType Directory -Path $config[1] -Force | Out-Null
        $text = [IO.File]::ReadAllText((Join-Path $package.InstallLocation $config[0]))
        if ($config[2]) {
            $key = ($config[2] -split '=')[0]
            $text = [regex]::Replace($text, "(?m)^$key=.*$", '') + "`n" + $config[2] + "`n"
        }
        [IO.File]::WriteAllText((Join-Path $config[1] '.env.template'), $text, [Text.UTF8Encoding]::new($false))
        $active = Join-Path $config[1] '.env'
        if (Test-Path -LiteralPath $active) { Remove-Item -LiteralPath $active -Force }
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
        $result.GatewayTemplatesObserved =
            (Test-Path -LiteralPath (Join-Path $templateScopes[2][1] '.env.template')) -and
            (Test-Path -LiteralPath (Join-Path $templateScopes[2][1] '.dev.env.template'))
        if ($result.BootUiObserved -and $result.RuntimeObserved -and $result.GatewayObserved -and
            $result.GatewayTemplatesObserved) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not ($result.BootUiObserved -and $result.RuntimeObserved -and $result.GatewayObserved -and
        $result.GatewayTemplatesObserved)) {
        throw 'Installed activation did not expose boot UI, start Runtime/Gateway and seed Gateway templates within the deadline.'
    }
    foreach ($name in @('.env.template', '.dev.env.template')) {
        $result.SeededTemplates += Assert-SeededTemplate ($templateScopes[2][0] + '/' + $name) $templateScopes[2][1]
    }
    $stages = @(@($profileRoot, $virtualProfileRoot) | ForEach-Object {
        $journalDirectory = Join-Path $_ 'diagnostics/startup'
        if (Test-Path -LiteralPath $journalDirectory) {
            Get-ChildItem -LiteralPath $journalDirectory -Filter '*.jsonl' | ForEach-Object {
                Get-Content -LiteralPath $_.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Stage }
            }
        }
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
        try {
            foreach ($stateRoot in @($profileRoot, $virtualProfileRoot) | Where-Object { $_ }) {
                $diagnostics = Join-Path $stateRoot 'diagnostics/startup'
                if (Test-Path -LiteralPath $diagnostics) {
                    $label = if ($stateRoot -eq $profileRoot) { 'physical-startup' } else { 'virtualized-startup' }
                    $journalEvidence = Join-Path $ReportDirectory $label
                    New-Item -ItemType Directory -Path $journalEvidence | Out-Null
                    foreach ($journal in Get-ChildItem -LiteralPath $diagnostics -Filter '*.jsonl') {
                        # Evidence crosses the same AppX protection boundary as
                        # templates; retain contents, never File.Copy metadata.
                        Copy-SharedJournalContents $journal.FullName (Join-Path $journalEvidence $journal.Name)
                    }
                }
            }
            Get-TestPackageProcesses | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'processes.json')
        } catch { $cleanupFailures.Add('Evidence capture failed: ' + $_.Exception.Message) }
        foreach ($process in Get-TestPackageProcesses) {
            try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop }
            catch { $cleanupFailures.Add('Test process cleanup failed: ' + $_.Exception.Message) }
        }
        try { Wait-TestPackageProcessesStopped }
        catch { $cleanupFailures.Add('Test process exit verification failed: ' + $_.Exception.Message) }
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
            if ($trustOwnedByTest) {
                [SharpClawInstalledProbe]::RemoveGuestTrust($certificate)
            }
            if ($trustOwnedByTest -and [SharpClawInstalledProbe]::HasGuestTrust($certificate)) {
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
