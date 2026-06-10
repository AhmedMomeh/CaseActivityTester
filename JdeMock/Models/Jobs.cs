using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ActivityTester.JdeMock.Models
{
    // ----- Request -----------------------------------------------------------

    public class GetJobsListRequest
    {
        [JsonPropertyName("DepartmentFilter")]
        public string DepartmentFilter { get; set; }
    }

    // ----- Response ----------------------------------------------------------

    public class GetJobsListResponse : JdeEnvelope
    {
        [JsonPropertyName("JobsList"), JsonPropertyOrder(1)]
        public List<JobEntry> JobsList { get; set; } = new();
    }

    // C# class is named "JobEntry" (not "Job") to avoid clashing with any other
    // "Job" type that may be pulled in transitively (e.g. Hangfire's Job). JSON
    // wire shape is controlled by the JsonPropertyName / JsonPropertyOrder
    // attributes — order matches production JDE row-by-row so a diff of mock
    // vs. real responses lines up cleanly.
    //
    // Production sample (reference):
    //   {
    //     "JobCode": "RE0029",
    //     "JobTitleEnglish": "Senior Officer - Business Development",
    //     "JobTitleArabic": " ",
    //     "Department": "101PPD",
    //     "DepartmentDescription": "Real Estate Division",
    //     "GradeLevel": "F2",
    //     "Grade": "F",
    //     "DepartmentOwner": 1370877,
    //     "DepartmentOwnerName": "AHMED MOUSA SAIF HUWAIR ALZAROONI",
    //     "DepartmentOwnerJobCode": "RE0001",
    //     "DepartmentOwnerJobTitle": "Chief Real Estate Officer"
    //   }
    public class JobEntry
    {
        [JsonPropertyName("JobCode"), JsonPropertyOrder(1)]
        public string JobCode { get; set; }

        [JsonPropertyName("JobTitleEnglish"), JsonPropertyOrder(2)]
        public string JobTitleEnglish { get; set; }

        // The real JDE returns a single space " " when the Arabic title isn't
        // populated, NOT an empty string and NOT null. Mirror that.
        [JsonPropertyName("JobTitleArabic"), JsonPropertyOrder(3)]
        public string JobTitleArabic { get; set; } = " ";

        [JsonPropertyName("Department"), JsonPropertyOrder(4)]
        public string Department { get; set; }

        [JsonPropertyName("DepartmentDescription"), JsonPropertyOrder(5)]
        public string DepartmentDescription { get; set; }

        // Production splits the grade into two fields:
        //   GradeLevel — full code including sub-level (e.g. "F2", "A1")
        //   Grade      — just the letter band       (e.g. "F", "A")
        // Mirror both — the Case forms may bind to either. " " (single space)
        // mirrors JDE's "no value" sentinel; never empty / null.
        [JsonPropertyName("GradeLevel"), JsonPropertyOrder(6)]
        public string GradeLevel { get; set; } = " ";

        [JsonPropertyName("Grade"), JsonPropertyOrder(7)]
        public string Grade { get; set; } = " ";

        // Department head — long ("Address Book number" in JDE-speak) plus
        // the head's name and their own job code/title. Real JDE returns 0
        // / " " when the department has no recorded head.
        [JsonPropertyName("DepartmentOwner"), JsonPropertyOrder(8)]
        public long DepartmentOwner { get; set; }

        [JsonPropertyName("DepartmentOwnerName"), JsonPropertyOrder(9)]
        public string DepartmentOwnerName { get; set; } = " ";

        [JsonPropertyName("DepartmentOwnerJobCode"), JsonPropertyOrder(10)]
        public string DepartmentOwnerJobCode { get; set; } = " ";

        [JsonPropertyName("DepartmentOwnerJobTitle"), JsonPropertyOrder(11)]
        public string DepartmentOwnerJobTitle { get; set; } = " ";
    }
}
