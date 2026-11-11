using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IsolatedSwBridge
{
    internal sealed class Server
    {
        private readonly object outputGate = new object();
        private readonly object tasksGate = new object();
        private readonly List<Task> tasks = new List<Task>();
        private readonly StaExecutor sta;
        private readonly CadAdapter cad;
        private readonly Jobs jobs;
        private readonly Dictionary<string, object> schema;
        private volatile bool initialized;
        private string protocol = "2025-06-18";
        public Server(Options options, StaExecutor executor)
        {
            sta = executor; cad = new CadAdapter(options); jobs = new Jobs(options, cad, executor);
            schema = J.Map(J.Parse(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schemas", "bridge.schema.json"))));
        }
        private void Send(object message) { lock (outputGate) { Console.Out.WriteLine(J.Json(message)); Console.Out.Flush(); } }
        private void RpcError(object id, int code, string message) { Send(J.Obj("jsonrpc", "2.0", "id", id, "error", J.Obj("code", code, "message", message))); }
        private void Reply(object id, object value) { Send(J.Obj("jsonrpc", "2.0", "id", id, "result", value)); }
        private Dictionary<string, object> InputSchema(string name)
        {
            var defs = J.Map(schema["$defs"]); var included = new Dictionary<string, object>();
            Action<object> walk = null;
            walk = value =>
            {
                var map = value as Dictionary<string, object>;
                if (map != null)
                {
                    if (map.ContainsKey("$ref") && map["$ref"] is string && ((string)map["$ref"]).StartsWith("#/$defs/", StringComparison.Ordinal))
                    {
                        string key = ((string)map["$ref"]).Substring("#/$defs/".Length);
                        if (!included.ContainsKey(key)) { included.Add(key, defs[key]); walk(defs[key]); }
                    }
                    foreach (var item in map.Values) walk(item);
                }
                else { var array = value as object[]; if (array != null) foreach (var item in array) walk(item); }
            };
            var result = J.Map(J.Parse(J.Json(defs[name]))); walk(result);
            if (included.Count != 0) result["$defs"] = included;
            return result;
        }
        private object Tools()
        {
            string[] names = { "sw_status", "sw_inspect", "sw_submit", "sw_job", "sw_api_catalog", "sw_api_object" };
            string[] definitions = { "StatusInput", "InspectInput", "SubmitInput", "JobInput", "ApiCatalogInput", "ApiObjectInput" };
            string[] descriptions = {
                "Attach to an existing SolidWorks instance without starting it, and read version, document summaries and actual capabilities. Does not change CAD documents.",
                "Read a current document by its session-local ID. Optional geometry, named dimensions and feature states. Does not rebuild or move components.",
                "Validate or queue saved-part copy updates, exports, assembly insertion, or api_batch calls to official COM interfaces. api_batch operates on live objects in raw API units and can modify/save/delete files according to the requested API; no automatic copy or rollback. Requires --allow-write.",
                "Read persistent job state, or request cancellation at a step boundary. A failed job remains a successful status lookup.",
                "Search the loaded official Interop interfaces, exact method/ref/out signatures and enum values without connecting to CAD. Paginate with offset/limit. Metadata availability does not prove runtime version/license/context support. Use sw_submit action api_batch to invoke discovered members.",
                "Read a page of a stored API array or release a session-local transport reference. Does not invoke CAD model methods. Reacquire stale references after document changes." };
            var tools = new List<object>();
            for (int i = 0; i < names.Length; i++) tools.Add(J.Obj("name", names[i], "description", descriptions[i],
                "inputSchema", InputSchema(definitions[i]), "annotations", J.Obj("readOnlyHint", i < 2 || i >= 4, "destructiveHint", i == 2, "openWorldHint", i == 2)));
            return J.Obj("tools", tools);
        }
        private async Task<object> Tool(string name, Dictionary<string, object> args)
        {
            if (name == "sw_status")
            {
                J.Keys(args);
                return await sta.Read(() => { var data = cad.Status(); return J.Result(cad.SessionId, data); }).ConfigureAwait(false);
            }
            if (name == "sw_inspect")
            {
                J.Keys(args, "session_id", "document_id", "scopes"); J.GuidValue(args, "session_id");
                return await sta.Read(() => J.Result(cad.SessionId, cad.Inspect(args))).ConfigureAwait(false);
            }
            if (name == "sw_submit") return await jobs.Submit(args).ConfigureAwait(false);
            if (name == "sw_job") return jobs.Query(args);
            if (name == "sw_api_catalog") return J.Result(null, cad.Api.Read(args));
            if (name == "sw_api_object") return await sta.Read(() => J.Result(cad.SessionId, cad.ReadApiObject(args))).ConfigureAwait(false);
            throw new Fault("UNSUPPORTED_ACTION", "Unknown tool: " + name);
        }
        private async Task Handle(Dictionary<string, object> message)
        {
            object id = J.Get(message, "id");
            bool notification = !message.ContainsKey("id");
            string method;
            try { method = J.Str(message, "method"); }
            catch { if (!notification) RpcError(id, -32600, "Invalid request."); return; }
            if (notification) return;
            try
            {
                if ((string)J.Get(message, "jsonrpc", "") != "2.0") { RpcError(id, -32600, "jsonrpc must be 2.0."); return; }
                var args = J.Map(J.Get(message, "params", J.Obj()));
                if (method == "initialize")
                {
                    string requested = J.Str(args, "protocolVersion");
                    protocol = new[] { "2025-06-18", "2025-03-26", "2024-11-05" }.Contains(requested) ? requested : "2025-06-18";
                    initialized = true;
                    Reply(id, J.Obj("protocolVersion", protocol, "capabilities", J.Obj("tools", J.Obj("listChanged", false)),
                        "serverInfo", J.Obj("name", "solidworks-local-01a1cb2e", "version", "0.4.0"),
                        "instructions", "Local attach-only bridge. Read-only by default. Check sw_status before submitting work; capabilities are explicit."));
                    return;
                }
                if (method == "ping") { Reply(id, J.Obj()); return; }
                if (!initialized) { RpcError(id, -32002, "Initialize the MCP server first."); return; }
                if (method == "tools/list") { Reply(id, Tools()); return; }
                if (method == "tools/call")
                {
                    string tool = J.Str(args, "name");
                    if (!new[] { "sw_status", "sw_inspect", "sw_submit", "sw_job", "sw_api_catalog", "sw_api_object" }.Contains(tool)) { RpcError(id, -32602, "Unknown tool name."); return; }
                    var input = J.Map(J.Get(args, "arguments", J.Obj())); object content;
                    try { content = await Tool(tool, input).ConfigureAwait(false); }
                    catch (Exception ex) { content = J.Result(cad.SessionId, null, "error", J.Get(input, "request_id") as string, Fault.Describe(ex)); }
                    var result = J.Obj("content", new[] { J.Obj("type", "text", "text", J.Json(content)) },
                        "isError", (string)J.Map(content)["status"] == "error");
                    if (protocol == "2025-06-18") result["structuredContent"] = content;
                    Reply(id, result); return;
                }
                RpcError(id, -32601, "Method not found.");
            }
            catch (Exception ex) { RpcError(id, -32602, ex.Message); }
        }
        public void Run()
        {
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 1024 * 1024) { RpcError(null, -32600, "Request exceeds 1 MiB."); continue; }
                Dictionary<string, object> request;
                try { request = J.Map(J.Parse(line)); }
                catch { RpcError(null, -32700, "Invalid JSON object."); continue; }
                lock (tasksGate)
                {
                    tasks.RemoveAll(t => t.IsCompleted);
                    if (tasks.Count >= 16) { RpcError(J.Get(request, "id"), -32000, "Too many outstanding requests."); continue; }
                    tasks.Add(Handle(request));
                }
            }
            Task[] pending; lock (tasksGate) pending = tasks.ToArray();
            try { Task.WaitAll(pending, 30000); } catch { }
            jobs.Shutdown();
        }
    }
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                var options = Options.Parse(args);
                Directory.CreateDirectory(options.StateRoot);
                bool created;
                using (var stateLock = new Mutex(true, "Local\\CodexSwBridgeState_" + J.Hash(options.StateRoot.ToUpperInvariant()).Substring(0, 24), out created))
                {
                    if (!created) throw new InvalidOperationException("This state directory is already owned by another bridge. Use a distinct --state-root.");
                    using (var executor = new StaExecutor())
                    {
                        var server = new Server(options, executor); server.Run();
                    }
                    stateLock.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("Bridge startup failed: " + ex.Message); return 2; }
        }
    }
}
