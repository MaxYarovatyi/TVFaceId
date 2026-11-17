using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Database;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Web.Services;

namespace Web.Controllers
{
    [ApiController]
    [Route("api/recognition")]
    public sealed class RecognitionController() : ControllerBase
    {
        [HttpPost("scan")]
        public IActionResult ForceScan([FromServices] IEnumerable<IHostedService> services)
        {
            services.OfType<RecognitionWorker>().FirstOrDefault()?.ForceProcess();
            return Ok();
        }
    }
}