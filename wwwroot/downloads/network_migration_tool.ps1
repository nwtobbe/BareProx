<#
.SYNOPSIS
    Exports network settings before migration and imports them to a new adapter after migration.
    Useful for VMware to Proxmox migrations where the NIC changes from VMXNET3 to VirtIO.

.DESCRIPTION
    This script saves the IPv4 configuration of the currently active network adapter to a JSON file.
    It can then read that file and apply the settings to a new adapter detected on the system.
    It includes logic to remove "Ghost" adapters, uninstall VMware Tools, and install VirtIO drivers/agent.

.NOTES
    File Name: network_migration_tool.ps1
    Author: Gemini
    Version: 2.0
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
    Write-Host "   VM Migration Network Helper Tool v2.0" -ForegroundColor Cyan
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
    
    # Get active adapter (connected and has a gateway)
    $netConfig = Get-NetIPConfiguration | Where-Object { 
        $_.IPv4DefaultGateway -ne $null -and $_.NetAdapter.Status -eq "Up" 
    } | Select-Object -First 1

    if (-not $netConfig) {
        Write-Error "No active network adapter with a gateway found!"
        return
    }

    Write-Host "Found Adapter: $($netConfig.InterfaceAlias) ($($netConfig.InterfaceDescription))"
    
    $settings = @{
        IPv4Address = $netConfig.IPv4Address.IPAddress
        PrefixLength = $netConfig.IPv4Address.PrefixLength
        DefaultGateway = $netConfig.IPv4DefaultGateway.NextHop
        DNSServers = $netConfig.DNSServer.ServerAddresses
        OriginalName = $netConfig.InterfaceAlias
        OriginalMAC = $netConfig.NetAdapter.MacAddress
        OriginalDesc = $netConfig.InterfaceDescription
    }

    $jsonPayload = $settings | ConvertTo-Json -Depth 3
    $jsonPayload | Out-File -FilePath $BackupFile -Encoding UTF8

    Write-Host "------------------------------------------"
    Write-Host "IP Address  : $($settings.IPv4Address)"
    Write-Host "Subnet Prefix: /$($settings.PrefixLength)"
    Write-Host "Gateway     : $($settings.DefaultGateway)"
    Write-Host "DNS         : $($settings.DNSServers -join ', ')"
    Write-Host "------------------------------------------"
    Write-Host "Settings saved successfully to: $BackupFile" -ForegroundColor Green
}

function Uninstall-VMwareTools {
    Write-Host "`n[VMWARE TOOLS CHECK]" -ForegroundColor Magenta
    
    # Search Registry for VMware Tools (More reliable/faster than Win32_Product)
    $uninstallKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    $vmToolsEntry = $null
    
    foreach ($keyPath in $uninstallKeys) {
        if (Test-Path $keyPath) {
            $entries = Get-ChildItem $keyPath -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath }
            $found = $entries | Where-Object { $_.DisplayName -eq "VMware Tools" }
            if ($found) { $vmToolsEntry = $found; break }
        }
    }

    if ($vmToolsEntry) {
        Write-Host "VMware Tools found installed (Version: $($vmToolsEntry.DisplayVersion))"
        $prompt = Read-Host "Do you want to uninstall VMware Tools now? (Recommended before migration) (Y/N)"
        
        if ($prompt -eq 'Y') {
            Write-Host "Uninstalling VMware Tools... This may take a few minutes." -ForegroundColor Yellow
            
            # The PSChildName is usually the MSI GUID (e.g., {C2A6F2E4...})
            $guid = $vmToolsEntry.PSChildName
            
            # Construct MSI Exec command: /x = uninstall, /qn = quiet no UI, /norestart = do not reboot automatically
            $args = "/x $guid /qn /norestart"
            
            Try {
                $process = Start-Process "msiexec.exe" -ArgumentList $args -Wait -PassThru
                
                if ($process.ExitCode -eq 0) {
                    Write-Host "VMware Tools uninstalled successfully." -ForegroundColor Green
                } elseif ($process.ExitCode -eq 3010) {
                     Write-Host "VMware Tools uninstalled. A REBOOT IS REQUIRED." -ForegroundColor Yellow
                } else {
                    Write-Error "Uninstallation failed with exit code: $($process.ExitCode)"
                }
            } Catch {
                Write-Error "Failed to start uninstaller: $_"
            }
        } else {
            Write-Host "Skipping uninstallation." -ForegroundColor Gray
        }
    } else {
        Write-Host "VMware Tools not found in registry." -ForegroundColor Gray
    }
}

