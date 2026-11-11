using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace IsolatedSwBridge
{
    // The installed Interop metadata is the source of truth, never an LLM-maintained method list.
    internal sealed class ApiCatalog
    {
        private readonly Type[] types;
        private readonly Assembly[] assemblies;
        public ApiCatalog(Options options)
        {
            var loaded = new List<Assembly> { typeof(ISldWorks).Assembly, typeof(swDocumentTypes_e).Assembly };
            foreach (string path in options.ApiLibraries) loaded.Add(Assembly.LoadFrom(path));
            assemblies = loaded.Distinct().ToArray();
            types = assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsPublic && (t.IsInterface || t.IsEnum))
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        }
        public Type Find(string name, bool requireInterface)
        {
            var found = types.Where(t => (t.FullName == name || t.Name == name) && (!requireInterface || t.IsInterface)).ToArray();
            if (found.Length != 1) throw new Fault("VALIDATION_FAILED", "API type is missing or ambiguous: " + name + ". Use sw_api_catalog and the full type name.");
            return found[0];
        }
        private static Type Element(Type t) { return t.IsByRef || t.IsArray ? t.GetElementType() : t; }
        private static bool UnsupportedType(Type t)
        {
            t = Element(t);
            return t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr) || typeof(Delegate).IsAssignableFrom(t);
        }
        public static string Limitation(MethodInfo method)
        {
            if (method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal))
                return "Event callbacks require a separate in-process/add-in implementation.";
            if (UnsupportedType(method.ReturnType) || method.GetParameters().Any(p => UnsupportedType(p.ParameterType)))
                return "Native pointers and callback delegates cannot be transported through JSON.";
            // These signatures expose a raw buffer as a single primitive managed value.
            string bare = method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Name.Substring(4) : method.Name;
            string global = bare.Length > 1 && bare[0] == 'I' ? bare.Substring(1) : null;
            string accessor = method.Name.Length == bare.Length ? "" : method.Name.Substring(0, 4);
            if (global != null && method.DeclaringType.GetMethods().Any(m => m.Name == accessor + global) &&
                (method.ReturnType.IsByRef || (method.ReturnType.IsPrimitive && method.ReturnType != typeof(bool) && method.ReturnType != typeof(void)) ||
                 method.GetParameters().Any(p => p.ParameterType.IsByRef && Element(p.ParameterType).IsPrimitive)))
                return "Potential in-process raw-buffer method. Use the corresponding global/SAFEARRAY method.";
            return null;
        }
        private static object DescribeMethod(MethodInfo m)
        {
            return J.Obj("name", m.Name, "return_type", m.ReturnType.FullName, "parameter_types", m.GetParameters().Select(p => p.ParameterType.FullName).ToArray(),
                "parameters", m.GetParameters().Select(p => J.Obj("name", p.Name, "type", p.ParameterType.FullName,
                    "is_out", p.IsOut, "is_by_ref", p.ParameterType.IsByRef, "is_optional", p.IsOptional)).ToArray(),
                "transport_supported", Limitation(m) == null, "limitation", Limitation(m),
                "cad_behavior_verified", false, "documentation_url", HelpUrl(m));
        }
        private static string HelpUrl(MethodInfo method)
        {
            Type t = method.DeclaringType;
            if (t.Assembly != typeof(ISldWorks).Assembly) return null;
            string member = method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) ? method.Name.Substring(4) : method.Name;
            return "https://help.solidworks.com/" + (t.Assembly.GetName().Version.Major + 1992) + "/english/api/sldworksapi/" +
                t.Namespace + "~" + t.FullName + "~" + member + ".html";
        }
        public object Read(Dictionary<string, object> input)
        {
            J.Keys(input, "operation", "query", "type_name", "member", "offset", "limit");
            string op = J.Str(input, "operation");
            int offset = Page(input, "offset", 0, 0, Int32.MaxValue), limit = Page(input, "limit", 40, 1, 100);
            string query = J.Get(input, "query", "") as string;
            if (query == null || query.Length > 200) throw new Fault("VALIDATION_FAILED", "query must be a string of at most 200 characters.");
            var data = J.Obj("kind", "api_catalog", "operation", op, "assemblies", assemblies.Select(a => J.Obj("name", a.GetName().Name, "version", a.GetName().Version.ToString())).ToArray(),
                "runtime_connection_checked", false, "units", "Raw official API units; usually metres/radians. Check the method's documentation.",
                "coverage", "Loaded managed COM interfaces only. Metadata and transport support do not certify runtime availability, licensing, context or method behavior.");
            if (op == "summary")
            {
                data["interface_count"] = types.Count(t => t.IsInterface); data["enum_count"] = types.Count(t => t.IsEnum);
                data["method_count"] = types.Where(t => t.IsInterface).Sum(t => t.GetMethods().Length);
                data["transport_candidate_count"] = types.Where(t => t.IsInterface).Sum(t => t.GetMethods().Count(m => Limitation(m) == null));
            }
            else if (op == "types")
            {
                var found = types.Where(t => Contains(t.FullName, query)).ToArray();
                data["total"] = found.Length; data["offset"] = offset;
                data["items"] = found.Skip(offset).Take(limit).Select(t => J.Obj("name", t.Name, "full_name", t.FullName, "kind", t.IsEnum ? "enum" : "interface")).ToArray();
            }
            else if (op == "describe")
            {
                var t = Find(J.Str(input, "type_name"), false); string member = J.Get(input, "member", "") as string;
                if (member == null) throw new Fault("VALIDATION_FAILED", "member must be a string.");
                data["type_name"] = t.FullName;
                if (t.IsEnum)
                {
                    var fields = t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => Contains(f.Name, member)).OrderBy(f => f.Name).ToArray();
                    data["total"] = fields.Length; data["offset"] = offset;
                    data["items"] = fields.Skip(offset).Take(limit).Select(f => J.Obj("name", f.Name, "value", f.GetRawConstantValue())).ToArray();
                }
                else
                {
                    var found = t.GetMethods().Where(m => Contains(m.Name, member)).OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.GetParameters().Length).ToArray();
                    data["total"] = found.Length; data["offset"] = offset; data["items"] = found.Skip(offset).Take(limit).Select(DescribeMethod).ToArray();
                }
            }
            else if (op == "search")
            {
                if (String.IsNullOrWhiteSpace(query)) throw new Fault("VALIDATION_FAILED", "search requires a nonempty query.");
                string typeFilter = J.Get(input, "type_name", "") as string;
                if (typeFilter == null) throw new Fault("VALIDATION_FAILED", "type_name must be a string.");
                var found = types.Where(t => t.IsInterface && Contains(t.FullName, typeFilter)).SelectMany(t => t.GetMethods()
                    .Where(m => Contains(m.Name, query)).Select(m => J.Obj("type_name", t.FullName, "method", DescribeMethod(m)))).ToArray();
                data["total"] = found.Length; data["offset"] = offset; data["items"] = found.Skip(offset).Take(limit).ToArray();
            }
            else throw new Fault("VALIDATION_FAILED", "operation must be summary, types, describe or search.");
            return data;
        }
        private static bool Contains(string text, string query) { return text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
        private static int Page(Dictionary<string, object> d, string key, int fallback, int min, int max)
        {
            if (!d.ContainsKey(key)) return fallback;
            double n = J.Number(d[key]);
            if (n != Math.Truncate(n) || n < min || n > max) throw new Fault("VALIDATION_FAILED", "Invalid pagination field: " + key);
            return (int)n;
        }
        public MethodInfo Resolve(Dictionary<string, object> step)
        {
            var type = Find(J.Str(step, "interface"), true);
            string operation = J.Get(step, "operation", "call") as string, member = J.Str(step, "member");
            if (!new[] { "call", "get", "set" }.Contains(operation)) throw new Fault("VALIDATION_FAILED", "API operation must be call, get or set.");
            string methodName = operation == "call" ? member : (operation == "get" ? "get_" : "set_") + member;
            int count = J.Items(J.Get(step, "arguments", new object[0])).Length;
            var found = type.GetMethods().Where(m => m.Name == methodName && m.GetParameters().Length == count).ToArray();
            if (step.ContainsKey("parameter_types"))
            {
                var signature = J.Items(step["parameter_types"]).Select(x => x as string).ToArray();
                if (signature.Any(s => s == null)) throw new Fault("VALIDATION_FAILED", "parameter_types must contain type names.");
                found = found.Where(m => m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(signature)).ToArray();
            }
            if (found.Length != 1) throw new Fault("VALIDATION_FAILED", "API member/signature is missing or ambiguous: " + type.Name + "." + methodName + ". Supply every parameter (null for out), and parameter_types if needed.");
            string limitation = Limitation(found[0]);
            if (limitation != null) throw new Fault("UNSUPPORTED_ACTION", limitation);
            return found[0];
        }
        public object EnumValue(object input)
        {
            var names = input is string ? new object[] { input } : J.Items(input);
            if (names.Length == 0) throw new Fault("VALIDATION_FAILED", "An enum flag list cannot be empty.");
            long result = 0; Type enumType = null;
            foreach (object raw in names)
            {
                string name = raw as string; int at = name == null ? -1 : name.LastIndexOf('.');
                if (at <= 0) throw new Fault("VALIDATION_FAILED", "Use EnumType.Member for enum values.");
                Type t = Find(name.Substring(0, at), false);
                if (!t.IsEnum || (enumType != null && enumType != t)) throw new Fault("VALIDATION_FAILED", "Flag values must belong to one official enum.");
                var field = t.GetField(name.Substring(at + 1), BindingFlags.Public | BindingFlags.Static);
                if (field == null || !field.IsLiteral) throw new Fault("VALIDATION_FAILED", "Unknown enum value: " + name);
                enumType = t; result |= Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture);
            }
            return result;
        }
    }
}
