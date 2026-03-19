using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using MiniMCP;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "unity_test_runner",
        "Lists available Unity Test Runner tests or runs them in EditMode or PlayMode.",
        Group = "Editor")]
    public sealed class UnityTestRunnerTool : MiniMcpTypedTool<UnityTestRunnerTool.Arguments>, IMiniMcpToolThreadingValidated
    {
        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Use 'list' to inspect available tests or 'run' to execute them.", EnumValues = new[] { "list", "run" })]
            public string action;

            [MiniMcpSchemaProperty(Description = "Test mode to inspect or execute.", EnumValues = new[] { "edit", "play" })]
            public string mode;

            [MiniMcpSchemaProperty(Description = "Optional fully-qualified test name filter for execution or listing.")]
            public string testFilter;

            [MiniMcpSchemaProperty(Description = "Timeout for listing or execution in seconds.", Minimum = 5, Maximum = 1800)]
            public int timeoutSeconds;

            [MiniMcpSchemaProperty(Description = "Include failed test details in run results.")]
            public bool includeFailedDetails;
        }

        public override MiniMcpToolCallResult Execute(string argumentsJson)
        {
            var action = "run";
            var modeText = "edit";
            var testFilter = string.Empty;
            var timeoutSeconds = 180;
            var includeFailedDetails = true;

            MiniMcpJson.TryExtractStringProperty(argumentsJson, "action", out action);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "mode", out modeText);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "testFilter", out testFilter);
            MiniMcpJson.TryExtractIntProperty(argumentsJson, "timeoutSeconds", out timeoutSeconds);
            MiniMcpJson.TryExtractBoolProperty(argumentsJson, "includeFailedDetails", out includeFailedDetails);

            if (timeoutSeconds < 5)
            {
                timeoutSeconds = 5;
            }
            else if (timeoutSeconds > 1800)
            {
                timeoutSeconds = 1800;
            }

            var mode = ParseMode(modeText);
            if (mode == null)
            {
                return MiniMcpToolCallResult.Error("{\"error\":\"Invalid mode. Use 'edit' or 'play'.\"}");
            }

            if (string.IsNullOrWhiteSpace(action))
            {
                action = "run";
            }

            if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                return ListTests(modeText, mode.Value, testFilter, timeoutSeconds);
            }

            if (!action.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                return MiniMcpToolCallResult.Error("{\"error\":\"Invalid action. Use 'list' or 'run'.\"}");
            }

            return RunTests(modeText, mode.Value, testFilter, timeoutSeconds, includeFailedDetails);
        }

        private static MiniMcpToolCallResult ListTests(string modeText, TestMode mode, string testFilter, int timeoutSeconds)
        {
            var tracker = new TestListTracker();
            TestRunnerApi api = null;

            try
            {
                string dispatchError;
                var dispatched = MiniMcpEditorThread.Invoke(() =>
                {
                    api = ScriptableObject.CreateInstance<TestRunnerApi>();
                    api.RetrieveTestList(mode, root => tracker.Finish(root));
                }, TimeSpan.FromSeconds(10), out dispatchError);

                if (!dispatched)
                {
                    return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"" + MiniMcpJson.EscapeJson(dispatchError) + "\"}");
                }

                if (!tracker.WaitForFinish(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    return MiniMcpToolCallResult.Error("{\"status\":\"timeout\",\"message\":\"Unity Test Runner test discovery timed out.\"}");
                }

                return MiniMcpToolCallResult.Ok(BuildListResultJson(modeText, testFilter, tracker.RootTest, tracker.DiscoveryWarning));
            }
            catch (Exception ex)
            {
                return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"" + MiniMcpJson.EscapeJson(ex.Message) + "\"}");
            }
            finally
            {
                CleanupApi(api, null);
            }
        }

        private static MiniMcpToolCallResult RunTests(string modeText, TestMode mode, string testFilter, int timeoutSeconds, bool includeFailedDetails)
        {
            var tracker = new RunTracker(includeFailedDetails);
            var callback = new TestRunCallbacks(tracker);
            TestRunnerApi api = null;

            try
            {
                var filter = new Filter
                {
                    testMode = mode
                };

                if (!string.IsNullOrWhiteSpace(testFilter))
                {
                    filter.testNames = new[] { testFilter.Trim() };
                }

                var settings = new ExecutionSettings(filter)
                {
                    runSynchronously = mode == TestMode.EditMode
                };
                var startedUtc = DateTime.UtcNow;

                string dispatchError;
                var dispatched = MiniMcpEditorThread.Invoke(() =>
                {
                    api = ScriptableObject.CreateInstance<TestRunnerApi>();
                    api.RegisterCallbacks(callback);
                    api.Execute(settings);
                }, TimeSpan.FromSeconds(10), out dispatchError);

                if (!dispatched)
                {
                    return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"" + MiniMcpJson.EscapeJson(dispatchError) + "\"}");
                }

                if (!tracker.WaitForFinish(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    return MiniMcpToolCallResult.Error("{\"status\":\"timeout\",\"message\":\"Unity Test Runner execution timed out.\"}");
                }

                var durationMs = (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
                return MiniMcpToolCallResult.Ok(BuildRunResultJson(modeText, testFilter, durationMs, tracker));
            }
            catch (Exception ex)
            {
                return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"" + MiniMcpJson.EscapeJson(ex.Message) + "\"}");
            }
            finally
            {
                CleanupApi(api, callback);
            }
        }

        private static void CleanupApi(TestRunnerApi api, ICallbacks callback)
        {
            if (api == null)
            {
                return;
            }

            string cleanupError;
            MiniMcpEditorThread.Invoke(() =>
            {
                if (callback != null)
                {
                    api.UnregisterCallbacks(callback);
                }

                UnityEngine.Object.DestroyImmediate(api);
            }, TimeSpan.FromSeconds(5), out cleanupError);
        }

        private static TestMode? ParseMode(string modeText)
        {
            if (string.IsNullOrWhiteSpace(modeText) || modeText.Equals("edit", StringComparison.OrdinalIgnoreCase))
            {
                return TestMode.EditMode;
            }

            if (modeText.Equals("play", StringComparison.OrdinalIgnoreCase))
            {
                return TestMode.PlayMode;
            }

            return null;
        }

        private static string BuildRunResultJson(string modeText, string testFilter, int durationMs, RunTracker tracker)
        {
            var builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"status\":\"");
            builder.Append(tracker.RootResult != null && tracker.RootResult.FailCount > 0 ? "failed" : "passed");
            builder.Append("\",");
            builder.Append("\"action\":\"run\",");
            builder.Append("\"mode\":\"");
            builder.Append(MiniMcpJson.EscapeJson(string.IsNullOrWhiteSpace(modeText) ? "edit" : modeText));
            builder.Append("\",");
            builder.Append("\"testFilter\":\"");
            builder.Append(MiniMcpJson.EscapeJson(testFilter ?? string.Empty));
            builder.Append("\",");
            builder.Append("\"durationMs\":");
            builder.Append(durationMs);

            var root = tracker.RootResult;
            if (root != null)
            {
                builder.Append(",\"total\":");
                builder.Append(root.PassCount + root.FailCount + root.SkipCount + root.InconclusiveCount);
                builder.Append(",\"passed\":");
                builder.Append(root.PassCount);
                builder.Append(",\"failed\":");
                builder.Append(root.FailCount);
                builder.Append(",\"skipped\":");
                builder.Append(root.SkipCount);
                builder.Append(",\"inconclusive\":");
                builder.Append(root.InconclusiveCount);
            }

            if (tracker.FailedDetails.Count > 0)
            {
                builder.Append(",\"failedTests\":[");
                for (var i = 0; i < tracker.FailedDetails.Count; i++)
                {
                    var failed = tracker.FailedDetails[i];
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append("{\"name\":\"");
                    builder.Append(MiniMcpJson.EscapeJson(failed.Name));
                    builder.Append("\",\"message\":\"");
                    builder.Append(MiniMcpJson.EscapeJson(failed.Message));
                    builder.Append("\",\"stackTrace\":\"");
                    builder.Append(MiniMcpJson.EscapeJson(failed.StackTrace));
                    builder.Append("\"}");
                }

                builder.Append(']');
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string BuildListResultJson(string modeText, string testFilter, ITestAdaptor root, string warning)
        {
            var flatTests = new List<DiscoveredTest>();
            CollectDiscoveredTests(root, flatTests);

            if (!string.IsNullOrWhiteSpace(testFilter))
            {
                flatTests = flatTests
                    .Where(test => test.FullName.IndexOf(testFilter, StringComparison.OrdinalIgnoreCase) >= 0
                        || test.Name.IndexOf(testFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            var builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"status\":\"ok\",");
            builder.Append("\"action\":\"list\",");
            builder.Append("\"mode\":\"");
            builder.Append(MiniMcpJson.EscapeJson(string.IsNullOrWhiteSpace(modeText) ? "edit" : modeText));
            builder.Append("\",");
            builder.Append("\"testFilter\":\"");
            builder.Append(MiniMcpJson.EscapeJson(testFilter ?? string.Empty));
            builder.Append("\",");
            builder.Append("\"count\":");
            builder.Append(flatTests.Count);

            if (!string.IsNullOrWhiteSpace(warning))
            {
                builder.Append(",\"warning\":\"");
                builder.Append(MiniMcpJson.EscapeJson(warning));
                builder.Append('"');
            }

            builder.Append(",\"items\":[");
            for (var i = 0; i < flatTests.Count; i++)
            {
                var test = flatTests[i];
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"name\":\"");
                builder.Append(MiniMcpJson.EscapeJson(test.Name));
                builder.Append("\",\"fullName\":\"");
                builder.Append(MiniMcpJson.EscapeJson(test.FullName));
                builder.Append("\",\"isSuite\":");
                builder.Append(test.IsSuite ? "true" : "false");
                builder.Append("}");
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static void CollectDiscoveredTests(ITestAdaptor root, List<DiscoveredTest> sink)
        {
            if (root == null)
            {
                return;
            }

            var children = root.Children;
            if (children == null)
            {
                return;
            }

            foreach (var child in children.Where(c => c != null))
            {
                CollectDiscoveredTestsRecursive(child, sink);
            }
        }

        private static void CollectDiscoveredTestsRecursive(ITestAdaptor test, List<DiscoveredTest> sink)
        {
            var children = test.Children;
            var hasChildren = children != null && children.Any(c => c != null);
            sink.Add(new DiscoveredTest
            {
                Name = test.Name ?? string.Empty,
                FullName = test.FullName ?? test.Name ?? string.Empty,
                IsSuite = hasChildren
            });

            if (!hasChildren)
            {
                return;
            }

            foreach (var child in children.Where(c => c != null))
            {
                CollectDiscoveredTestsRecursive(child, sink);
            }
        }

        private sealed class RunTracker
        {
            private readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
            private readonly bool includeFailedDetails;

            public RunTracker(bool includeFailedDetails)
            {
                this.includeFailedDetails = includeFailedDetails;
            }

            public ITestResultAdaptor RootResult { get; private set; }
            public List<FailedTest> FailedDetails { get; } = new List<FailedTest>();

            public void Finish(ITestResultAdaptor root)
            {
                this.RootResult = root;
                if (this.includeFailedDetails && root != null)
                {
                    CollectFailures(root, this.FailedDetails);
                }

                this.done.Set();
            }

            public bool WaitForFinish(TimeSpan timeout)
            {
                return this.done.Wait(timeout);
            }

            private static void CollectFailures(ITestResultAdaptor result, List<FailedTest> sink)
            {
                if (result == null)
                {
                    return;
                }

                if (result.TestStatus == TestStatus.Failed)
                {
                    sink.Add(new FailedTest
                    {
                        Name = result.FullName ?? result.Name ?? string.Empty,
                        Message = result.Message ?? string.Empty,
                        StackTrace = result.StackTrace ?? string.Empty
                    });

                    if (sink.Count >= 25)
                    {
                        return;
                    }
                }

                var children = result.Children;
                if (children == null)
                {
                    return;
                }

                foreach (var child in children.Where(c => c != null))
                {
                    if (sink.Count >= 25)
                    {
                        return;
                    }

                    CollectFailures(child, sink);
                }
            }
        }

        private sealed class FailedTest
        {
            public string Name;
            public string Message;
            public string StackTrace;
        }

        private sealed class DiscoveredTest
        {
            public string Name;
            public string FullName;
            public bool IsSuite;
        }

        private sealed class TestListTracker
        {
            private readonly ManualResetEventSlim done = new ManualResetEventSlim(false);

            public ITestAdaptor RootTest { get; private set; }
            public string DiscoveryWarning { get; private set; }

            public void Finish(ITestAdaptor root, string warning = null)
            {
                this.RootTest = root;
                this.DiscoveryWarning = warning ?? string.Empty;
                this.done.Set();
            }

            public bool WaitForFinish(TimeSpan timeout)
            {
                return this.done.Wait(timeout);
            }
        }

        private sealed class TestRunCallbacks : ICallbacks
        {
            private readonly RunTracker tracker;

            public TestRunCallbacks(RunTracker tracker)
            {
                this.tracker = tracker;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                this.tracker.Finish(result);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }
    }
}
