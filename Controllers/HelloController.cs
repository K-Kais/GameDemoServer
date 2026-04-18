using Microsoft.AspNetCore.Mvc;

namespace GameDemoServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HelloController : ControllerBase
{
    [HttpGet]
    public ActionResult<string> Get()
    {
        return "Hello World";
    }
}
