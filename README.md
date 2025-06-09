# BareProx

BareProx is an ASP.NET Core MVC application, as evidenced by its Controllers, Views, and Program.cs files ([github.com](https://github.com/nwtobbe/BareProx)). It provides management of Proxmox backups and NetApp SnapMirror configurations, including snapshot creation, SnapLock (tamper‑proof snapshots), and restore operations via a user-friendly web interface ([github.com](https://github.com/nwtobbe/BareProx)).

## Features

* **Proxmox Integration**: Monitor host status and health, create snapshots (with I/O freeze and memory support), and manage snapshot lifecycles ([github.com](https://github.com/nwtobbe/BareProx))
* **NetApp SnapMirror & SnapLock**: Configure and monitor SnapMirror relationships, support SnapLock tamper‑proof snapshots ([github.com](https://github.com/nwtobbe/BareProx))
* **Scheduling**: Define hourly and daily backup jobs with customizable retention, automatic orphaned snapshot cleanup ([github.com](https://github.com/nwtobbe/BareProx))
* **User Management**: Authentication via ASP.NET Core Identity with support for multiple users and roles ([github.com](https://github.com/nwtobbe/BareProx))
* **Logging & Monitoring**: File logging with categories, Proxmox health dashboard, status warnings ([github.com](https://github.com/nwtobbe/BareProx))
* **Dockerized Deployment**: Deploy with Docker Compose using volume mappings for configuration and persistent data ([github.com](https://github.com/nwtobbe/BareProx))

## Prerequisites

* [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
* Docker & Docker Compose

## Installation

### Clone the repository

```bash
git clone https://github.com/nwtobbe/BareProx.git
cd BareProx
```

### Local Development

```bash
dotnet build
dotnet run --urls "http://localhost:5000"
```

### Docker Compose

1. Create host directories for config and data:

   ```bash
   sudo mkdir -p /var/bareprox/config /var/bareprox/data
   sudo chown -R 1001:1001 /var/bareprox/{config,data}
   ```
2. Configuration files will be created during first run `/var/bareprox/config`:
2.1 Database will be created in `/var/bareprox/data/BareProxDB.db` on first run.

3. Start the service:

   ```bash
   cd /path/to/BareProx
   ```

docker compose up -d

```

Volumes map as follows:
- `./bareprox-config:/config`
- `./bareprox-data:/data` ([github.com](https://github.com/nwtobbe/BareProx))

## Configuration


Browse to `http://<HOST>:<PORT>` and log in with the default user Overseer and P@ssw0rd! . Use the web UI to:

- View Proxmox cluster health and snapshot status  
- Configure new backup tasks and SnapMirror relationships  
- Monitor job history and logs  

## Contributing

1. Fork the repository.  
2. Create a feature branch (`git checkout -b feature/YourFeature`).  
3. Commit your changes and open a pull request.

## License

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation; either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details:

<https://www.gnu.org/licenses/gpl-3.0.en.html>

```
