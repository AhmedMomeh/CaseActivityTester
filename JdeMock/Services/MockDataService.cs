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

        // -------- Nationalities (ISO 3166-1 alpha-2 subset, formatted to match JDE) --------
        // The first batch is the 23 entries the user supplied verbatim (A* → BH);
        // the rest covers the common nationalities a UAE-based HR portal sees in
        // practice. Add/remove rows here to extend the mock dataset.
        private readonly List<NationalityEntry> _nationalities = new()
        {
            new() { Code = "AD", Description = "Andorra" },
            new() { Code = "AE", Description = "United Arab Emirates (UAE)" },
            new() { Code = "AF", Description = "Afghanistan" },
            new() { Code = "AG", Description = "Antigua and Barbuda" },
            new() { Code = "AI", Description = "Anguilla" },
            new() { Code = "AL", Description = "Albania" },
            new() { Code = "AM", Description = "Armenia" },
            new() { Code = "AN", Description = "Netherlands Antilles" },
            new() { Code = "AO", Description = "Angola" },
            new() { Code = "AQ", Description = "Antarctica" },
            new() { Code = "AR", Description = "Argentina" },
            new() { Code = "AS", Description = "American Samoa" },
            new() { Code = "AT", Description = "Austria" },
            new() { Code = "AU", Description = "Australia" },
            new() { Code = "AW", Description = "Aruba" },
            new() { Code = "AZ", Description = "Azerbaijan" },
            new() { Code = "BA", Description = "Bosnia and Herzegovina" },
            new() { Code = "BB", Description = "Barbados" },
            new() { Code = "BD", Description = "Bangladesh" },
            new() { Code = "BE", Description = "Belgium" },
            new() { Code = "BF", Description = "Burkina Faso" },
            new() { Code = "BG", Description = "Bulgaria" },
            new() { Code = "BH", Description = "Bahrain" },
            new() { Code = "BI", Description = "Burundi" },
            new() { Code = "BJ", Description = "Benin" },
            new() { Code = "BN", Description = "Brunei Darussalam" },
            new() { Code = "BO", Description = "Bolivia" },
            new() { Code = "BR", Description = "Brazil" },
            new() { Code = "BS", Description = "Bahamas" },
            new() { Code = "BT", Description = "Bhutan" },
            new() { Code = "BW", Description = "Botswana" },
            new() { Code = "BY", Description = "Belarus" },
            new() { Code = "BZ", Description = "Belize" },
            new() { Code = "CA", Description = "Canada" },
            new() { Code = "CD", Description = "Democratic Republic of the Congo" },
            new() { Code = "CF", Description = "Central African Republic" },
            new() { Code = "CG", Description = "Congo" },
            new() { Code = "CH", Description = "Switzerland" },
            new() { Code = "CI", Description = "Côte d'Ivoire" },
            new() { Code = "CL", Description = "Chile" },
            new() { Code = "CM", Description = "Cameroon" },
            new() { Code = "CN", Description = "China" },
            new() { Code = "CO", Description = "Colombia" },
            new() { Code = "CR", Description = "Costa Rica" },
            new() { Code = "CU", Description = "Cuba" },
            new() { Code = "CV", Description = "Cabo Verde" },
            new() { Code = "CY", Description = "Cyprus" },
            new() { Code = "CZ", Description = "Czech Republic" },
            new() { Code = "DE", Description = "Germany" },
            new() { Code = "DJ", Description = "Djibouti" },
            new() { Code = "DK", Description = "Denmark" },
            new() { Code = "DM", Description = "Dominica" },
            new() { Code = "DO", Description = "Dominican Republic" },
            new() { Code = "DZ", Description = "Algeria" },
            new() { Code = "EC", Description = "Ecuador" },
            new() { Code = "EE", Description = "Estonia" },
            new() { Code = "EG", Description = "Egypt" },
            new() { Code = "ER", Description = "Eritrea" },
            new() { Code = "ES", Description = "Spain" },
            new() { Code = "ET", Description = "Ethiopia" },
            new() { Code = "FI", Description = "Finland" },
            new() { Code = "FJ", Description = "Fiji" },
            new() { Code = "FR", Description = "France" },
            new() { Code = "GA", Description = "Gabon" },
            new() { Code = "GB", Description = "United Kingdom" },
            new() { Code = "GE", Description = "Georgia" },
            new() { Code = "GH", Description = "Ghana" },
            new() { Code = "GR", Description = "Greece" },
            new() { Code = "HK", Description = "Hong Kong" },
            new() { Code = "HR", Description = "Croatia" },
            new() { Code = "HU", Description = "Hungary" },
            new() { Code = "ID", Description = "Indonesia" },
            new() { Code = "IE", Description = "Ireland" },
            new() { Code = "IL", Description = "Israel" },
            new() { Code = "IN", Description = "India" },
            new() { Code = "IQ", Description = "Iraq" },
            new() { Code = "IR", Description = "Iran" },
            new() { Code = "IS", Description = "Iceland" },
            new() { Code = "IT", Description = "Italy" },
            new() { Code = "JM", Description = "Jamaica" },
            new() { Code = "JO", Description = "Jordan" },
            new() { Code = "JP", Description = "Japan" },
            new() { Code = "KE", Description = "Kenya" },
            new() { Code = "KH", Description = "Cambodia" },
            new() { Code = "KR", Description = "South Korea" },
            new() { Code = "KW", Description = "Kuwait" },
            new() { Code = "KZ", Description = "Kazakhstan" },
            new() { Code = "LB", Description = "Lebanon" },
            new() { Code = "LK", Description = "Sri Lanka" },
            new() { Code = "LU", Description = "Luxembourg" },
            new() { Code = "LY", Description = "Libya" },
            new() { Code = "MA", Description = "Morocco" },
            new() { Code = "MX", Description = "Mexico" },
            new() { Code = "MY", Description = "Malaysia" },
            new() { Code = "NG", Description = "Nigeria" },
            new() { Code = "NL", Description = "Netherlands" },
            new() { Code = "NO", Description = "Norway" },
            new() { Code = "NP", Description = "Nepal" },
            new() { Code = "NZ", Description = "New Zealand" },
            new() { Code = "OM", Description = "Oman" },
            new() { Code = "PH", Description = "Philippines" },
            new() { Code = "PK", Description = "Pakistan" },
            new() { Code = "PL", Description = "Poland" },
            new() { Code = "PT", Description = "Portugal" },
            new() { Code = "QA", Description = "Qatar" },
            new() { Code = "RO", Description = "Romania" },
            new() { Code = "RU", Description = "Russia" },
            new() { Code = "SA", Description = "Saudi Arabia" },
            new() { Code = "SD", Description = "Sudan" },
            new() { Code = "SE", Description = "Sweden" },
            new() { Code = "SG", Description = "Singapore" },
            new() { Code = "SY", Description = "Syria" },
            new() { Code = "TH", Description = "Thailand" },
            new() { Code = "TN", Description = "Tunisia" },
            new() { Code = "TR", Description = "Türkiye" },
            new() { Code = "UA", Description = "Ukraine" },
            new() { Code = "UG", Description = "Uganda" },
            new() { Code = "US", Description = "United States" },
            new() { Code = "UZ", Description = "Uzbekistan" },
            new() { Code = "VE", Description = "Venezuela" },
            new() { Code = "VN", Description = "Viet Nam" },
            new() { Code = "YE", Description = "Yemen" },
            new() { Code = "ZA", Description = "South Africa" },
            new() { Code = "ZM", Description = "Zambia" },
            new() { Code = "ZW", Description = "Zimbabwe" },
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

        // No filter on nationalities — real JDE returns the full ISO list. The
        // mock returns the full seeded set above; the request body is currently
        // ignored.
        public List<NationalityEntry> GetNationalities() => _nationalities;

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
