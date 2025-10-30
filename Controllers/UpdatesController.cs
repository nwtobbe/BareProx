using BareProx.Services.Updates;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Controllers
{
    [Route("api/updates")]
    public class UpdatesController : Controller
    {
        private readonly IUpdateChecker _checker;
        public UpdatesController(IUpdateChecker checker) => _checker = checker;

        [HttpGet("status")]
        public async Task<IActionResult> Status(CancellationToken ct)
        {
            var info = await _checker.CheckAsync(ct);
            return Json(info);
        }
    }
}
