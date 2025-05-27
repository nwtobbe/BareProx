// Controllers/UsersController.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using BareProx.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class UsersController : Controller
    {
        private readonly UserManager<IdentityUser> _users;

        public UsersController(UserManager<IdentityUser> users)
            => _users = users;

        // GET: /Users
        public async Task<IActionResult> Index()
        {
            // capture "now" as a constant for the EF query
            var now = DateTimeOffset.UtcNow;

            // project without the null-propagating operator
            var list = await _users.Users
                .Select(u => new UserListItemVm
                {
                    Id = u.Id,
                    UserName = u.UserName,
                    Email = u.Email,
                    IsLocked = u.LockoutEnd.HasValue && u.LockoutEnd > now,
                    LockoutEnd = u.LockoutEnd.HasValue
                                   ? u.LockoutEnd.Value.UtcDateTime
                                   : (DateTime?)null
                })
                .ToListAsync();

            return View(list);
        }

        // POST: /Users/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateUserVm m)
        {
            if (!ModelState.IsValid) return RedirectToAction("Index");
            var user = new IdentityUser { UserName = m.UserName, Email = m.Email };
            var res = await _users.CreateAsync(user, m.Password);
            if (res.Succeeded) TempData["Msg"] = "User created.";
            else TempData["Error"] = string.Join("; ", res.Errors.Select(e => e.Description));
            return RedirectToAction("Index");
        }

        // POST: /Users/Edit
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditUserVm m)
        {
            if (!ModelState.IsValid) return RedirectToAction("Index");
            var user = await _users.FindByIdAsync(m.Id);
            if (user == null) return NotFound();

            user.Email = m.Email;
            var res = await _users.UpdateAsync(user);
            if (!res.Succeeded)
            {
                TempData["Error"] = string.Join("; ", res.Errors.Select(e => e.Description));
                return RedirectToAction("Index");
            }

            // lock/unlock
            if (m.Lock)
                await _users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            else
                await _users.SetLockoutEndDateAsync(user, null);

            TempData["Msg"] = "User updated.";
            return RedirectToAction("Index");
        }

        // POST: /Users/ChangePassword
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordVm m)
        {
            if (!ModelState.IsValid) return RedirectToAction("Index");
            var user = await _users.FindByIdAsync(m.Id);
            if (user == null) return NotFound();

            // remove old passwords
            var token = await _users.GeneratePasswordResetTokenAsync(user);
            var res = await _users.ResetPasswordAsync(user, token, m.NewPassword);
            if (res.Succeeded) TempData["Msg"] = "Password changed.";
            else TempData["Error"] = string.Join("; ", res.Errors.Select(e => e.Description));
            return RedirectToAction("Index");
        }
    }
}
