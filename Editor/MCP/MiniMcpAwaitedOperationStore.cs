using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiniMCP.Editor
{
    [InitializeOnLoad]
    internal static class MiniMcpAwaitedOperationStore
    {
        private const string SessionStoreKey = "MiniMCP.AwaitedOperations.Store";
        private const int MaxCompletedOperations = 20;
        private static readonly object Gate = new object();
        private static StoreData cachedStore;

        static MiniMcpAwaitedOperationStore()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        public static AwaitedOperationRecord BeginOrReuseOperation(string toolName, string kind, int timeoutMs)
        {
            lock (Gate)
            {
                var store = LoadStoreUnderLock();
                for (var i = 0; i < store.Operations.Count; i++)
                {
                    var operation = store.Operations[i];
                    if (!string.Equals(operation.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsTerminalState(operation.State))
                    {
                        continue;
                    }

                    return Clone(operation);
                }

                var createdAtUtc = DateTime.UtcNow;
                var createdAtText = createdAtUtc.ToString("O");
                var record = new AwaitedOperationRecord
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    ToolName = toolName ?? string.Empty,
                    Kind = kind ?? string.Empty,
                    State = "running",
                    TimeoutMs = timeoutMs,
                    OutcomeKnown = false,
                    CreatedAtUtc = createdAtText,
                    StartedAtUtc = createdAtText,
                    FinishedAtUtc = string.Empty,
                    SummaryMessage = string.Empty,
                    ErrorMessage = string.Empty,
                    RelayCompletionPending = false,
                    RelayCompletionDelivered = false
                };

                store.Operations.Add(record);
                TrimCompletedOperationsUnderLock(store);
                SaveStoreUnderLock(store);
                return Clone(record);
            }
        }

        public static bool TryCompleteRunningOperation(string kind, string state, bool outcomeKnown, string summaryMessage, string errorMessage = "")
        {
            lock (Gate)
            {
                var store = LoadStoreUnderLock();
                for (var index = store.Operations.Count - 1; index >= 0; index--)
                {
                    var operation = store.Operations[index];
                    if (!string.Equals(operation.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsTerminalState(operation.State))
                    {
                        continue;
                    }

                    operation.State = NormalizeState(state);
                    operation.OutcomeKnown = outcomeKnown;
                    operation.FinishedAtUtc = DateTime.UtcNow.ToString("O");
                    operation.SummaryMessage = summaryMessage ?? string.Empty;
                    operation.ErrorMessage = errorMessage ?? string.Empty;
                    operation.RelayCompletionPending = true;
                    operation.RelayCompletionDelivered = false;
                    SaveStoreUnderLock(store);
                    return true;
                }

                return false;
            }
        }

        public static bool TryBuildCompletionPayload(out string payloadJson)
        {
            lock (Gate)
            {
                var store = LoadStoreUnderLock();
                for (var i = store.Operations.Count - 1; i >= 0; i--)
                {
                    var operation = store.Operations[i];
                    if (!operation.RelayCompletionPending || operation.RelayCompletionDelivered)
                    {
                        continue;
                    }

                    payloadJson = BuildCompletionPayload(operation);
                    return true;
                }

                payloadJson = string.Empty;
                return false;
            }
        }

        public static void MarkCompletionDelivered(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                return;
            }

            lock (Gate)
            {
                var store = LoadStoreUnderLock();
                for (var i = 0; i < store.Operations.Count; i++)
                {
                    var operation = store.Operations[i];
                    if (!string.Equals(operation.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    operation.RelayCompletionPending = false;
                    operation.RelayCompletionDelivered = true;
                    SaveStoreUnderLock(store);
                    return;
                }
            }
        }

        private static void OnEditorUpdate()
        {
            if (!TryBuildCompletionPayload(out var payloadJson))
            {
                return;
            }

            if (!MiniMcpRelayService.TryNotifyAwaitedOperationCompletion(payloadJson, out var operationId))
            {
                return;
            }

            MarkCompletionDelivered(operationId);
        }

        private static StoreData LoadStoreUnderLock()
        {
            if (cachedStore != null)
            {
                return cachedStore;
            }

            var raw = SessionState.GetString(SessionStoreKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                cachedStore = new StoreData();
                return cachedStore;
            }

            try
            {
                cachedStore = JsonUtility.FromJson<StoreData>(raw) ?? new StoreData();
            }
            catch
            {
                cachedStore = new StoreData();
            }

            if (cachedStore.Operations == null)
            {
                cachedStore.Operations = new List<AwaitedOperationRecord>();
            }

            return cachedStore;
        }

        private static void SaveStoreUnderLock(StoreData store)
        {
            cachedStore = store ?? new StoreData();
            TrimCompletedOperationsUnderLock(cachedStore);
            SessionState.SetString(SessionStoreKey, JsonUtility.ToJson(cachedStore));
        }

        private static void TrimCompletedOperationsUnderLock(StoreData store)
        {
            if (store == null || store.Operations == null)
            {
                return;
            }

            var completedCount = 0;
            for (var i = 0; i < store.Operations.Count; i++)
            {
                if (IsTerminalState(store.Operations[i].State))
                {
                    completedCount++;
                }
            }

            if (completedCount <= MaxCompletedOperations)
            {
                return;
            }

            for (var i = 0; i < store.Operations.Count && completedCount > MaxCompletedOperations;)
            {
                if (!IsTerminalState(store.Operations[i].State))
                {
                    i++;
                    continue;
                }

                store.Operations.RemoveAt(i);
                completedCount--;
            }
        }

        private static bool IsTerminalState(string state)
        {
            var normalized = NormalizeState(state);
            return normalized == "completed"
                || normalized == "completed_with_errors"
                || normalized == "failed"
                || normalized == "timed_out"
                || normalized == "canceled";
        }

        private static string NormalizeState(string state)
        {
            return string.IsNullOrWhiteSpace(state) ? "pending" : state.Trim().ToLowerInvariant();
        }

        private static string BuildCompletionPayload(AwaitedOperationRecord operation)
        {
            return "{"
                + "\"operationId\":\"" + MiniMcpJson.EscapeJson(operation.OperationId ?? string.Empty) + "\"," 
                + "\"toolName\":\"" + MiniMcpJson.EscapeJson(operation.ToolName ?? string.Empty) + "\"," 
                + "\"kind\":\"" + MiniMcpJson.EscapeJson(operation.Kind ?? string.Empty) + "\"," 
                + "\"state\":\"" + MiniMcpJson.EscapeJson(NormalizeState(operation.State)) + "\"," 
                + "\"timeoutMs\":" + operation.TimeoutMs + ","
                + "\"outcomeKnown\":" + (operation.OutcomeKnown ? "true" : "false") + ","
                + "\"createdAtUtc\":" + SerializeNullableString(operation.CreatedAtUtc) + ","
                + "\"startedAtUtc\":" + SerializeNullableString(operation.StartedAtUtc) + ","
                + "\"finishedAtUtc\":" + SerializeNullableString(operation.FinishedAtUtc) + ","
                + "\"summaryMessage\":" + SerializeNullableString(operation.SummaryMessage) + ","
                + "\"errorMessage\":" + SerializeNullableString(operation.ErrorMessage)
                + "}";
        }

        private static string SerializeNullableString(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "null"
                : "\"" + MiniMcpJson.EscapeJson(value) + "\"";
        }

        private static AwaitedOperationRecord Clone(AwaitedOperationRecord source)
        {
            return new AwaitedOperationRecord
            {
                OperationId = source.OperationId,
                ToolName = source.ToolName,
                Kind = source.Kind,
                State = source.State,
                TimeoutMs = source.TimeoutMs,
                OutcomeKnown = source.OutcomeKnown,
                CreatedAtUtc = source.CreatedAtUtc,
                StartedAtUtc = source.StartedAtUtc,
                FinishedAtUtc = source.FinishedAtUtc,
                SummaryMessage = source.SummaryMessage,
                ErrorMessage = source.ErrorMessage,
                RelayCompletionPending = source.RelayCompletionPending,
                RelayCompletionDelivered = source.RelayCompletionDelivered
            };
        }

        [Serializable]
        private sealed class StoreData
        {
            public List<AwaitedOperationRecord> Operations = new List<AwaitedOperationRecord>();
        }

        [Serializable]
        internal sealed class AwaitedOperationRecord
        {
            public string OperationId;
            public string ToolName;
            public string Kind;
            public string State;
            public int TimeoutMs;
            public bool OutcomeKnown;
            public string CreatedAtUtc;
            public string StartedAtUtc;
            public string FinishedAtUtc;
            public string SummaryMessage;
            public string ErrorMessage;
            public bool RelayCompletionPending;
            public bool RelayCompletionDelivered;
        }
    }
}