using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityPipeline.ProjectAuditor.Editor;

namespace UnityPipeline.ProjectAuditor.Tests
{
    public class AuditTests
    {
        static JObject Row(string category = "Code", string severity = "Warning") => new JObject {
            ["category"] = category, ["severity"] = severity, ["description"] = "test"
        };

        [Test] public void RejectsConcurrentScanAndActiveDisposal()
        {
            var store = new AuditStore(); var scan = store.Begin();
            Assert.Throws<InvalidOperationException>(() => store.Begin());
            Assert.Throws<InvalidOperationException>(() => store.Dispose(scan.Id));
            Assert.AreEqual("queued", (string)store.Status(scan.Id)["status"]);
        }
        [Test] public void PagesFrozenResultsWithStableFilters()
        {
            var store = new AuditStore(); var scan = store.Begin(); var row = Row();
            store.Complete(scan, new[] { row, Row("Texture"), Row() }); row["category"] = "Changed";
            var page = store.Results(scan.Id, 0, 1, "code", "warning");
            Assert.AreEqual(3, (int)page["total_count"]); Assert.AreEqual(2, (int)page["filtered_count"]);
            Assert.AreEqual(1, (int)page["next_offset"]);
            Assert.AreEqual(JTokenType.Null, store.Results(scan.Id, 1, 1, "code", "warning")["next_offset"].Type);
            Assert.AreEqual(0, ((JArray)store.Results(scan.Id, 0, 10, "Missing", "")["items"]).Count);
        }
        [Test] public void RejectsInvalidPagesAndUnavailableResults()
        {
            var store = new AuditStore(); var scan = store.Begin();
            Assert.Throws<ArgumentException>(() => store.Results(scan.Id, -1, 1, "", ""));
            Assert.Throws<ArgumentException>(() => store.Results(scan.Id, 0, 101, "", ""));
            Assert.Throws<InvalidOperationException>(() => store.Results(scan.Id, 0, 1, "", ""));
        }
        [Test] public void ExpiryEvictionAndDisposalAreExplicit()
        {
            var now = DateTime.UtcNow; var store = new AuditStore(1, 1, () => now);
            var first = store.Begin(); store.Complete(first, Array.Empty<JObject>());
            var second = store.Begin(); Assert.Throws<KeyNotFoundException>(() => store.Status(first.Id));
            store.Fail(second, "TEST", "failure"); now += TimeSpan.FromMinutes(2);
            Assert.Throws<KeyNotFoundException>(() => store.Status(second.Id));
            var third = store.Begin(); store.Complete(third, Array.Empty<JObject>()); store.Dispose(third.Id);
            Assert.Throws<KeyNotFoundException>(() => store.Results(third.Id, 0, 1, "", ""));
        }
        [Test] public void SessionRestartCannotAliasAnOldScan()
        {
            var first = new AuditStore().Begin(); var other = new AuditStore();
            Assert.AreNotEqual(first.Id, other.Begin().Id);
            Assert.Throws<KeyNotFoundException>(() => other.Status(first.Id));
        }
        [Test] public void OversizeReportsNeverBecomePartialSuccess()
        {
            var store = new AuditStore(); var scan = store.Begin();
            Assert.Throws<InvalidOperationException>(() => store.Complete(scan, Enumerable.Repeat(Row(), AuditStore.MaxRows + 1)));
            Assert.AreNotEqual("completed", scan.Status); Assert.IsEmpty(scan.Rows);
            Assert.Throws<InvalidOperationException>(() => store.Complete(scan, new[] { new JObject { ["x"] = new string('x', AuditStore.MaxSnapshotBytes) } }));
        }
        [Test] public void FailedOrUnknownModuleCannotBecomeCleanSuccess()
        {
            var store = new AuditStore(); var scan = store.Begin(); scan.HasFailedModule = true;
            Assert.Throws<InvalidOperationException>(() => store.Complete(scan, Array.Empty<JObject>()));
            Assert.AreNotEqual("completed", scan.Status);
        }
        public class Descriptor { public string Id = "RULE-1"; public string Recommendation = "Fix it"; }
        public class Id { public Descriptor GetDescriptor() => new Descriptor(); }
        public class Item
        {
            public Id Id => new Id(); public string Category => "Code"; public string Severity => "Warning";
            public string Description => new string('x', 4000); public string RelativePath => "Assets/Test.cs";
            public int Line => 7; public object[] CustomProperties => new object[] { 9007199254740993L, new object(), "value" };
            public string[] UpgradeProperties => new[] { "old", "new" }; public bool IsUpgradeIssue => true;
        }
        [Test] public void ExplicitMappingPreservesIdentityAndHandlesOptionalFields()
        {
            var row = ProjectAuditorAdapter.MapItem(new Item());
            Assert.AreEqual("RULE-1", (string)row["rule_id"]); Assert.AreEqual(7, (int)row["line"]);
            Assert.AreEqual("9007199254740993", (string)row["custom_properties"][0]);
            Assert.AreEqual(JTokenType.Null, row["custom_properties"][1].Type);
            Assert.IsTrue(((JArray)row["unsupported_fields"]).Values<string>().Contains("LogLevel"));
            Assert.LessOrEqual(((string)row["description"]).Length, 2048);
            Assert.AreEqual("new", (string)row["upgrade_properties"][1]);
        }
        [Test] public void CommandErrorsAreStructured()
        {
            var error = (JObject)ProjectAuditCommands.Results("missing", -1);
            Assert.IsFalse((bool)error["success"]); Assert.AreEqual("INVALID_ARGUMENT", (string)error["error_code"]);
        }
        [Test] public void InvalidCategoryIsRejectedBeforeScan()
        {
            var adapter = new ProjectAuditorAdapter();
            Assert.Throws<ArgumentException>(() => adapter.ValidateCategories("this_is_not_a_category"));
            Assert.IsEmpty(adapter.ValidateCategories(""));
        }

        // Run only in a disposable test project with Auditor and its rules installed.
        [UnityTest, Category("LiveAuditor")]
        public IEnumerator RealSettingsScanHasCoverageAndStructuredPages()
        {
            return CheckLiveScan("ProjectSetting");
        }
        [UnityTest, Category("LiveAuditor")]
        public IEnumerator RealDefaultScanHasCoverageAndStructuredPages()
        {
            return CheckLiveScan("");
        }
        static IEnumerator CheckLiveScan(string categories)
        {
            var start = (JObject)ProjectAuditCommands.Start(categories);
            Assert.IsNotNull(start["scan_id"], start.ToString());
            var id = (string)start["scan_id"]; var deadline = DateTime.UtcNow.AddMinutes(3);
            JObject status;
            do
            {
                yield return null;
                status = (JObject)ProjectAuditCommands.Status(id);
                Assert.Less(DateTime.UtcNow, deadline, status.ToString());
            } while ((string)status["status"] == "queued" || (string)status["status"] == "scanning");
            Assert.AreEqual("completed", (string)status["status"], status.ToString());
            Assert.Greater(((JArray)status["completed_modules"]).Count, 0);
            var results = (JObject)ProjectAuditCommands.Results(id);
            Assert.IsNotNull(results["items"], results.ToString());
            UnityEngine.Debug.Log("AuditLiveEvidence: " + status.ToString(Newtonsoft.Json.Formatting.None));
            foreach (var item in (JArray)results["items"])
                Assert.AreNotEqual(JTokenType.Null, item["rule_id"].Type, item.ToString());
            Assert.IsTrue((bool)((JObject)ProjectAuditCommands.Dispose(id))["disposed"]);
        }
    }
}
