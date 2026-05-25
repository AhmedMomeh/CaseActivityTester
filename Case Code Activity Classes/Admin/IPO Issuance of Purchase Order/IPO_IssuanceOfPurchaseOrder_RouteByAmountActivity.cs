using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;

namespace Shared.Activities
{
    public class IPO_IssuanceOfPurchaseOrder_RouteByAmountActivity : ActivityTemplate
    {
        // Based on the IPO template:
        //   Purchase Order path AND amount >= 25,000 -> route to CEO
        //   Otherwise                               -> go directly 
        private const decimal CEO_AMOUNT = 25000;

        public override void Execute(WorkflowItem workflowItem)
        {
            string purchaseType = GetProp(workflowItem, "purchaseType");
            decimal amount = ParseDecimal(GetProp(workflowItem, "totalAmount"));

            string nextApprovalRoute;
            if (string.Equals(purchaseType, "PurchaseOrder", StringComparison.OrdinalIgnoreCase)
                && amount >= CEO_AMOUNT)
            {
                nextApprovalRoute = "NeedCEO";
            }
            else
            {
                nextApprovalRoute = "Direct";
            }

            SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);
        }

        public override void Complete(WorkflowItem workflowItem) { }

        private static string GetProp(WorkflowItem item, string key)
        {
            try
            {
                var v = item?.Properties?[key]?.Value;
                return v == null ? string.Empty : Convert.ToString(v);
            }
            catch { return string.Empty; }
        }

        private static void SetProp(WorkflowItem item, string key, object value)
        {
            if (item == null || item.Properties == null) return;
            try
            {
                var prop = item.Properties[key];
                if (prop != null) prop.Value = value;
            }
            catch { }
        }

        private static decimal ParseDecimal(string s)
        {
            decimal d;
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return 0m;
        }
    }
}