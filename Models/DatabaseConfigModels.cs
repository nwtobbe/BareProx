/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */


using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{
    public class DatabaseConfigModels : IValidatableObject
    {
        public string DbType { get; set; } = "SqlServer"; // SqlServer or Sqlite

        public string DbServer { get; set; }
        public string DbName { get; set; }
        public string DbUser { get; set; }
        public string DbPassword { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(DbType))
            {
                yield return new ValidationResult("Database type is required.", new[] { nameof(DbType) });
            }

            if (string.IsNullOrWhiteSpace(DbName))
            {
                yield return new ValidationResult("The DbName field is required.", new[] { nameof(DbName) });
            }

            if (DbType == "SqlServer")
            {
                if (string.IsNullOrWhiteSpace(DbServer))
                    yield return new ValidationResult("The DbServer field is required.", new[] { nameof(DbServer) });

                if (string.IsNullOrWhiteSpace(DbUser))
                    yield return new ValidationResult("The DbUser field is required.", new[] { nameof(DbUser) });

                if (string.IsNullOrWhiteSpace(DbPassword))
                    yield return new ValidationResult("The DbPassword field is required.", new[] { nameof(DbPassword) });
            }
        }
    }
}
