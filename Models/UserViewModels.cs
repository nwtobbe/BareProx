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

using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{
    // For listing users
    public class UserListItemVm
    {
        public string Id { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IsLocked { get; set; }
        public DateTime? LockoutEnd { get; set; }
    }

    // For creating
    public class CreateUserVm
    {
        [Required] public string UserName { get; set; } = "";
        [Required][EmailAddress] public string Email { get; set; } = "";
        [Required][DataType(DataType.Password)] public string Password { get; set; } = "";
    }

    // For editing email & lock
    public class EditUserVm
    {
        [Required] public string Id { get; set; } = "";
        [Required][EmailAddress] public string Email { get; set; } = "";
        public bool Lock { get; set; }
    }

    // For password change
    public class ChangePasswordVm
    {
        [Required] public string Id { get; set; } = "";
        [Required][DataType(DataType.Password)] public string NewPassword { get; set; } = "";
        [Required]
        [Compare("NewPassword")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }
}
