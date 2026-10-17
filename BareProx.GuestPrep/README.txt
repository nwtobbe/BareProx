BareProx GuestPrep - Program.cs updated draft (2026-10-13)

Replace BareProx.GuestPrep/Program.cs with the supplied source and publish with your existing .NET 10 project. Do not deploy this draft to production without testing on a snapshot.

Changes:
- --disable-vmci optional flag on uninstall-vmware-tools, only on verified VMware source, after successful VMware Tools uninstall. Marks ShutdownRequired/NextBootMustBeProxmox, does not reboot automatically. Leave VMCI loaded until the source is shut down. Intended as FINAL guest operation before source shutdown.
- Cleanup allowlist includes obsolete Intel 82574L VMware E1000E PCI VEN_8086 DEV_10D3 SUBSYS_07D015AD NICs; still requires Proxmox and DEVPKEY_Device_IsPresent=False.
- Fix VMCI INF discovery: rename PowerShell $matches variable, which collided with special automatic $Matches from -match operator, to $foundPackages. Verify vmci.inf before pnputil removal; never /force.
- Suppress PowerShell ProgressPreference and CLIXML diagnostic noise in the integrated wrapper.
- If a maintenance JSON result says Success:false, the executable exits nonzero.
- Cleanup blocked by still-running VMCI now flags RebootRequired=true.
- Non-interactive --apply requires --yes.

Source VMware finalization (elevated CMD, only after driver preparation and network capture):
BareProx.GuestPrep.exe uninstall-vmware-tools --apply --yes --disable-vmci --allow-reboot-required --output C:\test\vmware-uninstall.json

Shut down VMware VM (DO NOT BOOT IT AGAIN ON VMWARE), migrate and boot on Proxmox, verify VirtIO SCSI and QEMU GA, then:
BareProx.GuestPrep.exe cleanup-vmware --apply --yes --cleanup-vmci-driver --output C:\test\cleanup-result.json
BareProx.GuestPrep.exe cleanup-guestprep --apply --yes --output C:\test\phantom-result.json

This remains the PowerShell-backed integrated implementation, NOT a native C# port. It requires Windows PowerShell 5.1 locally but no external PS1 files or internet.

CAUTION: Uninstalling VMware Tools may terminate VMware Guest Operations. Disable VMCI after uninstall may not execute if transport/process is terminated. Verify output JSON and VMCI Start=4 before source shutdown. Test with console and recoverable snapshot; do not disable VMCI for ongoing VMware operation.

Corrections:
- Fix bad single-backslash embedded PowerShell regex (real cause of cleanup-guestprep failure); use literal PCI prefix comparisons instead.
- Preserve stage diagnostics; clear stale JSON and verify each operation.
- Make network mapping validation explicit.
- Record waiting status when startup task runs under VMware or unknown hypervisor.
- Verify startup task deletion before recording complete success.
- Require absent VMCI service after cleanup.
- Recheck only the targeted obsolete PnP device before removal for improved performance.
- Mark driver cleanup skipped/pending-reboot as incomplete, not successful.
- Treat unparseable maintenance JSON as nonzero exit status.