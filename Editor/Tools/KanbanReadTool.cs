using System;
using System.Collections.Generic;
using System.Text;
using MiniMCP;
using MiniMCP.Kanban;
using MiniMCP.Kanban.Editor;
using UnityEditor;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "kanban_read",
        "Returns compact Kanban board summaries. This overview never includes full card descriptions or comments, and defaults to Planning, Todo, In Progress, and In Review when includeColumns is omitted.",
        Group = "Kanban")]
    public sealed class KanbanReadTool : MiniMcpTypedMainThreadTool<KanbanReadTool.Arguments>
    {
        private const int DefaultDescriptionPreviewLength = 160;
        private const int MinDescriptionPreviewLength = 40;
        private const int MaxDescriptionPreviewLength = 600;

        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Optional asset path to a specific KanbanPlan. If omitted, all KanbanPlan assets are returned.")]
            public string assetPath;

            [MiniMcpSchemaProperty(Description = "Optional comma, semicolon, or pipe separated list of columns to include. Valid values: Planning, Todo, In Progress, In Review, Finished. When omitted, kanban_read defaults to all non-finished columns. Finished is only returned when explicitly named.")]
            public string includeColumns;

            [MiniMcpSchemaProperty(Description = "Optional category name or id filter. Only cards matching this primary category are returned.")]
            public string category;

            [MiniMcpSchemaProperty(Description = "Optional tag filter. Use a single tag name/id or multiple separated by commas, semicolons, or pipes. All listed tags must match.")]
            public string tag;

            [MiniMcpSchemaProperty(Description = "Optional full text filter across title, description, category, tags, and comments.")]
            public string searchText;

            [MiniMcpSchemaProperty(Description = "Optional preview length for compact card descriptions.", Minimum = MinDescriptionPreviewLength, Maximum = MaxDescriptionPreviewLength)]
            public int descriptionPreviewLength;
        }

        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            string assetPath = string.Empty;
            string includeColumns = string.Empty;
            string category = string.Empty;
            string tag = string.Empty;
            string searchText = string.Empty;
            int descriptionPreviewLength;

            MiniMcpJson.TryExtractStringProperty(argumentsJson, "assetPath", out assetPath);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "includeColumns", out includeColumns);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "category", out category);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "tag", out tag);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "searchText", out searchText);
            if (!MiniMcpJson.TryExtractIntProperty(argumentsJson, "descriptionPreviewLength", out descriptionPreviewLength))
            {
                descriptionPreviewLength = DefaultDescriptionPreviewLength;
            }

            string resultJson = ExecuteOnMainThread(assetPath, includeColumns, category, tag, searchText, descriptionPreviewLength);
            return MiniMcpToolCallResult.Ok(resultJson ?? "{\"mode\":\"overview\",\"planCount\":0,\"plans\":[]}");
        }

        private static string ExecuteOnMainThread(string assetPath, string includeColumns, string category, string tag, string searchText, int descriptionPreviewLength)
        {
            string normalizedAssetPath = (assetPath ?? string.Empty).Trim();
            string normalizedCategory = (category ?? string.Empty).Trim();
            string normalizedSearchText = (searchText ?? string.Empty).Trim();
            List<string> tagFilters = ParseList(tag);
            List<KanbanCardStatus> includedStatuses = ParseIncludedStatuses(includeColumns);
            int previewLength = Math.Max(MinDescriptionPreviewLength, Math.Min(MaxDescriptionPreviewLength, descriptionPreviewLength));

            if (!string.IsNullOrWhiteSpace(normalizedAssetPath))
            {
                KanbanPlan specificPlan = AssetDatabase.LoadAssetAtPath<KanbanPlan>(normalizedAssetPath);
                if (specificPlan == null)
                {
                    throw new InvalidOperationException("KanbanPlan asset not found at the requested assetPath.");
                }

                return BuildResultJson(new[] { specificPlan }, normalizedAssetPath, normalizedCategory, tagFilters, normalizedSearchText, includedStatuses, previewLength);
            }

            string[] guids = AssetDatabase.FindAssets("t:KanbanPlan");
            KanbanPlan[] plans = new KanbanPlan[guids.Length];
            for (int index = 0; index < guids.Length; index++)
            {
                string planPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                plans[index] = AssetDatabase.LoadAssetAtPath<KanbanPlan>(planPath);
            }

            return BuildResultJson(plans, string.Empty, normalizedCategory, tagFilters, normalizedSearchText, includedStatuses, previewLength);
        }

        private static string BuildResultJson(KanbanPlan[] plans, string requestedAssetPath, string categoryFilter, List<string> tagFilters, string searchText, List<KanbanCardStatus> includedStatuses, int previewLength)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"mode\":\"overview\",");
            builder.Append("\"summaryOnly\":true,");
            builder.Append("\"requestedAssetPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(requestedAssetPath ?? string.Empty));
            builder.Append("\",\"filters\":{");
            builder.Append("\"category\":\"");
            builder.Append(MiniMcpJson.EscapeJson(categoryFilter ?? string.Empty));
            builder.Append("\",\"searchText\":\"");
            builder.Append(MiniMcpJson.EscapeJson(searchText ?? string.Empty));
            builder.Append("\",\"tags\":[");

            for (int tagIndex = 0; tagFilters != null && tagIndex < tagFilters.Count; tagIndex++)
            {
                if (tagIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(MiniMcpJson.EscapeJson(tagFilters[tagIndex]));
                builder.Append('"');
            }

            builder.Append("],\"descriptionPreviewLength\":");
            builder.Append(previewLength);
            builder.Append("},\"includedColumns\":[");

            for (int statusIndex = 0; statusIndex < includedStatuses.Count; statusIndex++)
            {
                if (statusIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(MiniMcpJson.EscapeJson(KanbanPlan.GetColumnTitle(includedStatuses[statusIndex])));
                builder.Append('"');
            }

            builder.Append("],\"planCount\":");
            builder.Append(plans?.Length ?? 0);
            builder.Append(",\"plans\":[");

            bool wrotePlan = false;
            if (plans != null)
            {
                foreach (KanbanPlan plan in plans)
                {
                    if (plan == null)
                    {
                        continue;
                    }

                    if (wrotePlan)
                    {
                        builder.Append(',');
                    }

                    AppendPlanOverviewJson(builder, plan, categoryFilter, tagFilters, searchText, includedStatuses, previewLength);
                    wrotePlan = true;
                }
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static void AppendPlanOverviewJson(StringBuilder builder, KanbanPlan plan, string categoryFilter, List<string> tagFilters, string searchText, List<KanbanCardStatus> includedStatuses, int previewLength)
        {
            string assetPath = AssetDatabase.GetAssetPath(plan);
            plan.EnsureInitialized();

            builder.Append("{");
            builder.Append("\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(plan.name ?? string.Empty));
            builder.Append("\",\"assetPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(assetPath ?? string.Empty));
            builder.Append("\",\"description\":\"");
            builder.Append(MiniMcpJson.EscapeJson(plan.Description ?? string.Empty));
            builder.Append("\",\"categoryCount\":");
            builder.Append(plan.Categories.Count);
            builder.Append(",\"categories\":[");

            for (int categoryIndex = 0; categoryIndex < plan.Categories.Count; categoryIndex++)
            {
                if (categoryIndex > 0)
                {
                    builder.Append(',');
                }

                AppendLabelDefinitionJson(builder, plan.Categories[categoryIndex]);
            }

            builder.Append("],\"tagCount\":");
            builder.Append(plan.Tags.Count);
            builder.Append(",\"tags\":[");

            for (int tagIndex = 0; tagIndex < plan.Tags.Count; tagIndex++)
            {
                if (tagIndex > 0)
                {
                    builder.Append(',');
                }

                AppendLabelDefinitionJson(builder, plan.Tags[tagIndex]);
            }

            builder.Append("],\"columnCount\":");
            builder.Append(includedStatuses.Count);
            builder.Append(",\"columns\":[");

            for (int columnIndex = 0; columnIndex < includedStatuses.Count; columnIndex++)
            {
                KanbanCardStatus status = includedStatuses[columnIndex];
                List<KanbanCardAsset> cards = plan.GetCards(status);
                List<KanbanCardAsset> filteredCards = new List<KanbanCardAsset>();

                for (int cardIndex = 0; cardIndex < cards.Count; cardIndex++)
                {
                    KanbanCardAsset card = cards[cardIndex];
                    if (plan.CardMatchesFilters(card, categoryFilter, tagFilters, searchText))
                    {
                        filteredCards.Add(card);
                    }
                }

                if (columnIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"status\":\"");
                builder.Append(MiniMcpJson.EscapeJson(status.ToString()));
                builder.Append("\",\"title\":\"");
                builder.Append(MiniMcpJson.EscapeJson(KanbanPlan.GetColumnTitle(status)));
                builder.Append("\",\"cardCount\":");
                builder.Append(filteredCards.Count);
                builder.Append(",\"cards\":[");

                for (int cardIndex = 0; cardIndex < filteredCards.Count; cardIndex++)
                {
                    if (cardIndex > 0)
                    {
                        builder.Append(',');
                    }

                    AppendCardSummaryJson(builder, plan, filteredCards[cardIndex], previewLength);
                }

                builder.Append("]}");
            }

            builder.Append("]}");
        }

        internal static void AppendCardSummaryJson(StringBuilder builder, KanbanPlan plan, KanbanCardAsset card, int previewLength)
        {
            KanbanUserContext.Identity mcpIdentity = KanbanUserContext.CreateMcpIdentity();
            KanbanCategoryDefinition category = plan.GetCategoryById(card.CategoryId);
            string description = card.Description ?? string.Empty;
            bool hasMoreDescription = description.Length > previewLength;
            string preview = hasMoreDescription ? description.Substring(0, previewLength).TrimEnd() + "..." : description;

            builder.Append("{\"id\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Id ?? string.Empty));
            builder.Append("\",\"title\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Title ?? string.Empty));
            builder.Append("\",\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Status.ToString()));
            builder.Append("\",\"descriptionPreview\":\"");
            builder.Append(MiniMcpJson.EscapeJson(preview));
            builder.Append("\",\"hasMoreDescription\":");
            builder.Append(hasMoreDescription ? "true" : "false");
            builder.Append(",\"descriptionLength\":");
            builder.Append(description.Length);
            builder.Append(",\"createdBy\":{\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.CreatedByName ?? string.Empty));
            builder.Append("\",\"id\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.CreatedById ?? string.Empty));
            builder.Append("\",\"kind\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.CreatedByKind ?? string.Empty));
            builder.Append("\"},\"category\":");

            if (category != null)
            {
                AppendLabelDefinitionJson(builder, category);
            }
            else
            {
                builder.Append("null");
            }

            builder.Append(",\"tags\":[");
            bool wroteTag = false;
            int resolvedTagCount = 0;
            for (int tagIndex = 0; tagIndex < card.TagIds.Count; tagIndex++)
            {
                KanbanTagDefinition tag = plan.GetTagById(card.TagIds[tagIndex]);
                if (tag == null)
                {
                    continue;
                }

                if (wroteTag)
                {
                    builder.Append(',');
                }

                AppendLabelDefinitionJson(builder, tag);
                wroteTag = true;
                resolvedTagCount++;
            }

            builder.Append("],\"isLocked\":");
            builder.Append(card.IsLocked ? "true" : "false");
            builder.Append(",\"readOnly\":");
            builder.Append(card.IsLocked ? "true" : "false");
            builder.Append(",\"tagCount\":");
            builder.Append(resolvedTagCount);
            builder.Append(",\"commentCount\":");
            builder.Append(card.Comments.Count);
            AppendUnreadCommentStateJson(builder, card, mcpIdentity.DisplayName, mcpIdentity.Id, mcpIdentity.Kind);
            builder.Append('}');
        }

        internal static void AppendUnreadCommentStateJson(StringBuilder builder, KanbanCardAsset card, string readerName, string readerId, string readerKind)
        {
            int unreadCommentCount = card != null ? card.GetUnreadCommentCount(readerName, readerId, readerKind) : 0;
            builder.Append(",\"unreadCommentCount\":");
            builder.Append(unreadCommentCount);
            builder.Append(",\"hasUnreadComments\":");
            builder.Append(unreadCommentCount > 0 ? "true" : "false");
        }

        internal static void AppendLabelDefinitionJson(StringBuilder builder, KanbanLabelDefinition definition)
        {
            builder.Append("{\"id\":\"");
            builder.Append(MiniMcpJson.EscapeJson(definition?.Id ?? string.Empty));
            builder.Append("\",\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(definition?.Name ?? string.Empty));
            builder.Append("\",\"color\":\"");
            builder.Append(MiniMcpJson.EscapeJson(ColorToHex(definition?.Color ?? default)));
            builder.Append("\",\"ruleText\":");
            builder.Append(string.IsNullOrWhiteSpace(definition?.RuleText)
                ? "null"
                : "\"" + MiniMcpJson.EscapeJson(definition.RuleText) + "\"");
            builder.Append("}");
        }

        private static string ColorToHex(UnityEngine.Color color)
        {
            return "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(color);
        }

        internal static List<string> ParseList(string value)
        {
            List<string> results = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return results;
            }

            string[] split = value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in split)
            {
                string normalized = item.Trim();
                if (normalized.Length == 0 || !unique.Add(normalized))
                {
                    continue;
                }

                results.Add(normalized);
            }

            return results;
        }

        private static List<KanbanCardStatus> ParseIncludedStatuses(string includeColumns)
        {
            List<string> items = ParseList(includeColumns);
            if (items.Count == 0)
            {
                return new List<KanbanCardStatus>
                {
                    KanbanCardStatus.Planning,
                    KanbanCardStatus.Todo,
                    KanbanCardStatus.InProgress,
                    KanbanCardStatus.InReview,
                };
            }

            List<KanbanCardStatus> results = new List<KanbanCardStatus>();
            HashSet<KanbanCardStatus> unique = new HashSet<KanbanCardStatus>();
            foreach (string item in items)
            {
                KanbanCardStatus status = ParseStatus(item);
                if (unique.Add(status))
                {
                    results.Add(status);
                }
            }

            return results;
        }

        internal static KanbanCardStatus ParseStatus(string value)
        {
            string normalized = (value ?? string.Empty).Trim().Replace(" ", string.Empty);
            if (normalized.Equals("Planning", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Plan", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Planning;
            }

            if (normalized.Equals("Todo", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Todo;
            }

            if (normalized.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.InProgress;
            }

            if (normalized.Equals("InReview", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Review", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.InReview;
            }

            if (normalized.Equals("Finished", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Done", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Finished;
            }

            throw new InvalidOperationException("Invalid includeColumns value. Use Planning, Todo, In Progress, In Review, or Finished.");
        }
    }
}