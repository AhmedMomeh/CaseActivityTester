using System.Text.Json.Serialization;

namespace ActivityTester.JdeMock.Models
{
    // Mirrors the staging endpoint:
    //   POST https://ucuatais01.unioncoop.ae/jderest/orchestrator/GetEmployeeInfo
    //
    // Lookup key is the JDE StaffID (also called "FileNo" in the by-email
    // endpoint). Response shape mirrors production verbatim, including the
    // JDE convention of returning a string-typed payload with JSON null only
    // for known-missing dates (ProbationDate).

    // ----- Request -----------------------------------------------------------

    public class GetEmployeeInfoRequest
    {
        [JsonPropertyName("StaffID")]
        public string StaffID { get; set; }
    }

    // ----- Response ----------------------------------------------------------

    public class GetEmployeeInfoResponse : JdeEnvelope
    {
        [JsonPropertyName("EmployeeName"), JsonPropertyOrder(1)]
        public string EmployeeName { get; set; }

        // "Designation" carries the JOB CODE (e.g. "HR099"); the human-
        // readable label is in DesignationDesc. Real JDE keeps the two
        // separate so callers can render the code or the label per context.
        [JsonPropertyName("Designation"), JsonPropertyOrder(2)]
        public string Designation { get; set; }

        [JsonPropertyName("DesignationDesc"), JsonPropertyOrder(3)]
        public string DesignationDesc { get; set; }

        // Dates are formatted dd/MM/yyyy by production JDE - no timezone,
        // no time-of-day component. Stay string-typed to match the wire shape.
        [JsonPropertyName("DOJ"), JsonPropertyOrder(4)]
        public string DOJ { get; set; }

        [JsonPropertyName("DepartmentOrBranch"), JsonPropertyOrder(5)]
        public string DepartmentOrBranch { get; set; }

        [JsonPropertyName("DepartmentOrBranchName"), JsonPropertyOrder(6)]
        public string DepartmentOrBranchName { get; set; }

        [JsonPropertyName("DateInCurrentPosition"), JsonPropertyOrder(7)]
        public string DateInCurrentPosition { get; set; }

        // Production splits the grade into two fields:
        //   GradeLevel — full code including sub-level (e.g. "F1", "A1")
        //   Grade      — just the letter band       (e.g. "F", "A")
        [JsonPropertyName("GradeLevel"), JsonPropertyOrder(8)]
        public string GradeLevel { get; set; }

        [JsonPropertyName("Grade"), JsonPropertyOrder(9)]
        public string Grade { get; set; }

        // Real JDE returns a JSON null (NOT the string "null") for this field
        // when there's no probation - which is why this property is a
        // reference type with no default. JsonSerializer emits `null` for
        // C# null, matching the production wire shape.
        [JsonPropertyName("ProbationDate"), JsonPropertyOrder(10)]
        public string ProbationDate { get; set; }
    }
}
