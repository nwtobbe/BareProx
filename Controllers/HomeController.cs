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
        ViewData["Version"] = version;
        return View();
    }
    public IActionResult Help() => View();
}

