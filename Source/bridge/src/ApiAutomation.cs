using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;

namespace IsolatedSwBridge
{
    internal sealed partial class CadAdapter
    {
        private sealed class ApiObject
        {
            internal object Value;
            internal string Session;
            internal Dictionary<string, string> Revisions;
        }
        private readonly Dictionary<string, ApiObject> apiObjects = new Dictionary<string, ApiObject>();
        public readonly ApiCatalog Api;
        private void ResetApiObjects() { apiObjects.Clear(); }
        public object ReadApiObject(Dictionary<string, object> input)
        {
            J.Keys(input, "session_id", "object_id", "operation", "offset", "limit");
            RequireSession(J.GuidValue(input, "session_id")); string id = J.Str(input, "object_id"), operation = J.Str(input, "operation");
            var value = ApiLookup(id);
            if (operation == "release") { apiObjects.Remove(id); return J.Obj("kind", "api_object", "object_id", id, "released", true); }
            if (operation != "read") throw new Fault("VALIDATION_FAILED", "Object operation must be read or release.");
            double start = J.Number(J.Get(input, "offset", 0)), count = J.Number(J.Get(input, "limit", 40));
            if (start < 0 || start > Int32.MaxValue || start != Math.Truncate(start) || count < 1 || count > 100 || count != Math.Truncate(count))
                throw new Fault("VALIDATION_FAILED", "Object paging requires offset >= 0 and limit between 1 and 100.");
            CheckObjectRevision(value); var array = value.Value as Array;
            if (array == null || array.Rank != 1) return J.Obj("kind", "api_object", "object_id", id, "is_com_object", Marshal.IsComObject(value.Value), "items", new object[0]);
            var created = new List<string>(); var items = new List<object>();
            try
            {
                for (int i = (int)start; i < array.Length && items.Count < (int)count; i++) items.Add(EncodeApiValue(array.GetValue(i), array.GetType().GetElementType(), created, 0));
                foreach (string fresh in created) apiObjects[fresh].Revisions = new Dictionary<string, string>(value.Revisions);
            }
            catch { foreach (string fresh in created) apiObjects.Remove(fresh); throw; }
            return J.Obj("kind", "api_object", "object_id", id, "array_length", array.Length, "offset", (int)start, "items", items);
        }

