using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.PackageManager;

namespace UnityPipeline.ProjectAuditor.Editor
{
    [InitializeOnLoad]
    public static class ProjectAuditCommands
    {
        static readonly AuditStore Store = new AuditStore();
        static AuditStore.Scan pending;
        static string[] categories;
        static ProjectAuditorAdapter adapter;

        static ProjectAuditCommands() { EditorApplication.update += Update; }

        [CliCommand("project_audit_start", "Queue structured static analysis. All categories by default. Poll project_audit_status, then page project_audit_results. No fixes, CSV export, or project setting changes. One scan at a time; compilation can briefly occupy the Editor main thread.", MainThreadRequired = false, Tags = new[] { "extensions/audit" })]
        public static object Start([CliArg("categories", "Optional comma-separated Auditor categories; invalid names are rejected.")] string categories = "")
        {
            return Guard(() => {
                var resolved = new ProjectAuditorAdapter();
                var requested = resolved.ValidateCategories(categories);
                lock (Store.Gate)
                {
                    var scan = Store.Begin();
                    adapter = resolved; ProjectAuditCommands.categories = requested; pending = scan;
                    return Store.Status(scan.Id);
                }
            });
        }

        [CliCommand("project_audit_status", "Read session-local audit status and coverage evidence, without waiting for the Editor main thread. Empty scan_id selects the latest retained scan. Lost/expired IDs require a new scan.", MainThreadRequired = false, Tags = new[] { "extensions/audit" })]
        public static object Status([CliArg("scan_id", "Scan identity returned by project_audit_start; optional for latest.")] string scan_id = "") => Guard(() => Store.Status(scan_id));

        [CliCommand("project_audit_results", "Read immutable structured findings, not CSV. Follow next_offset until null. Severity/category filters match exact names ignoring case. Findings are snapshot evidence, not current runtime health.", MainThreadRequired = false, Tags = new[] { "extensions/audit" })]
        public static object Results(
            [CliArg("scan_id", "Required retained scan identity.")] string scan_id,
            [CliArg("offset", "Zero-based offset within filtered results.")] int offset = 0,
            [CliArg("limit", "Maximum items, 1..100; response byte limit may shorten a page.")] int limit = 50,
            [CliArg("category", "Optional exact category name.")] string category = "",
            [CliArg("severity", "Optional exact severity name.")] string severity = "") => Guard(() => Store.Results(scan_id, offset, limit, category, severity));

        [CliCommand("project_audit_dispose", "Release a terminal scan's retained findings. Does not delete files or cancel running analysis. Results will no longer be available to any client.", MainThreadRequired = false, Tags = new[] { "extensions/audit" })]
        public static object Dispose([CliArg("scan_id", "Required terminal scan identity.")] string scan_id) => Guard(() => Store.Dispose(scan_id));

        static void Update()
        {
            AuditStore.Scan scan; ProjectAuditorAdapter resolved; string[] requested;
            lock (Store.Gate)
            {
                if (pending == null) return;
                scan = pending; pending = null; resolved = adapter; requested = categories;
                scan.Status = "scanning";
            }
            try
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("EDITOR_BUSY: stop Play Mode and wait for compilation/import before starting an audit");
                var installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                if (resolved.AssemblyIdentity.StartsWith("UnityEditor.ProjectAuditorModule", StringComparison.Ordinal) &&
                    !installed.Any(p => p.name == "com.unity.project-auditor-rules"))
                    throw new NotSupportedException("Built-in Project Auditor requires com.unity.project-auditor-rules. Install the rules before scanning; missing rules is not a clean audit.");
                var invoke = resolved.Start(requested, report => {
                    try
                    {
                        lock (Store.Gate)
                        {
                            if (scan.CompletedModules.Count == 0) throw new NotSupportedException("No completed analysis modules were reported. Coverage cannot be verified; check Auditor rules and categories.");
                            scan.Evidence["coverage"] = "Completed modules reported by Auditor; only diagnostic issues retained, not inventory insights.";
                        }
                        Store.Complete(scan, resolved.Snapshot(report));
                    }
                    catch (Exception ex) { Failure(scan, ex); }
                }, (module, success) => {
                    lock (Store.Gate)
                    {
                        if (!success || scan.CompletedModules.Count >= 128) scan.HasFailedModule = true;
                        if (scan.CompletedModules.Count < 128) scan.CompletedModules.Add(module.Length > 256 ? module.Substring(0, 256) : module);
                    }
                }, out var evidence);
                evidence["unity_version"] = UnityEngine.Application.unityVersion;
                evidence["editor_build_target"] = EditorUserBuildSettings.activeBuildTarget.ToString();
                evidence["project_path"] = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
                evidence["extension_version"] = "0.1.0";
                evidence["rules_package_version"] = installed.FirstOrDefault(p => p.name == "com.unity.project-auditor-rules")?.version;
                lock (Store.Gate) scan.Evidence = evidence;
                invoke();
            }
            catch (Exception ex) { Failure(scan, ex); }
        }

        static void Failure(AuditStore.Scan scan, Exception ex)
        {
            var cause = ex.GetBaseException();
            Store.Fail(scan, cause is NotSupportedException ? "AUDITOR_UNAVAILABLE" : "AUDIT_FAILED", cause.Message,
                cause is NotSupportedException ? "unavailable" : "failed");
        }
        static object Guard(Func<JObject> action)
        {
            try { return action(); }
            catch (Exception ex)
            {
                var cause = ex.GetBaseException();
                return new JObject { ["schema_version"] = AuditStore.Schema, ["success"] = false,
                    ["status"] = cause is NotSupportedException ? "unavailable" : "error",
                    ["error_code"] = cause is NotSupportedException ? "AUDITOR_UNAVAILABLE" : cause is ArgumentException ? "INVALID_ARGUMENT" : cause is System.Collections.Generic.KeyNotFoundException ? "AUDIT_SCAN_NOT_FOUND" : "AUDIT_STATE_CONFLICT",
                    ["error"] = cause.Message };
            }
        }
    }
}
