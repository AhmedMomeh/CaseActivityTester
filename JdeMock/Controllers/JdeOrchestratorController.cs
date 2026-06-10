using ActivityTester.JdeMock.Authentication;
using ActivityTester.JdeMock.Models;
using ActivityTester.JdeMock.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace ActivityTester.JdeMock.Controllers
{
    /// <summary>
    /// Mock implementations of the three JDE Orchestrator endpoints the Case
    /// Portal integration depends on. All endpoints require Basic Auth (see
    /// <see cref="BasicAuthenticationHandler"/>). The request/response shapes
    /// match production JDE exactly — including the trailing <c>jde__*</c>
    /// timing fields and the quirky " " / "null" sentinel values.
    ///
    /// Every endpoint awaits a configurable artificial delay before responding
    /// (<c>JdeMock:ResponseDelayMs</c> in appsettings.json, default 8000 ms).
    /// Lets consumers exercise loaders / timeouts / parallelism without
    /// pointing at real JDE. Set to 0 to disable the delay.
    /// </summary>
    [ApiController]
    [Route("jderest/orchestrator")]
    [Authorize(AuthenticationSchemes = BasicAuthenticationHandler.SchemeName)]
    [Consumes("application/json")]
    [Produces("application/json")]
    public sealed class JdeOrchestratorController : ControllerBase
    {
        private readonly MockDataService _data;
        private readonly ILogger<JdeOrchestratorController> _log;
        private readonly int _delayMs;

        public JdeOrchestratorController(
            MockDataService data,
            ILogger<JdeOrchestratorController> log,
            IConfiguration config)
        {
            _data    = data;
            _log     = log;
            _delayMs = config.GetValue("JdeMock:ResponseDelayMs", 8000);
        }

        // Single helper so every endpoint applies the same delay shape.
        // Logs once per request so you can see the simulated latency in the
        // mock console — handy when correlating loader timing on the client.
        private async Task SimulateLatencyAsync(string endpoint)
        {
            if (_delayMs <= 0) return;
            _log.LogInformation("{Endpoint}: simulating latency  {DelayMs} ms", endpoint, _delayMs);
            await Task.Delay(_delayMs);
        }

        // -------------------------------------------------------------------
        // POST /jderest/orchestrator/GetDepartmentList
        //   Request:  { "CompanyFilter": "00001" }
        //   Response: { "DepartmentList": [...], "jde__status": "SUCCESS", ... }
        // -------------------------------------------------------------------
        [HttpPost("GetDepartmentList")]
        public async Task<ActionResult<GetDepartmentListResponse>> GetDepartmentList(
            [FromBody] GetDepartmentListRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation(
                "GetDepartmentList received  CompanyFilter='{Filter}'",
                request?.CompanyFilter);

            await SimulateLatencyAsync(nameof(GetDepartmentList));

            var list = _data.GetDepartments(request?.CompanyFilter);

            var resp = new GetDepartmentListResponse { DepartmentList = list };
            resp.SetTimings(start, JdeTime.NowUae());

            _log.LogInformation(
                "GetDepartmentList returned  {Count} department(s)  in {Seconds:N3}s",
                list.Count, resp.ServerExecutionSeconds);
            return Ok(resp);
        }

        // -------------------------------------------------------------------
        // POST /jderest/orchestrator/GetEmployeeInfoByEmail
        //   Request:  { "Email": "m.sharaf@unioncoop.ae" }
        //   Response: { "Email":..., "FileNo":..., "Name":..., "jde__status":... }
        // For unknown emails the mock returns the literal string "null" for
        // text fields (same as real JDE), NOT a JSON null.
        // -------------------------------------------------------------------
        [HttpPost("GetEmployeeInfoByEmail")]
        public async Task<ActionResult<GetEmployeeInfoByEmailResponse>> GetEmployeeInfoByEmail(
            [FromBody] GetEmployeeInfoByEmailRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation(
                "GetEmployeeInfoByEmail received  Email='{Email}'",
                request?.Email);

            await SimulateLatencyAsync(nameof(GetEmployeeInfoByEmail));

            var resp = _data.GetEmployee(request?.Email);
            resp.SetTimings(start, JdeTime.NowUae());

            _log.LogInformation(
                "GetEmployeeInfoByEmail returned  Email='{Email}'  FileNo={FileNo}  in {Seconds:N3}s",
                resp.Email, resp.FileNo, resp.ServerExecutionSeconds);
            return Ok(resp);
        }

        // -------------------------------------------------------------------
        // POST /jderest/orchestrator/GetJobsList
        //   Request:  { "DepartmentFilter": "101IT" }
        //   Response: { "JobsList": [...], "jde__status":"SUCCESS", ... }
        // -------------------------------------------------------------------
        [HttpPost("GetJobsList")]
        public async Task<ActionResult<GetJobsListResponse>> GetJobsList(
            [FromBody] GetJobsListRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation(
                "GetJobsList received  DepartmentFilter='{Filter}'",
                request?.DepartmentFilter);

            await SimulateLatencyAsync(nameof(GetJobsList));

            var list = _data.GetJobs(request?.DepartmentFilter);

            var resp = new GetJobsListResponse { JobsList = list };
            resp.SetTimings(start, JdeTime.NowUae());

            _log.LogInformation(
                "GetJobsList returned  {Count} job(s)  in {Seconds:N3}s",
                list.Count, resp.ServerExecutionSeconds);
            return Ok(resp);
        }

        // -------------------------------------------------------------------
        // POST /jderest/orchestrator/GetNationalitiesList
        //   Request:  {}                 (no filters in production JDE today)
        //   Response: { "NationalitiesList": [...], "jde__status":"SUCCESS", ... }
        // -------------------------------------------------------------------
        [HttpPost("GetNationalitiesList")]
        public async Task<ActionResult<GetNationalitiesListResponse>> GetNationalitiesList(
            [FromBody] GetNationalitiesListRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation("GetNationalitiesList received");

            await SimulateLatencyAsync(nameof(GetNationalitiesList));

            var list = _data.GetNationalities();

            var resp = new GetNationalitiesListResponse { NationalitiesList = list };
            resp.SetTimings(start, JdeTime.NowUae());

            _log.LogInformation(
                "GetNationalitiesList returned  {Count} nationality(ies)  in {Seconds:N3}s",
                list.Count, resp.ServerExecutionSeconds);
            return Ok(resp);
        }
    }
}
