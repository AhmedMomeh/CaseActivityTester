using ActivityTester.JdeMock.Authentication;
using ActivityTester.JdeMock.Models;
using ActivityTester.JdeMock.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ActivityTester.JdeMock.Controllers
{
    /// <summary>
    /// Mock implementations of the three JDE Orchestrator endpoints the Case
    /// Portal integration depends on. All endpoints require Basic Auth (see
    /// <see cref="BasicAuthenticationHandler"/>). The request/response shapes
    /// match production JDE exactly — including the trailing <c>jde__*</c>
    /// timing fields and the quirky " " / "null" sentinel values.
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

        public JdeOrchestratorController(MockDataService data, ILogger<JdeOrchestratorController> log)
        {
            _data = data;
            _log  = log;
        }

        // -------------------------------------------------------------------
        // POST /jderest/orchestrator/GetDepartmentList
        //   Request:  { "CompanyFilter": "00001" }
        //   Response: { "DepartmentList": [...], "jde__status": "SUCCESS", ... }
        // -------------------------------------------------------------------
        [HttpPost("GetDepartmentList")]
        public ActionResult<GetDepartmentListResponse> GetDepartmentList(
            [FromBody] GetDepartmentListRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation(
                "GetDepartmentList received  CompanyFilter='{Filter}'",
                request?.CompanyFilter);

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
        public ActionResult<GetEmployeeInfoByEmailResponse> GetEmployeeInfoByEmail(
            [FromBody] GetEmployeeInfoByEmailRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation(
                "GetEmployeeInfoByEmail received  Email='{Email}'",
                request?.Email);

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
        public ActionResult<GetJobsListResponse> GetJobsList(
            [FromBody] GetJobsListRequest request)
        {
            var start = JdeTime.NowUae();
            _log.LogInformation(
                "GetJobsList received  DepartmentFilter='{Filter}'",
                request?.DepartmentFilter);

            var list = _data.GetJobs(request?.DepartmentFilter);

            var resp = new GetJobsListResponse { JobsList = list };
            resp.SetTimings(start, JdeTime.NowUae());

            _log.LogInformation(
                "GetJobsList returned  {Count} job(s)  in {Seconds:N3}s",
                list.Count, resp.ServerExecutionSeconds);
            return Ok(resp);
        }
    }
}
