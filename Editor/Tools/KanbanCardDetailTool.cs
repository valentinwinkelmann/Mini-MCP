using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MiniMCP;
using MiniMCP.Kanban;
using MiniMCP.Kanban.Editor;
using UnityEditor;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "kanban_card_read",
        "Returns full details for a single Kanban card, including full description and comments.",
        Group = "Kanban")]
    public sealed class KanbanCardDetailTool : MiniMcpTypedMainThreadTool<KanbanCardDetailTool.Arguments>
    {
        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Optional asset path to a specific KanbanPlan. Required if multiple plans exist.")]
            public string assetPath;

            [MiniMcpSchemaProperty(Description = "Card id to inspect in detail.", Required = true)]
            public string cardId;
        }

        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            string assetPath = string.Empty;
            string cardId = string.Empty;
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "assetPath", out assetPath);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "cardId", out cardId);

            string resultJson = ExecuteOnMainThread(assetPath, cardId);
            return MiniMcpToolCallResult.Ok(resultJson ?? "{\"status\":\"not_found\"}");
        }

        private static string ExecuteOnMainThread(string assetPath, string cardId)
        {
            string normalizedCardId = (cardId ?? string.Empty).Trim();
            if (normalizedCardId.Length == 0)
            {
                throw new InvalidOperationException("cardId is required for kanban_card_read.");
            }

            KanbanPlan plan = ResolvePlan(assetPath, normalizedCardId);
            plan.EnsureInitialized();
            KanbanCardAsset card = plan.GetCard(normalizedCardId);
            if (card == null)
            {
                throw new InvalidOperationException("Kanban card not found for the requested cardId.");
            }

            KanbanUserContext.Identity mcpIdentity = KanbanUserContext.CreateMcpIdentity();
            int markedAsReadCount = card.MarkCommentsAsRead(mcpIdentity.DisplayName, mcpIdentity.Id, mcpIdentity.Kind);
            if (markedAsReadCount > 0)
            {
                EditorUtility.SetDirty(card);
                EditorUtility.SetDirty(plan);
                AssetDatabase.SaveAssets();
                KanbanBoardWindow.RefreshOpenBoards();
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("{\"mode\":\"card_detail\",\"plan\":{");
            builder.Append("\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(plan.name ?? string.Empty));
            builder.Append("\",\"assetPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(AssetDatabase.GetAssetPath(plan) ?? string.Empty));
            builder.Append("\"},\"columnTitle\":\"");
            builder.Append(MiniMcpJson.EscapeJson(KanbanPlan.GetColumnTitle(card.Status)));
            builder.Append("\",\"readReceiptUpdate\":{\"markedAsReadCount\":");
            builder.Append(markedAsReadCount);
            builder.Append("},\"workflowContext\":");
            AppendWorkflowContextJson(builder, card);
            builder.Append(",\"card\":");
            AppendCardDetailJson(builder, plan, card, mcpIdentity.DisplayName, mcpIdentity.Id, mcpIdentity.Kind);
            builder.Append('}');
            return builder.ToString();
        }

        private static KanbanPlan ResolvePlan(string assetPath, string cardId)
        {
            string normalizedAssetPath = (assetPath ?? string.Empty).Trim();
            if (normalizedAssetPath.Length > 0)
            {
                KanbanPlan specificPlan = AssetDatabase.LoadAssetAtPath<KanbanPlan>(normalizedAssetPath);
                if (specificPlan == null)
                {
                    throw new InvalidOperationException("KanbanPlan asset not found at the requested assetPath.");
                }

                return specificPlan;
            }

            string[] guids = AssetDatabase.FindAssets("t:KanbanPlan");
            List<KanbanPlan> matches = new List<KanbanPlan>();
            for (int index = 0; index < guids.Length; index++)
            {
                string planPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                KanbanPlan plan = AssetDatabase.LoadAssetAtPath<KanbanPlan>(planPath);
                if (plan == null)
                {
                    continue;
                }

                plan.EnsureInitialized();
                if (plan.GetCard(cardId) != null)
                {
                    matches.Add(plan);
                }
            }

            if (matches.Count == 0)
            {
                throw new InvalidOperationException("Kanban card not found in any KanbanPlan asset.");
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException("Multiple KanbanPlan assets contain that cardId. Specify assetPath explicitly.");
            }

            return matches[0];
        }

        private static void AppendCardDetailJson(StringBuilder builder, KanbanPlan plan, KanbanCardAsset card, string readerName, string readerId, string readerKind)
        {
            KanbanCategoryDefinition category = plan.GetCategoryById(card.CategoryId);
            builder.Append("{\"id\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Id ?? string.Empty));
            builder.Append("\",\"title\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Title ?? string.Empty));
            builder.Append("\",\"description\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Description ?? string.Empty));
            builder.Append("\",\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.Status.ToString()));
            builder.Append("\",\"createdBy\":{\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.CreatedByName ?? string.Empty));
            builder.Append("\",\"id\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.CreatedById ?? string.Empty));
            builder.Append("\",\"kind\":\"");
            builder.Append(MiniMcpJson.EscapeJson(card.CreatedByKind ?? string.Empty));
            builder.Append("\"},\"category\":");

            if (category != null)
            {
                KanbanReadTool.AppendLabelDefinitionJson(builder, category);
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

                KanbanReadTool.AppendLabelDefinitionJson(builder, tag);
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
            KanbanReadTool.AppendUnreadCommentStateJson(builder, card, readerName, readerId, readerKind);
            builder.Append(",\"comments\":[");

            for (int commentIndex = 0; commentIndex < card.Comments.Count; commentIndex++)
            {
                KanbanCardCommentData comment = card.Comments[commentIndex];
                if (commentIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"text\":\"");
                builder.Append(MiniMcpJson.EscapeJson(comment.Text ?? string.Empty));
                builder.Append("\",\"createdAtUtc\":\"");
                builder.Append(MiniMcpJson.EscapeJson(comment.CreatedAtUtc ?? string.Empty));
                builder.Append("\",\"authorName\":\"");
                builder.Append(MiniMcpJson.EscapeJson(comment.AuthorName ?? string.Empty));
                builder.Append("\",\"authorId\":\"");
                builder.Append(MiniMcpJson.EscapeJson(comment.AuthorId ?? string.Empty));
                builder.Append("\",\"authorKind\":\"");
                builder.Append(MiniMcpJson.EscapeJson(comment.AuthorKind ?? string.Empty));
                builder.Append("\"}");
            }

            builder.Append("]}");
        }

        private static void AppendWorkflowContextJson(StringBuilder builder, KanbanCardAsset card)
        {
            string currentColumn = KanbanPlan.GetColumnTitle(card.Status);
            string recommendedAction = GetRecommendedAction(card);
            string nextColumn = GetSuggestedNextColumn(card);

            builder.Append("{\"currentColumn\":\"");
            builder.Append(MiniMcpJson.EscapeJson(currentColumn));
            builder.Append("\",\"recommendedAction\":\"");
            builder.Append(MiniMcpJson.EscapeJson(recommendedAction));
            builder.Append("\",\"suggestedNextColumn\":\"");
            builder.Append(MiniMcpJson.EscapeJson(nextColumn));
            builder.Append("\",\"labelRuleText\":");
            string labelRuleText = BuildLabelRuleText(card);
            builder.Append(string.IsNullOrWhiteSpace(labelRuleText)
                ? "null"
                : "\"" + MiniMcpJson.EscapeJson(labelRuleText) + "\"");
            builder.Append(",\"rules\":[");

            AppendStringArray(builder, BuildWorkflowRules(card, labelRuleText));
            builder.Append(']');
            builder.Append(",\"allowedActions\":[");
            AppendStringArray(builder, BuildAllowedActions(card));
            builder.Append("]}");
        }

        private static string[] BuildWorkflowRules(KanbanCardAsset card, string labelRuleText)
        {
            List<string> rules = new List<string>();

            if (card != null && card.IsLocked)
            {
                rules.Add("Locked cards should not be edited or moved.");
                rules.Add("At most add comments when clarification is needed.");
                AppendLabelRuleIfPresent(rules, labelRuleText);
                return rules.ToArray();
            }

            switch (card.Status)
            {
                case KanbanCardStatus.Planning:
                    rules.Add("Planning cards are not for active implementation work yet.");
                    rules.Add("Do not work a Planning card through MCP. At most add comments with important observations or refinement notes.");
                    break;
                case KanbanCardStatus.Todo:
                    rules.Add("Before starting implementation, move the card to In Progress.");
                    rules.Add("Todo means the work is queued but not actively being worked on yet.");
                    break;
                case KanbanCardStatus.InProgress:
                    rules.Add("In Progress means active implementation is underway.");
                    rules.Add("If the work is paused or deprioritized, move the card back to Todo.");
                    rules.Add("When the work is done, move the card to In Review instead of directly to Finished.");
                    break;
                case KanbanCardStatus.InReview:
                    rules.Add("In Review means implementation exists and should be validated or checked.");
                    rules.Add("MCP agents must not move In Review cards to Finished on their own.");
                    rules.Add("Only a human reviewer or an explicit review instruction should advance the card to Finished.");
                    break;
                default:
                    rules.Add("Finished cards may be read and commented on freely.");
                    rules.Add("Move a Finished card back to Todo or In Progress only when current work materially reopens it.");
                    break;
            }

            AppendLabelRuleIfPresent(rules, labelRuleText);
            return rules.ToArray();
        }

        private static void AppendLabelRuleIfPresent(List<string> rules, string labelRuleText)
        {
            if (!string.IsNullOrWhiteSpace(labelRuleText))
            {
                rules.Add(labelRuleText);
            }
        }

        private static string BuildLabelRuleText(KanbanCardAsset card)
        {
            if (card == null)
            {
                return string.Empty;
            }

            KanbanPlan plan = ResolvePlan(string.Empty, card.Id ?? string.Empty);
            if (plan == null)
            {
                return string.Empty;
            }

            List<string> ruleFragments = new List<string>();

            KanbanCategoryDefinition category = plan.GetCategoryById(card.CategoryId);
            if (category != null && !string.IsNullOrWhiteSpace(category.RuleText))
            {
                ruleFragments.Add(category.RuleText.Trim());
            }

            foreach (string tagId in card.TagIds)
            {
                KanbanTagDefinition tag = plan.GetTagById(tagId);
                if (tag == null || string.IsNullOrWhiteSpace(tag.RuleText))
                {
                    continue;
                }

                string ruleText = tag.RuleText.Trim();
                if (!ruleFragments.Contains(ruleText, StringComparer.Ordinal))
                {
                    ruleFragments.Add(ruleText);
                }
            }

            if (ruleFragments.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", ruleFragments);
        }

        private static string[] BuildAllowedActions(KanbanCardAsset card)
        {
            if (card != null && card.IsLocked)
            {
                return new[] { "add_comment" };
            }

            switch (card.Status)
            {
                case KanbanCardStatus.Planning:
                    return new[] { "add_comment" };
                case KanbanCardStatus.Todo:
                    return new[] { "update_card", "add_comment", "move_to_in_progress" };
                case KanbanCardStatus.InProgress:
                    return new[] { "update_card", "add_comment", "move_to_todo", "move_to_in_review" };
                case KanbanCardStatus.InReview:
                    return new[] { "add_comment", "move_to_in_progress" };
                default:
                    return new[] { "add_comment", "move_to_todo", "move_to_in_progress" };
            }
        }

        private static string GetRecommendedAction(KanbanCardAsset card)
        {
            if (card != null && card.IsLocked)
            {
                return "Do not edit this card. Only comment if necessary.";
            }

            switch (card.Status)
            {
                case KanbanCardStatus.Planning:
                    return "Do not work this Planning card yet. Only add comments if an important refinement or concern needs to be captured.";
                case KanbanCardStatus.Todo:
                    return "Move the card to In Progress before starting the work.";
                case KanbanCardStatus.InProgress:
                    return "Either continue the work toward In Review or move the card back to Todo if the work is being paused.";
                case KanbanCardStatus.InReview:
                    return "Wait for review feedback. Only move the card back to In Progress if more implementation work is needed.";
                default:
                    return "Leave the card finished unless current work truly reopens it, then move it back to Todo or In Progress.";
            }
        }

        private static string GetSuggestedNextColumn(KanbanCardAsset card)
        {
            if (card != null && card.IsLocked)
            {
                return string.Empty;
            }

            switch (card.Status)
            {
                case KanbanCardStatus.Planning:
                    return string.Empty;
                case KanbanCardStatus.Todo:
                    return "In Progress";
                case KanbanCardStatus.InProgress:
                    return "In Review";
                case KanbanCardStatus.InReview:
                    return "In Progress";
                default:
                    return "Todo";
            }
        }

        private static void AppendStringArray(StringBuilder builder, IReadOnlyList<string> values)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(MiniMcpJson.EscapeJson(values[index] ?? string.Empty));
                builder.Append('"');
            }
        }
    }
}