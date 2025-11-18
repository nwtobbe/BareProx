# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),  
and this project ~adheres to [Semantic Versioning](https://semver.org/).



## [1.0.2511.1720] - 2025-11-17

### Changed
- Settings / Migration / Settings. Added status if configured or not.

### Fixed
- Added code to handle people not able to keep the naming correct for Proxmox volumes / NetApp volumes and Junction Paths.
- Filter on iso:s on backup



## [1.0.2511.1718] - 2025-11-17

### Changed
- Job concurrency decreased to 2 from 4 to lower the load on the sqlite db. If someone notices db issues please report and we lower it to 1.
- Page Jobs. Limit the number of rows to display at a time.
- Migration. Changed from VirtIO Block Device to the correct SCSI Device when using prepare for VirtIO!

### Fixed
- Restore Now not handling Locking



## [1.0.2511.1415] - 2025-11-14

## Added
- Mount vm disks from snapshots to another vm under Netapp / Snapshots. It is available from backups created from this version and forward.

### Changed
- Small change to the restoreflow to better handle missmatch in volume names
- Netapp / Snapshots
- Selected NetApp volumes now displays the number of selected volumes.



## [1.0.2511.1213] - 2025-11-12

## Added
- BareProx_qeryDB, extra DB
- API Tokens, default for new clusters, enabled in settings for existing cluster

### Changed
- When adding a Proxmox cluster now we can use the API instead of SSH for discovery
- Help! Started writing the booring help-text
- Updated restore functionality to better handle errors and volume lookups



## [1.0.2511.0713] - 2025-11-07

## Added
- Possibility to add certificates in the form of .pfx (PKCS#12)

### Changed
- Page Settings/System. New layout
- Page Settings/Proxmox New add cluster method



## [1.0.2511.0519] - 2025-11-05

### Changed
- Added ca-certificates to docker image



## [1.0.2511.0518] - 2025-11-05

### Changed
- Code cleanup



## [1.0.2511.0513] - 2025-11-05

### Added
- Email Notifications / Backup Schedule instead of global. Notifications gets disabled when applying this update.

### Changed
- Page Settings NetApp. Small help text for volumes
- Page Settings Proxmox. Only one cluster is now allowed.
- Page Backup Create/Edit. Added Notifications
- Improvements on background services

### Fixed
- Update check
- Validation for storageselection.



## [1.0.2511.0413] - 2025-11-04

### Added
- Check for in use selected storage on netapp controllers

### Changed
- Page NetApp Settings. Selection of volumes
- Page Home. Host info

### Fixed
- Changes in db for NetApp Selected volumes


## [1.0.2511.0413] - 2025-11-04

### Fixed
- Migration not honoring the selected host
- Multiple netapp controllers causing confusion.



## [1.0.2510.3113] - 2025-10-31

### Fixed
- Page Settings Netapp. Selected volumes not getting display as selected.



## [1.0.2510.3113] - 2025-10-31

### Added
- Basic email notification
- Update notifications

### Changed
- Page. Settings/System
- Page. Cleanup. Now only one click at a time.
- Page. Proxmox. Added search and expand

### Fixed
- Page. Jobs. Truncated error messages



## [1.0.2510.2015] - 2025-10-25

### Changed
- About Page
- Job Page
- Logging
- Netapp volume clone delete now skipping recovery queue

### Fixed
- Security updates
- Performance improvements
- DB improvements
- Snaplock not reading the compliance clock for reference


## [1.0.2510.2014] - 2025-10-20

### Changed
- Security updates

### Fixed
- Snapshots not getting the correct labels Manual/hourly/daily/weekly



## [1.0.2510.2013] - 2025-10-20

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



## [1.0.2510.0914] - 2025-10-09

### Fixed

- paused VM not resumed by BareProx, Fixed by lchanouha (Sorry for the late merge)

## [1.0.2509.2513] – 2025-09-25

### Fixed

- Changed the way how Snapmirror relationships are updated. 
- Fixed cleanup when removing netapp-cluster
- Update of variuos modules.



## [1.0.2509.0420] - 2025-09-04

### Fixed

- Restore of EFI boot disks.
- Minor menu issue



## [1.0.2506.1614] - 2025-06-16

### Added

- More help text

### Fixed

- Restore view now grouping vm:s and a new dialogue appears to select restore point when clicking on a vm.
- User management. Now we can create lock and delete users again.
- Restore vm as create new vm. Changed move vm-disks to new vmid to instead create symlink to new id. Just to mitigate low space issues.
  And it's faster.
- Mount from Secondary is now working
- Restore from Secondary is now working.



## [1.0.2506.1708] - 2025-06-17

### Added

- Cleanup, added unmount from all hosts and delete mountpoint. Proxmox Api only removes the mountpoint in gui, not from the hosts.
- SSH for some functions since the Proxmox API is quite restrictive.

### Fixed

- Restore, changed from api to ssh for configuration files due to the fact that proxmox api does not handle restoring vm:s with snapshots
- Backup, changed reading vm:s configurations from api to ssh due to that we don't get all properties when reading from api.
- Restore, changed when restoring a vm with disabled nics. The nics, now, actually gets disabled instead of removed.
- Backup/Restore, new db-entries for config. Old restores may not work without manual configuration.