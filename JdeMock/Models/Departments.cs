using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ActivityTester.JdeMock.Models
{
    // ----- Request -----------------------------------------------------------

    public class GetDepartmentListRequest
    {
        [JsonPropertyName("CompanyFilter")]
        public string CompanyFilter { get; set; }
    }

    // ----- Response ----------------------------------------------------------

    public class GetDepartmentListResponse : JdeEnvelope
    {
        [JsonPropertyName("DepartmentList"), JsonPropertyOrder(1)]
        public List<DepartmentEntry> DepartmentList { get; set; } = new();
    }

    // C# class is named "DepartmentEntry" (not "Department") to avoid CS0542 —
    // a property named "Department" inside a class also named "Department" is a
    // compile error. The JSON wire format still uses "Department" via the
    // JsonPropertyName attribute below.
    public class DepartmentEntry
    {
        [JsonPropertyName("Department")]
        public string Department { get; set; }

        [JsonPropertyName("DepartmentDesc")]
        public string DepartmentDesc { get; set; }
    }
}
