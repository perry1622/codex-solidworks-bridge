using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// Reads local installation information and assembly metadata only.
// It never starts SolidWorks, creates a COM instance, or invokes a model API.
internal static class SwApiProbe
{
    private const string Version = "0.1.1";
    private static readonly List<string> Warnings = new List<string>();
    private static readonly Dictionary<string, List<string>> Candidates =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Libraries = {
        "SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll",
        "SolidWorks.Interop.swpublished.dll"
    };

    private static Dictionary<string, object> D(params object[] pairs)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]);
        return d;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string output = null;
        object result;
        int exitCode = 0;
        try
        {
            var options = Parse(args);
            options.TryGetValue("--output", out output);
            if (options.ContainsKey("--help"))
                result = D("tool", "sw-api-probe", "version", Version,
                    "usage", new[] {
                        "sw-api-probe.exe",
                        "sw-api-probe.exe --output installation.json",
                        "sw-api-probe.exe --install-dir <local SolidWorks directory>",
                        "sw-api-probe.exe --list-types",
                        "sw-api-probe.exe --type IFeatureManager --member FeatureExtrusion3",
                        "sw-api-probe.exe --type swEndConditions_e"
                    });
            else
            {
                string explicitRoot;
                if (options.TryGetValue("--install-dir", out explicitRoot))
                    AddCandidate(explicitRoot, "explicit_argument");
                else Discover();
                var installations = Candidates.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                    .Where(c => File.Exists(Path.Combine(c.Key, Libraries[0])) ||
                                File.Exists(Path.Combine(c.Key, "api", "redist", Libraries[0])))
                    .Select(c => DescribeInstallation(c.Key, c.Value)).ToArray();
                result = D("schema_version", "1.0", "tool_version", Version,
                    "status", installations.Length == 0 ? "not_found" : "ok",
                    "read_only", true, "model_connection_verified", false,
                    "installations", installations, "toolbox_libraries", ToolboxLibraries(), "warnings", Warnings);
                if (installations.Length == 0) exitCode = 1;
                if (options.ContainsKey("--type") || options.ContainsKey("--list-types"))
                {
                    if (installations.Length == 0)
                        throw new InvalidOperationException("No local API libraries found. Use --install-dir.");
                    if (installations.Length != 1)
                        throw new InvalidOperationException("Multiple installations found. Select one with --install-dir.");
                    var root = (string)installations[0]["install_dir"];
                    ((Dictionary<string, object>)result)["api"] = ReadMetadata(root, options);
                }
            }
        }
        catch (Exception e)
        {
            result = D("schema_version", "1.0", "tool_version", Version,
                "status", "error", "read_only", true, "model_connection_verified", false,
                "error", D("type", e.GetType().Name, "message", e.Message), "warnings", Warnings);
            exitCode = 2;
        }
        var json = new JavaScriptSerializer { MaxJsonLength = 16000000 }.Serialize(result);
        if (output != null)
        {
            try
            {
                var path = LocalPath(output);
                var parent = Path.GetDirectoryName(path);
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                if (File.Exists(path)) throw new IOException("Output exists; choose a new output file.");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.WriteLine(json);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Output could not be written: " + e.Message);
                Console.WriteLine(json);
                return 3;
            }
        }
        Console.WriteLine(json);
        return exitCode;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (d.ContainsKey(key)) throw new ArgumentException("Duplicate argument: " + key);
            if (key == "--help" || key == "--list-types") d.Add(key, "true");
            else if (key == "--output" || key == "--install-dir" || key == "--type" || key == "--member")
            {
                if (++i >= args.Length || args[i].StartsWith("--"))
                    throw new ArgumentException("Missing value for " + key);
                d.Add(key, args[i]);
            }
            else throw new ArgumentException("Unknown argument: " + key);
        }
        if (d.ContainsKey("--member") && !d.ContainsKey("--type"))
            throw new ArgumentException("--member requires --type.");
        if (d.ContainsKey("--list-types") && d.ContainsKey("--type"))
            throw new ArgumentException("Choose --list-types or --type.");
        return d;
    }

    private static string LocalPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (expanded.StartsWith(@"\\") || expanded.IndexOf("://", StringComparison.Ordinal) >= 0)
            throw new ArgumentException("Only local filesystem paths are accepted.");
        var path = Path.GetFullPath(expanded);
        if (new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network)
            throw new ArgumentException("Network drives are not supported.");
        return path;
    }

    private static void AddCandidate(string value, string evidence)
    {
        if (String.IsNullOrWhiteSpace(value)) return;
        try
        {
            var root = LocalPath(value.Trim().Trim('"'));
            foreach (var p in new[] { root, Path.Combine(root, "SOLIDWORKS") })
            {
                var normalized = p.TrimEnd(Path.DirectorySeparatorChar);
                List<string> sources;
                if (!Candidates.TryGetValue(normalized, out sources))
                    Candidates.Add(normalized, sources = new List<string>());
                if (!sources.Contains(evidence)) sources.Add(evidence);
            }
        }
        catch (Exception e) { Warnings.Add("Skipped installation hint: " + e.Message); }
    }

    private static void Discover()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                {
                    foreach (var progId in classes.GetSubKeyNames().Where(n =>
                        n.Equals("SldWorks.Application", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("SldWorks.Application.", StringComparison.OrdinalIgnoreCase)))
                    using (var clsidKey = classes.OpenSubKey(progId + @"\CLSID"))
                    {
                        var clsid = clsidKey == null ? null : clsidKey.GetValue(null) as string;
                        if (clsid == null) continue;
                        using (var server = classes.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32"))
                        {
                            var command = server == null ? null : server.GetValue(null) as string;
                            if (String.IsNullOrWhiteSpace(command)) continue;
                            command = Environment.ExpandEnvironmentVariables(command.Trim());
                            string exe;
                            if (command.StartsWith("\""))
                            {
                                var end = command.IndexOf('"', 1);
                                if (end < 1) continue;
                                exe = command.Substring(1, end - 1);
                            }
                            else
                            {
                                var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                                if (end < 0) continue;
                                exe = command.Substring(0, end + 4);
                            }
                            AddCandidate(Path.GetDirectoryName(exe), "registry:" + progId + ":" + view);
                        }
                    }
                }
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                using (var machine = RegistryKey.OpenBaseKey(hive, view))
                using (var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    using (var item = uninstall.OpenSubKey(name))
                    {
                        if (item == null) continue;
                        var display = item.GetValue("DisplayName") as string;
                        if (display != null && display.IndexOf("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) >= 0)
                            AddCandidate(item.GetValue("InstallLocation") as string, "uninstall:" + view);
                    }
                }
            }
            catch (Exception e) { Warnings.Add("Some registry hints unavailable: " + e.Message); }
        }
        foreach (var process in Process.GetProcessesByName("SLDWORKS"))
        using (process)
        {
            try { AddCandidate(Path.GetDirectoryName(process.MainModule.FileName), "running_process"); }
            catch { if (!Warnings.Contains("Some process paths were inaccessible; no process was started or modified."))
                Warnings.Add("Some process paths were inaccessible; no process was started or modified."); }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!String.IsNullOrEmpty(root))
            {
                AddCandidate(Path.Combine(root, "SOLIDWORKS Corp", "SOLIDWORKS"), "default_location");
                AddCandidate(Path.Combine(root, "SOLIDWORKS Corp", "SOLIDWORKS 2026"), "default_location");
            }
    }

    private static string LibraryPath(string root, string file)
    {
        foreach (var p in new[] { Path.Combine(root, file), Path.Combine(root, "api", "redist", file) })
            if (File.Exists(p)) return p;
        return null;
    }
    private static object[] ToolboxLibraries()
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        try
        {
            using (var machine = RegistryKey.OpenBaseKey(hive, view))
            using (var installed = machine.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (installed == null) continue;
                foreach (var version in installed.GetSubKeyNames().Where(n => n.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase)))
                using (var general = installed.OpenSubKey(version + @"\General"))
                {
                    var value = general == null ? null : general.GetValue("Toolbox Data Location") as string;
                    if (String.IsNullOrWhiteSpace(value)) continue;
                    string path = LocalPath(value).TrimEnd(Path.DirectorySeparatorChar);
                    result[path] = D("data_dir", path, "browser_dir", Path.Combine(path, "browser"), "registered_version", version,
                        "exists", Directory.Exists(path), "geometry_or_size_verified", false);
                }
            }
        }
        catch (Exception e) { Warnings.Add("Some Toolbox library hints unavailable: " + e.Message); }
        return result.Values.ToArray();
    }

    private static Dictionary<string, object> DescribeInstallation(string root, List<string> evidence)
    {
        var dlls = new List<object>();
        foreach (var name in Libraries)
        {
            var path = LibraryPath(root, name);
            if (path == null) continue;
            var version = FileVersionInfo.GetVersionInfo(path);
            dlls.Add(D("name", name, "path", path, "file_version", version.FileVersion));
        }
        var docs = new List<string>();
        foreach (var file in new[] { "sldworksapi.chm", "sldworksapiprogguide.chm", "swconst.chm" })
        {
            var p = Path.Combine(root, "api", file);
            if (File.Exists(p)) docs.Add(p);
        }
        var exe = Path.Combine(root, "SLDWORKS.exe");
        return D("install_dir", root, "sources", evidence, "libraries", dlls,
            "executable", File.Exists(exe) ? exe : null,
            "product_version", File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null,
            "local_help", docs, "bridge_loaded", "unknown", "compatibility", "not_verified");
    }

    private static object ReadMetadata(string root, Dictionary<string, string> options)
    {
        ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs e)
        {
            var simple = new AssemblyName(e.Name).Name + ".dll";
            var local = LibraryPath(root, simple);
            return local == null ? Assembly.ReflectionOnlyLoad(e.Name) : Assembly.ReflectionOnlyLoadFrom(local);
        };
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
        try
        {
            var types = new List<Type>();
            foreach (var file in Libraries)
            {
                var path = LibraryPath(root, file);
                if (path == null) continue;
                try { types.AddRange(Assembly.ReflectionOnlyLoadFrom(path).GetTypes()); }
                catch (ReflectionTypeLoadException e)
                {
                    types.AddRange(e.Types.Where(t => t != null));
                    Warnings.Add("Some API metadata dependencies could not be resolved.");
                }
            }
            if (options.ContainsKey("--list-types"))
                return D("metadata_only", true, "types", types.Where(t => t.IsInterface || t.IsEnum)
                    .Select(t => D("name", t.Name, "full_name", t.FullName, "kind", t.IsEnum ? "enum" : "interface"))
                    .OrderBy(t => (string)t["full_name"]).ToArray());
            string wanted = options["--type"];
            var found = types.Where(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                t.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length != 1) throw new ArgumentException("Type not found or ambiguous: " + wanted);
            var type = found[0];
            string member;
            options.TryGetValue("--member", out member);
            var methods = type.GetMethods().Where(m => member == null ||
                m.Name.Equals(member, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("get_" + member, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("set_" + member, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (member != null && methods.Length == 0)
                throw new ArgumentException("Member not found: " + wanted + "." + member);
            var description = D("metadata_only", true, "assembly", type.Assembly.FullName,
                "type", type.FullName, "kind", type.IsEnum ? "enum" : "interface",
                "methods", methods.Select(m => D("name", m.Name, "return_type", m.ReturnType.FullName,
                    "parameters", m.GetParameters().Select(p => D("name", p.Name,
                        "type", p.ParameterType.FullName, "is_out", p.IsOut,
                        "is_by_ref", p.ParameterType.IsByRef, "is_optional", p.IsOptional)).ToArray())).ToArray());
            if (type.IsEnum)
                description["enum_values"] = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(f => f.IsLiteral).Select(f => D("name", f.Name, "value", f.GetRawConstantValue())).ToArray();
            return description;
        }
        finally { AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver; }
    }
}
