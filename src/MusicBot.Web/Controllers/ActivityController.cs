using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController, Route("api/activity")]
public class ActivityController : ControllerBase
{
    [HttpGet] public IActionResult Get() => Ok(ActivityJournal.Snapshot());
}
