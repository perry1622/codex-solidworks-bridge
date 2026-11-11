using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace IsolatedSwBridge
{
    internal sealed class DocEntry
    {
        public string Id;
        public IModelDoc2 Doc;
        public Dictionary<string, string> Dimensions = new Dictionary<string, string>();
        public Dictionary<string, int> DimensionTypes = new Dictionary<string, int>();
    }
    internal sealed class Candidate
    {
        public ISldWorks App;
        public int Pid;
        public string Moniker;
    }
    internal sealed partial class CadAdapter
    {
        [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
        [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
        private readonly Options options;
        private ISldWorks app;
        private int pid;
        private long processStart;
        private readonly Dictionary<string, DocEntry> docs = new Dictionary<string, DocEntry>();
        public string SessionId { get; private set; }
        public string WriteLockIdentity { get { return pid + "_" + processStart; } }
        public CadAdapter(Options o) { options = o; Api = new ApiCatalog(o); }

        private List<Candidate> Enumerate()
        {
            var found = new List<Candidate>();
            IRunningObjectTable rot = null; IBindCtx ctx = null; IEnumMoniker enumerator = null;
            try
            {
                Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out rot));
                Marshal.ThrowExceptionForHR(CreateBindCtx(0, out ctx));
                rot.EnumRunning(out enumerator);
                var one = new IMoniker[1];
                while (enumerator.Next(1, one, IntPtr.Zero) == 0)
                {
                    try
                    {
                        string name; one[0].GetDisplayName(ctx, null, out name);
                        if (!Regex.IsMatch(name, @"(?:^|[!:])SolidWorks_PID_\d+$", RegexOptions.IgnoreCase)) continue;
                        object value; rot.GetObject(one[0], out value);
                        var sw = value as ISldWorks;
                        if (sw == null) continue;
                        int actualPid = sw.GetProcessID();
                        if (!found.Any(c => c.Pid == actualPid)) found.Add(new Candidate { App = sw, Pid = actualPid, Moniker = name });
                    }
                    catch (COMException) { }
                    finally { if (one[0] != null) { Marshal.ReleaseComObject(one[0]); one[0] = null; } }
                }
            }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                if (ctx != null) Marshal.ReleaseComObject(ctx);
                if (rot != null) Marshal.ReleaseComObject(rot);
            }
            // GetActiveObject only attaches to a registered object. It never creates SolidWorks.
            if (found.Count == 0)
            {
                try
                {
                    var sw = Marshal.GetActiveObject("SldWorks.Application") as ISldWorks;
                    if (sw != null) found.Add(new Candidate { App = sw, Pid = sw.GetProcessID(), Moniker = "GetActiveObject" });
                }
                catch (COMException) { }
            }
            return found;
        }

        private static Dictionary<string, object> Describe(Candidate c)
        { return J.Obj("instance_id", "solidworks-pid-" + c.Pid, "pid", c.Pid, "solidworks_revision", c.App.RevisionNumber()); }

        private bool StillConnected()
        {
            if (app == null) return false;
            try
            {
                if (app.GetProcessID() == pid && Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks == processStart) return true;
            }
            catch { }
            app = null; SessionId = null; docs.Clear(); ResetApiObjects(); return false;
        }
        public Dictionary<string, object> Status()
        {
            var candidates = new List<Candidate>();
            if (!StillConnected())
            {
                candidates = Enumerate();
                var selected = options.Pid.HasValue ? candidates.Where(c => c.Pid == options.Pid.Value).ToList() : candidates;
                if (selected.Count == 1)
                {
                    app = selected[0].App; pid = selected[0].Pid;
                    processStart = Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks;
                    SessionId = Guid.NewGuid().ToString("D"); docs.Clear(); ResetApiObjects();
                }
                else
                {
                    return J.Obj("connection", selected.Count > 1 ? "ambiguous" : "disconnected", "instance", null,
                        "candidates", candidates.Select(Describe).ToArray(), "busy", false,
                        "busy_scope", "bridge_queue_only", "documents", new object[0],
                        "capabilities", Capabilities(), "observed_at", J.Now());
                }
            }
            RefreshDocuments();
            var activeDocument = app.IActiveDoc2 as IModelDoc2;
            var activeEntry = docs.Values.FirstOrDefault(d => Object.ReferenceEquals(d.Doc, activeDocument));
            return J.Obj("connection", "connected", "instance", Describe(new Candidate { App = app, Pid = pid }),
                "candidates", new object[0], "busy", false, "busy_scope", "bridge_queue_only",
                "documents", docs.Values.Select(Summary).ToArray(), "active_document_id", activeEntry == null ? null : activeEntry.Id,
                "capabilities", Capabilities(), "observed_at", J.Now());
        }
        private object Capabilities()
        {
            return J.Obj("actions", options.AllowWrite ? new[] { "set_parameters", "export_document", "assemble_demo", "insert_component_with_mates", "activate_document", "api_batch" } : new string[0],
                "recipe_versions", new string[0], "export_formats", options.AllowWrite ? new[] { "parasolid", "step", "stl", "png" } : new string[0],
                "native_motion_study", false, "write_enabled", options.AllowWrite,
                "write_validation", "see_package_validation_report", "api_catalog", true,
                "api_invocation", options.AllowWrite, "api_invocation_mode", "live_objects_raw_official_units_no_automatic_copy_or_rollback");
        }
        public void RequireSession(string session)
        {
            if (!StillConnected() || session != SessionId) throw new Fault("SW_DISCONNECTED", "Session is absent, disconnected or stale. Call sw_status and inspect again.");
        }
        private void RefreshDocuments()
        {
            var raw = app.GetDocuments() as object[] ?? new object[0];
            var live = new HashSet<string>();
            foreach (var item in raw)
            {
                var document = item as IModelDoc2;
                if (document == null) continue;
                DocEntry existing = docs.Values.FirstOrDefault(d => Object.ReferenceEquals(d.Doc, document));
                if (existing == null)
                {
                    existing = new DocEntry { Id = "doc-" + Guid.NewGuid().ToString("N"), Doc = document };
                    docs.Add(existing.Id, existing);
                }
                live.Add(existing.Id);
            }
            foreach (string key in docs.Keys.Where(k => !live.Contains(k)).ToArray()) docs.Remove(key);
        }
        private string Configuration(IModelDoc2 doc)
        {
            if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING) return null;
            var config = doc.ConfigurationManager.ActiveConfiguration;
            return config == null ? null : config.Name;
        }
        private string Revision(DocEntry d)
        {
            string path = d.Doc.GetPathName();
            string stamp = File.Exists(path) ? new FileInfo(path).LastWriteTimeUtc.Ticks + ":" + new FileInfo(path).Length : "unsaved";
            return "rev-" + J.Hash(SessionId + "|" + d.Id + "|" + d.Doc.GetUpdateStamp() + "|" + d.Doc.GetSaveFlag() + "|" + Configuration(d.Doc) + "|" + path + "|" + stamp).Substring(0, 32);
        }
        private Dictionary<string, object> Summary(DocEntry d)
        {
            string path = d.Doc.GetPathName(); int kind = d.Doc.GetType();
            bool readOnly = !String.IsNullOrEmpty(path) && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            return J.Obj("document_id", d.Id, "path", String.IsNullOrEmpty(path) ? null : path,
                "document_type", kind == 1 ? "part" : kind == 2 ? "assembly" : "drawing",
                "configuration", Configuration(d.Doc), "revision", Revision(d), "dirty", d.Doc.GetSaveFlag(), "read_only", readOnly);
        }
        private DocEntry Find(string id)
        {
            RefreshDocuments(); DocEntry d;
            if (!docs.TryGetValue(id, out d)) throw new Fault("DOC_NOT_FOUND", "Document is closed or its identifier is stale.");
            return d;
        }
        public void Preconditions(Dictionary<string, object> args)
        {
            RequireSession(J.Str(args, "session_id"));
            var seen = new HashSet<string>();
            foreach (var x in J.Items(J.Get(args, "expected_documents")))
            {
                var e = J.Map(x); J.Keys(e, "document_id", "revision");
                string id = J.Str(e, "document_id");
                if (!seen.Add(id)) throw new Fault("VALIDATION_FAILED", "Duplicate document precondition.");
                var doc = Find(id);
                if (Revision(doc) != J.Str(e, "revision")) throw new Fault("DOC_REVISION_MISMATCH", "Document changed after inspection: " + id);
            }
            var action = J.Map(J.Get(args, "action"));
            if (J.Str(action, "type") == "api_batch") { ApiPreconditions(args); return; }
            string documentId = J.Str(action, "document_id");
            if (!seen.Contains(documentId)) throw new Fault("VALIDATION_FAILED", "Target document must be included in expected_documents.");
            var target = Find(documentId);
            if (J.Str(action, "type") == "activate_document") return;
            if (J.Str(action, "type") == "assemble_demo") { DemoPreconditions(target); return; }
            if (J.Str(action, "type") == "insert_component_with_mates") { PinPreconditions(target, action); return; }
            if (target.Doc.GetSaveFlag()) throw new Fault("DOC_DIRTY", "Target has unsaved changes; source is preserved.");
            if (target.Doc.SketchManager.ActiveSketch != null) throw new Fault("SKETCH_ACTIVE", "A sketch is being edited; source is preserved.");
            if (target.Doc.GetType() != 1) throw new Fault("UNSUPPORTED_ACTION", "Version 0.1 writes only independent saved part documents.");
            string source = target.Doc.GetPathName();
            if (String.IsNullOrEmpty(source) || !File.Exists(source)) throw new Fault("DOC_NOT_FOUND", "A saved native part is required.");
            if (target.Doc.Extension.ListExternalFileReferencesCount() != 0) throw new Fault("UNSUPPORTED_ACTION", "Parts with external references are not written by this version.");
            if ((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0) throw new Fault("DOC_READONLY", "Source is read-only.");
            string type = J.Str(action, "type");
            if (type == "set_parameters") ResolveChanges(target, action);
        }

        private static double ReadDimension(IDimension dimension)
        {
            var values = dimension.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[];
            if (values == null || values.Length != 1) throw new Fault("API_FAILED", "Could not read one dimension value.");
            return values[0];
        }
        private static bool Angular(int type) { return type == (int)swDimensionType_e.swAngularDimension || type == (int)swDimensionType_e.swAngularOrdinateDimension; }
        private static bool Linear(int type)
        {
            return new[] { 1, 2, 4, 5, 6, 7, 8, 9, 11, 12, 14, 15 }.Contains(type);
        }
        public Dictionary<string, object> Inspect(Dictionary<string, object> args)
        {
            RequireSession(J.Str(args, "session_id"));
            var d = Find(J.Str(args, "document_id")); string before = Revision(d);
            var scopes = new HashSet<string>((J.Get(args, "scopes") == null ? new object[] { "summary" } : J.Items(args["scopes"])).Select(x => x as string));
            if (scopes.Count == 0 || (J.Get(args, "scopes") != null && scopes.Count != J.Items(args["scopes"]).Length))
                throw new Fault("VALIDATION_FAILED", "Inspection scopes must be nonempty and unique.");
            if (scopes.Any(s => !new[] { "summary", "geometry", "parameters", "references", "mates", "checks", "snapshot" }.Contains(s)))
                throw new Fault("VALIDATION_FAILED", "Unknown inspection scope.");
            var parameters = new List<object>(); var references = new List<object>(); var checks = new List<object>();
            object geometry = null; var details = new Dictionary<string, object>();
            if (scopes.Contains("geometry"))
            {
                if (d.Doc.GetType() == 1)
                {
                    var bodies = ((IPartDoc)d.Doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? new object[0];
                    double volume = 0; double[] bounds = null;
                    foreach (var raw in bodies)
                    {
                        var body = (IBody2)raw; var mass = body.GetMassProperties(1) as double[];
                        if (mass == null || mass.Length < 4) throw new Fault("API_FAILED", "Body mass properties unavailable.");
                        volume += mass[3] * 1e9;
                        var box = body.GetBodyBox() as double[];
                        if (box == null || box.Length != 6) throw new Fault("API_FAILED", "Body bounds unavailable.");
                        if (bounds == null) bounds = (double[])box.Clone();
                        else for (int k = 0; k < 3; k++) { bounds[k] = Math.Min(bounds[k], box[k]); bounds[k + 3] = Math.Max(bounds[k + 3], box[k + 3]); }
                    }
                    geometry = J.Obj("solid_body_count", bodies.Length, "volume_mm3", volume,
                        "bbox_mm", bounds == null ? null : bounds.Select(v => v * 1000).ToArray(), "frame", "component_frame");
                    details["surfaces"] = DescribePartSurfaces(d.Doc);
                    details["configurations"] = d.Doc.GetConfigurationNames();
                }
                else if (d.Doc.GetType() == 2)
                {
                    var components = ((IAssemblyDoc)d.Doc).GetComponents(false) as object[] ?? new object[0];
                    details["components"] = components.Take(100).Select(raw => DescribeAssemblyComponent(d, (IComponent2)raw)).ToArray();
                    details["component_count"] = components.Length;
                }
            }
            if (scopes.Contains("parameters") || scopes.Contains("references") || scopes.Contains("checks"))
            {
                var features = new List<object>(); var dimensionNames = new HashSet<string>();
                var feature = d.Doc.FirstFeature() as IFeature;
                int count = 0;
                while (feature != null && count++ < 1000)
                {
                    if (scopes.Contains("checks"))
                    {
                        bool warning; int code = feature.GetErrorCode2(out warning);
                        features.Add(J.Obj("name", feature.Name, "type", feature.GetTypeName2(), "error_code", code, "is_warning", warning));
                    }
                    if (scopes.Contains("parameters") || scopes.Contains("references"))
                    {
                        object displayRaw = feature.GetFirstDisplayDimension(); int guard = 0;
                        while (displayRaw != null && guard++ < 300)
                        {
                            var display = displayRaw as IDisplayDimension;
                            var dimension = display == null ? null : display.GetDimension2(0) as IDimension;
                            if (dimension != null)
                            {
                                string name = dimension.GetNameForSelection(); int type = display.Type2;
                                if (dimensionNames.Add(name) && (Angular(type) || Linear(type)))
                                {
                                    string id = "dim-" + J.Hash(d.Id + "|" + before + "|" + name).Substring(0, 24);
                                    d.Dimensions[id] = name;
                                    d.DimensionTypes[name] = type;
                                    var parameter = J.Obj("reference_id", id, "role", name,
                                        Angular(type) ? "value_deg" : "value_mm", ReadDimension(dimension) * (Angular(type) ? 180 / Math.PI : 1000),
                                        "writable", dimension.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriving);
                                    parameters.Add(parameter);
                                    references.Add(J.Obj("reference_id", id, "role", name, "entity_type", "dimension", "resolution", "unique"));
                                }
                            }
                            displayRaw = feature.GetNextDisplayDimension(displayRaw);
                        }
                    }
                    feature = feature.GetNextFeature() as IFeature;
                }
                if (feature != null) checks.Add(J.Check("FEATURE_LIMIT", "not_run", "bounded feature walk", "Remaining features were omitted after 1000 entries."));
                if (scopes.Contains("checks"))
                {
                    details["features"] = features;
                    checks.Add(J.Check("REBUILD", "not_run", "read-only inspection", "Inspection does not rebuild or modify the document."));
                    checks.Add(J.Check("MOTION", "not_run", "read-only inspection", "No motion study or constraint motion test was run."));
                }
            }
            object mateSummary = new object[0];
            if (scopes.Contains("mates") && d.Doc.GetType() == 2)
            {
                mateSummary = AssemblyMateFeatures(d.Doc).Select(feature => {
                    bool warning; int error = feature.GetErrorCode2(out warning); var mate = (IMate2)feature.GetSpecificFeature2();
                    return J.Obj("role", feature.Name, "kind", mate.Type == (int)swMateType_e.swMateCONCENTRIC ? "concentric" : mate.Type == (int)swMateType_e.swMateCOINCIDENT ? "coincident" : "type_" + mate.Type,
                        "state", error == 0 ? "not_checked" : "failed");
                }).ToArray();
                checks.Add(J.Check("MATE_READBACK", "pass", "mate feature type and error flags", "Mate identities were read without rebuilding; this inspection does not certify a new solve or a motion study."));
            }
            if (scopes.Contains("snapshot")) details["snapshot"] = CaptureWindow(d);
            if (Revision(d) != before) throw new Fault("DOC_REVISION_MISMATCH", "Document changed during inspection; inspect again.");
            return J.Obj("document", Summary(d), "scopes_returned", scopes.ToArray(), "geometry", geometry,
                "parameters", parameters, "references", references, "mates", mateSummary, "checks", checks, "details", details);
        }

        private List<Tuple<string, double, bool>> ResolveChanges(DocEntry d, Dictionary<string, object> action)
        {
            var changes = new List<Tuple<string, double, bool>>(); var seen = new HashSet<string>();
            foreach (var raw in J.Items(action["parameters"]))
            {
                var change = J.Map(raw); string id = J.Str(change, "reference_id"), name;
                if (!seen.Add(id)) throw new Fault("VALIDATION_FAILED", "Duplicate dimension update.");
                if (!d.Dimensions.TryGetValue(id, out name)) throw new Fault("DOC_REVISION_MISMATCH", "Inspect dimension references in this session before writing.");
                if (id != "dim-" + J.Hash(d.Id + "|" + Revision(d) + "|" + name).Substring(0, 24))
                    throw new Fault("DOC_REVISION_MISMATCH", "Dimension reference belongs to an older document revision.");
                var dim = d.Doc.Parameter(name) as IDimension;
                if (dim == null) throw new Fault("REFERENCE_AMBIGUOUS", "Dimension reference no longer resolves: " + name);
                int dimensionType;
                if (!d.DimensionTypes.TryGetValue(name, out dimensionType)) throw new Fault("DOC_REVISION_MISMATCH", "Inspect the dimension type before writing.");
                bool angular = Angular(dimensionType);
                if (!angular && !Linear(dimensionType)) throw new Fault("UNSUPPORTED_ACTION", "Dimension type is unsupported.");
                if (change.ContainsKey(angular ? "value_mm" : "value_deg")) throw new Fault("VALIDATION_FAILED", "Dimension units do not match the reference type.");
                double value = J.Number(J.Get(change, angular ? "value_deg" : "value_mm"));
                if (!angular && value <= 0) throw new Fault("VALIDATION_FAILED", "Linear dimension must be positive.");
                if (dim.DrivenState != (int)swDimensionDrivenState_e.swDimensionDriving) throw new Fault("UNSUPPORTED_ACTION", "Driven dimensions cannot be updated.");
                changes.Add(Tuple.Create(name, value, angular));
            }
            return changes;
        }

        public void Execute(Dictionary<string, object> args, JobRecord job)
        {
            Preconditions(args);
            if (J.Str(J.Map(args["action"]), "type") == "api_batch") { ExecuteApi(args, job); return; }
            if (J.Str(J.Map(args["action"]), "type") == "activate_document")
            {
                var target = Find(J.Str(J.Map(args["action"]), "document_id")); int error = 0;
                app.ActivateDoc3(target.Doc.GetTitle(), false, 0, ref error);
                if (error != 0 || !Object.ReferenceEquals(app.IActiveDoc2, target.Doc)) throw new Fault("API_FAILED", "Document activation was not confirmed.");
                if (target.Doc.GetType() == 2) ((IAssemblyDoc)target.Doc).UpdateBox();
                target.Doc.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
                target.Doc.ViewZoomtofit2(); target.Doc.GraphicsRedraw2();
                job.AddCheck(J.Check("ACTIVE_DOCUMENT", "pass", "IActiveDoc2 identity", "Requested existing document is active; no model geometry was modified."));
                return;
            }
            if (J.Str(J.Map(args["action"]), "type") == "assemble_demo") { AssembleDemo(args, job); return; }
            if (J.Str(J.Map(args["action"]), "type") == "insert_component_with_mates")
            {
                var target = Find(J.Str(J.Map(args["action"]), "document_id"));
                try { InsertPinWithMates(args, job); }
                finally
                {
                    int activationError = 0;
                    app.ActivateDoc3(target.Doc.GetTitle(), false, 0, ref activationError);
                    job.SetRestoration(activationError == 0 && Object.ReferenceEquals(app.IActiveDoc2, target.Doc) ? "succeeded" : "failed");
                }
                return;
            }
            var action = J.Map(args["action"]); var source = Find(J.Str(action, "document_id"));
            string sourceRevision = Revision(source), sourcePath = source.Doc.GetPathName();
            string sourceHash = J.FileHash(sourcePath);
            string outputDir = Paths.Within(options.OutputRoot, J.Str(args, "output_dir"));
            string run = Paths.Within(options.OutputRoot, Path.Combine(outputDir, ".runs", job.RequestId));
            Directory.CreateDirectory(run);
            string copy = Path.Combine(run, "part.SLDPRT");
            if (File.Exists(copy)) throw new Fault("OUTCOME_UNKNOWN", "Request staging file already exists; inspect the previous job instead of replaying.");
            job.Step("copy_source");
            File.Copy(sourcePath, copy, false);
            if (sourceRevision != Revision(source) || sourceHash != J.FileHash(sourcePath)) throw new Fault("DOC_REVISION_MISMATCH", "Source changed while making a copy.");
            var activeBefore = app.IActiveDoc2 as IModelDoc2;
            string activeTitle = activeBefore == null ? null : activeBefore.GetTitle();
            IModelDoc2 owned = null; bool cleanOwned = false;
            try
            {
                job.ThrowIfCancelled(); job.Step("open_isolated_copy");
                int error = 0, warning = 0;
                owned = app.OpenDoc6(copy, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    Configuration(source.Doc), ref error, ref warning) as IModelDoc2;
                if (owned == null || error != 0) throw new Fault("API_FAILED", "Could not open staging copy. error=" + error + " warning=" + warning) { ApiMethod = "OpenDoc6", ApiStatus = error };
                job.SetChanged("modified", "staged");
                if (J.Str(action, "type") == "set_parameters")
                {
                    var changes = ResolveChanges(source, action);
                    foreach (var change in changes)
                    {
                        job.ThrowIfCancelled(); job.Step("set_dimension:" + change.Item1);
                        var dim = owned.Parameter(change.Item1) as IDimension;
                        if (dim == null) throw new Fault("REFERENCE_AMBIGUOUS", "Dimension missing in copy: " + change.Item1);
                        int status = dim.SetSystemValue3(change.Item2 * (change.Item3 ? Math.PI / 180 : .001),
                            (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                        if (status != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                            throw new Fault("API_FAILED", "Dimension update failed.") { ApiMethod = "IDimension.SetSystemValue3", ApiStatus = status };
                    }
                    job.Step("rebuild_and_readback");
                    if (!owned.EditRebuild3()) throw new Fault("REBUILD_FAILED", "Rebuild of staging copy failed.");
                    foreach (var change in changes)
                    {
                        var dim = owned.Parameter(change.Item1) as IDimension;
                        double observed = ReadDimension(dim) * (change.Item3 ? 180 / Math.PI : 1000);
                        double tolerance = change.Item3 ? .0001 : .00001;
                        if (Math.Abs(observed - change.Item2) > tolerance) throw new Fault("API_FAILED", "Dimension readback differs from requested value.");
                    }
                    error = warning = 0;
                    if (!owned.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref error, ref warning) || error != 0)
                        throw new Fault("API_FAILED", "Saving staging copy failed.") { ApiMethod = "Save3", ApiStatus = error };
                    job.AddCheck(J.Check("DIMENSION_READBACK", "pass", "native parameter readback", "Updated dimensions agree within the documented tolerance."));
                }
                cleanOwned = !owned.GetSaveFlag();
                job.ThrowIfCancelled();
                if (sourceRevision != Revision(source) || sourceHash != J.FileHash(sourcePath)) throw new Fault("DOC_REVISION_MISMATCH", "Source changed while the job was running.");
                string finalDir = Paths.Within(options.OutputRoot, Path.Combine(outputDir, job.RequestId));
                Directory.CreateDirectory(finalDir);
                string artifact;
                string format;
                if (J.Str(action, "type") == "export_document")
                {
                    format = J.Str(action, "format");
                    string ext = format == "parasolid" ? "x_t" : format;
                    artifact = Path.Combine(finalDir, "part." + ext);
                    if (File.Exists(artifact)) throw new Fault("OUTCOME_UNKNOWN", "Output already exists; not overwriting.");
                    job.Step("export:" + format); error = warning = 0;
                    bool exported = owned.Extension.SaveAs(artifact, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref error, ref warning);
                    if (!exported || error != 0 || !File.Exists(artifact) || new FileInfo(artifact).Length == 0)
                        throw new Fault("EXPORT_FAILED", "Export did not produce a valid nonempty file. warning=" + warning) { ApiMethod = "IModelDocExtension.SaveAs", ApiStatus = error };
                    format = ext;
                }
                else
                {
                    format = "sldprt"; artifact = Path.Combine(finalDir, "part.SLDPRT"); File.Copy(copy, artifact, false);
                }
                job.AddArtifact(J.Obj("path", artifact, "format", format, "size_bytes", new FileInfo(artifact).Length,
                    "sha256", J.FileHash(artifact), "document_id", null, "revision", null,
                    "verification", J.Check("FILE_EXISTS", "pass", "API result, file length and SHA-256", "File is nonempty; re-open validation of exported geometry has not been run.")));
                job.AddCheck(J.Check("SOURCE_PRESERVED", "pass", "document revision and source SHA-256", "Source part was not modified by this bridge."));
                job.SetChanged("modified", "published");
            }
            finally
            {
                // Never close a user document or discard an edited staging copy after a failed write.
                bool restored = owned == null || (cleanOwned && !owned.GetSaveFlag());
                if (owned != null && cleanOwned && !owned.GetSaveFlag())
                    try { app.CloseDoc(owned.GetTitle()); } catch { restored = false; }
                if (activeTitle != null)
                    try { int e = 0; app.ActivateDoc3(activeTitle, false, 0, ref e); if (e != 0) restored = false; } catch { restored = false; }
                job.SetRestoration(restored ? "succeeded" : "failed");
            }
        }
    }
}
