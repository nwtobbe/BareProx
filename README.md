# BareProx 2025, What Netapp did not want to give you
sudo useradd --system --create-home --shell /bin/bash bareprox
sudo usermod -aG docker bareprox

sudo mkdir -p /var/bareprox/config
sudo mkdir -p /var/bareprox/data
sudo chown -R bareprox:bareprox /var/bareprox

sudo su - bareprox
cd /path/to/BareProx/
docker compose up -d

sudo nano /etc/systemd/system/bareprox.service

[Unit]
Description=BareProx Docker Compose App
Requires=docker.service
After=docker.service

[Service]
WorkingDirectory=/home/bareprox/bareprox   # Adjust to your actual compose folder
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down
Restart=always
User=bareprox
Group=bareprox
TimeoutStartSec=0

[Install]
WantedBy=multi-user.target

sudo systemctl daemon-reexec
sudo systemctl daemon-reload
sudo systemctl enable bareprox.service
sudo systemctl start bareprox.service

systemctl status bareprox.service


apt-get install sudo ca-certificates curl gnupg lsb-release

Docker compose
Authentication failed error page?
Add schedules + run
Add select storage svm:s for primary, volumes to use
	and use it in functions
Add select storage svms: for secondary, volumes to use
	and user it in functions
Fix create backup so you only can select volumes that have nfs and are in use by proxmox

Fix add cluster
	Automatically add all nodes to the cluster by one ip
	Add scheduled task to check access

secondary...
	Partially implemented
tps...
mail
logging
More logging add categories and sorting, scavange with janitor
extra verifications

Add rename files via api ontap to restore
Missing snapshots?



Done.
Fixed minor issue with schedules.
AutoBuildVersion
fix install db / first run
Add lock for snapshots / clones
Timezones under settings
Fix in restorecontroller ClusterName = "ProxMox",
Added Self signed certificate creation
Fixed local Timezone for snapshots
New date/time formatting for snapshots
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
