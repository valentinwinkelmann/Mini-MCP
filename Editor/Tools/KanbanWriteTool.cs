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
        "kanban_write",
        "Creates cards, moves cards, updates card text, and adds comments to Kanban plans.",
        Group = "Kanban")]
    public sealed class KanbanWriteTool : MiniMcpTypedMainThreadTool<KanbanWriteTool.Arguments>
    {
        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Requested action.", Required = true, EnumValues = new[] { "create_card", "move_card", "update_title", "update_description", "update_card", "add_comment" })]
            public string action;

            [MiniMcpSchemaProperty(Description = "Optional asset path to a specific KanbanPlan. Required if multiple plans exist.")]
            public string assetPath;

            [MiniMcpSchemaProperty(Description = "Card id for actions that target an existing card.")]
            public string cardId;

            [MiniMcpSchemaProperty(Description = "Target column title or status. Valid fixed columns are Planning, Todo, In Progress, In Review, and Finished.")]
            public string column;

            [MiniMcpSchemaProperty(Description = "Card title for create or update actions.")]
            public string title;

            [MiniMcpSchemaProperty(Description = "Card description for create or update actions.")]
            public string description;

            [MiniMcpSchemaProperty(Description = "Optional primary category name or id for create/update actions. Use an empty string in update_card to clear it.")]
            public string category;

            [MiniMcpSchemaProperty(Description = "Optional comma, semicolon, or pipe separated tag names/ids for create/update actions. Use an empty string in update_card to clear all tags.")]
            public string tags;

            [MiniMcpSchemaProperty(Description = "Comment text to append to a card.")]
            public string comment;
        }

        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            var action = string.Empty;
            var assetPath = string.Empty;
            var cardId = string.Empty;
            var column = string.Empty;
            var title = string.Empty;
            var description = string.Empty;
            var category = string.Empty;
            var tags = string.Empty;
            var comment = string.Empty;

            MiniMcpJson.TryExtractStringProperty(argumentsJson, "action", out action);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "assetPath", out assetPath);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "cardId", out cardId);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "column", out column);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "title", out title);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "description", out description);
            bool hasCategory = MiniMcpJson.TryExtractStringProperty(argumentsJson, "category", out category);
            bool hasTags = MiniMcpJson.TryExtractStringProperty(argumentsJson, "tags", out tags);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "comment", out comment);

            string resultJson = ExecuteOnMainThread(action, assetPath, cardId, column, title, description, category, hasCategory, tags, hasTags, comment);
            return MiniMcpToolCallResult.Ok(resultJson ?? "{\"status\":\"noop\"}");
        }

        private static string ExecuteOnMainThread(string action, string assetPath, string cardId, string column, string title, string description, string category, bool hasCategory, string tags, bool hasTags, string comment)
        {
            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedAction != "create_card"
                && normalizedAction != "move_card"
                && normalizedAction != "update_title"
                && normalizedAction != "update_description"
                && normalizedAction != "update_card"
                && normalizedAction != "add_comment")
            {
                throw new InvalidOperationException("Invalid action. Use create_card, move_card, update_title, update_description, update_card, or add_comment.");
            }

            KanbanPlan plan = ResolvePlan(assetPath);
            plan.EnsureInitialized();

            switch (normalizedAction)
            {
                case "create_card":
                    return CreateCard(plan, column, title, description, category, hasCategory, tags, hasTags);
                case "move_card":
                    return MoveCard(plan, cardId, column);
                case "update_title":
                    return UpdateTitle(plan, cardId, title);
                case "update_description":
                    return UpdateDescription(plan, cardId, description);
                case "update_card":
                    return UpdateCard(plan, cardId, title, description, category, hasCategory, tags, hasTags);
                default:
                    return AddComment(plan, cardId, comment);
            }
        }

        private static string CreateCard(KanbanPlan plan, string column, string title, string description, string category, bool hasCategory, string tags, bool hasTags)
        {
            KanbanCardStatus targetStatus = ResolveStatus(plan, column);
            string categoryId = hasCategory ? ResolveCategoryId(plan, category) : string.Empty;
            List<string> tagIds = hasTags ? ResolveTagIds(plan, tags) : new List<string>();
            KanbanUserContext.Identity mcpIdentity = KanbanUserContext.CreateMcpIdentity();

            Undo.RecordObject(plan, "Create Kanban Card via MCP");
            KanbanCardAsset card = plan.AddCard(targetStatus, string.IsNullOrWhiteSpace(title) ? "New Task" : title, description ?? string.Empty, mcpIdentity.DisplayName, mcpIdentity.Id, mcpIdentity.Kind);
            if (hasCategory || hasTags)
            {
                if (!plan.UpdateCardMetadata(card.Id, categoryId, tagIds))
                {
                    throw new InvalidOperationException("The requested card metadata could not be applied.");
                }
            }

            PersistPlan(plan);
            return BuildResultJson("created", plan, card, KanbanPlan.GetColumnTitle(targetStatus), null);
        }

        private static string MoveCard(KanbanPlan plan, string cardId, string column)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new InvalidOperationException("cardId is required for move_card.");
            }

            KanbanCardAsset card = GetExistingCard(plan, cardId);
            EnsureCardWritable(card, "move");

            KanbanCardStatus targetStatus = ResolveStatus(plan, column);
            EnsureMcpMoveAllowed(card, targetStatus);
            Undo.RecordObject(plan, "Move Kanban Card via MCP");
            if (!plan.MoveCard(cardId.Trim(), targetStatus))
            {
                throw new InvalidOperationException("The requested card could not be moved.");
            }

            PersistPlan(plan);
            return BuildResultJson("moved", plan, plan.GetCard(cardId.Trim()), KanbanPlan.GetColumnTitle(targetStatus), null);
        }

        private static string UpdateTitle(KanbanPlan plan, string cardId, string title)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new InvalidOperationException("cardId is required for update_title.");
            }

            KanbanCardAsset card = GetExistingCard(plan, cardId);
            EnsureCardWritable(card, "update");

            Undo.RecordObject(plan, "Update Kanban Card Title via MCP");
            if (!plan.UpdateCardTitle(cardId.Trim(), title ?? string.Empty))
            {
                throw new InvalidOperationException("The requested card title could not be updated.");
            }

            PersistPlan(plan);
            card = plan.GetCard(cardId.Trim());
            return BuildResultJson("updated_title", plan, card, card != null ? KanbanPlan.GetColumnTitle(card.Status) : string.Empty, null);
        }

        private static string UpdateDescription(KanbanPlan plan, string cardId, string description)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new InvalidOperationException("cardId is required for update_description.");
            }

            KanbanCardAsset card = GetExistingCard(plan, cardId);
            EnsureCardWritable(card, "update");

            Undo.RecordObject(plan, "Update Kanban Card Description via MCP");
            if (!plan.UpdateCardDescription(cardId.Trim(), description ?? string.Empty))
            {
                throw new InvalidOperationException("The requested card description could not be updated.");
            }

            PersistPlan(plan);
            card = plan.GetCard(cardId.Trim());
            return BuildResultJson("updated_description", plan, card, card != null ? KanbanPlan.GetColumnTitle(card.Status) : string.Empty, null);
        }

        private static string UpdateCard(KanbanPlan plan, string cardId, string title, string description, string category, bool hasCategory, string tags, bool hasTags)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new InvalidOperationException("cardId is required for update_card.");
            }

            KanbanCardAsset card = GetExistingCard(plan, cardId);
            EnsureCardWritable(card, "update");

            Undo.RecordObject(plan, "Update Kanban Card via MCP");
            bool titleChanged = plan.UpdateCardTitle(cardId.Trim(), title ?? string.Empty);
            bool descriptionChanged = plan.UpdateCardDescription(cardId.Trim(), description ?? string.Empty);
            bool metadataChanged = ApplyMetadata(plan, card, category, hasCategory, tags, hasTags);
            if (!titleChanged || !descriptionChanged || !metadataChanged)
            {
                throw new InvalidOperationException("The requested card could not be updated.");
            }

            PersistPlan(plan);
            card = plan.GetCard(cardId.Trim());
            return BuildResultJson("updated_card", plan, card, card != null ? KanbanPlan.GetColumnTitle(card.Status) : string.Empty, null);
        }

        private static string AddComment(KanbanPlan plan, string cardId, string comment)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new InvalidOperationException("cardId is required for add_comment.");
            }

            if (string.IsNullOrWhiteSpace(comment))
            {
                throw new InvalidOperationException("comment is required for add_comment.");
            }

            Undo.RecordObject(plan, "Add Kanban Card Comment via MCP");
            KanbanUserContext.Identity mcpIdentity = KanbanUserContext.CreateMcpIdentity();
            KanbanCardCommentData createdComment = plan.AddComment(cardId.Trim(), comment, mcpIdentity.DisplayName, mcpIdentity.Id, mcpIdentity.Kind);
            if (createdComment == null)
            {
                throw new InvalidOperationException("The requested comment could not be added.");
            }

            PersistPlan(plan);
            KanbanCardAsset card = plan.GetCard(cardId.Trim());
            return BuildResultJson("comment_added", plan, card, card != null ? KanbanPlan.GetColumnTitle(card.Status) : string.Empty, createdComment);
        }

        private static KanbanPlan ResolvePlan(string assetPath)
        {
            string normalizedAssetPath = (assetPath ?? string.Empty).Trim();
            if (normalizedAssetPath.Length > 0)
            {
                KanbanPlan plan = AssetDatabase.LoadAssetAtPath<KanbanPlan>(normalizedAssetPath);
                if (plan == null)
                {
                    throw new InvalidOperationException("KanbanPlan asset not found at the requested assetPath.");
                }

                return plan;
            }

            string[] guids = AssetDatabase.FindAssets("t:KanbanPlan");
            if (guids.Length == 0)
            {
                throw new InvalidOperationException("No KanbanPlan assets were found.");
            }

            if (guids.Length > 1)
            {
                throw new InvalidOperationException("Multiple KanbanPlan assets were found. Provide assetPath.");
            }

            string resolvedPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<KanbanPlan>(resolvedPath);
        }

        private static KanbanCardStatus ResolveStatus(KanbanPlan plan, string column)
        {
            KanbanCardStatus? parsedStatus = plan.TryParseStatus(column);
            if (!parsedStatus.HasValue)
            {
                throw new InvalidOperationException("column is required and must be Planning, Todo, In Progress, In Review, or Finished.");
            }

            return parsedStatus.Value;
        }

        private static bool ApplyMetadata(KanbanPlan plan, KanbanCardAsset card, string category, bool hasCategory, string tags, bool hasTags)
        {
            if (card == null)
            {
                return false;
            }

            if (!hasCategory && !hasTags)
            {
                return true;
            }

            string nextCategoryId = card.CategoryId;
            if (hasCategory)
            {
                nextCategoryId = ResolveCategoryId(plan, category);
            }

            List<string> nextTagIds = new List<string>(card.TagIds);
            if (hasTags)
            {
                nextTagIds = ResolveTagIds(plan, tags);
            }

            if (!plan.UpdateCardMetadata(card.Id, nextCategoryId, nextTagIds))
            {
                throw new InvalidOperationException("The requested card metadata could not be updated.");
            }

            return true;
        }

        private static string ResolveCategoryId(KanbanPlan plan, string category)
        {
            string normalized = (category ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            KanbanCategoryDefinition definition = plan.FindCategory(normalized);
            if (definition == null)
            {
                throw new InvalidOperationException("The requested category could not be found on the plan.");
            }

            return definition.Id;
        }

        private static List<string> ResolveTagIds(KanbanPlan plan, string tags)
        {
            List<string> resolvedTagIds = new List<string>();
            HashSet<string> uniqueTagIds = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(tags))
            {
                return resolvedTagIds;
            }

            string[] split = tags.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawTag in split)
            {
                string normalized = rawTag.Trim();
                if (normalized.Length == 0)
                {
                    continue;
                }

                KanbanTagDefinition definition = plan.FindTag(normalized);
                if (definition == null)
                {
                    throw new InvalidOperationException("The requested tag '" + normalized + "' could not be found on the plan.");
                }

                if (uniqueTagIds.Add(definition.Id))
                {
                    resolvedTagIds.Add(definition.Id);
                }
            }

            return resolvedTagIds;
        }

        private static void PersistPlan(KanbanPlan plan)
        {
            EditorUtility.SetDirty(plan);
            AssetDatabase.SaveAssets();
            KanbanBoardWindow.RefreshOpenBoards();
        }

        private static KanbanCardAsset GetExistingCard(KanbanPlan plan, string cardId)
        {
            KanbanCardAsset card = plan.GetCard(cardId.Trim());
            if (card == null)
            {
                throw new InvalidOperationException("The requested card could not be found.");
            }

            return card;
        }

        private static void EnsureCardWritable(KanbanCardAsset card, string attemptedAction)
        {
            if (card != null && card.IsLocked)
            {
                throw new InvalidOperationException("The requested card is locked and read-only. It cannot be " + attemptedAction + ". Only comments may be added.");
            }
        }

        private static void EnsureMcpMoveAllowed(KanbanCardAsset card, KanbanCardStatus targetStatus)
        {
            if (card == null)
            {
                throw new InvalidOperationException("The requested card could not be found.");
            }

            if (card.Status == targetStatus)
            {
                return;
            }

            switch (card.Status)
            {
                case KanbanCardStatus.Planning:
                    throw new InvalidOperationException("Planning cards must not be worked through MCP. Only add comments with important notes or refinements.");
                case KanbanCardStatus.Todo:
                    if (targetStatus != KanbanCardStatus.InProgress)
                    {
                        throw new InvalidOperationException("Todo cards may only be moved to In Progress through MCP before work begins.");
                    }

                    return;
                case KanbanCardStatus.InProgress:
                    if (targetStatus != KanbanCardStatus.Todo && targetStatus != KanbanCardStatus.InReview)
                    {
                        throw new InvalidOperationException("In Progress cards may only move back to Todo when paused or forward to In Review when implementation is done.");
                    }

                    return;
                case KanbanCardStatus.InReview:
                    if (targetStatus != KanbanCardStatus.InProgress)
                    {
                        throw new InvalidOperationException("In Review cards must not be moved to Finished through MCP. Only move them back to In Progress if more implementation work is required.");
                    }

                    return;
                case KanbanCardStatus.Finished:
                    if (targetStatus != KanbanCardStatus.Todo && targetStatus != KanbanCardStatus.InProgress)
                    {
                        throw new InvalidOperationException("Finished cards may only be reopened to Todo or In Progress through MCP when current work materially reopens them.");
                    }

                    return;
                default:
                    return;
            }
        }

        private static string BuildResultJson(string status, KanbanPlan plan, KanbanCardAsset card, string columnTitle, KanbanCardCommentData createdComment)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(status));
            builder.Append("\",\"plan\":{\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(plan.name ?? string.Empty));
            builder.Append("\",\"assetPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(AssetDatabase.GetAssetPath(plan) ?? string.Empty));
            builder.Append("\",\"description\":\"");
            builder.Append(MiniMcpJson.EscapeJson(plan.Description ?? string.Empty));
            builder.Append("\"}");

            if (card != null)
            {
                builder.Append(",\"columnTitle\":\"");
                builder.Append(MiniMcpJson.EscapeJson(columnTitle ?? string.Empty));
                builder.Append("\",\"card\":");
                AppendCardJson(builder, plan, card);
            }

            if (createdComment != null)
            {
                builder.Append(",\"createdComment\":{\"text\":\"");
                builder.Append(MiniMcpJson.EscapeJson(createdComment.Text ?? string.Empty));
                builder.Append("\",\"createdAtUtc\":\"");
                builder.Append(MiniMcpJson.EscapeJson(createdComment.CreatedAtUtc ?? string.Empty));
                builder.Append("\",\"authorName\":\"");
                builder.Append(MiniMcpJson.EscapeJson(createdComment.AuthorName ?? string.Empty));
                builder.Append("\",\"authorId\":\"");
                builder.Append(MiniMcpJson.EscapeJson(createdComment.AuthorId ?? string.Empty));
                builder.Append("\",\"authorKind\":\"");
                builder.Append(MiniMcpJson.EscapeJson(createdComment.AuthorKind ?? string.Empty));
                builder.Append("\"}");
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendCardJson(StringBuilder builder, KanbanPlan plan, KanbanCardAsset card)
        {
            KanbanUserContext.Identity mcpIdentity = KanbanUserContext.CreateMcpIdentity();
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
            builder.Append("\"}");
                builder.Append(",\"category\":");
            if (category != null)
            {
                builder.Append("{\"id\":\"");
                builder.Append(MiniMcpJson.EscapeJson(category.Id ?? string.Empty));
                builder.Append("\",\"name\":\"");
                builder.Append(MiniMcpJson.EscapeJson(category.Name ?? string.Empty));
                builder.Append("\",\"color\":\"#");
                builder.Append(UnityEngine.ColorUtility.ToHtmlStringRGB(category.Color));
                builder.Append("\"}");
            }
            else
            {
                builder.Append("null");
            }
            builder.Append(",\"tags\":[");

            var wroteTag = false;
            var resolvedTagCount = 0;
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

                builder.Append("{\"id\":\"");
                builder.Append(MiniMcpJson.EscapeJson(tag.Id ?? string.Empty));
                builder.Append("\",\"name\":\"");
                builder.Append(MiniMcpJson.EscapeJson(tag.Name ?? string.Empty));
                builder.Append("\",\"color\":\"#");
                builder.Append(UnityEngine.ColorUtility.ToHtmlStringRGB(tag.Color));
                builder.Append("\"}");
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
            KanbanReadTool.AppendUnreadCommentStateJson(builder, card, mcpIdentity.DisplayName, mcpIdentity.Id, mcpIdentity.Kind);
            builder.Append(",\"comments\":[");

            for (int index = 0; index < card.Comments.Count; index++)
            {
                KanbanCardCommentData existingComment = card.Comments[index];
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"text\":\"");
                builder.Append(MiniMcpJson.EscapeJson(existingComment.Text ?? string.Empty));
                builder.Append("\",\"createdAtUtc\":\"");
                builder.Append(MiniMcpJson.EscapeJson(existingComment.CreatedAtUtc ?? string.Empty));
                builder.Append("\",\"authorName\":\"");
                builder.Append(MiniMcpJson.EscapeJson(existingComment.AuthorName ?? string.Empty));
                builder.Append("\",\"authorId\":\"");
                builder.Append(MiniMcpJson.EscapeJson(existingComment.AuthorId ?? string.Empty));
                builder.Append("\",\"authorKind\":\"");
                builder.Append(MiniMcpJson.EscapeJson(existingComment.AuthorKind ?? string.Empty));
                builder.Append("\"}");
            }

            builder.Append("]}");
        }
    }
}