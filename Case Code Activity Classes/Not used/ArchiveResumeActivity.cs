using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Activities
{
    internal class ArchiveResumeActivity : ActivityTemplate
    {
        public override void Complete(WorkflowItem workflowItem)
        {
            // No-op: this activity is fully automated.
        }

        public override void Execute(WorkflowItem workflowItem)
        {
            string DocumentId = workflowItem.Properties["DocumentId"].Value.ToString();
            Document document = new Document().Find(Convert.ToInt64(DocumentId));
            document.StatusId = 14;  //Approved
            document.Update();
        }
    }
}
