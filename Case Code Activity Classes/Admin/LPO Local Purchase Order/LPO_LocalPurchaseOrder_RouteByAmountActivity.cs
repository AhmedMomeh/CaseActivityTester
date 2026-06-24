using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;

namespace Shared.Activities
{
    public class LPO_LocalPurchaseOrder_RouteByAmountActivity : ActivityTemplate
    {
        // Final approval rules:
        //   Contract        -> CBSO only (already done)           -> "Direct"
        //   Non-Contract, > 25,000 -> needs CEO                   -> "NeedsCEO"
        //   Non-Contract, <= 25,000 -> CBSO only (already done)   -> "Direct"
        private const decimal CEO_THRESHOLD = 25000;

        public override void Execute(WorkflowItem workflowItem)
        {
            string poType = GetProp(workflowItem, "purchaseOrderType");
            decimal amount = ParseDecimal(GetProp(workflowItem, "totalAmount"));

            string nextApprovalRoute =
                string.Equals(poType, "NonContract", StringComparison.OrdinalIgnoreCase)
                && amount >= CEO_THRESHOLD
                    ? "NeedCEO"
                    : "Direct";

            SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);
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