# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),  
and this project ~adheres to [Semantic Versioning](https://semver.org/).



## [1.0.2510.2015] - 2025-10-25

### Added
-

### Changed
- About Page

### Fixed
- Security updates, including CVE-2025-55315, for real this time.
- Performance improvements
- DB improvements



## [1.0.2510.2014] - 2025-10-20

### Added
-

### Changed
- Security updates

### Fixed
- Snapshots not getting the correct labels Manual/hourly/daily/weekly



## [1.0.2510.2013] – 2025-10-20

### Added
- Support for Snapshots as volume chains
- A bonus feature
- compression of old logfiles (keeps ~30 days)
- Restore:
	Generate new uuid + vmgenid
	Generate new mac addresses for nics
	Rollback snapshot
	VmState Warning for Rollback
- Probably new bugs.
- Account verification for Netapp Clusters on add/edit

### Changed
- Settings / Proxmox
- Settings / Netapp
- Backup / Create Schedules
- Backup / Edit Schedules

### Fixed

- Job cleanup after 30 days (failed/cancelled/stuck)
- Snapshots not getting the correct labels Manual/hourly/daily/weekly
- VMID duplication issue when creating new VMs
- Excluded VMs in jobs are now gettings excluded from proxmox options
- Security updates, including CVE-2025-55315
- Performance improvements



## [1.0.2510.0914] – 2025-10-09

### Fixed

- paused VM not resumed by BareProx, Fixed by lchanouha (Sorry for the late merge)

## [1.0.2509.2513] – 2025-09-25

### Fixed

- Changed the way how Snapmirror relationships are updated. 
- Fixed cleanup when removing netapp-cluster
- Update of variuos modules.



## [1.0.2509.0420] – 2025-09-04

### Fixed

- Restore of EFI boot disks.
- Minor menu issue



## [1.0.2506.1614] – 2025-06-16

### Added

- More help text

### Fixed

- Restore view now grouping vm:s and a new dialogue appears to select restore point when clicking on a vm.
- User management. Now we can create lock and delete users again.
- Restore vm as create new vm. Changed move vm-disks to new vmid to instead create symlink to new id. Just to mitigate low space issues.
  And it's faster.
- Mount from Secondary is now working
- Restore from Secondary is now working.



## [1.0.2506.1708] – 2025-06-17

### Added

- Cleanup, added unmount from all hosts and delete mountpoint. Proxmox Api only removes the mountpoint in gui, not from the hosts.
- SSH for some functions since the Proxmox API is quite restrictive.

### Fixed

- Restore, changed from api to ssh for configuration files due to the fact that proxmox api does not handle restoring vm:s with snapshots
- Backup, changed reading vm:s configurations from api to ssh due to that we don't get all properties when reading from api.
- Restore, changed when restoring a vm with disabled nics. The nics, now, actually gets disabled instead of removed.
- Backup/Restore, new db-entries for config. Old restores may not work without manual configuration.