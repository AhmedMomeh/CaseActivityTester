using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ActivityTester.JdeMock.Models
{
    // ----- Request -----------------------------------------------------------
    // Empty body in production JDE — kept as a class (instead of just `object`)
    // so the controller can still bind cleanly and future filter fields can be
    // added here without changing the route signature.

    public class GetNationalitiesListRequest { }

    // ----- Response ----------------------------------------------------------

    public class GetNationalitiesListResponse : JdeEnvelope
    {
        [JsonPropertyName("NationalitiesList"), JsonPropertyOrder(1)]
        public List<NationalityEntry> NationalitiesList { get; set; } = new();
    }

    public class NationalityEntry
    {
        [JsonPropertyName("Code")]
        public string Code { get; set; }

        [JsonPropertyName("Description")]
        public string Description { get; set; }
    }
}
