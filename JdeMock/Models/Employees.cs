using System.Text.Json.Serialization;

namespace ActivityTester.JdeMock.Models
{
    // ----- Request -----------------------------------------------------------

    public class GetEmployeeInfoByEmailRequest
    {
        [JsonPropertyName("Email")]
        public string Email { get; set; }
    }

    // ----- Response ----------------------------------------------------------
    // The real JDE Orchestrator returns the literal STRING "null" (not a JSON
    // null) when an employee isn't found — including for the DepartmentDescription
    // field — so we mirror that behavior. DepartmentCode is the exception: it
    // returns an empty string, not "null". This is intentional, not a bug.

    public class GetEmployeeInfoByEmailResponse : JdeEnvelope
    {
        [JsonPropertyName("Email"), JsonPropertyOrder(1)]
        public string Email { get; set; }

        [JsonPropertyName("FileNo"), JsonPropertyOrder(2)]
        public int FileNo { get; set; }

        [JsonPropertyName("Name"), JsonPropertyOrder(3)]
        public string Name { get; set; }

        [JsonPropertyName("JobCode"), JsonPropertyOrder(4)]
        public string JobCode { get; set; }

        [JsonPropertyName("JobDesc"), JsonPropertyOrder(5)]
        public string JobDesc { get; set; }

        [JsonPropertyName("DepartmentCode"), JsonPropertyOrder(6)]
        public string DepartmentCode { get; set; }

        [JsonPropertyName("DepartmentDescription"), JsonPropertyOrder(7)]
        public string DepartmentDescription { get; set; }

        [JsonPropertyName("IsHR"), JsonPropertyOrder(8)]
        public bool IsHR { get; set; }
    }
}
