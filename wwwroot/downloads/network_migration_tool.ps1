<#
.SYNOPSIS
    Exports network settings before migration and imports them to a new adapter after migration.
    Useful for VMware to Proxmox migrations where the NIC changes from VMXNET3 to VirtIO.

.DESCRIPTION
    Saves IPv4 configuration to JSON. Uninstalls VMware Tools (v13+).
    Installs VirtIO drivers. Restores IP settings on the new adapter.
    Tests connectivity by pinging the Default Gateway.

.NOTES
    Version: 2.2
    Author: Patrik Westberg
#>

# Check for Administrator privileges
if (-NOT ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Warning "Please run this script as Administrator!"
    Break
}

# Configuration
$BackupFile = "C:\NetworkSettings_Migration.json"

function Get-Menu {
    Clear-Host
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "    VM Migration Network Helper Tool v2.2" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "1. EXPORT Current Network Settings (Only Export)" -ForegroundColor Green
    Write-Host "2. IMPORT Settings to New Adapter (Run on Proxmox)" -ForegroundColor Yellow
    Write-Host "3. PREP VM (Run on VMware - Standard Install)" -ForegroundColor Magenta
    Write-Host "4. POST-MIGRATION REPAIR (Run on Proxmox in SATA Mode)" -ForegroundColor Red
    Write-Host "5. View Saved Config"
    Write-Host "Q. Quit"
    Write-Host "==========================================" -ForegroundColor Cyan
}

function Export-Settings {
    Write-Host "`n[EXPORT MODE]" -ForegroundColor Green

    $netConfig = Get-NetIPConfiguration | Where-Object {
        $_.IPv4DefaultGateway -ne $null -and $_.NetAdapter.Status -eq "Up"
    } | Select-Object -First 1

    if (-not $netConfig) {
        Write-Error "No active network adapter with a gateway found!"
        return
    }

    Write-Host "Found Adapter: $($netConfig.InterfaceAlias) ($($netConfig.InterfaceDescription))"

    $settings = @{
        IPv4Address    = $netConfig.IPv4Address.IPAddress
        PrefixLength   = $netConfig.IPv4Address.PrefixLength
        DefaultGateway = $netConfig.IPv4DefaultGateway.NextHop
        DNSServers     = $netConfig.DNSServer.ServerAddresses
        OriginalName   = $netConfig.InterfaceAlias
        OriginalMAC    = $netConfig.NetAdapter.MacAddress
        OriginalDesc   = $netConfig.InterfaceDescription
    }

    $jsonPayload = $settings | ConvertTo-Json -Depth 3
    $jsonPayload | Out-File -FilePath $BackupFile -Encoding UTF8

    Write-Host "------------------------------------------"
    Write-Host "IP Address   : $($settings.IPv4Address)"
    Write-Host "Subnet Prefix: /$($settings.PrefixLength)"
    Write-Host "Gateway      : $($settings.DefaultGateway)"
    Write-Host "DNS          : $($settings.DNSServers -join ', ')"
    Write-Host "------------------------------------------"
    Write-Host "Settings saved successfully to: $BackupFile" -ForegroundColor Green
}

function Uninstall-VMwareTools {
    Write-Host "`n[VMWARE TOOLS CHECK]" -ForegroundColor Magenta

    $uninstallKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    $vmToolsEntry = $null
    foreach ($keyPath in $uninstallKeys) {
        if (Test-Path $keyPath) {
            $entries = Get-ChildItem $keyPath -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath }
            $found = $entries | Where-Object { $_.DisplayName -like "*VMware Tools*" }
            if ($found) { $vmToolsEntry = $found; break }
        }
    }

    if ($vmToolsEntry) {
        Write-Host "VMware Tools found (Version: $($vmToolsEntry.DisplayVersion))"
        $prompt = Read-Host "Uninstall now? (Y/N)"

        if ($prompt -eq 'Y') {
            Write-Host "Uninstalling... Please wait." -ForegroundColor Yellow
            $msiGuid = $vmToolsEntry.PSChildName
            $msiParams = "/x $msiGuid /qn /norestart"

            Try {
                $process = Start-Process "msiexec.exe" -ArgumentList $msiParams -Wait -PassThru
                if ($process.ExitCode -eq 0) {
                    Write-Host "Successfully uninstalled." -ForegroundColor Green
                } elseif ($process.ExitCode -eq 3010) {
                     Write-Host "Uninstalled. REBOOT REQUIRED." -ForegroundColor Yellow
                } else {
                    Write-Error "Failed. Exit code: $($process.ExitCode)"
                }
            } Catch { Write-Error "Error: $_" }
        }
    } else { Write-Host "VMware Tools not found." -ForegroundColor Gray }
}

