using ActivityTester.JdeMock.Models;
using System.Collections.Generic;
using System.Linq;

namespace ActivityTester.JdeMock.Services
{
    /// <summary>
    /// In-memory dataset that backs the three mock endpoints. The data is hand-
    /// seeded to cover the typical "happy path" Case Portal integration tests
    /// expect to see (a few departments, jobs in the IT department, a known
    /// employee and an unknown one).
    ///
    /// Real JDE has thousands of rows; the mock has just enough to exercise
    /// filtering. To extend, add entries to the lists below.
    /// </summary>
    public sealed class MockDataService
    {
        // -------- Departments --------
        private readonly List<DepartmentEntry> _departments = new()
        {
            new() { Department = "101GM", DepartmentDesc = "CEO Office" },
            new() { Department = "101IA", DepartmentDesc = "Internal Auditor's Office" },
            new() { Department = "101IT", DepartmentDesc = "Information Technology Dept." },
            new() { Department = "101HR", DepartmentDesc = "Human Resources Dept." },
            new() { Department = "101FN", DepartmentDesc = "Finance Dept." },
        };

        // -------- Jobs --------
        private readonly List<JobEntry> _jobs = new()
        {
            new() { JobCode = "IT132", JobTitleEnglish = "Assistant Manager Cloud & Infrastructure",
                    Department = "101IT", DepartmentDescription = "Information Technology Dept." , PayGrade="A"},
            new() { JobCode = "IT131", JobTitleEnglish = "Senior Manager - Architecture Enterprise",
                    Department = "101IT", DepartmentDescription = "Information Technology Dept." , PayGrade="B"},
            new() { JobCode = "HR101", JobTitleEnglish = "Senior HR Business Partner",
                    Department = "101HR", DepartmentDescription = "Human Resources Dept." , PayGrade="C"},
            new() { JobCode = "FN201", JobTitleEnglish = "Finance Analyst",
                    Department = "101FN", DepartmentDescription = "Finance Dept." , PayGrade = "D"},
        };

        // -------- Employees (known ones — anything else returns the JDE "null" placeholder) --------
        private readonly Dictionary<string, GetEmployeeInfoByEmailResponse> _knownEmployees =
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                ["admin@unioncoop.ae"] = new GetEmployeeInfoByEmailResponse
                {
                    FileNo = 1001,
                    Name = "Admin User",
                    JobCode = "IT132",
                    JobDesc = "Assistant Manager Cloud & Infrastructure",
                    DepartmentCode = "101IT",
                    DepartmentDescription = "Information Technology Dept.",
                    IsHR = false,
                },
                ["hr.head@unioncoop.ae"] = new GetEmployeeInfoByEmailResponse
                {
                    FileNo = 2050,
                    Name = "HR Head",
                    JobCode = "HR101",
                    JobDesc = "Senior HR Business Partner",
                    DepartmentCode = "101HR",
                    DepartmentDescription = "Human Resources Dept.",
                    IsHR = true,
                },
            };

        // -------- Public API --------

        // CompanyFilter is accepted but currently doesn't filter — the seed
        // data is all "00001". Real JDE returns rows scoped by company; the
        // mock returns everything for simplicity.
        public List<DepartmentEntry> GetDepartments(string companyFilter) => _departments;

        // Filters jobs by department code. Empty/null filter returns everything.
        public List<JobEntry> GetJobs(string departmentFilter)
        {
            if (string.IsNullOrWhiteSpace(departmentFilter)) return _jobs;
            return _jobs.Where(j => string.Equals(j.Department, departmentFilter,
                                                  System.StringComparison.OrdinalIgnoreCase))
                        .ToList();
        }

        // Returns the employee record by email, or the JDE "not found" sentinel:
        // string fields set to the literal "null" (not a JSON null), FileNo=0,
        // IsHR=false. This matches what the production JDE Orchestrator returns
        // for unknown emails.
        public GetEmployeeInfoByEmailResponse GetEmployee(string email)
        {
            if (!string.IsNullOrEmpty(email) && _knownEmployees.TryGetValue(email, out var hit))
            {
                // Don't return the cached instance directly — callers mutate jde__ timestamps.
                return new GetEmployeeInfoByEmailResponse
                {
                    Email = email,
                    FileNo = hit.FileNo,
                    Name = hit.Name,
                    JobCode = hit.JobCode,
                    JobDesc = hit.JobDesc,
                    DepartmentCode = hit.DepartmentCode,
                    DepartmentDescription = hit.DepartmentDescription,
                    IsHR = hit.IsHR,
                };
            }

            return new GetEmployeeInfoByEmailResponse
            {
                Email = email ?? "",
                FileNo = 0,
                Name = "null",
                JobCode = "null",
                JobDesc = "null",
                DepartmentCode = "",
                DepartmentDescription = "null",
                IsHR = false,
            };
        }
    }
}
