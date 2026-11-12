using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace IsolatedSwBridge
{
    internal sealed partial class CadAdapter
    {
        private void InspectConstraints(DocEntry document, Dictionary<string, object> details, List<object> checks)
        {
            if (document.Doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                checks.Add(J.Check("ASSEMBLY_CONSTRAINTS", "not_run", "document type", "Constraint readback applies to assemblies."));
                return;
            }
            var components = ((IAssemblyDoc)document.Doc).GetComponents(false) as object[] ?? new object[0];
            details["component_count"] = components.Length;
            details["constraint_context"] = "model_state_not_motion_timeline";
            details["component_constraints"] = components.Take(100).Cast<IComponent2>().Select(component => {
                int status = component.GetConstrainedStatus();
                var transform = component.Transform2;
                return J.Obj("name", component.Name2, "path", component.GetPathName(), "fixed", component.IsFixed(),
                    "suppression_status", component.GetSuppression(), "constraint_status", status,
                    "constraint_status_name", Enum.GetName(typeof(swConstrainedStatus_e), status) ?? "unknown",
                    "transform_data", transform == null ? null : transform.ArrayData);
            }).ToArray();
            checks.Add(J.Check("ASSEMBLY_CONSTRAINTS", "pass", "native component constraint states",
                "Readback only; native status is not a degree-of-freedom count, motion test or interference check."));
            if (components.Length > 100) checks.Add(J.Check("CONSTRAINT_LIMIT", "not_run", "bounded component readback", "Only the first 100 components were inspected."));
        }
    }
}