        internal static void ApiShape(ApiCatalog catalog, Dictionary<string, object> action)
        {
            J.Keys(action, "type", "steps", "expected_active_document_id", "release_objects");
            var steps = J.Items(J.Get(action, "steps"));
            if (steps.Length < 1 || steps.Length > 128) throw new Fault("VALIDATION_FAILED", "api_batch requires 1 to 128 steps.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in steps)
            {
                var step = J.Map(raw);
                J.Keys(step, "id", "target", "interface", "operation", "member", "arguments", "parameter_types", "return_mode");
                if (step.ContainsKey("return_mode") && !new[] { "auto", "reference" }.Contains(J.Str(step, "return_mode"))) throw new Fault("VALIDATION_FAILED", "return_mode must be auto or reference.");
                string id = J.Str(step, "id");
                if (!Regex.IsMatch(id, @"^[A-Za-z][A-Za-z0-9_]{0,47}$") || ids.Contains(id)) throw new Fault("VALIDATION_FAILED", "Step IDs must be unique identifiers of at most 48 characters.");
                if (!step.ContainsKey("target") || step["target"] == null) throw new Fault("VALIDATION_FAILED", "Each API step needs an explicit target.");
                MethodInfo method = catalog.Resolve(step);
                CheckApiValue(catalog, step["target"], ids, 0, true);
                var args = J.Items(J.Get(step, "arguments", new object[0]));
                var parameters = method.GetParameters();
                for (int i = 0; i < args.Length; i++)
                {
                    CheckApiValue(catalog, args[i], ids, 0, false);
                    // Resolve literal values now so a later typo cannot cause a partially executed batch.
                    if (!HasReference(args[i]))
                    {
                        var value = Literal(catalog, args[i]);
                        if (!parameters[i].IsOut) ConvertApiArgument(value, parameters[i]);
                        else if (value != null) throw new Fault("VALIDATION_FAILED", "Use null for out parameter: " + parameters[i].Name);
                    }
                }
                ids.Add(id);
            }
            if (action.ContainsKey("expected_active_document_id") && action["expected_active_document_id"] != null) J.Str(action, "expected_active_document_id");
            if (action.ContainsKey("release_objects"))
                foreach (object id in J.Items(action["release_objects"]))
                    if (!(id is string) || !((string)id).StartsWith("api-", StringComparison.Ordinal)) throw new Fault("VALIDATION_FAILED", "release_objects must contain API object IDs.");
        }
        private static bool HasReference(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) return map.Keys.Any(k => k == "$ref" || k == "$object" || k == "$document") || map.Values.Any(HasReference);
            var array = value as object[]; return array != null && array.Any(HasReference);
        }
        private static void CheckApiValue(ApiCatalog catalog, object value, HashSet<string> previous, int depth, bool target)
        {
            if (depth > 16) throw new Fault("VALIDATION_FAILED", "API values exceed the nesting limit.");
            if (target && value is string)
            {
                if ((string)value != "application") throw new Fault("VALIDATION_FAILED", "The only string target is application; use an explicit object/document/step reference for other targets.");
                return;
            }
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                if (map.ContainsKey("$ref"))
                {
                    J.Keys(map, "$ref", "path");
                    if (!previous.Contains(J.Str(map, "$ref"))) throw new Fault("VALIDATION_FAILED", "A $ref must point to an earlier step.");
                    if (map.ContainsKey("path")) foreach (object item in J.Items(map["path"]))
                        if (!(item is string) && (!(item is int) || (int)item < 0)) throw new Fault("VALIDATION_FAILED", "Reference paths contain field names or nonnegative integer indices.");
                }
                else if (map.ContainsKey("$object")) { J.Keys(map, "$object"); J.Str(map, "$object"); }
                else if (map.ContainsKey("$document")) { J.Keys(map, "$document"); J.Str(map, "$document"); }
                else if (!target && map.ContainsKey("$enum")) { J.Keys(map, "$enum"); catalog.EnumValue(map["$enum"]); }
                else if (!target && map.ContainsKey("$array"))
                {
                    J.Keys(map, "$array", "items"); ArrayElement(J.Str(map, "$array"));
                    var items = J.Items(J.Get(map, "items"));
                    if (items.Length > 16384) throw new Fault("VALIDATION_FAILED", "Input arrays are limited to 16384 entries.");
                    foreach (object item in items) CheckApiValue(catalog, item, previous, depth + 1, false);
                }
                else if (!target && map.ContainsKey("$missing"))
                { J.Keys(map, "$missing"); if (!(map["$missing"] is bool) || !(bool)map["$missing"]) throw new Fault("VALIDATION_FAILED", "$missing must be true."); }
                else throw new Fault("VALIDATION_FAILED", "Unknown API value descriptor.");
                return;
            }
            if (target) throw new Fault("VALIDATION_FAILED", "Invalid API target.");
            var array = value as object[];
            if (array != null)
            {
                if (array.Length > 16384) throw new Fault("VALIDATION_FAILED", "Input arrays are limited to 16384 entries.");
                foreach (object item in array) CheckApiValue(catalog, item, previous, depth + 1, false);
            }
            else if (value != null && !(value is string) && !(value is bool)) J.Number(value);
        }
        private static Type ArrayElement(string name)
        {
            switch (name)
            {
                case "double": return typeof(double);
                case "int": return typeof(int);
                case "long": return typeof(long);
                case "short": return typeof(short);
                case "byte": return typeof(byte);
                case "bool": return typeof(bool);
                case "string": return typeof(string);
                case "object": return typeof(object);
                case "dispatch": return typeof(DispatchWrapper);
                default: throw new Fault("VALIDATION_FAILED", "Array type must be double, int, long, short, byte, bool, string, object or dispatch.");
            }
        }
        private static object Literal(ApiCatalog catalog, object raw)
        {
            var map = raw as Dictionary<string, object>;
            if (map != null)
            {
                if (map.ContainsKey("$enum")) return catalog.EnumValue(map["$enum"]);
                if (map.ContainsKey("$missing")) return Type.Missing;
                if (map.ContainsKey("$array")) return MakeArray(J.Str(map, "$array"), J.Items(map["items"]).Select(v => Literal(catalog, v)).ToArray());
                throw new Fault("VALIDATION_FAILED", "An object reference cannot be evaluated as a literal.");
            }
            var array = raw as object[];
            return array == null ? raw : array.Select(v => Literal(catalog, v)).ToArray();
        }
        private static Array MakeArray(string kind, object[] values)
        {
            Type element = ArrayElement(kind); var result = Array.CreateInstance(element, values.Length);
            for (int i = 0; i < values.Length; i++) result.SetValue(element == typeof(DispatchWrapper) ? new DispatchWrapper(values[i]) : ConvertApiValue(values[i], element), i);
            return result;
        }
        private static object ConvertApiArgument(object value, ParameterInfo p)
        {
            if (value == Type.Missing)
            {
                if (!p.IsOptional) throw new Fault("VALIDATION_FAILED", "Parameter is not optional: " + p.Name);
                return Type.Missing;
            }
            Type type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
            return p.IsOut ? (type.IsValueType ? Activator.CreateInstance(type) : null) : ConvertApiValue(value, type);
        }
        private static object ConvertApiValue(object value, Type type)
        {
            if (value == null)
            {
                if (type.IsValueType) throw new Fault("VALIDATION_FAILED", "null is not valid for " + type.FullName);
                return null;
            }
            if (type == typeof(object) || type.IsInstanceOfType(value)) return value;
            if (type.IsArray)
            {
                var array = value as Array;
                if (array == null || array.Rank != 1) throw new Fault("VALIDATION_FAILED", "Expected a one-dimensional array for " + type.FullName);
                Type element = type.GetElementType(); var converted = Array.CreateInstance(element, array.Length);
                for (int i = 0; i < array.Length; i++) converted.SetValue(ConvertApiValue(array.GetValue(i), element), i);
                return converted;
            }
            if (type.IsEnum) return Enum.ToObject(type, ConvertApiValue(value, Enum.GetUnderlyingType(type)));
            if (type.IsInterface || Marshal.IsComObject(value)) throw new Fault("VALIDATION_FAILED", "The API object does not implement " + type.FullName);
            if (type == typeof(string) || type == typeof(bool)) throw new Fault("VALIDATION_FAILED", "Expected an exact " + type.Name + " value.");
            if (value is string || value is bool) throw new Fault("VALIDATION_FAILED", "Expected a numeric value for " + type.FullName);
            try
            {
                double number = J.Number(value);
                if (type != typeof(double) && type != typeof(float) && type != typeof(decimal) && number != Math.Truncate(number))
                    throw new Fault("VALIDATION_FAILED", "An integer parameter cannot accept a fractional value.");
                object result = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
                if (result is float && (Single.IsInfinity((float)result) || Single.IsNaN((float)result))) throw new OverflowException();
                return result;
            }
            catch (Fault) { throw; }
            catch { throw new Fault("VALIDATION_FAILED", "Value cannot be represented as " + type.FullName); }
        }
        private Dictionary<string, string> ApiRevisions()
        {
            RefreshDocuments(); return docs.Values.ToDictionary(d => d.Id, Revision, StringComparer.Ordinal);
        }
        private ApiObject ApiLookup(string id)
        {
            ApiObject value;
            if (!apiObjects.TryGetValue(id, out value) || value.Session != SessionId) throw new Fault("DOC_REVISION_MISMATCH", "API object is absent or belongs to an earlier session: " + id);
            return value;
        }
        private void CheckObjectRevision(ApiObject value)
        {
            foreach (var item in value.Revisions)
                if (Revision(Find(item.Key)) != item.Value) throw new Fault("DOC_REVISION_MISMATCH", "An API object's document changed. Reacquire the object from the current document.");
        }
        private void ApiPreconditions(Dictionary<string, object> args)
        {
            var action = J.Map(args["action"]); ApiShape(Api, action);
            var expected = new HashSet<string>(J.Items(args["expected_documents"]).Select(x => J.Str(J.Map(x), "document_id")));
            RefreshDocuments();
            if (docs.Keys.Any(id => !expected.Contains(id))) throw new Fault("VALIDATION_FAILED", "api_batch requires expected_documents for every currently open document, because application-level methods can affect any of them.");
            if (action.ContainsKey("expected_active_document_id"))
            {
                var active = docs.Values.FirstOrDefault(d => Object.ReferenceEquals(d.Doc, app.IActiveDoc2));
                if ((active == null ? null : active.Id) != (action["expected_active_document_id"] as string))
                    throw new Fault("DOC_REVISION_MISMATCH", "The active document changed before API execution.");
            }
            WalkDescriptors(action["steps"], map => {
                if (map.ContainsKey("$document") && !expected.Contains(J.Str(map, "$document"))) throw new Fault("VALIDATION_FAILED", "Document references must appear in expected_documents.");
                if (map.ContainsKey("$object")) CheckObjectRevision(ApiLookup(J.Str(map, "$object")));
            });
            if (action.ContainsKey("release_objects")) foreach (string id in J.Items(action["release_objects"])) ApiLookup(id);
        }
        private static void WalkDescriptors(object value, Action<Dictionary<string, object>> action)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) { action(map); foreach (var item in map.Values) WalkDescriptors(item, action); }
            var array = value as object[]; if (array != null) foreach (var item in array) WalkDescriptors(item, action);
        }
        private object ResolveApiValue(object raw, Dictionary<string, Dictionary<string, object>> previous)
        {
            var map = raw as Dictionary<string, object>;
            if (map != null)
            {
                if (map.ContainsKey("$object")) return ApiLookup(J.Str(map, "$object")).Value;
                if (map.ContainsKey("$document")) return Find(J.Str(map, "$document")).Doc;
                if (map.ContainsKey("$ref"))
                {
                    object result = previous[J.Str(map, "$ref")];
                    var path = J.Get(map, "path") == null ? new object[] { "return_value" } : J.Items(map["path"]);
                    foreach (object segment in path)
                    {
                        var record = result as Dictionary<string, object>; var array = result as Array;
                        if (segment is string && record != null && record.ContainsKey((string)segment)) result = record[(string)segment];
                        else if (segment is int && array != null && array.Rank == 1 && (int)segment >= 0 && (int)segment < array.Length) result = array.GetValue((int)segment);
                        else throw new Fault("VALIDATION_FAILED", "A step result path does not resolve.");
                    }
                    return result;
                }
                if (map.ContainsKey("$array")) return MakeArray(J.Str(map, "$array"), J.Items(map["items"]).Select(v => ResolveApiValue(v, previous)).ToArray());
                return Literal(Api, raw);
            }
            var items = raw as object[];
            return items == null ? raw : items.Select(v => ResolveApiValue(v, previous)).ToArray();
        }
        private object EncodeApiValue(object value, Type declared, List<string> created, int depth, bool reference = false)
        {
            if (value == null || value == DBNull.Value) return null;
            if (depth > 16) throw new Fault("OUTCOME_UNKNOWN", "API result nesting exceeded the transport limit; inspect the job and CAD state.");
            if (value is string || value is bool || value.GetType().IsPrimitive || value is decimal) return value;
            if (value.GetType().IsEnum) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is DateTime) return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            var array = value as Array;
            if (array != null && array.Rank == 1 && array.Length <= 2048 && !reference)
            {
                var items = new object[array.Length];
                for (int i = 0; i < items.Length; i++) items[i] = EncodeApiValue(array.GetValue(i), array.GetType().GetElementType(), created, depth + 1);
                return items;
            }
            if (!Marshal.IsComObject(value) && array == null) throw new Fault("OUTCOME_UNKNOWN", "The API returned an unsupported managed value: " + value.GetType().FullName);
            if (apiObjects.Count >= 4096) throw new Fault("QUEUE_FULL", "API object capacity reached. Use release_objects to release unneeded references.");
            string id = "api-" + Guid.NewGuid().ToString("N");
            apiObjects.Add(id, new ApiObject { Value = value, Session = SessionId, Revisions = new Dictionary<string, string>() }); created.Add(id);
            return J.Obj("object_id", id, "session_id", SessionId, "declared_type", declared.FullName,
                "array_length", array == null ? (object)null : array.Length,
                "lifetime", "Current bridge session; reacquire after document changes. Use $object to pass this value.");
        }
        private void ExecuteApi(Dictionary<string, object> args, JobRecord job)
        {
            var action = J.Map(args["action"]); var results = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var created = new List<string>(); bool success = false;
            try
            {
                foreach (object raw in J.Items(action["steps"]))
                {
                    job.ThrowIfCancelled(); var step = J.Map(raw); MethodInfo method = Api.Resolve(step);
                    string id = J.Str(step, "id"); job.Step("api:" + id + ":" + method.DeclaringType.Name + "." + method.Name);
                    object target = step["target"] is string ? app : ResolveApiValue(step["target"], results);
                    if (target == null || !method.DeclaringType.IsInstanceOfType(target)) throw new Fault("VALIDATION_FAILED", "The target does not implement " + method.DeclaringType.FullName);
                    var parameters = method.GetParameters(); var supplied = J.Items(J.Get(step, "arguments", new object[0]));
                    var converted = supplied.Select((v, i) => ConvertApiArgument(ResolveApiValue(v, results), parameters[i])).ToArray();
                    object returned;
                    job.SetApiEffectsUnknown();
                    try { returned = method.Invoke(target, converted); }
                    catch (TargetInvocationException ex)
                    {
                        var com = ex.InnerException as COMException;
                        throw new Fault("API_FAILED", method.DeclaringType.Name + "." + method.Name + ": " + (ex.InnerException ?? ex).Message)
                        { ApiMethod = method.DeclaringType.FullName + "." + method.Name, ApiStatus = com == null ? (object)null : com.ErrorCode };
                    }
                    var outputs = new Dictionary<string, object>(); var encoded = new Dictionary<string, object>();
                    for (int i = 0; i < parameters.Length; i++) if (parameters[i].ParameterType.IsByRef || parameters[i].IsOut)
                    {
                        outputs.Add(parameters[i].Name, converted[i]);
                        encoded.Add(parameters[i].Name, EncodeApiValue(converted[i], parameters[i].ParameterType.GetElementType(), created, 0));
                    }
                    results.Add(id, J.Obj("return_value", returned, "out_parameters", outputs));
                    job.AddApiResult(J.Obj("id", id, "interface", method.DeclaringType.FullName, "member", method.Name,
                        "return_value", EncodeApiValue(returned, method.ReturnType, created, 0, (string)J.Get(step, "return_mode", "auto") == "reference"), "out_parameters", encoded,
                        "api_call_completed", true, "cad_result_verified", false));
                }
                var finalRevisions = ApiRevisions();
                foreach (string id in created) apiObjects[id].Revisions = new Dictionary<string, string>(finalRevisions);
                if (action.ContainsKey("release_objects")) foreach (string id in J.Items(action["release_objects"])) apiObjects.Remove(id);
                job.AddCheck(J.Check("API_BATCH_COMPLETED", "pass", "official Interop invocation and ref/out readback", "All API calls returned. Individual return codes, feature errors, geometry, saves and licensing still require task-specific verification."));
                job.SetDocuments(docs.Values.Select(Summary).Cast<object>().ToArray()); success = true;
            }
            finally
            {
                // A failed batch may have changed CAD. Its intermediate references must not be reused.
                if (!success) foreach (string id in created) apiObjects.Remove(id);
            }
        }
    }
}
