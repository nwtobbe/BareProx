# BareProx 2025, What Netapp did not want to give you
sudo useradd --system --create-home --shell /bin/bash bareprox
sudo usermod -aG docker bareprox

sudo mkdir -p /var/bareprox/config
sudo mkdir -p /var/bareprox/data
# sudo chown -R bareprox:bareprox /var/bareprox
sudo chown -R 1001:1001 /var/bareprox/config
sudo chown -R 1001:1001 /var/bareprox/data
systemctl enable docker

sudo su - bareprox
cd /path/to/BareProx/
docker compose up -d
apt-get install sudo ca-certificates curl gnupg lsb-release


# ---------------------- Fix
Fix retention

Authentication failed error page?
Add schedules + run

Fix add cluster
	Automatically add all nodes to the cluster by one ip
	Add scheduled task to check access

secondary...
	Partially implemented, no restore from secondary yet
tps...
mail
logging
More logging add categories and sorting, scavange with janitor
extra verifications


Done.
Added Orphaned snapshots
Fixed timezone on debian, again and again
Fixed edit schedule again
Disabled clean orpahned snapshots from primarey storage
Added rename files when doing an inplace restore.
Fixed Restore, cancel
Fixed edit storage on netapp, return to correct page when clicking on save
Fixed view under edit and create sched.
Fixed account page
Fixed when creating backup, only select that are marked as used in proxmox and storage
Fixed timeschedule for hourly.
Docker compose + build instructions
Fixed minor issue with schedules.
AutoBuildVersion
fix install db / first run
Add lock for snapshots / clones
Timezones under settings
Fix in restorecontroller ClusterName = "ProxMox",
Add select storage svm:s for primary, volumes to use
	and use it in functions
Add select storage svms: for secondary, volumes to use
	and user it in functions
Added Self signed certificate creation
Fixed local Timezone for snapshots
New date/time formatting for snapshots
Fix create backup so you only can select volumes that have nfs and are in use by proxmox
Added UserManagement
Added paths for first run creation
	volumes:
  - ./bareprox-config:/config
	-	appsettings.json
	-	DatabaseConfig.json
  - ./bareprox-data:/data
	-	BareProxDB.db
-Changed to SQL-lite
-Add options for io-freeze, Proxmox Snapshot, include memory and don't try to suspend
-Add a table for snapshots with create date an retention to delete later:
	Added a check if the snapshot can be deleted, let the record stay an retry until successful
-encrypt passwords in db Netapp + Proxmox + tokens
-Not updating BackupRecord correctly
-Added Setup if no db is configured
	Added verification if the db is reachable and works.. check permissions etc.
-Added Encryption for dbpassword
-added default user during setup
