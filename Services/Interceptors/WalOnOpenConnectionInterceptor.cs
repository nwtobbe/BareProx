/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Diagnostics;

namespace BareProx.Services.Interceptors
{
    public class WalOnOpenConnectionInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (connection is SqliteConnection)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA busy_timeout = 5000;
                ";

                try
                {
                    cmd.ExecuteNonQuery();
                }
                // Use integer codes directly since SqliteErrorCode enum is not available in EF Core 8/9
                // 8 = SQLITE_READONLY, 1032 = SQLITE_IOERR_READONLY
                catch (SqliteException ex) when (
                    ex.SqliteErrorCode == 8 ||     // SQLITE_READONLY
                    ex.SqliteErrorCode == 1032     // SQLITE_IOERR_READONLY
                )
                {
                    Debug.WriteLine($"[WalInterceptor] PRAGMA failed: {ex.Message}");
                }
            }
        }
    }
}
