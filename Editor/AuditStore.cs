using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityPipeline.ProjectAuditor.Editor
{
    // Only bounded JSON snapshots are retained: a Report's dependency graph can be arbitrarily large.
    // All access (including callbacks) is serialized by Gate. Never call Unity APIs under this lock.
    public sealed class AuditStore
    {
        public const string Schema = "unity-pipeline-project-audit/v1";
        public const int MaxRows = 10000;
        public const int MaxSnapshotBytes = 8 * 1024 * 1024;
        public readonly object Gate = new object();
        readonly Dictionary<string, Scan> scans = new Dictionary<string, Scan>();
        readonly Func<DateTime> clock;
        readonly int capacity;
        readonly TimeSpan ttl;
        public readonly string SessionId = Guid.NewGuid().ToString("N");

        public sealed class Scan
        {
            public string Id, Status = "queued", ErrorCode, Error;
            public DateTime Created, Finished;
            public JObject Evidence = new JObject();
            public List<string> Rows = new List<string>();
            public HashSet<string> CompletedModules = new HashSet<string>(StringComparer.Ordinal);
            public bool HasFailedModule;
            public bool Active => Status == "queued" || Status == "scanning";
        }

        public AuditStore(int capacity = 3, double ttlMinutes = 30, Func<DateTime> clock = null)
        {
            this.capacity = Math.Max(1, capacity);
            ttl = TimeSpan.FromMinutes(ttlMinutes);
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public Scan Begin()
        {
            lock (Gate)
            {
                Prune();
                if (scans.Values.Any(s => s.Active)) throw new InvalidOperationException("AUDIT_BUSY");
                while (scans.Count >= capacity) scans.Remove(scans.Values.OrderBy(s => s.Created).First().Id);
                var scan = new Scan { Id = SessionId + "-" + Guid.NewGuid().ToString("N"), Created = clock() };
                scans.Add(scan.Id, scan);
                return scan;
            }
        }

        public void Complete(Scan scan, IEnumerable<JObject> rows)
        {
            lock (Gate)
                if (scan.HasFailedModule) throw new InvalidOperationException("AUDIT_MODULE_FAILED: a module failed, was cancelled, or returned an unverified outcome; do not treat this as a clean scan.");
            var snapshot = new List<string>();
            long bytes = 0;
            foreach (var row in rows)
            {
                var json = row.ToString(Formatting.None);
                bytes += Encoding.Unicode.GetByteCount(json) + 256;
                if (snapshot.Count >= MaxRows || bytes > MaxSnapshotBytes)
                    throw new InvalidOperationException("AUDIT_RESULT_LIMIT: narrow the requested categories and rerun; no partial success was retained.");
                snapshot.Add(json);
            }
            lock (Gate)
            {
                scan.Rows = snapshot;
                scan.Status = "completed";
                scan.Finished = clock();
            }
        }

        public void Fail(Scan scan, string code, string error, string status = "failed")
        {
            lock (Gate)
            {
                scan.Rows.Clear(); scan.Status = status; scan.ErrorCode = code;
                scan.Error = error.Length > 2048 ? error.Substring(0, 2048) : error;
                scan.Finished = clock();
            }
        }

        public JObject Status(string id = "")
        {
            lock (Gate)
            {
                Prune();
                var scan = string.IsNullOrEmpty(id) ? scans.Values.OrderByDescending(s => s.Created).FirstOrDefault() : Find(id);
                if (scan == null) return new JObject { ["schema_version"] = Schema, ["editor_session_id"] = SessionId, ["status"] = "idle" };
                return new JObject {
                    ["schema_version"] = Schema, ["scan_id"] = scan.Id, ["editor_session_id"] = SessionId,
                    ["status"] = scan.Status, ["created_at"] = scan.Created.ToString("O"),
                    ["finished_at"] = scan.Active ? null : scan.Finished.ToString("O"),
                    ["issue_count"] = scan.Status == "completed" ? new JValue(scan.Rows.Count) : JValue.CreateNull(),
                    ["completed_modules"] = new JArray(scan.CompletedModules.OrderBy(x => x)),
                    ["evidence"] = scan.Evidence.DeepClone(), ["error_code"] = scan.ErrorCode, ["error"] = scan.Error,
                    ["retention"] = "session-local; terminal scans expire after 30 minutes or capacity eviction"
                };
            }
        }

        public JObject Results(string id, int offset, int limit, string category, string severity)
        {
            if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("offset must be >= 0 and limit must be 1..100");
            lock (Gate)
            {
                Prune(); var scan = Find(id);
                if (scan.Status != "completed") throw new InvalidOperationException("AUDIT_NOT_COMPLETED");
                // Stable order is the frozen report order, never a changing live Report enumeration.
                var rows = scan.Rows.Select(JObject.Parse).Where(r =>
                    (string.IsNullOrEmpty(category) || string.Equals((string)r["category"], category, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(severity) || string.Equals((string)r["severity"], severity, StringComparison.OrdinalIgnoreCase))).ToList();
                var page = new JArray(); var pageBytes = 0; var index = offset;
                for (; index < rows.Count && page.Count < limit; index++)
                {
                    var row = rows[index]; var size = Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                    if (page.Count > 0 && pageBytes + size > 128 * 1024) break;
                    page.Add(row); pageBytes += size;
                }
                return new JObject {
                    ["schema_version"] = Schema, ["scan_id"] = id, ["total_count"] = scan.Rows.Count,
                    ["filtered_count"] = rows.Count, ["offset"] = offset,
                    ["next_offset"] = index < rows.Count ? new JValue(index) : JValue.CreateNull(), ["items"] = page,
                    ["evidence"] = scan.Evidence.DeepClone()
                };
            }
        }

        public JObject Dispose(string id)
        {
            lock (Gate)
            {
                Prune(); var scan = Find(id);
                if (scan.Active) throw new InvalidOperationException("AUDIT_BUSY: disposal does not cancel analysis");
                scans.Remove(id);
                return new JObject { ["scan_id"] = id, ["disposed"] = true };
            }
        }

        Scan Find(string id)
        {
            if (string.IsNullOrEmpty(id) || !scans.TryGetValue(id, out var scan))
                throw new KeyNotFoundException("AUDIT_SCAN_NOT_FOUND: expired, disposed, unknown, or interrupted by an Editor/domain restart; start a new scan.");
            return scan;
        }
        void Prune()
        {
            foreach (var id in scans.Values.Where(s => !s.Active && clock() - s.Finished >= ttl).Select(s => s.Id).ToArray()) scans.Remove(id);
        }
    }
}
