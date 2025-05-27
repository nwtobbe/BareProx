using BareProx.Models;
using Microsoft.AspNetCore.Mvc;
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
}

