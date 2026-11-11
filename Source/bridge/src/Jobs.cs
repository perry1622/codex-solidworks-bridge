using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IsolatedSwBridge
{
    internal sealed class JobRecord
    {
        private readonly object gate = new object();
        private readonly string path;
        internal readonly string Hash;
        internal readonly string Session;
        internal readonly Dictionary<string, object> Data;
        private volatile bool cancel;
        public string RequestId { get { return (string)Data["request_id"]; } }
        public string JobId { get { return (string)Data["job_id"]; } }
        public JobRecord(string stateRoot, string request, string session, string hash)
        {
            Session = session; Hash = hash; path = Path.Combine(stateRoot, request + ".json");
            Data = J.Obj("job_id", Guid.NewGuid().ToString("D"), "request_id", request,
                "state", "queued", "active_step", null, "completed_steps", new List<object>(),
                "observed_at", J.Now(), "documents", new List<object>(),
                "changes", J.Obj("model_state", "unchanged", "publication", "none", "source_preserved", true, "restoration", "not_needed"),
                "checks", new List<object>(), "motion_samples", new List<object>(), "artifacts", new List<object>(), "error", null);
            Persist();
        }
        public JobRecord(string file)
        {
            path = file; var stored = J.Map(J.Parse(File.ReadAllText(file)));
            Hash = J.Str(stored, "hash"); Session = J.Str(stored, "session_id"); Data = J.Map(stored["job"]);
            foreach (string key in new[] { "completed_steps", "documents", "checks", "motion_samples", "artifacts" })
                Data[key] = J.Items(Data[key]).ToList();
            string state = (string)Data["state"];
            if (state == "queued" || state == "running" || state == "cancel_requested")
            {
                Data["state"] = "interrupted";
                Data["error"] = new Fault("OUTCOME_UNKNOWN", "Bridge restarted before the job had a confirmed result; no replay was attempted.").DataObject();
                J.Map(Data["changes"])["model_state"] = "unknown"; Persist();
            }
        }
        private void Persist()
        {
            Data["observed_at"] = J.Now();
            J.AtomicNew(path, J.Obj("hash", Hash, "session_id", Session, "job", Data));
        }
        public object Snapshot() { lock (gate) return J.Parse(J.Json(Data)); }
        public bool Terminal
        { get { lock (gate) return new[] { "succeeded", "failed", "cancelled", "interrupted", "outcome_unknown" }.Contains((string)Data["state"]); } }
        public void Start() { lock (gate) { ThrowIfCancelled(); Data["state"] = "running"; Persist(); } }
        public void Cancel() { lock (gate) { if (!Terminal) { cancel = true; Data["state"] = "cancel_requested"; Persist(); } } }
        public void ThrowIfCancelled() { if (cancel) throw new OperationCanceledException("Cancellation requested; stopped at a step boundary."); }
        public void Step(string step)
        {
            lock (gate)
            {
                if (Data["active_step"] != null) ((List<object>)Data["completed_steps"]).Add(Data["active_step"]);
                Data["active_step"] = step; Persist();
            }
        }
        public void AddCheck(object check) { lock (gate) { ((List<object>)Data["checks"]).Add(check); Persist(); } }
        public void AddArtifact(object artifact) { lock (gate) { ((List<object>)Data["artifacts"]).Add(artifact); Persist(); } }
        public void AddApiResult(object result)
        {
            lock (gate)
            {
                if (!Data.ContainsKey("api_results")) Data["api_results"] = new List<object>();
                ((List<object>)Data["api_results"]).Add(result); Persist();
            }
        }
        public void SetApiEffectsUnknown()
        {
            lock (gate)
            {
                var changes = J.Map(Data["changes"]); changes["model_state"] = "unknown";
                changes["source_preserved"] = null; Persist();
            }
        }
        public void SetDocuments(object[] documents) { lock (gate) { Data["documents"] = documents.ToList(); Persist(); } }
        public void SetChanged(string state, string publication)
        { lock (gate) { var c = J.Map(Data["changes"]); c["model_state"] = state; c["publication"] = publication; Persist(); } }
        public void SetRestoration(string value) { lock (gate) { J.Map(Data["changes"])["restoration"] = value; Persist(); } }
        public void SetSourcePreserved(bool value) { lock (gate) { J.Map(Data["changes"])["source_preserved"] = value; Persist(); } }
        public void Finish(Exception error)
        {
            lock (gate)
            {
                if (error == null)
                {
                    if (Data["active_step"] != null) ((List<object>)Data["completed_steps"]).Add(Data["active_step"]);
                    Data["active_step"] = null; Data["state"] = "succeeded";
                }
                else if (error is OperationCanceledException) Data["state"] = "cancelled";
                else
                {
                    Data["state"] = "failed";
                    var details = Fault.Describe(error);
                    if (details["step"] == null) details["step"] = Data["active_step"];
                    Data["error"] = details;
                }
                Persist();
            }
        }
    }

    internal sealed class Jobs
    {
        private readonly Options options;
        private readonly CadAdapter cad;
        private readonly StaExecutor sta;
        private readonly object gate = new object();
        private readonly Dictionary<string, JobRecord> records = new Dictionary<string, JobRecord>();
        private readonly List<Task> running = new List<Task>();
        public Jobs(Options o, CadAdapter c, StaExecutor s)
        {
            options = o; cad = c; sta = s; Directory.CreateDirectory(o.StateRoot);
            foreach (string path in Directory.GetFiles(o.StateRoot, "*.json"))
            {
                var record = new JobRecord(path); records.Add(record.RequestId, record);
            }
        }
        public async Task<object> Submit(Dictionary<string, object> args)
        {
            J.Keys(args, "schema_version", "request_id", "session_id", "expected_documents", "execution", "output_dir", "action");
            if (J.Str(args, "schema_version") != "1.0") throw new Fault("VALIDATION_FAILED", "Unsupported schema_version.");
            string request = J.GuidValue(args, "request_id"), session = J.GuidValue(args, "session_id");
            string hash = J.Hash(J.Json(J.Canonical(args)));
            JobRecord existing;
            lock (gate)
                if (records.TryGetValue(request, out existing))
                {
                    if (existing.Hash != hash) throw new Fault("REQUEST_ID_CONFLICT", "Request UUID is already associated with different input.");
                    return J.Result(session, existing.Snapshot(), "accepted", request);
                }
            if (!options.AllowWrite) throw new Fault("UNSUPPORTED_ACTION", "This instance is read-only. Start a separate bridge with --allow-write to enable copy-based writes.");
            string execution = J.Str(args, "execution");
            if (execution != "validate" && execution != "execute") throw new Fault("VALIDATION_FAILED", "execution must be validate or execute.");
            var action = J.Map(J.Get(args, "action")); string type = J.Str(action, "type");
            if (type == "set_parameters")
            {
                J.Keys(action, "type", "document_id", "parameters");
                var changes = J.Items(J.Get(action, "parameters"));
                if (changes.Length == 0 || changes.Length > 32) throw new Fault("VALIDATION_FAILED", "Expected 1 to 32 dimension changes.");
                foreach (var raw in changes)
                {
                    var change = J.Map(raw); J.Keys(change, "reference_id", "value_mm", "value_deg");
                    J.Str(change, "reference_id");
                    if (change.ContainsKey("value_mm") == change.ContainsKey("value_deg")) throw new Fault("VALIDATION_FAILED", "Specify exactly one dimension unit.");
                    J.Number(change[change.ContainsKey("value_mm") ? "value_mm" : "value_deg"]);
                }
            }
            else if (type == "export_document")
            {
                J.Keys(action, "type", "document_id", "format");
                if (!new[] { "parasolid", "step", "stl", "png" }.Contains(J.Str(action, "format"))) throw new Fault("UNSUPPORTED_ACTION", "Export format not supported.");
            }
            else if (type == "api_batch") CadAdapter.ApiShape(cad.Api, action);
            else if (type == "assemble_demo" || type == "activate_document")
            {
                J.Keys(action, "type", "document_id"); J.Str(action, "document_id");
            }
            else if (type == "insert_component_with_mates")
            {
                J.Keys(action, "type", "document_id", "base_component_id", "hole_reference_id", "seat_reference_id", "source_part_path", "source_configuration");
                foreach (string key in new[] { "document_id", "base_component_id", "hole_reference_id", "seat_reference_id", "source_part_path" }) J.Str(action, key);
                if (action.ContainsKey("source_configuration")) J.Str(action, "source_configuration");
            }
            else throw new Fault("UNSUPPORTED_ACTION", "Unknown action. Use sw_status for available actions or sw_api_catalog for official API discovery.");
            Paths.Within(options.OutputRoot, J.Str(args, "output_dir"));
            await sta.Read(() => { cad.Preconditions(args); return true; }).ConfigureAwait(false);
            if (execution == "validate") return J.Result(session, J.Obj("valid", true, "action_type", type,
                "will_modify_documents", type == "api_batch" ? J.Items(args["expected_documents"]).Select(x => J.Str(J.Map(x), "document_id")).ToArray() : type == "assemble_demo" || type == "insert_component_with_mates" ? new[] { J.Str(action, "document_id") } : new string[0],
                "planned_files", new[] { type == "api_batch" ? "Determined by the requested official API calls; live objects, raw API units, no automatic copy, save, rollback or output-path sandbox." : type == "insert_component_with_mates" ? "New independent library-part copy and assembly file under a request-specific directory; existing part files preserved." : "New files under a request-specific directory." }), "ok", request);
            JobRecord job;
            lock (gate)
            {
                if (records.TryGetValue(request, out existing))
                {
                    if (existing.Hash != hash) throw new Fault("REQUEST_ID_CONFLICT", "Request input conflict.");
                    return J.Result(session, existing.Snapshot(), "accepted", request);
                }
                if (records.Values.Count(r => !r.Terminal) >= 8) throw new Fault("QUEUE_FULL", "Write job queue is full.");
                job = new JobRecord(options.StateRoot, request, session, hash); records.Add(request, job);
                Task task;
                try
                {
                    task = sta.Run(() =>
                    {
                        try
                        {
                            job.Start();
                            using (var mutex = new Mutex(false, "Local\\CodexSwBridgeWrite_" + cad.WriteLockIdentity))
                            {
                                bool held = false;
                                try
                                {
                                    try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                                    if (!held) throw new Fault("SW_BUSY", "Another instance of this bridge is writing to the same CAD process.");
                                    cad.Execute(args, job);
                                }
                                finally { if (held) mutex.ReleaseMutex(); }
                            }
                            job.Finish(null);
                        }
                        catch (Exception ex) { job.Finish(ex); }
                        return true;
                    });
                }
                catch (Exception ex) { job.Finish(ex); task = Task.FromResult(false); }
                running.Add(task);
            }
            return J.Result(session, job.Snapshot(), "accepted", request);
        }
        public object Query(Dictionary<string, object> args)
        {
            J.Keys(args, "session_id", "job_id", "operation");
            string session = J.GuidValue(args, "session_id"), id = J.GuidValue(args, "job_id"), operation = J.Str(args, "operation");
            if (session != cad.SessionId) throw new Fault("SW_DISCONNECTED", "Call sw_status to obtain the current session.");
            if (operation != "get" && operation != "cancel") throw new Fault("VALIDATION_FAILED", "operation must be get or cancel.");
            lock (gate)
            {
                var record = records.Values.FirstOrDefault(r => r.JobId == id);
                if (record == null) throw new Fault("VALIDATION_FAILED", "Job was not found in this bridge's state directory.");
                if (operation == "cancel") record.Cancel();
                return J.Result(session, record.Snapshot());
            }
        }
        public void Shutdown()
        {
            Task[] tasks;
            lock (gate) { foreach (var r in records.Values) r.Cancel(); tasks = running.ToArray(); }
            try { Task.WaitAll(tasks, 10000); } catch { }
        }
    }
}
