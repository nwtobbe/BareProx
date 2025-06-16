# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),  
and this project adheres to [Semantic Versioning](https://semver.org/).

### Changed

- …

### Fixed

- …

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