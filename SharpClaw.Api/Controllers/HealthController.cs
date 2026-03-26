using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public IActionResult Get()
    => Ok(new HealthResponse("ok", "SharpClaw"));
}