using BareProx.Models;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Threading.Tasks;


    public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
    public IActionResult Privacy()
    {
        return View();
    }
    public IActionResult About()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        var licensePath = Path.Combine(Directory.GetCurrentDirectory(), "LICENSE");
        var licenseText = System.IO.File.Exists(licensePath)
            ? System.IO.File.ReadAllText(licensePath)
            : "License file not found.";

        ViewData["Version"] = version;
        ViewData["LicenseText"] = licenseText;
        return View();
    }
    public IActionResult Help() => View();
}

