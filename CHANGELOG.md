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

## [1.0.2506.1614] – 2025-09-04


### Fixed

- Restore of EFI boot disks.
- 
