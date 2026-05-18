using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;

namespace Shared.Activities
{
    public class RFQ_RequestforQuotation_RouteByAmountActivity : ActivityTemplate
    {
        private const decimal SEALED_THRESHOLD = 100000;

        public override void Execute(WorkflowItem workflowItem)
        {
            decimal amount = ParseDecimal(GetProp(workflowItem, "estimatedAmount"));
            bool isSealed = amount >= SEALED_THRESHOLD;

            SetProp(workflowItem, "quotationPath", isSealed ? "Sealed" : "Email");
            SetProp(workflowItem, "rfqType", isSealed ? "SealedEnvelope" : "Email");
            SetProp(workflowItem, "receivingMethod", isSealed ? "Physical" : "Email");
        }

        public override void Complete(WorkflowItem workflowItem) { }

        private static string GetProp(WorkflowItem i, string k)
        {
            try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }

        private static void SetProp(WorkflowItem i, string k, object v)
        { if (i?.Properties == null) return; try { var p = i.Properties[k]; if (p != null) p.Value = v; } catch { } }

        private static decimal ParseDecimal(string s)
        { decimal d; return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0m; }
    }
}