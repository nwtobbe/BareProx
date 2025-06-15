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
