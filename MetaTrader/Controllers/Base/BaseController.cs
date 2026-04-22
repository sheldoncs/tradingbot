using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FATCA3.Web.Base
{
    public class BaseController : ControllerBase
    {
        protected readonly ILogger _logger;

        public BaseController(ILogger logger)
        {
            _logger = logger;
        }
    }
}
