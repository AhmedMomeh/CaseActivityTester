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
    // wire shape is controlled by the JsonPropertyName attributes.
    public class JobEntry
    {
        [JsonPropertyName("JobCode")]
        public string JobCode { get; set; }

        [JsonPropertyName("JobTitleEnglish")]
        public string JobTitleEnglish { get; set; }

        // The real JDE returns a single space " " when the Arabic title isn't
        // populated, NOT an empty string and NOT null. Mirror that.
        [JsonPropertyName("JobTitleArabic")]
        public string JobTitleArabic { get; set; } = " ";

        [JsonPropertyName("Department")]
        public string Department { get; set; }

        [JsonPropertyName("DepartmentDescription")]
        public string DepartmentDescription { get; set; }

        // Same convention as JobTitleArabic — single space, not empty.
        [JsonPropertyName("PayGrade")]
        public string PayGrade { get; set; } = " ";
    }
}
