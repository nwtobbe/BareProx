# BareProx

BareProx is an ASP.NET Core MVC application, as evidenced by its Controllers, Views, and Program.cs files ([github.com](https://github.com/nwtobbe/BareProx)). It provides management of Proxmox backups—leveraging NetApp NFS datastores—and SnapMirror configurations, including snapshot creation, SnapLock (tamper‑proof snapshots), and restore operations via a user-friendly web interface ([github.com](https://github.com/nwtobbe/BareProx)).

## Features

- **Proxmox Integration**: Monitor host status and health, create snapshots (with I/O freeze and memory support), and manage snapshot lifecycles
- **NetApp NFS Datastores**: Use NetApp NFS volumes as Proxmox storage backends for VM disks and backups.
- **NetApp SnapMirror & SnapLock**: Configure and monitor SnapMirror relationships, support SnapLock tamper‑proof snapshots ([github.com](https://github.com/nwtobbe/BareProx))
- **Scheduling**: Define hourly and daily backup jobs with customizable retention, including manual cleanup of orphaned snapshots
- **User Management**: Authentication via ASP.NET Core Identity with support for multiple users and roles ([github.com](https://github.com/nwtobbe/BareProx))
- **Logging & Monitoring**: File logging with categories, Proxmox health dashboard, status warnings ([github.com](https://github.com/nwtobbe/BareProx))
- **Dockerized Deployment**: Deploy with Docker Compose using volume mappings for configuration and persistent data ([github.com](https://github.com/nwtobbe/BareProx))

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- Docker & Docker Compose

## Installation

### Clone the repository

```bash
git clone https://github.com/nwtobbe/BareProx.git
cd BareProx
```

### Local Development

```bash
dotnet build
dotnet run --urls "http://localhost:443"
```

### Docker Compose

There is a `docker-compose.yml` file included for easy deployment. It uses the official .NET 8.0 SDK image to build the application and run it in a container.

1. Create host directories for config and data:
  
  ```bash
  sudo mkdir -p /var/bareprox/config /var/bareprox/data
  sudo chown -R 1001:1001 /var/bareprox/{config,data}
  ```
  
2. Configuration files will be created during first run `/var/bareprox/config`:
  
3. Database will be created in `/var/bareprox/data/BareProxDB.db` on first run.
  
4. Start the service:
  
  ```bash
  cd /path/to/BareProx
  ```
  
  docker compose up -d
  

Volumes map as follows:

- `./bareprox-config:/config`
- `./bareprox-data:/data` ([github.com](https://github.com/nwtobbe/BareProx))

## Server Setup

Before deploying BareProx on your Debian 12 hosts, perform the following steps:

It still needs to be 100% verified since there is a lot already packaged in the docker image.

### 1. Install prerequisites

```bash
sudo apt-get update
sudo apt-get install -y sudo ca-certificates curl gnupg lsb-release git
```

### 2. Install .NET 8 runtime

```bash
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0
```

### 3. Install Docker & Compose

```bash
sudo mkdir -p /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian \
  $(lsb_release -cs) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
sudo chmod +x /usr/local/bin/docker-compose
sudo usermod -aG docker bareprox
```

### 4. Clone, build, and run

```bash
git clone git@github.com:nwtobbe/BareProx.git
cd BareProx

# Build the Docker image
docker build -t nwtobbe/bareprox:latest .

# Run container interactively
docker run -d --name BareProx --restart unless-stopped -p 443:443 -v /var/bareprox/config:/config -v /var/bareprox/data:/data nwtobbe/bareprox:latest
```

``` 
Example docker-compose.yml
services:
  web:
    image: nwtobbe/bareprox:latest
    container_name: bareprox
    restart: unless-stopped
    ports:
      - "443:443"
    volumes:
      - /var/bareprox/config:/config  # config
      - /var/bareprox/data:/data    # db
```

## Configuration

Browse to `http://<HOST>:<PORT>` and log in with the default user 'Overseer' and 'P@ssw0rd!'. Use the web UI to:

- Configure DB and restart the application.
- View Proxmox cluster health and snapshot status
- Configure new backup tasks and SnapMirror relationships
- Monitor job history and logs

#### System

- Set TimeZone
  
- Regenerate certificate if needed
  

#### Netapp Controllers

Add NetApp controller

Then Edit created NetApp controller to select storage to use.

Rinse and repeat for a secondary controller if any.

##### Proxmox

Create Proxmox cluster

A small hint: username@pam

Edit Created Proxmox cluster and add hosts in cluster. There is no api currently to do this automagically.

When done click authenticate.

And don't forget to select storage.

## Ports used / Firewall
443 to Proxmox host for api access.
22 to Proxmox host for ssh access.
443 to Netapp controllers for api access.

#### Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/YourFeature`).
3. Commit your changes and open a pull request.

## License

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation; either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details:

[https://www.gnu.org/licenses/gpl-3.0.en.html](https://www.gnu.org/licenses/gpl-3.0.en.html)