using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace UnityPipeline.ProjectAuditor.Editor
{
    // Only public API reflection. Assembly names differ between the standalone package and built-in module.
    // Keeping this optional avoids breaking every Pipeline command when Auditor is absent.
    public sealed class ProjectAuditorAdapter
    {
        readonly Type auditorType, paramsType, categoryType;
        readonly MethodInfo auditAsync;
        readonly FieldInfo categoriesField, completedField, moduleField;
        public string AssemblyIdentity => auditorType.Assembly.GetName().FullName;
        public string[] Categories => Enum.GetNames(categoryType);

        public ProjectAuditorAdapter()
        {
            auditorType = UnityEditor.TypeCache.GetTypesDerivedFrom<object>()
                .FirstOrDefault(t => t.FullName == "Unity.ProjectAuditor.Editor.ProjectAuditor");
            if (auditorType == null) throw new NotSupportedException("Project Auditor is not available in this Editor. Install a compatible Project Auditor package or use an Editor with the built-in module.");
            var assembly = auditorType.Assembly;
            paramsType = Require(assembly.GetType("Unity.ProjectAuditor.Editor.AnalysisParams"), "AnalysisParams");
            categoryType = Require(assembly.GetType("Unity.ProjectAuditor.Editor.IssueCategory"), "IssueCategory");
            auditAsync = Require(auditorType.GetMethods().FirstOrDefault(m => m.Name == "AuditAsync" && m.GetParameters().Length == 2), "AuditAsync");
            categoriesField = Require(paramsType.GetField("Categories"), "AnalysisParams.Categories");
            completedField = Require(paramsType.GetField("OnCompleted"), "AnalysisParams.OnCompleted");
            moduleField = Require(paramsType.GetField("OnModuleCompleted"), "AnalysisParams.OnModuleCompleted");
            var itemType = Require(assembly.GetType("Unity.ProjectAuditor.Editor.ReportItem"), "ReportItem");
            Require(itemType.GetMethod("IsIssue", Type.EmptyTypes), "ReportItem.IsIssue");
            foreach (var name in new[] { "Id", "Category", "Severity", "Description", "RelativePath", "Line" })
                Require(itemType.GetProperty(name), "ReportItem." + name);
        }

        public string[] ValidateCategories(string categories)
        {
            if ((categories ?? "").Length > 4096) throw new ArgumentException("categories is too long");
            var names = (categories ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in names)
                if (!Categories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Unknown category '" + name + "'. Valid values: " + string.Join(", ", Categories));
            return names;
        }

        // Must be called on Unity's main thread. Completion may be synchronous or on a worker thread.
        public Action Start(string[] categories, Action<object> completed, Action<string, bool> moduleCompleted, out JObject evidence)
        {
            var auditor = Activator.CreateInstance(auditorType);
            var parameters = Activator.CreateInstance(paramsType, new object[] { true });
            if (categories.Length > 0)
            {
                var element = Require(categoriesField.FieldType.GetElementType(), "Categories element type");
                var values = Array.CreateInstance(element, categories.Length);
                for (var i = 0; i < categories.Length; i++)
                {
                    var value = Enum.Parse(categoryType, categories[i], true);
                    values.SetValue(element == categoryType ? value : Activator.CreateInstance(element, value), i);
                }
                categoriesField.SetValue(parameters, values);
            }
            completedField.SetValue(parameters, Callback(completedField.FieldType, args => completed(args[0])));
            moduleField.SetValue(parameters, Callback(moduleField.FieldType, args => {
                var outcome = args.FirstOrDefault(x => x?.GetType().FullName == "Unity.ProjectAuditor.Editor.AnalysisResult");
                moduleCompleted(string.Join(",", args.Select(x => x?.ToString() ?? "unknown")),
                    string.Equals(outcome?.ToString(), "Success", StringComparison.Ordinal));
            }));
            evidence = new JObject {
                ["auditor_assembly"] = AssemblyIdentity,
                ["requested_categories"] = new JArray(categories),
                ["platform"] = Read(parameters, "Platform")?.ToString(),
                ["code_optimization"] = Read(parameters, "CodeOptimization")?.ToString(),
                ["code_analysis_flags"] = Read(parameters, "CodeAnalysisFlags")?.ToString(),
                ["coverage"] = "Pending completion; findings are a static-analysis snapshot, not proof of runtime correctness."
            };
            // Return a deferred invocation so the caller can publish evidence before synchronous callbacks.
            return new Action(() => auditAsync.Invoke(auditor, new[] { parameters, null }));
        }

        public IEnumerable<JObject> Snapshot(object report)
        {
            if (report == null) throw new InvalidOperationException("Auditor returned no Report");
            var method = Require(report.GetType().GetMethod("GetAllIssues", Type.EmptyTypes), "Report.GetAllIssues");
            if (!(method.Invoke(report, null) is IEnumerable items)) throw new InvalidOperationException("Auditor returned no issue collection");
            foreach (var item in items)
            {
                if (item == null) continue;
                var isIssue = Require(item.GetType().GetMethod("IsIssue", Type.EmptyTypes), "ReportItem.IsIssue");
                if (!(bool)isIssue.Invoke(item, null)) continue; // Inventory insights are deliberately not counted as findings.
                yield return MapItem(item);
            }
        }

        public static JObject MapItem(object item)
        {
            var missing = new JArray(); var warnings = new JArray();
            Func<string, object> optional = name => {
                if (!HasMember(item.GetType(), name)) { missing.Add(name); return null; }
                return Read(item, name);
            };
            var id = Read(item, "Id");
            var descriptor = id?.GetType().GetMethod("GetDescriptor", Type.EmptyTypes)?.Invoke(id, null);
            var row = new JObject {
                ["rule_id"] = Scalar(Read(descriptor, "Id") ?? id, warnings),
                ["category"] = Scalar(Read(item, "Category"), warnings),
                ["severity"] = Scalar(Read(item, "Severity"), warnings),
                ["description"] = Scalar(Read(item, "Description"), warnings),
                ["path"] = Scalar(Read(item, "RelativePath"), warnings),
                ["filename"] = Scalar(optional("Filename"), warnings),
                ["line"] = Scalar(Read(item, "Line"), warnings),
                ["log_level"] = Scalar(optional("LogLevel"), warnings),
                ["recommendation"] = Scalar(Read(descriptor, "Recommendation"), warnings),
                ["areas"] = Scalar(Read(descriptor, "Areas"), warnings),
                ["custom_properties"] = Values(optional("CustomProperties"), warnings),
                ["is_upgrade_issue"] = Scalar(optional("IsUpgradeIssue"), warnings),
                ["upgrade_properties"] = Values(optional("UpgradeProperties"), warnings),
                ["unsupported_fields"] = missing, ["warnings"] = warnings
            };
            return row;
        }

        static JToken Values(object value, JArray warnings)
        {
            if (value == null) return JValue.CreateNull();
            if (value is string || !(value is IEnumerable values)) return Scalar(value, warnings);
            var array = new JArray();
            foreach (var item in values)
            {
                if (array.Count >= 16) { warnings.Add("PROPERTY_COUNT_TRUNCATED"); break; }
                array.Add(Scalar(item, warnings, 512));
            }
            return array;
        }

        static JToken Scalar(object value, JArray warnings, int max = 2048)
        {
            if (value == null) return JValue.CreateNull();
            if (value is bool b) return new JValue(b);
            if (value is int i) return new JValue(i);
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d)) return new JValue(d);
            if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) return new JValue(f);
            if (!(value is string) && !value.GetType().IsEnum && !value.GetType().IsPrimitive && !(value is decimal))
            {
                warnings.Add("NON_SCALAR_PROPERTY_OMITTED");
                return JValue.CreateNull(); // No arbitrary object graph serialization or user ToString execution.
            }
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (text.Length > max) { text = text.Substring(0, max); warnings.Add("STRING_TRUNCATED"); }
            return new JValue(text); // Wide integers stay exact.
        }

        static Delegate Callback(Type type, Action<object[]> callback)
        {
            var args = type.GetMethod("Invoke").GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var packed = Expression.NewArrayInit(typeof(object), args.Select(a => Expression.Convert(a, typeof(object))));
            return Expression.Lambda(type, Expression.Invoke(Expression.Constant(callback), packed), args).Compile();
        }
        static bool HasMember(Type type, string name) => type.GetProperty(name) != null || type.GetField(name) != null;
        static object Read(object value, string name) => value == null ? null : value.GetType().GetProperty(name)?.GetValue(value) ?? value.GetType().GetField(name)?.GetValue(value);
        static T Require<T>(T value, string name) where T : class => value ?? throw new NotSupportedException("Project Auditor API is unsupported: " + name + " is missing");
    }
}
