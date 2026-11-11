using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace IsolatedSwBridge
{
    internal static class J
    {
        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]);
            return d;
        }
        public static string Json(object x) { return Serializer().Serialize(x); }
        public static object Parse(string x) { return Serializer().DeserializeObject(x); }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 96 }; }
        public static Dictionary<string, object> Map(object x)
        {
            var d = x as Dictionary<string, object>;
            if (d == null) throw new Fault("VALIDATION_FAILED", "Expected a JSON object.");
            return d;
        }
        public static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || !(v is string) || String.IsNullOrWhiteSpace((string)v))
                throw new Fault("VALIDATION_FAILED", "Missing or invalid string: " + k);
            return (string)v;
        }
        public static object Get(Dictionary<string, object> d, string k, object fallback = null) { object v; return d.TryGetValue(k, out v) ? v : fallback; }
        public static object[] Items(object x)
        {
            var array = x as object[];
            if (array != null) return array;
            var list = x as ArrayList;
            if (list != null) return list.ToArray();
            throw new Fault("VALIDATION_FAILED", "Expected a JSON array.");
        }
        public static void Keys(Dictionary<string, object> d, params string[] allowed)
        {
            foreach (string key in d.Keys) if (!allowed.Contains(key)) throw new Fault("VALIDATION_FAILED", "Unexpected field: " + key);
        }
        public static string GuidValue(Dictionary<string, object> d, string k)
        {
            string s = Str(d, k); Guid value;
            if (!Guid.TryParseExact(s, "D", out value)) throw new Fault("VALIDATION_FAILED", "Expected UUID: " + k);
            return value.ToString("D");
        }
        public static double Number(object x)
        {
            if (x == null || x is bool || x is string) throw new Fault("VALIDATION_FAILED", "Expected a finite JSON number.");
            double n;
            try { n = Convert.ToDouble(x, CultureInfo.InvariantCulture); }
            catch { throw new Fault("VALIDATION_FAILED", "Expected a finite JSON number."); }
            if (Double.IsNaN(n) || Double.IsInfinity(n)) throw new Fault("VALIDATION_FAILED", "Expected a finite JSON number.");
            return n;
        }
        public static string Hash(string s) { return Hash(Encoding.UTF8.GetBytes(s)); }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string FileHash(string path)
        {
            // CAD may already hold the source open for writing. Sharing permits a read-only
            // checksum without claiming a conflicting write lock; revision/hash checks still apply.
            using (var sha = SHA256.Create()) using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static object Canonical(object value)
        {
            var d = value as Dictionary<string, object>;
            if (d != null) return d.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => Canonical(x.Value));
            var a = value as object[];
            if (a != null) return a.Select(Canonical).ToArray();
            return value;
        }
        public static string Now() { return DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture); }
        public static Dictionary<string, object> Check(string code, string state, string method, string details)
        { return Obj("code", code, "state", state, "method", method, "details", details); }
        public static Dictionary<string, object> Result(string session, object data, string status = "ok", string request = null, object error = null)
        { return Obj("schema_version", "1.0", "request_id", request, "session_id", session, "status", status, "data", data, "error", error); }
        public static void AtomicNew(string path, object data)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(Json(data));
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { f.Write(bytes, 0, bytes.Length); f.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }

    internal sealed class Fault : Exception
    {
        public readonly string Code;
        public string ApiMethod;
        public object ApiStatus;
        public bool Retryable;
        public Fault(string code, string message) : base(message) { Code = code; }
        public Dictionary<string, object> DataObject()
        {
            return J.Obj("code", Code, "message", Message, "step", null, "api_method", ApiMethod,
                "api_status", ApiStatus, "hresult", null, "retryable", Retryable,
                "recovery", new[] { Retryable ? "Wait, then inspect current state before a new submission." : "Inspect the document and correct the cause before submitting a new request." });
        }
        public static Dictionary<string, object> Describe(Exception ex)
        {
            var fault = ex as Fault;
            if (fault != null) return fault.DataObject();
            var com = ex as COMException;
            var d = new Fault(com != null ? "API_FAILED" : "API_FAILED", ex.Message).DataObject();
            if (com != null) d["hresult"] = "0x" + unchecked((uint)com.ErrorCode).ToString("X8");
            return d;
        }
    }

    internal sealed class Options
    {
        public string StateRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state");
        public string OutputRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        public int? Pid;
        public bool AllowWrite;
        public readonly List<string> ApiLibraries = new List<string>();
        public string PartTemplate = @"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2026\templates\gb_part.prtdot";
        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--attach-only": break;
                    case "--allow-write": o.AllowWrite = true; break;
                    case "--pid": o.Pid = Int32.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--state-root": o.StateRoot = Next(args, ref i); break;
                    case "--output-root": o.OutputRoot = Next(args, ref i); break;
                    case "--part-template": o.PartTemplate = Next(args, ref i); break;
                    case "--api-library":
                        string library = Paths.Absolute(Next(args, ref i)); Paths.NoReparse(library);
                        if (!File.Exists(library)) throw new ArgumentException("API Interop library not found: " + library);
                        o.ApiLibraries.Add(library); break;
                    default: throw new ArgumentException("Unknown option: " + args[i]);
                }
            }
            o.StateRoot = Paths.Absolute(o.StateRoot); o.OutputRoot = Paths.Absolute(o.OutputRoot);
            Paths.NoReparse(o.StateRoot); Paths.NoReparse(o.OutputRoot);
            return o;
        }
        private static string Next(string[] args, ref int i) { if (++i >= args.Length) throw new ArgumentException("Missing option value."); return args[i]; }
    }

    internal static class Paths
    {
        public static string Absolute(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                throw new Fault("PATH_NOT_ALLOWED", "An absolute local drive path is required.");
            string full = Path.GetFullPath(path);
            if (full.Substring(2).Contains(":")) throw new Fault("PATH_NOT_ALLOWED", "Alternate data stream paths are not supported.");
            return full.Length <= 3 ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        public static string Within(string root, string path)
        {
            string full = Absolute(path), r = Absolute(root);
            string prefix = r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.Equals(r, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new Fault("PATH_NOT_ALLOWED", "Output path is outside this bridge's configured root.");
            NoReparse(full);
            return full;
        }
        public static void NoReparse(string path)
        {
            for (string p = Path.GetFullPath(path); !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new Fault("PATH_NOT_ALLOWED", "Directory links and reparse points are not supported: " + p);
        }
    }

    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int ticks, IntPtr info);
        [PreserveSig] int RetryRejectedCall(IntPtr callee, int ticks, int rejectType);
        [PreserveSig] int MessagePending(IntPtr callee, int ticks, int pendingType);
    }
    internal sealed class OleFilter : IOleMessageFilter
    {
        [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IOleMessageFilter filter, out IOleMessageFilter oldFilter);
        public static void Register() { IOleMessageFilter old; CoRegisterMessageFilter(new OleFilter(), out old); }
        public int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info) { return 0; }
        public int RetryRejectedCall(IntPtr callee, int ticks, int type) { return (type == 1 || type == 2) && ticks < 2000 ? 100 : -1; }
        public int MessagePending(IntPtr callee, int ticks, int type) { return 2; }
    }

    internal sealed class StaExecutor : IDisposable
    {
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private readonly Thread thread;
        private Control dispatch;
        private int pending;
        public StaExecutor()
        {
            thread = new Thread(() =>
            {
                OleFilter.Register();
                dispatch = new Control(); var handle = dispatch.Handle;
                ready.Set(); Application.Run(); dispatch.Dispose();
            });
            thread.IsBackground = true; thread.Name = "SolidWorks API STA";
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Wait();
        }
        public Task<T> Run<T>(Func<T> action)
        {
            if (Interlocked.Increment(ref pending) > 8)
            { Interlocked.Decrement(ref pending); throw new Fault("QUEUE_FULL", "CAD queue is full."); }
            var completion = new TaskCompletionSource<T>();
            try
            {
                dispatch.BeginInvoke((Action)(() =>
                {
                    try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); }
                    finally { Interlocked.Decrement(ref pending); }
                }));
            }
            catch { Interlocked.Decrement(ref pending); throw; }
            return completion.Task;
        }
        public async Task<T> Read<T>(Func<T> action)
        {
            var task = Run(action);
            if (await Task.WhenAny(task, Task.Delay(25000)).ConfigureAwait(false) != task)
                throw new Fault("SW_BUSY", "CAD read exceeded 25 seconds; the underlying read may still be pending.") { Retryable = true };
            return await task.ConfigureAwait(false);
        }
        public void Dispose() { try { dispatch.BeginInvoke((Action)(() => Application.ExitThread())); } catch { } }
    }
}
