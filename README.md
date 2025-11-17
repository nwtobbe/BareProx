# BareProx

BareProx is an ASP.NET Core MVC application, as evidenced by its Controllers, Views, and Program.cs files. It provides management of Proxmox backups—leveraging NetApp NFS datastores—and SnapMirror configurations, including snapshot creation, SnapLock (tamper-proof snapshots), and restore operations via a user-friendly web interface.

## Features

- **Proxmox Integration**: Monitor host status and health, create snapshots (with memory vm-state support), and manage snapshot lifecycles
- **Migration (VMware → Proxmox)**: Migrate VMs from VMware to Proxmox using NetApp NFS as an intermediary storage (needs to be enabled in the system/settings)
- **NetApp NFS Datastores**: Use NetApp NFS volumes as Proxmox storage backends for VM disks and backups.  
- **NetApp SnapMirror & SnapLock**: Replicate backups with SnapMirror to another location, support for SnapLock tamper-proof snapshots both on primary and secondary storage.  
- **Scheduling**: Define hourly, daily or weekly backup jobs with customizable retention  
- **User Management**: Authentication via ASP.NET Core Identity with support for multiple users and roles 
- **Logging & Monitoring**: File logging with categories, Proxmox health dashboard, status warnings  
- **Dockerized Deployment**: Deploy with Docker Compose using volume mappings for configuration and persistent data

## Prerequisites

- Debian or Ubuntu host (tested on Debian 13)
- NetApp ONTAP with NFS datastores
- Proxmox VE cluster (tested on Proxmox VE 9) 

---

## Server Setup

Before deploying BareProx on your Debian 13 hosts, perform the following steps.  
These instructions assume a fresh Debian “netinst” with SSH access.

> **Note:** You do **not** need to install the .NET runtime on the host when using Docker.

---

### 1. Create user and install base tools

On a fresh install:

```bash
# Install base packages
apt-get update
apt-get install -y sudo curl gpg ca-certificates lsb-release
```

**Create BareProx user and grant sudo:**

```bash
adduser bareprox
usermod -aG sudo bareprox
```

**A small tip to fix paths:**

```bash
cat << EOF | tee -a /etc/profile > /dev/null

if ! echo "\$PATH" | grep -q "/sbin"; then
    export PATH="/usr/local/sbin:/usr/sbin:/sbin:\$PATH"
fi
EOF
```

Log out, then log back in as **bareprox**.

---

### 2. Install Docker & Compose

```bash
sudo mkdir -p /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg

echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian $(lsb_release -cs) stable"  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Allow the BareProx user to manage Docker
sudo usermod -aG docker bareprox
```

> Log out and back in again so the \`docker\` group membership takes effect.

---

## Installation

### Docker Compose

There is a \`docker-compose.yml\` file included for easy deployment.  
It uses the prebuilt **BareProx** Docker image based on the official .NET 8 runtime.

1. **Create host directories for config and data:**

   ```bash
   sudo mkdir -p /var/bareprox/config /var/bareprox/data
   sudo chown -R 1001:1001 /var/bareprox/{config,data}
   ```

2. **Configuration files** and **log directory** will be created automatically on first run in \`/var/bareprox/config\`.

3. **Database** will be created on first run at \`/var/bareprox/data/BareProxDB.db\`.

4. **Create a \`docker-compose.yml\` file:**

   ```yaml
   # Example docker-compose.yml
   services:
     web:
       image: nwtobbe/bareprox:latest
       container_name: bareprox
       restart: unless-stopped
       ports:
         - "443:443"
       volumes:
         - /var/bareprox/config:/config  # Configuration & certificates
         - /var/bareprox/data:/data      # Database (BareProxDB.db)
       environment:
         - TZ=Europe/Stockholm           # Optional: set your timezone
   ```

5. **Start the service:**

   ```bash
   cd /path/to/BareProx
   docker compose up -d
   ```

BareProx will start automatically.  
Configuration files will and logs appear under \`/var/bareprox/config\`, and the SQLite databases under \`/var/bareprox/data/\`.

---

### Notes

- **Update BareProx:**

  ```bash
  docker compose down
  docker compose pull
  docker compose up -d
  ```

- **View logs:**

  ```bash
  docker logs -f bareprox
  # or check under /var/bareprox/config/logs
  ```

---

## Configuration

Browse to **https://&lt;HOST&gt;** and log in with the default user **Overseer** and password **P@ssw0rd!**  
Use the web UI to:

- Configure DB and restart the application.  
- View Proxmox cluster health and snapshot status.  
- Configure new backup tasks and SnapMirror relationships.  
- Monitor job history and logs.

### System

- Set TimeZone  
- Regenerate certificate if needed or Import a real certificate
- Enable experimental features (if any)

### NetApp Controllers

Add NetApp controller.  
Then edit the created NetApp controller to select storage to use.  
Rinse and repeat for a secondary controller if any.

### Proxmox

Create Proxmox cluster.  

> A small hint: root@pam

Edit the created Proxmox cluster and add hosts in the cluster.  
There is no API currently to do this automatically.  

When done, click **Authenticate**.  
And don’t forget to select datastores to use.

---

## Ports Used / Firewall

- **443** → Proxmox host for API access  
- **22** → Proxmox host for SSH access  
- **443** → NetApp controllers for API access  

---

## Things Not Working / Known Issues

- **TPM devices:** Proxmox does not allow snapshots of VMs with TPM devices. Under testing.
- **Cloud-Init drives:** Delete and recreate them after restore. They are temporary devices.
- ~~**Exclude from backup** You can exclude vm:s from backup but.. there is no code currently that does the actual exclusion.~~
- **VMware migration:** Only basic VM migration is supported. No advanced features like snapshots, linked clones, etc.
- **Spanning disks** are not supported, keep all disks for a VM on the same datastore!

---

## Contributing

1. Fork the repository.  
2. Create a feature branch (\`git checkout -b feature/YourFeature\`).  
3. Commit your changes and open a pull request.  

---

## License (AGPL-3.0)

This program is free software: you can redistribute it and/or modify it under the terms of the
GNU Affero General Public License as published by the Free Software Foundation, version 3.

This program is distributed in the hope that it will be useful,
but without any warranty; without even the implied warranty of merchantability or fitness for a particular purpose.
See the GNU Affero General Public License for more details:

https://www.gnu.org/licenses/agpl-3.0.en.html

AGPL notice: If you modify this application and make it available to users over a network, you must also offer those users access to the corresponding source code of your modified version under the AGPL-3.0.