function Install-VirtIODrivers {
    Write-Host "`n[VIRTIO DRIVER/AGENT INSTALL]" -ForegroundColor Magenta
    Write-Host "Standard Install (virtio-win-guest-tools.exe) - Installs Agent and Drivers."
    
    # Locate ISO/Installer Path
    $rootPath = $null
    $drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Description -match 'CD-ROM' -or $_.Used -gt 0 }
    
    # Look for common virtio files to confirm root path
    foreach ($drive in $drives) {
        if ((Test-Path "$($drive.Root)virtio-win-guest-tools.exe") -or (Test-Path "$($drive.Root)vioscsi")) {
            $rootPath = $drive.Root
            Write-Host "VirtIO Media found at: $rootPath" -ForegroundColor Green
            break
        }
    }

    if (-not $rootPath) {
        Write-Warning "VirtIO media not found automatically."
        $rootPath = Read-Host "Enter path to VirtIO drive/folder (e.g. D:\)"
    }

    # Sanitize root path (ensure trailing backslash)
    if ($rootPath -and -not $rootPath.EndsWith("\")) { $rootPath = "$rootPath\" }
    if (-not $rootPath -or -not (Test-Path $rootPath)) { Write-Error "Invalid path."; return }
    
    # STANDARD INSTALL ONLY
    $exePath = Join-Path $rootPath "virtio-win-guest-tools.exe"
    if (Test-Path $exePath) {
        Write-Host "Running Standard Installer..." -ForegroundColor Yellow
        Start-Process -FilePath $exePath -ArgumentList "/install", "/passive", "/norestart" -Wait
        Write-Host "Standard install complete." -ForegroundColor Green
    } else {
        Write-Error "Installer executable not found at $exePath"
    }
}

function Post-Migration-Repair {
    Write-Host "`n[POST-MIGRATION REPAIR]" -ForegroundColor Red
    Write-Host "This mode is designed to run ON PROXMOX while booted with the disk as SATA."
    Write-Host "It will help you enable the SCSI Controller so you can switch the Boot Disk to SCSI."
    
    # 1. Run Installer
    Write-Host "`nStep 1: Installing VirtIO Drivers..."
    Install-VirtIODrivers
    
    Write-Host "`nStep 2: The 'Dummy Disk' Check" -ForegroundColor Cyan
    Write-Host "If Windows is booted via SATA, it ignores the SCSI driver because it sees no SCSI devices."
    Write-Host "To fix this:"
    Write-Host "  1. Keep the VM running."
    Write-Host "  2. Go to Proxmox Web UI > Hardware > Add > Hard Disk."
    Write-Host "  3. Set Bus/Device = SCSI (VirtIO SCSI). Size = 1GB. Add it."
    Write-Host "  4. Watch this window. Windows should detect new hardware."
    
    Pause
    
    Write-Host "Scanning for SCSI Controllers..."
    $scsiCheck = Get-PnpDevice -Class SCSIAdapter | Where-Object { $_.FriendlyName -match "VirtIO" -or $_.FriendlyName -match "Red Hat" }
    
    if ($scsiCheck) {
        Write-Host "SUCCESS! VirtIO SCSI Controller detected:" -ForegroundColor Green
        $scsiCheck | Select-Object FriendlyName, Status, Class
        Write-Host "`nNEXT STEPS:" -ForegroundColor Cyan
        Write-Host "1. Shutdown the VM."
        Write-Host "2. Proxmox: Detach the SATA disk -> Double Click it -> Add as SCSI."
        Write-Host "3. CRITICAL: Go to VM 'Options' -> 'Boot Order'." -ForegroundColor Yellow
        Write-Host "   Ensure the new 'scsi0' disk is ENABLED and drag it to the TOP." -ForegroundColor Yellow
        Write-Host "4. Boot the VM."
    } else {
        Write-Warning "VirtIO SCSI Controller NOT detected yet."
        Write-Host "Make sure you added the dummy SCSI disk in Proxmox while the VM is running."
        Write-Host "Check Device Manager for 'Unknown Devices' and update driver pointing to the CD-ROM."
    }
}

function Import-Settings {
    Write-Host "`n[IMPORT MODE]" -ForegroundColor Yellow

    if (-not (Test-Path $BackupFile)) {
        Write-Error "Config file not found at $BackupFile. Did you run Export first?"
        return
    }

    $config = Get-Content -Path $BackupFile | ConvertFrom-Json

    # Find the new active adapter (Likely Red Hat VirtIO or Intel)
    $newAdapter = Get-NetAdapter | Where-Object { 
        $_.Status -eq "Up" -and 
        $_.Virtual -eq $false -and 
        $_.MacAddress -ne $config.OriginalMAC 
    } | Select-Object -First 1

    if (-not $newAdapter) {
        Write-Warning "Could not auto-detect a distinct new 'Up' adapter."
        Write-Host "Attempting to find ANY active adapter..."
        $newAdapter = Get-NetAdapter | Where-Object { $_.Status -eq "Up" } | Select-Object -First 1
    }

    if (-not $newAdapter) {
        Write-Error "No active network adapters found. Please check Proxmox Hardware settings."
        return
    }

    Write-Host "Target Adapter identified: $($newAdapter.Name) ($($newAdapter.InterfaceDescription))"
    
    $confirm = Read-Host "Are you sure you want to apply IP $($config.IPv4Address) to this adapter? (Y/N)"
    if ($confirm -ne 'Y') { return }

    Try {
        # Disable DHCP
        Write-Host "Disabling DHCP and clearing old IPs..." -ForegroundColor Gray
        Set-NetIPInterface -InterfaceIndex $newAdapter.ifIndex -Dhcp Disabled -ErrorAction SilentlyContinue
        Remove-NetIPAddress -InterfaceIndex $newAdapter.ifIndex -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
        
        # Set IP and Gateway
        Write-Host "Applying IP Address and Gateway..." -ForegroundColor Gray
        New-NetIPAddress -InterfaceIndex $newAdapter.ifIndex `
                         -IPAddress $config.IPv4Address `
                         -PrefixLength $config.PrefixLength `
                         -DefaultGateway $config.DefaultGateway `
                         -ErrorAction Stop

        # Set DNS
        Write-Host "Applying DNS Servers..." -ForegroundColor Gray
        Set-DnsClientServerAddress -InterfaceIndex $newAdapter.ifIndex -ServerAddresses $config.DNSServers

        Write-Host "Configuration applied successfully!" -ForegroundColor Green
        
        # Test Connectivity
        Start-Sleep -Seconds 2
        $test = Test-Connection -ComputerName 8.8.8.8 -Count 2 -Quiet
        if ($test) {
            Write-Host "Internet Connectivity Verified." -ForegroundColor Green
        } else {
            Write-Warning "Settings applied, but ping failed. Check your Proxmox bridge/VLAN settings."
        }

        # --- GHOST DEVICE REMOVAL ---
        Write-Host "`nChecking for Ghost Devices (Old Adapter)..." -ForegroundColor Gray
        $ghostAdapter = Get-NetAdapter -IncludeHidden | Where-Object { $_.MacAddress -eq $config.OriginalMAC -and $_.Status -ne 'Up' }
        
        if ($ghostAdapter) {
            Write-Host "`n[GHOST DEVICE DETECTED]" -ForegroundColor Yellow
            Write-Host "Old Adapter Found: $($ghostAdapter.InterfaceDescription)"
            
            $removeGhost = Read-Host "Do you want to remove this ghost device to prevent registry conflicts? (Y/N)"
            
            if ($removeGhost -eq 'Y') {
                $pnpDev = Get-PnpDevice -Class Net -Status Unknown | Where-Object { $_.FriendlyName -eq $ghostAdapter.InterfaceDescription } | Select-Object -First 1
                if ($pnpDev) {
                    Write-Host "Removing PnP Device..." -ForegroundColor Gray
                    $pnpDev | Uninstall-PnpDevice -Confirm:$false
                    Write-Host "Ghost device removed successfully." -ForegroundColor Green
                }
            }
        }

    } Catch {
        Write-Error "Failed to apply settings: $_"
    }
}

# Main Loop
do {
    Get-Menu
    $input = Read-Host "Select an option"
    switch ($input) {
        '1' { Export-Settings; Pause }
        '2' { Import-Settings; Pause }
        '3' { Uninstall-VMwareTools; Install-VirtIODrivers; Pause }
        '4' { Post-Migration-Repair; Pause }
        '5' { 
            if (Test-Path $BackupFile) { Get-Content $BackupFile } else { Write-Host "No file found." }
            Pause 
        }
        'Q' { exit }
        'q' { exit }
    }
} while ($true)