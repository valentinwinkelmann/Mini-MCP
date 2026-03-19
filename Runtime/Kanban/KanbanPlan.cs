using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MiniMCP.Kanban
{
    public enum KanbanCardStatus
    {
        Planning = 3,
        Todo = 0,
        InProgress = 1,
        Finished = 2,
        InReview = 4,
    }

    [CreateAssetMenu(fileName = "KanbanPlan", menuName = "MiniMCP/Kanban Plan")]
    public sealed class KanbanPlan : ScriptableObject
    {
        private static readonly KanbanCardStatus[] s_ColumnOrder =
        {
            KanbanCardStatus.Planning,
            KanbanCardStatus.Todo,
            KanbanCardStatus.InProgress,
            KanbanCardStatus.InReview,
            KanbanCardStatus.Finished,
        };

        [SerializeField, TextArea(3, 10)] private string m_Description;
        [SerializeField] private List<KanbanCategoryDefinition> m_Categories = new List<KanbanCategoryDefinition>();
        [SerializeField] private List<KanbanTagDefinition> m_Tags = new List<KanbanTagDefinition>();
        [SerializeField] private List<KanbanCardAsset> m_Cards = new List<KanbanCardAsset>();
        [SerializeField, HideInInspector] private List<KanbanColumnData> m_Columns = new List<KanbanColumnData>();

        public string Description
        {
            get => this.m_Description;
            set => this.m_Description = value ?? string.Empty;
        }

        public IReadOnlyList<KanbanCardAsset> Cards => this.m_Cards;

    public IReadOnlyList<KanbanCategoryDefinition> Categories => this.m_Categories;

    public IReadOnlyList<KanbanTagDefinition> Tags => this.m_Tags;

        public static IReadOnlyList<KanbanCardStatus> ColumnOrder => s_ColumnOrder;

        public void EnsureInitialized()
        {
            this.m_Description ??= string.Empty;

            if (this.m_Categories == null)
            {
                this.m_Categories = new List<KanbanCategoryDefinition>();
            }

            if (this.m_Tags == null)
            {
                this.m_Tags = new List<KanbanTagDefinition>();
            }

            if (this.m_Cards == null)
            {
                this.m_Cards = new List<KanbanCardAsset>();
            }

            if (this.m_Columns == null)
            {
                this.m_Columns = new List<KanbanColumnData>();
            }

            this.MigrateLegacyColumnsIfNeeded();
            this.NormalizeCategories();
            this.NormalizeTags();
            this.NormalizeCards();
            this.EnsureCardSubAssets();
        }

        public KanbanCardAsset AddCard(KanbanCardStatus status, string title, string description, string createdByName = "", string createdById = "", string createdByKind = "")
        {
            this.EnsureInitialized();

            KanbanCardAsset card = CreateInstance<KanbanCardAsset>();
            card.Initialize(title, description, status, false, createdByName, createdById, createdByKind);
            this.m_Cards.Insert(this.GetInsertIndexForStatusEnd(status), card);
            this.AttachCardSubAsset(card);
            return card;
        }

        public bool RemoveCard(string cardId)
        {
            this.EnsureInitialized();

            for (int index = 0; index < this.m_Cards.Count; index++)
            {
                KanbanCardAsset card = this.m_Cards[index];
                if (card == null || !string.Equals(card.Id, cardId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (card.IsLocked)
                {
                    return false;
                }

                this.m_Cards.RemoveAt(index);
                this.DestroyCardSubAsset(card);
                return true;
            }

            return false;
        }

        public bool UpdateCardTitle(string cardId, string title)
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (!CanEditCard(card))
            {
                return false;
            }

            card.Title = title ?? string.Empty;
            this.SyncCardAssetName(card);
            return true;
        }

        public bool UpdateCardDescription(string cardId, string description)
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (!CanEditCard(card))
            {
                return false;
            }

            card.Description = description ?? string.Empty;
            return true;
        }

        public bool UpdateCardMetadata(string cardId, string categoryId, IEnumerable<string> tagIds)
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (!CanEditCard(card))
            {
                return false;
            }

            string normalizedCategoryId = string.IsNullOrWhiteSpace(categoryId) ? string.Empty : categoryId.Trim();
            if (normalizedCategoryId.Length > 0 && this.GetCategoryById(normalizedCategoryId) == null)
            {
                return false;
            }

            HashSet<string> normalizedTagIds = new HashSet<string>(StringComparer.Ordinal);
            if (tagIds != null)
            {
                foreach (string rawTagId in tagIds)
                {
                    if (string.IsNullOrWhiteSpace(rawTagId))
                    {
                        continue;
                    }

                    string normalizedTagId = rawTagId.Trim();
                    if (this.GetTagById(normalizedTagId) == null)
                    {
                        return false;
                    }

                    normalizedTagIds.Add(normalizedTagId);
                }
            }

            card.CategoryId = normalizedCategoryId;
            card.TagIds.Clear();
            foreach (string tagId in normalizedTagIds)
            {
                card.TagIds.Add(tagId);
            }

            return true;
        }

        public KanbanCardCommentData AddComment(string cardId, string text, string authorName = "", string authorId = "", string authorKind = "")
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (card == null)
            {
                return null;
            }

            string normalizedText = (text ?? string.Empty).Trim();
            if (normalizedText.Length == 0)
            {
                return null;
            }

            KanbanCardCommentData comment = new KanbanCardCommentData(normalizedText, DateTime.UtcNow.ToString("o"), authorName, authorId, authorKind);
            comment.MarkAsReadBy(authorName, authorId, authorKind);
            card.Comments.Add(comment);
            return comment;
        }

        public bool RemoveComment(string cardId, int commentIndex)
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (card == null)
            {
                return false;
            }

            if (commentIndex < 0 || commentIndex >= card.Comments.Count)
            {
                return false;
            }

            card.Comments.RemoveAt(commentIndex);
            return true;
        }

        public bool MoveCard(string cardId, KanbanCardStatus targetStatus)
        {
            return this.MoveCard(cardId, targetStatus, null);
        }

        public bool MoveCard(string cardId, KanbanCardStatus targetStatus, string beforeCardId)
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (!CanEditCard(card))
            {
                return false;
            }

            KanbanCardAsset beforeCard = null;
            if (!string.IsNullOrWhiteSpace(beforeCardId))
            {
                beforeCard = this.GetCard(beforeCardId);
                if (beforeCard == null || beforeCard == card || beforeCard.Status != targetStatus)
                {
                    return false;
                }
            }

            int sourceIndex = this.m_Cards.IndexOf(card);
            if (sourceIndex < 0)
            {
                return false;
            }

            KanbanCardStatus sourceStatus = card.Status;
            this.m_Cards.RemoveAt(sourceIndex);

            card.Status = targetStatus;

            int insertIndex = beforeCard != null
                ? this.m_Cards.IndexOf(beforeCard)
                : this.GetInsertIndexForStatusEnd(targetStatus);

            if (insertIndex < 0 || insertIndex > this.m_Cards.Count)
            {
                insertIndex = this.m_Cards.Count;
            }

            this.m_Cards.Insert(insertIndex, card);

            bool movedToDifferentStatus = sourceStatus != targetStatus;
            bool movedWithinStatus = beforeCard != null || insertIndex != sourceIndex;
            return movedToDifferentStatus || movedWithinStatus;
        }

        public KanbanCardAsset DuplicateCard(string cardId)
        {
            this.EnsureInitialized();

            KanbanCardAsset source = this.GetCard(cardId);
            if (source == null)
            {
                return null;
            }

            string duplicateTitle = string.IsNullOrWhiteSpace(source.Title)
                ? "Kanban Card Copy"
                : source.Title.Trim() + " Copy";

            KanbanCardAsset duplicate = this.AddCard(source.Status, duplicateTitle, source.Description ?? string.Empty, source.CreatedByName, source.CreatedById, source.CreatedByKind);
            if (duplicate == null)
            {
                return null;
            }

            foreach (KanbanCardCommentData comment in source.Comments)
            {
                if (comment == null)
                {
                    continue;
                }

                comment.EnsureInitialized();
                duplicate.Comments.Add(comment.Clone());
            }

            duplicate.CategoryId = source.CategoryId;
            duplicate.TagIds.Clear();
            duplicate.TagIds.AddRange(source.TagIds);
            duplicate.IsLocked = false;
            this.SyncCardAssetName(duplicate);
            return duplicate;
        }

        public bool SetCardLocked(string cardId, bool isLocked)
        {
            this.EnsureInitialized();

            KanbanCardAsset card = this.GetCard(cardId);
            if (card == null || card.IsLocked == isLocked)
            {
                return false;
            }

            card.IsLocked = isLocked;
            return true;
        }

        public KanbanCardAsset GetCard(string cardId)
        {
            this.EnsureInitialized();

            foreach (KanbanCardAsset card in this.m_Cards)
            {
                if (card != null && string.Equals(card.Id, cardId, StringComparison.Ordinal))
                {
                    return card;
                }
            }

            return null;
        }

        public KanbanCategoryDefinition GetCategoryById(string categoryId)
        {
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                return null;
            }

            string normalizedId = categoryId.Trim();
            foreach (KanbanCategoryDefinition category in this.m_Categories)
            {
                if (category != null && string.Equals(category.Id, normalizedId, StringComparison.Ordinal))
                {
                    return category;
                }
            }

            return null;
        }

        public KanbanTagDefinition GetTagById(string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId))
            {
                return null;
            }

            string normalizedId = tagId.Trim();
            foreach (KanbanTagDefinition tag in this.m_Tags)
            {
                if (tag != null && string.Equals(tag.Id, normalizedId, StringComparison.Ordinal))
                {
                    return tag;
                }
            }

            return null;
        }

        public KanbanCategoryDefinition FindCategory(string value)
        {
            this.EnsureInitialized();
            return FindCategoryDefinition(this.m_Categories, value);
        }

        public KanbanTagDefinition FindTag(string value)
        {
            this.EnsureInitialized();
            return FindTagDefinition(this.m_Tags, value);
        }

        public bool CardMatchesFilters(KanbanCardAsset card, string categoryFilter, IEnumerable<string> tagFilters, string searchText)
        {
            this.EnsureInitialized();

            if (card == null)
            {
                return false;
            }

            KanbanCategoryDefinition cardCategory = this.GetCategoryById(card.CategoryId);
            if (!string.IsNullOrWhiteSpace(categoryFilter))
            {
                if (cardCategory == null || !MatchesDefinition(cardCategory, categoryFilter))
                {
                    return false;
                }
            }

            if (tagFilters != null)
            {
                foreach (string tagFilter in tagFilters)
                {
                    if (string.IsNullOrWhiteSpace(tagFilter))
                    {
                        continue;
                    }

                    bool matchedTag = false;
                    foreach (string tagId in card.TagIds)
                    {
                        KanbanTagDefinition tag = this.GetTagById(tagId);
                        if (tag != null && MatchesDefinition(tag, tagFilter))
                        {
                            matchedTag = true;
                            break;
                        }
                    }

                    if (!matchedTag)
                    {
                        return false;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            string normalizedSearch = searchText.Trim();
            if (normalizedSearch.Length == 0)
            {
                return true;
            }

            if (ContainsIgnoreCase(card.Title, normalizedSearch)
                || ContainsIgnoreCase(card.Description, normalizedSearch))
            {
                return true;
            }

            if (cardCategory != null && ContainsIgnoreCase(cardCategory.Name, normalizedSearch))
            {
                return true;
            }

            foreach (string tagId in card.TagIds)
            {
                KanbanTagDefinition tag = this.GetTagById(tagId);
                if (tag != null && ContainsIgnoreCase(tag.Name, normalizedSearch))
                {
                    return true;
                }
            }

            foreach (KanbanCardCommentData comment in card.Comments)
            {
                if (comment != null && ContainsIgnoreCase(comment.Text, normalizedSearch))
                {
                    return true;
                }
            }

            return false;
        }

        public List<KanbanCardAsset> GetCards(KanbanCardStatus status)
        {
            this.EnsureInitialized();

            List<KanbanCardAsset> result = new List<KanbanCardAsset>();
            foreach (KanbanCardAsset card in this.m_Cards)
            {
                if (card != null && card.Status == status)
                {
                    result.Add(card);
                }
            }

            return result;
        }

        public int GetCardCount(KanbanCardStatus status)
        {
            this.EnsureInitialized();

            int count = 0;
            foreach (KanbanCardAsset card in this.m_Cards)
            {
                if (card != null && card.Status == status)
                {
                    count++;
                }
            }

            return count;
        }

        public KanbanCardStatus? TryParseStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = value.Trim();
            if (normalized.Equals("Planning", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Plan", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Planned", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Planning;
            }

            if (normalized.Equals("Todo", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("To Do", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Backlog", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Todo;
            }

            if (normalized.Equals("In Progress", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("InProgress", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.InProgress;
            }

            if (normalized.Equals("In Review", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("InReview", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Review", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("in_review", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.InReview;
            }

            if (normalized.Equals("Finished", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Done", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Finished;
            }

            return null;
        }

        public static string GetColumnTitle(KanbanCardStatus status)
        {
            switch (status)
            {
                case KanbanCardStatus.Planning:
                    return "Planning";
                case KanbanCardStatus.Todo:
                    return "Todo";
                case KanbanCardStatus.InProgress:
                    return "In Progress";
                case KanbanCardStatus.InReview:
                    return "In Review";
                default:
                    return "Finished";
            }
        }

        private void OnEnable()
        {
            this.EnsureInitialized();
        }

        private void OnValidate()
        {
            this.EnsureInitialized();
        }

        private void NormalizeCards()
        {
            for (int index = this.m_Cards.Count - 1; index >= 0; index--)
            {
                KanbanCardAsset card = this.m_Cards[index];
                if (card == null)
                {
                    this.m_Cards.RemoveAt(index);
                    continue;
                }

                card.EnsureInitialized();
                if (!string.IsNullOrWhiteSpace(card.CategoryId) && this.GetCategoryById(card.CategoryId) == null)
                {
                    card.CategoryId = string.Empty;
                }

                if (card.TagIds != null)
                {
                    HashSet<string> uniqueTagIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int tagIndex = card.TagIds.Count - 1; tagIndex >= 0; tagIndex--)
                    {
                        string tagId = card.TagIds[tagIndex];
                        if (string.IsNullOrWhiteSpace(tagId))
                        {
                            card.TagIds.RemoveAt(tagIndex);
                            continue;
                        }

                        string normalizedTagId = tagId.Trim();
                        if (this.GetTagById(normalizedTagId) == null || !uniqueTagIds.Add(normalizedTagId))
                        {
                            card.TagIds.RemoveAt(tagIndex);
                            continue;
                        }

                        card.TagIds[tagIndex] = normalizedTagId;
                    }
                }

                this.SyncCardAssetName(card);
            }
        }

        private void NormalizeCategories()
        {
            NormalizeDefinitions(this.m_Categories, "Category", true);
        }

        private void NormalizeTags()
        {
            NormalizeDefinitions(this.m_Tags, "Tag", false);
        }

        private static void NormalizeDefinitions<TDefinition>(List<TDefinition> definitions, string defaultPrefix, bool useWarmPalette)
            where TDefinition : KanbanLabelDefinition, new()
        {
            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = definitions.Count - 1; index >= 0; index--)
            {
                TDefinition definition = definitions[index];
                if (definition == null)
                {
                    definitions[index] = new TDefinition();
                    definition = definitions[index];
                }

                definition.EnsureInitialized(index, defaultPrefix, useWarmPalette);
                if (!seenIds.Add(definition.Id))
                {
                    definition.ResetId();
                    definition.EnsureInitialized(index, defaultPrefix, useWarmPalette);
                    seenIds.Add(definition.Id);
                }
            }
        }

        private void MigrateLegacyColumnsIfNeeded()
        {
            bool hasLegacyData = this.m_Columns != null && this.m_Columns.Count > 0;
            bool hasCards = this.m_Cards != null && this.m_Cards.Count > 0;
            if (!hasLegacyData || hasCards)
            {
                return;
            }

            foreach (KanbanColumnData legacyColumn in this.m_Columns)
            {
                if (legacyColumn == null)
                {
                    continue;
                }

                legacyColumn.EnsureInitialized();
                KanbanCardStatus status = MapLegacyTitleToStatus(legacyColumn.Title);
                foreach (KanbanCardData legacyCard in legacyColumn.Cards)
                {
                    if (legacyCard == null)
                    {
                        continue;
                    }

                    legacyCard.EnsureInitialized();
                    KanbanCardAsset migratedCard = CreateInstance<KanbanCardAsset>();
                    migratedCard.Initialize(legacyCard.Title, legacyCard.Description, status);
                    migratedCard.SetId(legacyCard.Id);

                    foreach (KanbanCardCommentData comment in legacyCard.Comments)
                    {
                        if (comment == null)
                        {
                            continue;
                        }

                        comment.EnsureInitialized();
                        migratedCard.Comments.Add(comment.Clone());
                    }

                    this.m_Cards.Add(migratedCard);
                }
            }

            this.m_Columns.Clear();
        }

        private static KanbanCardStatus MapLegacyTitleToStatus(string title)
        {
            string normalized = (title ?? string.Empty).Trim();
            if (normalized.Equals("Planning", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Plan", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Planning;
            }

            if (normalized.Equals("In Progress", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.InProgress;
            }

            if (normalized.Equals("In Review", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Review", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.InReview;
            }

            if (normalized.Equals("Done", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Finished", StringComparison.OrdinalIgnoreCase))
            {
                return KanbanCardStatus.Finished;
            }

            return KanbanCardStatus.Todo;
        }

        private static bool CanEditCard(KanbanCardAsset card)
        {
            return card != null && !card.IsLocked;
        }

        private static bool ContainsIgnoreCase(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text)
                && !string.IsNullOrWhiteSpace(value)
                && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesDefinition(KanbanLabelDefinition definition, string filter)
        {
            if (definition == null || string.IsNullOrWhiteSpace(filter))
            {
                return false;
            }

            string normalizedFilter = filter.Trim();
            return string.Equals(definition.Id, normalizedFilter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(definition.Name, normalizedFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static KanbanCategoryDefinition FindCategoryDefinition(List<KanbanCategoryDefinition> definitions, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            foreach (KanbanCategoryDefinition definition in definitions)
            {
                if (MatchesDefinition(definition, value))
                {
                    return definition;
                }
            }

            return null;
        }

        private static KanbanTagDefinition FindTagDefinition(List<KanbanTagDefinition> definitions, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            foreach (KanbanTagDefinition definition in definitions)
            {
                if (MatchesDefinition(definition, value))
                {
                    return definition;
                }
            }

            return null;
        }

        private int GetInsertIndexForStatusEnd(KanbanCardStatus status)
        {
            int lastMatchingIndex = -1;
            int targetOrder = GetStatusOrder(status);

            for (int index = 0; index < this.m_Cards.Count; index++)
            {
                KanbanCardAsset card = this.m_Cards[index];
                if (card == null)
                {
                    continue;
                }

                if (card.Status == status)
                {
                    lastMatchingIndex = index;
                    continue;
                }

                if (lastMatchingIndex < 0 && GetStatusOrder(card.Status) > targetOrder)
                {
                    return index;
                }
            }

            return lastMatchingIndex >= 0 ? lastMatchingIndex + 1 : this.m_Cards.Count;
        }

        private static int GetStatusOrder(KanbanCardStatus status)
        {
            for (int index = 0; index < s_ColumnOrder.Length; index++)
            {
                if (s_ColumnOrder[index] == status)
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        private void SyncCardAssetName(KanbanCardAsset card)
        {
            if (card == null)
            {
                return;
            }

            string desiredName = string.IsNullOrWhiteSpace(card.Title) ? "Kanban Card" : card.Title.Trim();
            if (!string.Equals(card.name, desiredName, StringComparison.Ordinal))
            {
                card.name = desiredName;
            }
        }

        private void EnsureCardSubAssets()
        {
#if UNITY_EDITOR
            if (!AssetDatabase.Contains(this))
            {
                return;
            }

            foreach (KanbanCardAsset card in this.m_Cards)
            {
                this.AttachCardSubAsset(card);
            }
#endif
        }

        private void AttachCardSubAsset(KanbanCardAsset card)
        {
#if UNITY_EDITOR
            if (card == null)
            {
                return;
            }

            this.SyncCardAssetName(card);

            if (!AssetDatabase.Contains(this))
            {
                return;
            }

            if (!AssetDatabase.Contains(card))
            {
                AssetDatabase.AddObjectToAsset(card, this);
                EditorUtility.SetDirty(card);
                EditorUtility.SetDirty(this);
                AssetDatabase.SaveAssets();
            }
#endif
        }

        private void DestroyCardSubAsset(KanbanCardAsset card)
        {
#if UNITY_EDITOR
            if (card == null)
            {
                return;
            }

            if (AssetDatabase.Contains(card))
            {
                UnityEngine.Object.DestroyImmediate(card, true);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(card);
            }
#endif
        }
    }

    [Serializable]
    public sealed class KanbanCardCommentData
    {
        [SerializeField] private string m_Text;
        [SerializeField] private string m_CreatedAtUtc;
        [SerializeField] private string m_AuthorName;
        [SerializeField] private string m_AuthorId;
        [SerializeField] private string m_AuthorKind;
        [SerializeField] private List<KanbanCommentReadState> m_ReadBy = new List<KanbanCommentReadState>();

        public string Text
        {
            get => this.m_Text;
            set => this.m_Text = value;
        }

        public string CreatedAtUtc
        {
            get => this.m_CreatedAtUtc;
            set => this.m_CreatedAtUtc = value;
        }

        public string AuthorName
        {
            get => this.m_AuthorName;
            set => this.m_AuthorName = value;
        }

        public string AuthorId
        {
            get => this.m_AuthorId;
            set => this.m_AuthorId = value;
        }

        public string AuthorKind
        {
            get => this.m_AuthorKind;
            set => this.m_AuthorKind = value;
        }

        public List<KanbanCommentReadState> ReadBy => this.m_ReadBy;

        public KanbanCardCommentData(string text, string createdAtUtc, string authorName = "", string authorId = "", string authorKind = "")
        {
            this.m_Text = text;
            this.m_CreatedAtUtc = createdAtUtc;
            this.m_AuthorName = authorName;
            this.m_AuthorId = authorId;
            this.m_AuthorKind = authorKind;
            this.m_ReadBy = new List<KanbanCommentReadState>();
        }

        public KanbanCardCommentData Clone()
        {
            this.EnsureInitialized();

            KanbanCardCommentData clone = new KanbanCardCommentData(this.m_Text, this.m_CreatedAtUtc, this.m_AuthorName, this.m_AuthorId, this.m_AuthorKind);
            foreach (KanbanCommentReadState readState in this.m_ReadBy)
            {
                if (readState == null)
                {
                    continue;
                }

                readState.EnsureInitialized();
                clone.m_ReadBy.Add(new KanbanCommentReadState(readState.ReaderName, readState.ReaderId, readState.ReaderKind, readState.ReadAtUtc));
            }

            return clone;
        }

        public bool IsUnreadFor(string readerName, string readerId, string readerKind)
        {
            this.EnsureInitialized();
            if (this.IsAuthoredBy(readerName, readerId, readerKind))
            {
                return false;
            }

            return !this.HasBeenReadBy(readerName, readerId, readerKind);
        }

        public bool HasBeenReadBy(string readerName, string readerId, string readerKind)
        {
            this.EnsureInitialized();
            foreach (KanbanCommentReadState readState in this.m_ReadBy)
            {
                if (readState == null)
                {
                    continue;
                }

                readState.EnsureInitialized();
                if (MatchesIdentity(readState.ReaderName, readState.ReaderId, readState.ReaderKind, readerName, readerId, readerKind))
                {
                    return true;
                }
            }

            return false;
        }

        public bool MarkAsReadBy(string readerName, string readerId, string readerKind, string readAtUtc = null)
        {
            if (!TryNormalizeIdentity(readerName, readerId, readerKind, out string normalizedName, out string normalizedId, out string normalizedKind))
            {
                return false;
            }

            this.EnsureInitialized();
            string normalizedReadAtUtc = string.IsNullOrWhiteSpace(readAtUtc) ? DateTime.UtcNow.ToString("o") : readAtUtc.Trim();
            foreach (KanbanCommentReadState readState in this.m_ReadBy)
            {
                if (readState == null)
                {
                    continue;
                }

                readState.EnsureInitialized();
                if (!MatchesIdentity(readState.ReaderName, readState.ReaderId, readState.ReaderKind, normalizedName, normalizedId, normalizedKind))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(readState.ReadAtUtc))
                {
                    readState.ReadAtUtc = normalizedReadAtUtc;
                    return true;
                }

                return false;
            }

            this.m_ReadBy.Add(new KanbanCommentReadState(normalizedName, normalizedId, normalizedKind, normalizedReadAtUtc));
            return true;
        }

        public void EnsureInitialized()
        {
            this.m_Text ??= string.Empty;
            this.m_CreatedAtUtc ??= string.Empty;
            this.m_AuthorName ??= string.Empty;
            this.m_AuthorId ??= string.Empty;
            this.m_AuthorKind ??= string.Empty;
            this.m_ReadBy ??= new List<KanbanCommentReadState>();

            foreach (KanbanCommentReadState readState in this.m_ReadBy)
            {
                readState?.EnsureInitialized();
            }
        }

        private bool IsAuthoredBy(string readerName, string readerId, string readerKind)
        {
            return MatchesIdentity(this.m_AuthorName, this.m_AuthorId, this.m_AuthorKind, readerName, readerId, readerKind);
        }

        private static bool MatchesIdentity(string leftName, string leftId, string leftKind, string rightName, string rightId, string rightKind)
        {
            TryNormalizeIdentity(leftName, leftId, leftKind, out string normalizedLeftName, out string normalizedLeftId, out string normalizedLeftKind);
            TryNormalizeIdentity(rightName, rightId, rightKind, out string normalizedRightName, out string normalizedRightId, out string normalizedRightKind);

            if (normalizedLeftId.Length > 0 && normalizedRightId.Length > 0)
            {
                return string.Equals(normalizedLeftId, normalizedRightId, StringComparison.Ordinal);
            }

            if (normalizedLeftKind.Length > 0 && normalizedRightKind.Length > 0
                && normalizedLeftName.Length > 0 && normalizedRightName.Length > 0)
            {
                return string.Equals(normalizedLeftKind, normalizedRightKind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(normalizedLeftName, normalizedRightName, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool TryNormalizeIdentity(string name, string id, string kind, out string normalizedName, out string normalizedId, out string normalizedKind)
        {
            normalizedName = (name ?? string.Empty).Trim();
            normalizedId = (id ?? string.Empty).Trim();
            normalizedKind = (kind ?? string.Empty).Trim();
            return normalizedId.Length > 0 || normalizedName.Length > 0 || normalizedKind.Length > 0;
        }
    }

    [Serializable]
    public sealed class KanbanCommentReadState
    {
        [SerializeField] private string m_ReaderName;
        [SerializeField] private string m_ReaderId;
        [SerializeField] private string m_ReaderKind;
        [SerializeField] private string m_ReadAtUtc;

        public string ReaderName
        {
            get => this.m_ReaderName;
            set => this.m_ReaderName = value;
        }

        public string ReaderId
        {
            get => this.m_ReaderId;
            set => this.m_ReaderId = value;
        }

        public string ReaderKind
        {
            get => this.m_ReaderKind;
            set => this.m_ReaderKind = value;
        }

        public string ReadAtUtc
        {
            get => this.m_ReadAtUtc;
            set => this.m_ReadAtUtc = value;
        }

        public KanbanCommentReadState(string readerName, string readerId, string readerKind, string readAtUtc)
        {
            this.m_ReaderName = readerName;
            this.m_ReaderId = readerId;
            this.m_ReaderKind = readerKind;
            this.m_ReadAtUtc = readAtUtc;
        }

        public void EnsureInitialized()
        {
            this.m_ReaderName ??= string.Empty;
            this.m_ReaderId ??= string.Empty;
            this.m_ReaderKind ??= string.Empty;
            this.m_ReadAtUtc ??= string.Empty;
        }
    }

    [Serializable]
    public abstract class KanbanLabelDefinition
    {
        [SerializeField, HideInInspector] private string m_Id;
        [SerializeField] private string m_Name;
        [SerializeField] private Color m_Color;
        [SerializeField, TextArea(2, 6)] private string m_RuleText;

        public string Id => this.m_Id;

        public string Name
        {
            get => this.m_Name;
            set => this.m_Name = value;
        }

        public Color Color
        {
            get => this.m_Color;
            set => this.m_Color = value;
        }

        public string RuleText
        {
            get => this.m_RuleText;
            set => this.m_RuleText = value ?? string.Empty;
        }

        public void EnsureInitialized(int index, string defaultPrefix, bool useWarmPalette)
        {
            if (string.IsNullOrWhiteSpace(this.m_Id))
            {
                this.m_Id = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(this.m_Name))
            {
                this.m_Name = defaultPrefix + " " + (index + 1);
            }
            else
            {
                this.m_Name = this.m_Name.Trim();
            }

            this.m_RuleText ??= string.Empty;

            if (this.m_Color.a <= 0f)
            {
                this.m_Color = BuildDefaultColor(index, useWarmPalette);
            }
        }

        public void ResetId()
        {
            this.m_Id = string.Empty;
        }

        private static Color BuildDefaultColor(int index, bool useWarmPalette)
        {
            float hue = Mathf.Repeat((index * 0.137f) + (useWarmPalette ? 0.08f : 0.52f), 1f);
            float saturation = useWarmPalette ? 0.68f : 0.58f;
            float value = useWarmPalette ? 0.92f : 0.9f;
            return Color.HSVToRGB(hue, saturation, value);
        }
    }

    [Serializable]
    public sealed class KanbanCategoryDefinition : KanbanLabelDefinition
    {
    }

    [Serializable]
    public sealed class KanbanTagDefinition : KanbanLabelDefinition
    {
    }

    [Serializable]
    public sealed class KanbanColumnData
    {
        [SerializeField] private string m_Id;
        [SerializeField] private string m_Title;
        [SerializeField] private List<KanbanCardData> m_Cards = new List<KanbanCardData>();

        public string Id => this.m_Id;

        public string Title
        {
            get => this.m_Title;
            set => this.m_Title = value;
        }

        public List<KanbanCardData> Cards => this.m_Cards;

        public void EnsureInitialized()
        {
            this.m_Id ??= Guid.NewGuid().ToString("N");
            this.m_Title ??= string.Empty;
            this.m_Cards ??= new List<KanbanCardData>();

            foreach (KanbanCardData card in this.m_Cards)
            {
                card?.EnsureInitialized();
            }
        }
    }

    [Serializable]
    public sealed class KanbanCardData
    {
        [SerializeField] private string m_Id;
        [SerializeField] private string m_Title;
        [SerializeField] private string m_Description;
        [SerializeField] private List<KanbanCardCommentData> m_Comments = new List<KanbanCardCommentData>();

        public string Id => this.m_Id;
        public string Title => this.m_Title;
        public string Description => this.m_Description;
        public List<KanbanCardCommentData> Comments => this.m_Comments;

        public void EnsureInitialized()
        {
            this.m_Id ??= Guid.NewGuid().ToString("N");
            this.m_Title ??= string.Empty;
            this.m_Description ??= string.Empty;
            this.m_Comments ??= new List<KanbanCardCommentData>();

            foreach (KanbanCardCommentData comment in this.m_Comments)
            {
                comment?.EnsureInitialized();
            }
        }
    }
}
