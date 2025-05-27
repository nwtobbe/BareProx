// Models/UserViewModels.cs
using System;
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