function Install-VirtIODrivers {
    Write-Host "`n[VIRTIO DRIVER/AGENT INSTALL]" -ForegroundColor Magenta
    $rootPath = $null
    $drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Description -match 'CD-ROM' -or $_.Used -gt 0 }

    foreach ($drive in $drives) {
        if ((Test-Path "$($drive.Root)virtio-win-guest-tools.exe") -or (Test-Path "$($drive.Root)vioscsi")) {
            $rootPath = $drive.Root
            Write-Host "VirtIO Media found at: $rootPath" -ForegroundColor Green
            break
        }
    }

    if (-not $rootPath) { $rootPath = Read-Host "Enter VirtIO path (e.g. D:\)" }
    if ($rootPath -and -not $rootPath.EndsWith("\")) { $rootPath = "$rootPath\" }

    $exePath = Join-Path $rootPath "virtio-win-guest-tools.exe"
    if (Test-Path $exePath) {
        Write-Host "Running Standard Installer..." -ForegroundColor Yellow
        Start-Process -FilePath $exePath -ArgumentList "/install", "/passive", "/norestart" -Wait
        Write-Host "Install complete." -ForegroundColor Green
    } else { Write-Error "Installer not found at $exePath" }
}

function Post-Migration-Repair {
    Write-Host "`n[POST-MIGRATION REPAIR]" -ForegroundColor Red
    Install-VirtIODrivers
    Write-Host "`nAdd a 1GB VirtIO SCSI disk in Proxmox now to trigger driver loading." -ForegroundColor Cyan
    Pause
    $scsiCheck = Get-PnpDevice -Class SCSIAdapter | Where-Object { $_.FriendlyName -match "VirtIO" -or $_.FriendlyName -match "Red Hat" }
    if ($scsiCheck) { Write-Host "SUCCESS! VirtIO SCSI Controller detected." -ForegroundColor Green }
}

function Import-Settings {
    Write-Host "`n[IMPORT MODE]" -ForegroundColor Yellow

    if (-not (Test-Path $BackupFile)) {
        Write-Error "Config file not found."
        return
    }

    $config = Get-Content -Path $BackupFile | ConvertFrom-Json

    # Find new adapter
    $newAdapter = Get-NetAdapter | Where-Object {
        $_.Status -eq "Up" -and $_.Virtual -eq $false -and $_.MacAddress -ne $config.OriginalMAC
    } | Select-Object -First 1

    if (-not $newAdapter) {
        $newAdapter = Get-NetAdapter | Where-Object { $_.Status -eq "Up" } | Select-Object -First 1
    }

    if (-not $newAdapter) {
        Write-Error "No active network adapters found."
        return
    }

    Write-Host "Target: $($newAdapter.Name) ($($newAdapter.InterfaceDescription))"
    $confirm = Read-Host "Apply IP $($config.IPv4Address) to this adapter? (Y/N)"
    if ($confirm -ne 'Y') { return }

    Try {
        Set-NetIPInterface -InterfaceIndex $newAdapter.ifIndex -Dhcp Disabled -ErrorAction SilentlyContinue
        Remove-NetIPAddress -InterfaceIndex $newAdapter.ifIndex -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue

        New-NetIPAddress -InterfaceIndex $newAdapter.ifIndex `
                         -IPAddress $config.IPv4Address `
                         -PrefixLength $config.PrefixLength `
                         -DefaultGateway $config.DefaultGateway `
                         -ErrorAction Stop

        Set-DnsClientServerAddress -InterfaceIndex $newAdapter.ifIndex -ServerAddresses $config.DNSServers

        Write-Host "Configuration applied successfully!" -ForegroundColor Green

        # --- UPDATED CONNECTIVITY CHECK (PING GATEWAY) ---
        Write-Host "`nTesting connectivity to Default Gateway ($($config.DefaultGateway))..." -ForegroundColor Cyan
        Start-Sleep -Seconds 3 # Give the stack a moment to initialize

        if (Test-Connection -ComputerName $config.DefaultGateway -Count 3 -Quiet) {
            Write-Host "SUCCESS: Default Gateway is reachable." -ForegroundColor Green
        } else {
            Write-Warning "FAILED: Could not ping Default Gateway ($($config.DefaultGateway))."
            Write-Host "Check list:"
            Write-Host " 1. Is the Proxmox Bridge (vmbr) correct?"
            Write-Host " 2. Is the VLAN tag correct in Proxmox hardware settings?"
            Write-Host " 3. Does the gateway respond to ICMP/Ping?"
        }

        # Ghost Removal
        $ghostAdapter = Get-NetAdapter -IncludeHidden | Where-Object { $_.MacAddress -eq $config.OriginalMAC -and $_.Status -ne 'Up' }
        if ($ghostAdapter) {
            Write-Host "`n[GHOST DEVICE]" -ForegroundColor Yellow
            if ((Read-Host "Remove old adapter? (Y/N)") -eq 'Y') {
                $pnpDev = Get-PnpDevice -Class Net -Status Unknown | Where-Object { $_.FriendlyName -eq $ghostAdapter.InterfaceDescription } | Select-Object -First 1
                if ($pnpDev) { $pnpDev | Uninstall-PnpDevice -Confirm:$false; Write-Host "Ghost removed." }
            }
        }
    } Catch { Write-Error "Failed: $_" }
}

# Main Loop
do {
    Get-Menu
    $choice = Read-Host "Select an option"
    switch ($choice) {
        '1' { Export-Settings; Pause }
        '2' { Import-Settings; Pause }
        '3' { Uninstall-VMwareTools; Install-VirtIODrivers; Pause }
        '4' { Post-Migration-Repair; Pause }
        '5' { if (Test-Path $BackupFile) { Get-Content $BackupFile } else { Write-Host "No file found." }; Pause }
        'Q' { exit }
        'q' { exit }
    }
} while ($true)
