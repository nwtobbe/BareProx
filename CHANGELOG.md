# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),  
and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.0.2506.1708] – 2025-06-17

### Added

- Cleanup, added unmount from all hosts and delete mountpoint. Proxmox Api only removes the mountpoint in gui, not from the hosts.
- SSH for some functions since the Proxmox API is quite restrictive.

### Fixed

- Restore, changed from api to ssh for configuration files due to the fact that proxmox api does not handle restoring vm:s with snapshots
- Backup, changed reading vm:s configurations from api to ssh due to that we don't get all properties when reading from api.
- Restore, changed when restoring a vm with disabled nics. The nics, now, actually gets disabled instead of removed.
- Backup/Restore, new db-entries for config. Old restores may not work without manual configuration.

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

## [1.0.2509.0420] – 2025-09-04


### Fixed

- Restore of EFI boot disks.
- Minor menu issue

## [1.0.2509.2513] – 2025-09-25

### Fixed

- Changed the way how Snapmirror relationships are updated. 

## [1.0.2509.2513] – 2025-09-25

### Fixed

- Fixed cleanup when removing netapp-cluster
- Update of variuos modules.

## [1.0.2510.0914] – 2025-10-09

### Fixed

- paused VM not resumed by BareProx, Fixed by lchanouha (Sorry for the late merge)

## [1.0.2510.0914] – 2025-10-09

### New
- Added support for Snapshots as volume chains
- Added a bonus feature
- Added compression of old logfiles (keeps ~30 days)
- Restore:
	Added Generate new uuid + vmgenid
	Added Generate new mac addresses for nics
	Added Rollback snapshot
	Added VmState Warning for Rollback
- Probably new bugs.
- Changed Settings / Proxmox
- Changed Settings / Netapp

### Fixed

- Failed, cancelled, error-jobs, stuck running etc.. now gets pruned from the db after 30 days.
- Fixed an issue where vmid was added to {vmid}.conf when creating a new vm.
- Security fixes