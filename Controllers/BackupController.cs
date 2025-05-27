using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class BackupController : Controller
    {
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappService _netappService;
        private readonly ApplicationDbContext _context;
        private readonly IBackupService _backupService;
        private readonly IServiceScopeFactory _scopeFactory;

        public BackupController(ApplicationDbContext context, ProxmoxService proxmoxService, INetappService netappService, IBackupService backupService, IServiceScopeFactory scopeFactory)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netappService = netappService;
            _backupService = backupService;
            _scopeFactory = scopeFactory;
        }

        public IActionResult Backup()
        {
            var schedules = _context.BackupSchedules.ToList(); // List all backup schedules
            return View(schedules);
        }

        public async Task<IActionResult> CreateSchedule()
        {
            var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync();
            var netapp = await _context.NetappControllers.FirstOrDefaultAsync();

            if (cluster == null || netapp == null)
                return NotFound("Missing cluster or NetApp configuration.");

            var storageWithVms = await _proxmoxService.GetFilteredStorageWithVMsAsync(cluster.Id, netapp.Id);
            if (storageWithVms == null)
                return NotFound("Could not retrieve storage and VMs.");

            var viewModel = new CreateScheduleRequest
            {
                StorageOptions = storageWithVms.Keys.Select(s => new SelectListItem { Text = s, Value = s }).ToList(),
                AllVms = storageWithVms.SelectMany(kvp => kvp.Value.Select(vm => new SelectListItem { Text = $"{vm.Name} ({kvp.Key})", Value = vm.Id.ToString() })).ToList(),
                ExcludedVmIds = new List<string>()
            };

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSchedule(CreateScheduleRequest model)
        {
            if (!ModelState.IsValid || model.SingleSchedule == null)
            {
                var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync();
                var netapp = await _context.NetappControllers.FirstOrDefaultAsync();
                var storageWithVms = await _proxmoxService.GetFilteredStorageWithVMsAsync(cluster.Id, netapp.Id);

                model.StorageOptions = storageWithVms.Keys.Select(s => new SelectListItem { Text = s, Value = s }).ToList();
                model.AllVms = storageWithVms.SelectMany(kvp => kvp.Value.Select(vm => new SelectListItem { Text = $"{vm.Name} ({kvp.Key})", Value = vm.Id.ToString() })).ToList();

                TempData["ErrorMessage"] = "Please fill in all required schedule details.";
                return View(model);
            }

            var s = model.SingleSchedule;

            if (string.IsNullOrWhiteSpace(s.Type) ||
                s.DaysOfWeek == null || !s.DaysOfWeek.Any() ||
                (s.Type != "Hourly" && string.IsNullOrWhiteSpace(s.Time)) ||
                (s.Type == "Hourly" && (!s.StartHour.HasValue || !s.EndHour.HasValue)) ||
                s.RetentionCount < 1 || s.RetentionCount > 999 || string.IsNullOrWhiteSpace(s.RetentionUnit))
            {
                TempData["ErrorMessage"] = "Invalid schedule configuration.";
                return RedirectToAction("Backup");
            }

            var schedule = new BackupSchedule
            {
                Name = s.Label ?? $"{s.Type} Schedule",
                StorageName = model.StorageName,
                Schedule = s.Type,
                Frequency = s.Type == "Hourly" ? $"{s.StartHour}-{s.EndHour}" : string.Join(",", s.DaysOfWeek),
                TimeOfDay = s.Type == "Hourly" ? null : (TimeSpan.TryParse(s.Time, out var parsedTime) ? parsedTime : null),
                IsApplicationAware = model.IsApplicationAware,
                EnableIoFreeze = model.EnableIoFreeze,
                UseProxmoxSnapshot = model.UseProxmoxSnapshot,
                WithMemory = model.WithMemory,
                ExcludedVmIds = model.ExcludedVmIds != null ? string.Join(",", model.ExcludedVmIds) : null,
                RetentionCount = s.RetentionCount,
                RetentionUnit = s.RetentionUnit,
                LastRun = null
            };

            _context.BackupSchedules.Add(schedule);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Backup schedule created successfully!";
            return RedirectToAction("Backup");
        }

        public async Task<IActionResult> EditSchedule(int id)
        {
            var schedule = await _context.BackupSchedules.FindAsync(id);
            if (schedule == null)
                return NotFound();

            var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync();
            var netapp = await _context.NetappControllers.FirstOrDefaultAsync();
            var storageWithVms = await _proxmoxService.GetFilteredStorageWithVMsAsync(cluster.Id, netapp.Id);

            var model = new CreateScheduleRequest
            {
                Id = id,
                Name = schedule.Name,
                StorageName = schedule.StorageName,
                IsApplicationAware = schedule.IsApplicationAware,
                EnableIoFreeze = schedule.EnableIoFreeze,
                UseProxmoxSnapshot = schedule.UseProxmoxSnapshot,
                WithMemory = schedule.WithMemory,
                ExcludedVmIds = schedule.ExcludedVmIds?.Split(',').ToList() ?? new List<string>(),
                StorageOptions = storageWithVms.Keys.Select(s => new SelectListItem { Text = s, Value = s }).ToList(),
                AllVms = storageWithVms.SelectMany(kvp => kvp.Value.Select(vm => new SelectListItem { Text = $"{vm.Name} ({kvp.Key})", Value = vm.Id.ToString() })).ToList(),
                SingleSchedule = new ScheduleEntry
                {
                    Label = schedule.Name,
                    Type = schedule.Schedule,
                    DaysOfWeek = schedule.Frequency.Split(',').ToList(),
                    Time = schedule.TimeOfDay?.ToString(@"hh\:mm"),
                    StartHour = schedule.Frequency.Contains("-") ? int.Parse(schedule.Frequency.Split('-')[0]) : (int?)null,
                    EndHour = schedule.Frequency.Contains("-") ? int.Parse(schedule.Frequency.Split('-')[1]) : (int?)null,
                    RetentionCount = schedule.RetentionCount,
                    RetentionUnit = schedule.RetentionUnit
                }
            };

            ViewData["ScheduleId"] = id;
            return View("EditSchedule", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditSchedule(int id, CreateScheduleRequest model)
        {
            var schedule = await _context.BackupSchedules.FindAsync(id);
            if (schedule == null)
                return NotFound();

            var s = model.SingleSchedule;

            schedule.Name = s.Label ?? $"{s.Type} Schedule";
            schedule.StorageName = model.StorageName;
            schedule.Schedule = s.Type;
            schedule.Frequency = s.Type == "Hourly" ? $"{s.StartHour}-{s.EndHour}" : string.Join(",", s.DaysOfWeek);
            schedule.TimeOfDay = s.Type == "Hourly" ? null : (TimeSpan.TryParse(s.Time, out var parsedTime) ? parsedTime : null);
            schedule.IsApplicationAware = model.IsApplicationAware;
            schedule.EnableIoFreeze = model.EnableIoFreeze;
            schedule.UseProxmoxSnapshot = model.UseProxmoxSnapshot;
            schedule.WithMemory = model.WithMemory;
            schedule.ExcludedVmIds = model.ExcludedVmIds != null ? string.Join(",", model.ExcludedVmIds) : null;
            schedule.RetentionCount = s.RetentionCount;
            schedule.RetentionUnit = s.RetentionUnit;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Backup schedule updated successfully.";
            return RedirectToAction("Backup");
        }

        public IActionResult DeleteSchedule(int id)
        {
            var schedule = _context.BackupSchedules.Find(id);
            if (schedule != null)
            {
                _context.BackupSchedules.Remove(schedule);
                _context.SaveChanges();
            }

            return RedirectToAction("Backup");
        }

        [HttpPost]
        public IActionResult StartBackupNow([FromForm] BackupRequest request)
        {
            if (string.IsNullOrEmpty(request.StorageName) || request.RetentionCount < 1 || request.RetentionCount > 999)
                return BadRequest("Invalid input");

            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();

                var scopedBackupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<BackupController>>();

                try
                {
                    logger.LogInformation("Starting background backup for storage {StorageName}", request.StorageName);

                    await scopedBackupService.StartBackupAsync(
                        request.StorageName,
                        request.IsApplicationAware,
                        request.Label,
                        request.ClusterId,
                        request.NetappControllerId,
                        request.RetentionCount,
                        request.RetentionUnit,
                        request.EnableIoFreeze,
                        request.UseProxmoxSnapshot,
                        request.WithMemory,
                        request.DontTrySuspend,
                        request.ScheduleID
                        
                    );

                    logger.LogInformation("Backup process completed for {StorageName}", request.StorageName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Backup failed for {StorageName}: {Message}", request.StorageName, ex.Message);
                }
            });

            TempData["SuccessMessage"] = $"Backup for '{request.StorageName}' started in the background.";
            return RedirectToAction("ListVMs", "Proxmox");
        }
    }
}
