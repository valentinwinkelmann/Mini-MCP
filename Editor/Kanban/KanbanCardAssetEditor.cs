using System;
using System.Collections.Generic;
using MiniMCP;
using MiniMCP.Kanban;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MiniMCP.Kanban.Editor
{
    [CustomEditor(typeof(KanbanCardAsset))]
    public sealed class KanbanCardAssetEditor : UnityEditor.Editor
    {
        private const double DeferredSaveDelaySeconds = 0.35d;
        private const double DeferredRefreshDelaySeconds = 0.08d;
        private const string InspectorUxmlPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanCardInspector.uxml";
        private const string InspectorUssPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanCardInspector.uss";
        private const string CommentItemUxmlPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanCommentItem.uxml";
        private const string CurrentEditorAuthorKind = "editor";

        private static bool s_UpdateHookRegistered;
        private static bool s_PendingAssetSave;
        private static bool s_PendingBoardRefresh;
        private static double s_LastAssetChangeTime;
        private static double s_LastBoardRefreshRequestTime;

        private KanbanCardAsset m_Card;
        private KanbanPlan m_Plan;
        private Label m_TitleLabel;
        private Label m_SubtitleLabel;
        private VisualElement m_LockedHelpBoxHost;
        private HelpBox m_LockedHelpBox;
        private VisualElement m_DetailsContainer;
        private TextField m_TitleField;
        private EnumField m_StatusField;
        private VisualElement m_CategoryFieldHost;
        private PopupField<string> m_CategoryField;
        private VisualElement m_TagSelectionContainer;
        private PopupField<string> m_AddTagField;
        private TextField m_DescriptionField;
        private VisualElement m_CommentsContainer;
        private TextField m_NewCommentField;
        private Button m_AddCommentButton;
        private bool m_IsRefreshingUi;
        private VisualTreeAsset m_CommentItemTree;

        private void OnDisable()
        {
            FlushPendingEditorWork();
        }

        public override VisualElement CreateInspectorGUI()
        {
            this.m_Card = this.target as KanbanCardAsset;
            if (this.m_Card == null)
            {
                return new HelpBox("Kanban card could not be loaded.", HelpBoxMessageType.Error);
            }

            this.m_Card.EnsureInitialized();
            this.m_Plan = FindOwningPlan(this.m_Card);

            VisualTreeAsset inspectorTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(InspectorUxmlPath);
            StyleSheet inspectorStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>(InspectorUssPath);
            this.m_CommentItemTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(CommentItemUxmlPath);
            if (inspectorTree == null || inspectorStyle == null || this.m_CommentItemTree == null)
            {
                return new HelpBox("Kanban Card Inspector UI assets could not be loaded.", HelpBoxMessageType.Error);
            }

            TemplateContainer root = inspectorTree.CloneTree();
            root.styleSheets.Add(inspectorStyle);

            this.m_TitleLabel = root.Q<Label>("CardInspectorTitleLabel");
            this.m_SubtitleLabel = root.Q<Label>("CardInspectorSubtitleLabel");
            this.m_LockedHelpBoxHost = root.Q<VisualElement>("CardInspectorLockedHelpBoxHost");
            this.m_DetailsContainer = root.Q<VisualElement>("CardInspectorDetailsContainer");
            this.m_TitleField = root.Q<TextField>("CardInspectorTitleField");
            VisualElement statusFieldHost = root.Q<VisualElement>("CardInspectorStatusFieldHost");
            this.m_CategoryFieldHost = root.Q<VisualElement>("CardInspectorCategoryFieldHost");
            this.m_TagSelectionContainer = root.Q<VisualElement>("CardInspectorTagSelectionContainer");
            this.m_DescriptionField = root.Q<TextField>("CardInspectorDescriptionField");
            this.m_CommentsContainer = root.Q<VisualElement>("CardInspectorCommentsContainer");
            this.m_NewCommentField = root.Q<TextField>("CardInspectorNewCommentField");
            this.m_AddCommentButton = root.Q<Button>("CardInspectorAddCommentButton");

            if (this.m_TitleLabel == null
                || this.m_SubtitleLabel == null
                || this.m_LockedHelpBoxHost == null
                || this.m_DetailsContainer == null
                || this.m_TitleField == null
                || statusFieldHost == null
                || this.m_CategoryFieldHost == null
                || this.m_TagSelectionContainer == null
                || this.m_DescriptionField == null
                || this.m_CommentsContainer == null
                || this.m_NewCommentField == null
                || this.m_AddCommentButton == null)
            {
                return new HelpBox("Kanban Card Inspector UI is missing required elements.", HelpBoxMessageType.Error);
            }

            this.m_LockedHelpBox = new HelpBox("This card is locked. Title, status, description, moving and tool-based edits are read-only. Comments remain editable.", HelpBoxMessageType.Info);
            this.m_LockedHelpBox.AddToClassList("mini-mcp-kanban-card-inspector__lock-help");
            this.m_LockedHelpBoxHost.Add(this.m_LockedHelpBox);

            this.m_StatusField = new EnumField("Status", this.m_Card.Status);
            this.m_StatusField.AddToClassList("mini-mcp-kanban-card-inspector__field");
            statusFieldHost.Add(this.m_StatusField);

            this.RegisterCallbacks();
            this.RefreshUi();
            return root;
        }

        [OnOpenAsset]
        public static bool OpenKanbanCard(int instanceId, int line)
        {
            KanbanCardAsset card = EditorUtility.InstanceIDToObject(instanceId) as KanbanCardAsset;
            if (card == null)
            {
                return false;
            }

            KanbanPlan plan = FindOwningPlan(card);
            if (plan == null)
            {
                return false;
            }

            KanbanBoardWindow.Open(plan);
            Selection.activeObject = card;
            return true;
        }

        private void RegisterCallbacks()
        {
            this.m_TitleField.RegisterValueChangedCallback(evt =>
            {
                if (this.m_IsRefreshingUi || this.m_Card == null || this.m_Card.IsLocked)
                {
                    return;
                }

                ApplyCardChanges(this.m_Card, this.m_Plan, evt.newValue, this.m_DescriptionField.value, (KanbanCardStatus)this.m_StatusField.value);
                this.RefreshHeader();
            });

            this.m_StatusField.RegisterValueChangedCallback(evt =>
            {
                if (this.m_IsRefreshingUi || this.m_Card == null || this.m_Card.IsLocked || !(evt.newValue is KanbanCardStatus nextStatus))
                {
                    return;
                }

                ApplyCardChanges(this.m_Card, this.m_Plan, this.m_TitleField.value, this.m_DescriptionField.value, nextStatus);
                this.RefreshHeader();
            });

            this.m_DescriptionField.RegisterValueChangedCallback(evt =>
            {
                if (this.m_IsRefreshingUi || this.m_Card == null || this.m_Card.IsLocked)
                {
                    return;
                }

                ApplyCardChanges(this.m_Card, this.m_Plan, this.m_TitleField.value, evt.newValue, (KanbanCardStatus)this.m_StatusField.value);
            });

            this.m_NewCommentField.RegisterValueChangedCallback(evt =>
            {
                this.m_AddCommentButton.SetEnabled(!string.IsNullOrWhiteSpace(evt.newValue));
            });

            this.m_AddCommentButton.clicked += () =>
            {
                if (this.m_Card == null)
                {
                    return;
                }

                AddComment(this.m_Card, this.m_Plan, this.m_NewCommentField.value);
                this.m_NewCommentField.SetValueWithoutNotify(string.Empty);
                this.m_AddCommentButton.SetEnabled(false);
                this.RenderComments();
            };
        }

        private void RefreshUi()
        {
            if (this.m_Card == null)
            {
                return;
            }

            this.m_Card.EnsureInitialized();
            this.m_Plan = FindOwningPlan(this.m_Card);
            this.m_Plan?.EnsureInitialized();

            if (MarkCommentsAsRead(this.m_Card) > 0)
            {
                PersistChanges(this.m_Card, this.m_Plan, false);
            }

            this.m_IsRefreshingUi = true;
            this.m_TitleField.SetValueWithoutNotify(this.m_Card.Title ?? string.Empty);
            this.m_StatusField.SetValueWithoutNotify(this.m_Card.Status);
            this.m_DescriptionField.SetValueWithoutNotify(this.m_Card.Description ?? string.Empty);
            this.m_NewCommentField.SetValueWithoutNotify(string.Empty);
            this.m_AddCommentButton.SetEnabled(false);
            this.RebuildMetadataControls();
            this.m_IsRefreshingUi = false;

            this.RefreshHeader();
            this.RenderComments();
            this.m_DetailsContainer.SetEnabled(!this.m_Card.IsLocked);
            this.m_LockedHelpBox.style.display = this.m_Card.IsLocked ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RefreshHeader()
        {
            if (this.m_Card == null)
            {
                return;
            }

            this.m_TitleLabel.text = string.IsNullOrWhiteSpace(this.m_Card.Title) ? "Kanban Card" : this.m_Card.Title;
            this.m_SubtitleLabel.text = this.m_Plan == null
                ? KanbanPlan.GetColumnTitle(this.m_Card.Status)
                : this.m_Plan.name + " | " + KanbanPlan.GetColumnTitle(this.m_Card.Status);
        }

        private void RebuildMetadataControls()
        {
            this.m_CategoryFieldHost.Clear();
            this.m_TagSelectionContainer.Clear();

            List<string> categoryChoices = new List<string> { "No Category" };
            string selectedCategoryName = "No Category";

            if (this.m_Plan != null)
            {
                foreach (KanbanCategoryDefinition category in this.m_Plan.Categories)
                {
                    if (category == null)
                    {
                        continue;
                    }

                    categoryChoices.Add(category.Name);
                    if (!string.IsNullOrWhiteSpace(this.m_Card.CategoryId) && string.Equals(category.Id, this.m_Card.CategoryId, StringComparison.Ordinal))
                    {
                        selectedCategoryName = category.Name;
                    }
                }
            }

            this.m_CategoryField = new PopupField<string>("Category", categoryChoices, selectedCategoryName);
            this.m_CategoryField.AddToClassList("mini-mcp-kanban-card-inspector__field");
            this.m_CategoryField.RegisterValueChangedCallback(evt =>
            {
                if (this.m_IsRefreshingUi || this.m_Card == null || this.m_Card.IsLocked)
                {
                    return;
                }

                string nextCategoryId = string.Empty;
                if (this.m_Plan != null && !string.Equals(evt.newValue, "No Category", StringComparison.Ordinal))
                {
                    nextCategoryId = this.m_Plan.FindCategory(evt.newValue)?.Id ?? string.Empty;
                }

                ApplyCardMetadata(this.m_Card, this.m_Plan, nextCategoryId, this.m_Card.TagIds);
                this.RefreshUi();
            });
            this.m_CategoryFieldHost.Add(this.m_CategoryField);

            Label tagHeaderLabel = new Label("Tags");
            tagHeaderLabel.AddToClassList("mini-mcp-kanban-card-inspector__tag-selection-header");
            this.m_TagSelectionContainer.Add(tagHeaderLabel);

            VisualElement selectedTagList = new VisualElement();
            selectedTagList.AddToClassList("mini-mcp-kanban-card-inspector__tag-list");
            this.m_TagSelectionContainer.Add(selectedTagList);

            VisualElement addTagRow = new VisualElement();
            addTagRow.AddToClassList("mini-mcp-kanban-card-inspector__tag-add-row");
            this.m_TagSelectionContainer.Add(addTagRow);

            if (this.m_Plan == null || this.m_Plan.Tags.Count == 0)
            {
                Label emptyState = new Label("No tags defined on this board yet.");
                emptyState.AddToClassList("mini-mcp-kanban-card-inspector__empty-state");
                selectedTagList.Add(emptyState);
                return;
            }

            bool hasSelectedTags = false;

            foreach (KanbanTagDefinition tag in this.m_Plan.Tags)
            {
                if (tag == null)
                {
                    continue;
                }

                bool isSelected = this.m_Card.TagIds.Contains(tag.Id);
                if (!isSelected)
                {
                    continue;
                }

                hasSelectedTags = true;
                VisualElement tagChip = new VisualElement();
                tagChip.AddToClassList("mini-mcp-kanban-card-inspector__tag-chip");
                ApplyTagChipTint(tagChip, tag.Color);

                Label tagNameLabel = new Label(tag.Name);
                tagNameLabel.AddToClassList("mini-mcp-kanban-card-inspector__tag-chip-label");
                tagChip.Add(tagNameLabel);

                Button removeTagButton = new Button(() =>
                {
                    if (this.m_Card == null || this.m_Card.IsLocked)
                    {
                        return;
                    }

                    List<string> nextTagIds = new List<string>(this.m_Card.TagIds);
                    nextTagIds.Remove(tag.Id);
                    ApplyCardMetadata(this.m_Card, this.m_Plan, this.m_Card.CategoryId, nextTagIds);
                    this.RefreshUi();
                })
                {
                    text = "x"
                };
                removeTagButton.AddToClassList("mini-mcp-kanban-card-inspector__tag-chip-remove");
                tagChip.Add(removeTagButton);
                selectedTagList.Add(tagChip);
            }

            if (!hasSelectedTags)
            {
                Label emptySelectedTagsState = new Label("No tags assigned.");
                emptySelectedTagsState.AddToClassList("mini-mcp-kanban-card-inspector__empty-state");
                selectedTagList.Add(emptySelectedTagsState);
            }

            List<string> availableTagChoices = new List<string> { "Add Tag..." };
            foreach (KanbanTagDefinition tag in this.m_Plan.Tags)
            {
                if (tag != null && !this.m_Card.TagIds.Contains(tag.Id))
                {
                    availableTagChoices.Add(tag.Name);
                }
            }

            this.m_AddTagField = new PopupField<string>("Add Tag", availableTagChoices, 0);
            this.m_AddTagField.AddToClassList("mini-mcp-kanban-card-inspector__tag-add-field");
            this.m_AddTagField.SetEnabled(availableTagChoices.Count > 1 && !this.m_Card.IsLocked);
            this.m_AddTagField.RegisterValueChangedCallback(evt =>
            {
                if (this.m_IsRefreshingUi || this.m_Card == null || this.m_Card.IsLocked)
                {
                    return;
                }

                if (string.Equals(evt.newValue, "Add Tag...", StringComparison.Ordinal))
                {
                    return;
                }

                KanbanTagDefinition selectedTag = this.m_Plan?.FindTag(evt.newValue);
                if (selectedTag == null)
                {
                    this.m_AddTagField.SetValueWithoutNotify("Add Tag...");
                    return;
                }

                List<string> nextTagIds = new List<string>(this.m_Card.TagIds);
                if (!nextTagIds.Contains(selectedTag.Id))
                {
                    nextTagIds.Add(selectedTag.Id);
                }

                ApplyCardMetadata(this.m_Card, this.m_Plan, this.m_Card.CategoryId, nextTagIds);
                this.RefreshUi();
            });
            addTagRow.Add(this.m_AddTagField);
        }

        private void RenderComments()
        {
            this.m_CommentsContainer.Clear();

            if (this.m_Card == null || this.m_Card.Comments.Count == 0)
            {
                Label emptyState = new Label("No comments yet.");
                emptyState.AddToClassList("mini-mcp-kanban-card-inspector__empty-state");
                this.m_CommentsContainer.Add(emptyState);
                return;
            }

            for (int commentIndex = 0; commentIndex < this.m_Card.Comments.Count; commentIndex++)
            {
                KanbanCardCommentData comment = this.m_Card.Comments[commentIndex];
                if (comment == null)
                {
                    continue;
                }

                comment.EnsureInitialized();

                TemplateContainer commentTree = this.m_CommentItemTree.CloneTree();
                VisualElement commentRoot = commentTree.Q<VisualElement>("CommentItemRoot") ?? commentTree;
                Label timestampLabel = commentRoot.Q<Label>("CommentTimestampLabel");
                Label authorLabel = commentRoot.Q<Label>("CommentAuthorLabel");
                Button deleteButton = commentRoot.Q<Button>("CommentDeleteButton");
                Label commentTextLabel = commentRoot.Q<Label>("CommentTextLabel");
                Label readStateLabel = commentRoot.Q<Label>("CommentReadStateLabel");

                if (timestampLabel == null || authorLabel == null || deleteButton == null || commentTextLabel == null || readStateLabel == null)
                {
                    continue;
                }

                timestampLabel.text = FormatCommentTimestamp(comment.CreatedAtUtc);
                authorLabel.text = FormatCommentAuthor(comment);
                commentTextLabel.text = comment.Text ?? string.Empty;
                ConfigureReadStateLabel(readStateLabel, comment);

                bool isOwnComment = IsOwnComment(comment);
                bool isMcpComment = string.Equals(comment.AuthorKind, "mcp", StringComparison.OrdinalIgnoreCase);
                commentRoot.EnableInClassList("mini-mcp-kanban-card-inspector__comment--own", isOwnComment);
                commentRoot.EnableInClassList("mini-mcp-kanban-card-inspector__comment--foreign", !isOwnComment && !isMcpComment);
                commentRoot.EnableInClassList("mini-mcp-kanban-card-inspector__comment--mcp", isMcpComment);

                int capturedIndex = commentIndex;
                deleteButton.clicked += () =>
                {
                    DeleteComment(this.m_Card, this.m_Plan, capturedIndex);
                    this.RenderComments();
                };

                this.m_CommentsContainer.Add(commentRoot);
            }
        }

        private static void ApplyCardChanges(KanbanCardAsset card, KanbanPlan plan, string nextTitle, string nextDescription, KanbanCardStatus nextStatus)
        {
            Undo.RecordObject(card, "Edit Kanban Card");
            if (plan != null)
            {
                Undo.RecordObject(plan, "Edit Kanban Card");
                plan.UpdateCardTitle(card.Id, nextTitle);
                plan.UpdateCardDescription(card.Id, nextDescription);
                if (card.Status != nextStatus)
                {
                    plan.MoveCard(card.Id, nextStatus);
                }
            }
            else
            {
                card.Title = nextTitle ?? string.Empty;
                card.Description = nextDescription ?? string.Empty;
                card.Status = nextStatus;
                card.name = string.IsNullOrWhiteSpace(card.Title) ? "Kanban Card" : card.Title.Trim();
            }

            PersistChanges(card, plan, false);
        }

        private static void ApplyCardMetadata(KanbanCardAsset card, KanbanPlan plan, string nextCategoryId, IEnumerable<string> nextTagIds)
        {
            Undo.RecordObject(card, "Edit Kanban Card Metadata");
            if (plan != null)
            {
                Undo.RecordObject(plan, "Edit Kanban Card Metadata");
                if (!plan.UpdateCardMetadata(card.Id, nextCategoryId, nextTagIds))
                {
                    return;
                }
            }
            else
            {
                card.CategoryId = nextCategoryId ?? string.Empty;
                card.TagIds.Clear();
                if (nextTagIds != null)
                {
                    foreach (string tagId in nextTagIds)
                    {
                        if (!string.IsNullOrWhiteSpace(tagId))
                        {
                            card.TagIds.Add(tagId.Trim());
                        }
                    }
                }
            }

            PersistChanges(card, plan, false);
        }

        private static void AddComment(KanbanCardAsset card, KanbanPlan plan, string text)
        {
            KanbanUserContext.Identity identity = KanbanUserContext.GetCurrentEditorIdentity();
            Undo.RecordObject(card, "Add Kanban Comment");
            if (plan != null)
            {
                Undo.RecordObject(plan, "Add Kanban Comment");
                plan.AddComment(card.Id, text, identity.DisplayName, identity.Id, identity.Kind);
            }
            else
            {
                string normalized = (text ?? string.Empty).Trim();
                if (normalized.Length == 0)
                {
                    return;
                }

                card.Comments.Add(new KanbanCardCommentData(normalized, DateTime.UtcNow.ToString("o"), identity.DisplayName, identity.Id, identity.Kind));
            }

            PersistChanges(card, plan, true);
        }

        private static int MarkCommentsAsRead(KanbanCardAsset card)
        {
            if (card == null)
            {
                return 0;
            }

            KanbanUserContext.Identity identity = KanbanUserContext.GetCurrentEditorIdentity();
            return card.MarkCommentsAsRead(identity.DisplayName, identity.Id, identity.Kind);
        }

        private static void DeleteComment(KanbanCardAsset card, KanbanPlan plan, int commentIndex)
        {
            Undo.RecordObject(card, "Delete Kanban Comment");
            if (plan != null)
            {
                Undo.RecordObject(plan, "Delete Kanban Comment");
                plan.RemoveComment(card.Id, commentIndex);
            }
            else if (commentIndex >= 0 && commentIndex < card.Comments.Count)
            {
                card.Comments.RemoveAt(commentIndex);
            }

            PersistChanges(card, plan, true);
        }

        private static void PersistChanges(KanbanCardAsset card, KanbanPlan plan, bool saveImmediately)
        {
            EditorUtility.SetDirty(card);
            if (plan != null)
            {
                EditorUtility.SetDirty(plan);
            }

            if (saveImmediately)
            {
                AssetDatabase.SaveAssets();
                KanbanBoardWindow.RefreshOpenBoards();
                return;
            }

            QueueDeferredAssetSave();
            QueueDeferredBoardRefresh();
        }

        private static void QueueDeferredAssetSave()
        {
            s_PendingAssetSave = true;
            s_LastAssetChangeTime = EditorApplication.timeSinceStartup;
            EnsureUpdateHook();
        }

        private static void QueueDeferredBoardRefresh()
        {
            s_PendingBoardRefresh = true;
            s_LastBoardRefreshRequestTime = EditorApplication.timeSinceStartup;
            EnsureUpdateHook();
        }

        private static void EnsureUpdateHook()
        {
            if (s_UpdateHookRegistered)
            {
                return;
            }

            EditorApplication.update += OnEditorUpdate;
            s_UpdateHookRegistered = true;
        }

        private static void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;

            if (s_PendingBoardRefresh && now - s_LastBoardRefreshRequestTime >= DeferredRefreshDelaySeconds)
            {
                s_PendingBoardRefresh = false;
                KanbanBoardWindow.RefreshOpenBoards();
            }

            if (s_PendingAssetSave && now - s_LastAssetChangeTime >= DeferredSaveDelaySeconds)
            {
                s_PendingAssetSave = false;
                AssetDatabase.SaveAssets();
            }

            if (!s_PendingBoardRefresh && !s_PendingAssetSave && s_UpdateHookRegistered)
            {
                EditorApplication.update -= OnEditorUpdate;
                s_UpdateHookRegistered = false;
            }
        }

        private static void FlushPendingEditorWork()
        {
            if (s_PendingBoardRefresh)
            {
                s_PendingBoardRefresh = false;
                KanbanBoardWindow.RefreshOpenBoards();
            }

            if (s_PendingAssetSave)
            {
                s_PendingAssetSave = false;
                AssetDatabase.SaveAssets();
            }

            if (s_UpdateHookRegistered)
            {
                EditorApplication.update -= OnEditorUpdate;
                s_UpdateHookRegistered = false;
            }
        }

        private static KanbanPlan FindOwningPlan(KanbanCardAsset card)
        {
            string assetPath = AssetDatabase.GetAssetPath(card);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (UnityEngine.Object asset in assets)
            {
                if (asset is KanbanPlan plan)
                {
                    return plan;
                }
            }

            return null;
        }

        private static string FormatCommentTimestamp(string createdAtUtc)
        {
            if (DateTime.TryParse(createdAtUtc, out DateTime parsed))
            {
                return parsed.ToLocalTime().ToString("g");
            }

            return string.IsNullOrWhiteSpace(createdAtUtc) ? "Comment" : createdAtUtc;
        }

        private static string FormatCommentAuthor(KanbanCardCommentData comment)
        {
            if (comment == null)
            {
                return "Unknown";
            }

            if (!string.IsNullOrWhiteSpace(comment.AuthorName))
            {
                return comment.AuthorName;
            }

            if (string.Equals(comment.AuthorKind, "mcp", StringComparison.OrdinalIgnoreCase))
            {
                return "MCP";
            }

            return "Editor User";
        }

        private static void ConfigureReadStateLabel(Label readStateLabel, KanbanCardCommentData comment)
        {
            if (readStateLabel == null)
            {
                return;
            }

            string tooltip = BuildReadStateTooltip(comment);
            bool hasReaders = !string.IsNullOrWhiteSpace(tooltip);
            readStateLabel.style.display = hasReaders ? DisplayStyle.Flex : DisplayStyle.None;
            readStateLabel.text = "Read";
            readStateLabel.tooltip = tooltip;
        }

        private static string BuildReadStateTooltip(KanbanCardCommentData comment)
        {
            if (comment == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>();
            foreach (KanbanCommentReadState readState in comment.ReadBy)
            {
                if (readState == null)
                {
                    continue;
                }

                readState.EnsureInitialized();
                if (IsSameIdentity(comment.AuthorName, comment.AuthorId, comment.AuthorKind, readState.ReaderName, readState.ReaderId, readState.ReaderKind))
                {
                    continue;
                }

                string readerName = string.IsNullOrWhiteSpace(readState.ReaderName)
                    ? (string.IsNullOrWhiteSpace(readState.ReaderKind) ? "Unknown" : readState.ReaderKind)
                    : readState.ReaderName;
                string readAt = FormatCommentTimestamp(readState.ReadAtUtc);
                lines.Add(string.IsNullOrWhiteSpace(readAt) ? readerName : readerName + " | " + readAt);
            }

            if (lines.Count == 0)
            {
                return string.Empty;
            }

            return "Read by\n" + string.Join("\n", lines);
        }

        private static bool IsSameIdentity(string leftName, string leftId, string leftKind, string rightName, string rightId, string rightKind)
        {
            string normalizedLeftId = (leftId ?? string.Empty).Trim();
            string normalizedRightId = (rightId ?? string.Empty).Trim();
            if (normalizedLeftId.Length > 0 && normalizedRightId.Length > 0)
            {
                return string.Equals(normalizedLeftId, normalizedRightId, StringComparison.Ordinal);
            }

            string normalizedLeftName = (leftName ?? string.Empty).Trim();
            string normalizedRightName = (rightName ?? string.Empty).Trim();
            string normalizedLeftKind = (leftKind ?? string.Empty).Trim();
            string normalizedRightKind = (rightKind ?? string.Empty).Trim();

            return normalizedLeftName.Length > 0
                && normalizedRightName.Length > 0
                && normalizedLeftKind.Length > 0
                && normalizedRightKind.Length > 0
                && string.Equals(normalizedLeftName, normalizedRightName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizedLeftKind, normalizedRightKind, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOwnComment(KanbanCardCommentData comment)
        {
            if (comment == null)
            {
                return false;
            }

            KanbanUserContext.Identity identity = KanbanUserContext.GetCurrentEditorIdentity();
            if (!string.IsNullOrWhiteSpace(comment.AuthorId) && !string.IsNullOrWhiteSpace(identity.Id))
            {
                return string.Equals(comment.AuthorId, identity.Id, StringComparison.Ordinal);
            }

            return string.Equals(comment.AuthorKind, CurrentEditorAuthorKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(comment.AuthorName, identity.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyTagChipTint(VisualElement tagChip, Color color)
        {
            tagChip.style.backgroundColor = new StyleColor(new Color(color.r, color.g, color.b, 0.22f));
            tagChip.style.borderTopColor = new StyleColor(color);
            tagChip.style.borderRightColor = new StyleColor(color);
            tagChip.style.borderBottomColor = new StyleColor(color);
            tagChip.style.borderLeftColor = new StyleColor(color);
            tagChip.style.borderTopWidth = 1f;
            tagChip.style.borderRightWidth = 1f;
            tagChip.style.borderBottomWidth = 1f;
            tagChip.style.borderLeftWidth = 1f;

            float luminance = (color.r * 0.299f) + (color.g * 0.587f) + (color.b * 0.114f);
            Color foreground = luminance > 0.62f ? new Color(0.11f, 0.12f, 0.13f) : new Color(0.94f, 0.95f, 0.97f);
            tagChip.style.color = new StyleColor(foreground);
        }
    }
}