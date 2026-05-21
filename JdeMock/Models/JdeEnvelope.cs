using System;
using System.Text.Json.Serialization;

namespace ActivityTester.JdeMock.Models
{
    /// <summary>
    /// Common trailing fields every JDE Orchestrator response carries:
    /// <c>jde__status</c>, <c>jde__startTimestamp</c>, <c>jde__endTimestamp</c>,
    /// <c>jde__serverExecutionSeconds</c>.
    ///
    /// Derived response classes add their own payload properties (DepartmentList,
    /// JobsList, Email/Name/…). The envelope properties use <c>JsonPropertyOrder(100)</c>
    /// so they serialize AFTER the payload — matching the real JDE Orchestrator
    /// output where the business fields appear first and the jde__ fields trail.
    /// </summary>
    public abstract class JdeEnvelope
    {
        [JsonPropertyName("jde__status"), JsonPropertyOrder(100)]
        public string Status { get; set; } = "SUCCESS";

        [JsonPropertyName("jde__startTimestamp"), JsonPropertyOrder(101)]
        public string StartTimestamp { get; set; } = "";

        [JsonPropertyName("jde__endTimestamp"), JsonPropertyOrder(102)]
        public string EndTimestamp { get; set; } = "";

        [JsonPropertyName("jde__serverExecutionSeconds"), JsonPropertyOrder(103)]
        public double ServerExecutionSeconds { get; set; }

        public void SetTimings(DateTimeOffset start, DateTimeOffset end)
        {
            StartTimestamp = JdeTime.Format(start);
            EndTimestamp   = JdeTime.Format(end);
            ServerExecutionSeconds = Math.Round((end - start).TotalSeconds, 3);
        }
    }
}
