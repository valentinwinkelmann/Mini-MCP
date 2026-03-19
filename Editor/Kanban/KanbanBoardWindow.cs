using System;
using System.Collections.Generic;
using MiniMCP;
using MiniMCP.Kanban;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MiniMCP.Kanban.Editor
{
    public sealed class KanbanBoardWindow : EditorWindow
    {
        private const string WindowTitle = "Kanban Plan";
        private const string WindowUxmlPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanBoardWindow.uxml";
        private const string ColumnUxmlPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanColumn.uxml";
        private const string CardUxmlPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanCard.uxml";
        private const string BoardUssPath = "Packages/com.vwgamedev.mini-mcp/Editor/Kanban/UI/KanbanBoard.uss";

        [SerializeField] private KanbanPlan m_Plan;
        [SerializeField] private bool m_CardsCollapsed;

        private KanbanBoardController m_Controller;

        private void OnEnable()
        {
            Selection.selectionChanged += this.HandleSelectionChanged;
        }

        [MenuItem("Window/MiniMCP/Kanban Board")]
        public static void OpenWindow()
        {
            GetWindow<KanbanBoardWindow>(WindowTitle).Show();
        }

        public static void Open(KanbanPlan plan)
        {
            KanbanBoardWindow window = GetWindow<KanbanBoardWindow>(WindowTitle);
            window.SetPlan(plan);
            window.Show();
            window.Focus();
        }

        public void CreateGUI()
        {
            this.rootVisualElement.Clear();

            if (this.m_Plan == null && Selection.activeObject is KanbanPlan selectedPlan)
            {
                this.m_Plan = selectedPlan;
            }

            VisualTreeAsset windowTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(WindowUxmlPath);
            VisualTreeAsset columnTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ColumnUxmlPath);
            VisualTreeAsset cardTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(CardUxmlPath);
            StyleSheet boardStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>(BoardUssPath);

            if (windowTree == null || columnTree == null || cardTree == null || boardStyle == null)
            {
                this.rootVisualElement.Add(new HelpBox("MiniMCP Kanban UI assets could not be loaded.", HelpBoxMessageType.Error));
                return;
            }

            windowTree.CloneTree(this.rootVisualElement);
            this.rootVisualElement.styleSheets.Add(boardStyle);

            Label planNameLabel = this.rootVisualElement.Q<Label>("PlanNameLabel");
            Label statusLabel = this.rootVisualElement.Q<Label>("StatusLabel");
            ScrollView boardScrollView = this.rootVisualElement.Q<ScrollView>("BoardScrollView");
            VisualElement dragLayer = this.rootVisualElement.Q<VisualElement>("DragLayer");
            ToolbarToggle unreadToggle = this.rootVisualElement.Q<ToolbarToggle>("UnreadToggle");
            VisualElement unreadBadge = this.rootVisualElement.Q<VisualElement>("UnreadBadge");
            Label unreadCountLabel = this.rootVisualElement.Q<Label>("UnreadCount");
            ToolbarToggle collapseToggle = this.rootVisualElement.Q<ToolbarToggle>("ToolbarCollapseToggle");
            ToolbarSearchField searchField = this.rootVisualElement.Q<ToolbarSearchField>("ToolbarSearchField");

            if (planNameLabel == null
                || boardScrollView == null
                || dragLayer == null
                || unreadToggle == null
                || unreadBadge == null
                || unreadCountLabel == null
                || collapseToggle == null
                || searchField == null)
            {
                this.rootVisualElement.Clear();
                this.rootVisualElement.Add(new HelpBox("MiniMCP Kanban UI is missing required elements.", HelpBoxMessageType.Error));
                return;
            }

            boardScrollView.contentContainer.AddToClassList("mini-mcp-kanban-columns");
            dragLayer.pickingMode = PickingMode.Ignore;
            dragLayer.style.display = DisplayStyle.None;

            this.m_Controller?.Dispose();
            this.m_Controller = new KanbanBoardController(
                this,
                planNameLabel,
                statusLabel,
                boardScrollView,
                unreadToggle,
                unreadBadge,
                unreadCountLabel,
                collapseToggle,
                searchField,
                dragLayer,
                columnTree,
                cardTree,
                this.m_CardsCollapsed,
                collapsed => this.m_CardsCollapsed = collapsed
            );

            this.m_Controller.BindPlan(this.m_Plan);
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= this.HandleSelectionChanged;
            this.m_Controller?.Dispose();
            this.m_Controller = null;
        }

        private void SetPlan(KanbanPlan plan)
        {
            this.m_Plan = plan;
            this.titleContent = new GUIContent(plan != null ? $"{WindowTitle}: {plan.name}" : WindowTitle);

            if (this.m_Controller != null)
            {
                this.m_Controller.BindPlan(plan);
            }
        }

        private void HandleSelectionChanged()
        {
            if (Selection.activeObject is KanbanPlan selectedPlan)
            {
                this.SetPlan(selectedPlan);
                return;
            }

            this.m_Controller?.Render(preserveScrollOffset: true);
        }

        internal void RefreshBoard()
        {
            this.m_Controller?.Render(preserveScrollOffset: true);
        }

        public static void RefreshOpenBoards()
        {
            foreach (KanbanBoardWindow window in Resources.FindObjectsOfTypeAll<KanbanBoardWindow>())
            {
                window.RefreshBoard();
            }
        }

        private sealed class KanbanBoardController : IDisposable
        {
            private const int DescriptionPreviewCharacterLimit = 300;
            private readonly EditorWindow m_Window;
            private readonly Label m_PlanNameLabel;
            private readonly Label m_StatusLabel;
            private readonly ScrollView m_BoardScrollView;
            private readonly ToolbarToggle m_UnreadToggle;
            private readonly VisualElement m_UnreadBadge;
            private readonly Label m_UnreadCountLabel;
            private readonly ToolbarToggle m_CollapseToggle;
            private readonly ToolbarSearchField m_SearchField;
            private readonly VisualElement m_DragLayer;
            private readonly VisualTreeAsset m_ColumnTree;
            private readonly VisualTreeAsset m_CardTree;
            private readonly Action<bool> m_CollapseStateChanged;
            private readonly List<ColumnDropTarget> m_DropTargets = new List<ColumnDropTarget>();
            private readonly Dictionary<KanbanCardStatus, Vector2> m_ColumnScrollOffsets = new Dictionary<KanbanCardStatus, Vector2>();
            private readonly Dictionary<KanbanCardStatus, ScrollView> m_ColumnScrollViews = new Dictionary<KanbanCardStatus, ScrollView>();
            private readonly Dictionary<string, VisualElement> m_CardElementsById = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
            private readonly Dictionary<string, KanbanCardStatus> m_CardStatusesById = new Dictionary<string, KanbanCardStatus>(StringComparer.Ordinal);

            private KanbanPlan m_Plan;
            private DragState m_DragState;
            private bool m_CardsCollapsed;
            private bool m_ShowUnreadOnly;
            private string m_SearchText = string.Empty;
            private KanbanUserContext.Identity m_CurrentIdentity;

            public KanbanBoardController(
                EditorWindow window,
                Label planNameLabel,
                Label statusLabel,
                ScrollView boardScrollView,
                ToolbarToggle unreadToggle,
                VisualElement unreadBadge,
                Label unreadCountLabel,
                ToolbarToggle collapseToggle,
                ToolbarSearchField searchField,
                VisualElement dragLayer,
                VisualTreeAsset columnTree,
                VisualTreeAsset cardTree,
                bool cardsCollapsed,
                Action<bool> collapseStateChanged)
            {
                this.m_Window = window;
                this.m_PlanNameLabel = planNameLabel;
                this.m_StatusLabel = statusLabel;
                this.m_BoardScrollView = boardScrollView;
                this.m_UnreadToggle = unreadToggle;
                this.m_UnreadBadge = unreadBadge;
                this.m_UnreadCountLabel = unreadCountLabel;
                this.m_CollapseToggle = collapseToggle;
                this.m_SearchField = searchField;
                this.m_DragLayer = dragLayer;
                this.m_ColumnTree = columnTree;
                this.m_CardTree = cardTree;
                this.m_CardsCollapsed = cardsCollapsed;
                this.m_CollapseStateChanged = collapseStateChanged;
                this.m_ShowUnreadOnly = this.m_UnreadToggle.value;
                this.m_UnreadToggle.RegisterValueChangedCallback(this.HandleUnreadToggleChanged);
                this.m_CollapseToggle.SetValueWithoutNotify(this.m_CardsCollapsed);
                this.m_CollapseToggle.RegisterValueChangedCallback(this.HandleCollapseToggleChanged);
                this.m_SearchField.RegisterValueChangedCallback(this.HandleSearchChanged);
            }

            public void BindPlan(KanbanPlan plan)
            {
                this.CancelDrag();
                this.m_CurrentIdentity = KanbanUserContext.GetCurrentEditorIdentity();

                this.m_Plan = plan;

                if (this.m_Plan != null)
                {
                    Undo.RecordObject(this.m_Plan, "Initialize Kanban Plan");
                    this.m_Plan.EnsureInitialized();
                    EditorUtility.SetDirty(this.m_Plan);
                }

                this.Render();
            }

            public void Dispose()
            {
                this.CancelDrag();
                this.m_UnreadToggle?.UnregisterValueChangedCallback(this.HandleUnreadToggleChanged);
                this.m_CollapseToggle?.UnregisterValueChangedCallback(this.HandleCollapseToggleChanged);
                this.m_SearchField?.UnregisterValueChangedCallback(this.HandleSearchChanged);
            }

            public void BeginCardDrag(VisualElement cardElement, KanbanCardStatus sourceStatus, string cardId, Vector2 pointerPosition)
            {
                if (this.m_Plan == null || this.m_DragState != null)
                {
                    return;
                }

                KanbanCardAsset card = this.FindCard(cardId);
                if (card == null)
                {
                    return;
                }

                if (card.IsLocked)
                {
                    this.SetStatusMessage("Locked cards are read-only. Add comments in the Inspector if needed.");
                    return;
                }

                Vector2 cardPosition = cardElement.worldBound.position;
                Vector2 pointerOffset = pointerPosition - cardPosition;

                TemplateContainer proxy = this.m_CardTree.CloneTree();
                VisualElement proxyRoot = proxy.Q<VisualElement>("CardRoot") ?? proxy;
                Label titleLabel = proxyRoot.Q<Label>("CardTitleLabel");
                Label categoryLabel = proxyRoot.Q<Label>("CardCategoryLabel");
                Label descriptionLabel = proxyRoot.Q<Label>("CardDescriptionLabel");
                VisualElement badgeContainer = proxyRoot.Q<VisualElement>("CardBadgeContainer");
                VisualElement bodyContainer = proxyRoot.Q<VisualElement>("CardBodyContainer");
                VisualElement commentsFooter = proxyRoot.Q<VisualElement>("Comments");
                VisualElement unreadBadge = proxyRoot.Q<VisualElement>("UnreadBadge");
                Label commentCountLabel = proxyRoot.Q<Label>("CommentCountLabel");
                VisualElement createdByContainer = proxyRoot.Q<VisualElement>("CreatedBy");
                Label createdByInitialsLabel = proxyRoot.Q<Label>("CreatedByInitialsLabel");
                Label createdByNameLabel = proxyRoot.Q<Label>("CreatedByNameLabel");
                Button lockButton = proxyRoot.Q<Button>("LockCardButton");
                Button removeButton = proxyRoot.Q<Button>("RemoveCardButton");

                this.ApplyCardContent(proxyRoot, bodyContainer, badgeContainer, commentsFooter, unreadBadge, commentCountLabel, createdByContainer, createdByInitialsLabel, createdByNameLabel, titleLabel, categoryLabel, descriptionLabel, card);

                if (removeButton != null)
                {
                    removeButton.style.display = DisplayStyle.None;
                }

                if (lockButton != null)
                {
                    lockButton.style.display = card.IsLocked ? DisplayStyle.Flex : DisplayStyle.None;
                }

                if (card.IsLocked)
                {
                    proxyRoot.AddToClassList("locked");
                }
                else
                {
                    proxyRoot.RemoveFromClassList("locked");
                }

                proxyRoot.AddToClassList("mini-mcp-kanban-card--drag-proxy");
                proxyRoot.pickingMode = PickingMode.Ignore;
                proxyRoot.style.position = Position.Absolute;
                proxyRoot.style.width = cardElement.worldBound.width;

                cardElement.AddToClassList("mini-mcp-kanban-card--drag-source");
                cardElement.pickingMode = PickingMode.Ignore;

                this.m_DragLayer.style.display = DisplayStyle.Flex;
                this.m_DragLayer.Add(proxyRoot);

                this.m_DragState = new DragState(sourceStatus, cardId, cardElement, proxyRoot, pointerOffset);
                this.UpdateCardDrag(pointerPosition);
            }

            public void SetCardsCollapsed(bool collapsed)
            {
                this.m_CardsCollapsed = collapsed;
                this.UpdateFoldButtonText();
                this.Render();
            }

            public void UpdateCardDrag(Vector2 pointerPosition)
            {
                if (this.m_DragState == null)
                {
                    return;
                }

                Vector2 localPointer = this.m_DragLayer.WorldToLocal(pointerPosition);
                Vector2 position = localPointer - this.m_DragState.PointerOffset;

                this.m_DragState.ProxyElement.style.left = position.x;
                this.m_DragState.ProxyElement.style.top = position.y;

                this.UpdateHoveredColumn(pointerPosition);
            }

            public void EndCardDrag(Vector2 pointerPosition)
            {
                if (this.m_DragState == null)
                {
                    return;
                }

                this.UpdateHoveredColumn(pointerPosition);

                if (this.m_DragState.HoverStatus.HasValue)
                {
                    Undo.RecordObject(this.m_Plan, "Move Kanban Card");

                    if (this.m_Plan.MoveCard(
                        this.m_DragState.CardId,
                        this.m_DragState.HoverStatus.Value,
                        this.m_DragState.BeforeCardId))
                    {
                        EditorUtility.SetDirty(this.m_Plan);
                    }
                }

                this.CancelDrag();
                this.Render();
            }

            internal void Render(bool preserveScrollOffset = false)
            {
                Vector2 currentScrollOffset = preserveScrollOffset ? this.m_BoardScrollView.scrollOffset : Vector2.zero;
                string selectedCardId = this.GetSelectedCardId();

                if (preserveScrollOffset)
                {
                    this.CaptureColumnScrollOffsets();
                }

                this.m_BoardScrollView.contentContainer.Clear();
                this.m_DropTargets.Clear();
                this.m_ColumnScrollViews.Clear();
                this.m_CardElementsById.Clear();
                this.m_CardStatusesById.Clear();
                this.ClearHoveredColumn();

                if (this.m_Plan == null)
                {
                    this.m_Window.titleContent = new GUIContent(WindowTitle);
                    this.m_PlanNameLabel.text = "Kanban Board";
                    this.UpdateUnreadToolbarState();
                    this.UpdateFoldButtonText();
                    this.RenderPlanSelectionState();
                    return;
                }

                this.m_Plan.EnsureInitialized();
                this.m_CurrentIdentity = KanbanUserContext.GetCurrentEditorIdentity();
                this.m_Window.titleContent = new GUIContent($"{WindowTitle}: {this.m_Plan.name}");
                this.m_PlanNameLabel.text = this.m_Plan.name;
                this.ClearStatusMessage();
                this.UpdateUnreadToolbarState();
                this.UpdateFoldButtonText();

                foreach (KanbanCardStatus status in KanbanPlan.ColumnOrder)
                {
                    VisualElement columnView = this.CreateColumnView(status);
                    if (columnView != null)
                    {
                        this.m_BoardScrollView.contentContainer.Add(columnView);
                    }
                }

                if (preserveScrollOffset)
                {
                    this.m_BoardScrollView.schedule.Execute(() =>
                    {
                        this.m_BoardScrollView.scrollOffset = currentScrollOffset;
                        this.RestoreColumnScrollOffsets();

                        if (!string.IsNullOrWhiteSpace(selectedCardId))
                        {
                            this.ScrollSelectedCardIntoView(selectedCardId);
                        }
                    }).ExecuteLater(0);
                }
                else if (!string.IsNullOrWhiteSpace(selectedCardId))
                {
                    this.m_BoardScrollView.schedule.Execute(() => this.ScrollSelectedCardIntoView(selectedCardId)).ExecuteLater(0);
                }
            }

            private VisualElement CreateColumnView(KanbanCardStatus status)
            {
                List<KanbanCardAsset> visibleCards = this.GetVisibleCards(status);
                TemplateContainer container = this.m_ColumnTree.CloneTree();
                VisualElement columnRoot = container.Q<VisualElement>("ColumnRoot") ?? container;
                Label titleLabel = columnRoot.Q<Label>("ColumnTitleLabel");
                Label countLabel = columnRoot.Q<Label>("CardCountLabel");
                Button addCardButton = columnRoot.Q<Button>("AddCardButton");
                Button removeColumnButton = columnRoot.Q<Button>("RemoveColumnButton");
                ScrollView cardsScrollView = columnRoot.Q<ScrollView>("CardsScrollView");

                ColumnDropTarget dropTarget = new ColumnDropTarget(status, columnRoot);
                columnRoot.userData = dropTarget;
                this.m_DropTargets.Add(dropTarget);

                if (cardsScrollView != null)
                {
                    cardsScrollView.userData = dropTarget;
                    cardsScrollView.contentContainer.userData = dropTarget;
                    cardsScrollView.contentContainer.AddToClassList("mini-mcp-kanban-cards");
                    dropTarget.SetCardsContainer(cardsScrollView.contentContainer);
                    this.m_ColumnScrollViews[status] = cardsScrollView;
                    cardsScrollView.RegisterCallback<GeometryChangedEvent>(_ => this.StoreColumnScrollOffset(status, cardsScrollView.scrollOffset));
                }

                if (titleLabel != null)
                {
                    titleLabel.text = KanbanPlan.GetColumnTitle(status);
                }

                if (countLabel != null)
                {
                    int cardCount = visibleCards.Count;
                    countLabel.text = cardCount == 1 ? "1 card" : $"{cardCount} cards";
                }

                if (addCardButton != null)
                {
                    addCardButton.clicked += () =>
                    {
                        Undo.RecordObject(this.m_Plan, "Add Kanban Card");
                        KanbanUserContext.Identity identity = KanbanUserContext.GetCurrentEditorIdentity();
                        KanbanCardAsset card = this.m_Plan.AddCard(status, "New Task", string.Empty, identity.DisplayName, identity.Id, identity.Kind);
                        EditorUtility.SetDirty(this.m_Plan);

                        if (card != null)
                        {
                            this.SelectCardAsset(card);
                        }

                        this.Render();
                    };
                }

                if (removeColumnButton != null)
                {
                    removeColumnButton.style.display = DisplayStyle.None;
                }

                if (cardsScrollView != null)
                {
                    foreach (KanbanCardAsset card in visibleCards)
                    {
                        VisualElement cardView = this.CreateCardView(status, card);
                        cardsScrollView.contentContainer.Add(cardView);
                        dropTarget.RegisterCard(card.Id, cardView);
                        this.m_CardElementsById[card.Id] = cardView;
                        this.m_CardStatusesById[card.Id] = status;
                    }
                }

                return columnRoot;
            }

            private List<KanbanCardAsset> GetVisibleCards(KanbanCardStatus status)
            {
                List<KanbanCardAsset> cards = new List<KanbanCardAsset>();
                foreach (KanbanCardAsset card in this.m_Plan.GetCards(status))
                {
                    if (this.CardMatchesCurrentFilters(card))
                    {
                        cards.Add(card);
                    }
                }

                return cards;
            }

            private VisualElement CreateCardView(KanbanCardStatus status, KanbanCardAsset card)
            {
                TemplateContainer container = this.m_CardTree.CloneTree();
                VisualElement cardRoot = container.Q<VisualElement>("CardRoot") ?? container;
                VisualElement dragHandle = cardRoot.Q<VisualElement>("CardDragHandle") ?? cardRoot;
                Label titleLabel = cardRoot.Q<Label>("CardTitleLabel");
                Label categoryLabel = cardRoot.Q<Label>("CardCategoryLabel");
                Label descriptionLabel = cardRoot.Q<Label>("CardDescriptionLabel");
                VisualElement badgeContainer = cardRoot.Q<VisualElement>("CardBadgeContainer");
                VisualElement bodyContainer = cardRoot.Q<VisualElement>("CardBodyContainer");
                VisualElement commentsFooter = cardRoot.Q<VisualElement>("Comments");
                VisualElement unreadBadge = cardRoot.Q<VisualElement>("UnreadBadge");
                Label commentCountLabel = cardRoot.Q<Label>("CommentCountLabel");
                VisualElement createdByContainer = cardRoot.Q<VisualElement>("CreatedBy");
                Label createdByInitialsLabel = cardRoot.Q<Label>("CreatedByInitialsLabel");
                Label createdByNameLabel = cardRoot.Q<Label>("CreatedByNameLabel");
                Button lockCardButton = cardRoot.Q<Button>("LockCardButton");
                Button removeCardButton = cardRoot.Q<Button>("RemoveCardButton");

                this.ApplyCardContent(cardRoot, bodyContainer, badgeContainer, commentsFooter, unreadBadge, commentCountLabel, createdByContainer, createdByInitialsLabel, createdByNameLabel, titleLabel, categoryLabel, descriptionLabel, card);
                cardRoot.EnableInClassList("locked", card.IsLocked);

                if (lockCardButton != null)
                {
                    lockCardButton.style.display = card.IsLocked ? DisplayStyle.Flex : DisplayStyle.None;
                    lockCardButton.clicked += () => this.ToggleCardLock(card, false);
                }

                if (removeCardButton != null)
                {
                    removeCardButton.style.display = DisplayStyle.Flex;
                    removeCardButton.clicked += () => this.ConfirmDeleteCard(card);
                }

                cardRoot.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != (int)MouseButton.LeftMouse)
                    {
                        return;
                    }

                    if (IsWithinElement(evt.target as VisualElement, removeCardButton))
                    {
                        return;
                    }

                    this.SelectCardAsset(card);
                    evt.StopPropagation();
                });

                cardRoot.AddManipulator(new ContextualMenuManipulator(evt => this.BuildCardContextMenu(evt, card)));

                if (Selection.activeObject == card)
                {
                    cardRoot.AddToClassList("mini-mcp-kanban-card--selected");
                }

                if (!card.IsLocked)
                {
                    dragHandle.AddManipulator(new KanbanCardDragManipulator(this, cardRoot, status, card.Id));
                }

                return cardRoot;
            }

            private void BuildCardContextMenu(ContextualMenuPopulateEvent evt, KanbanCardAsset card)
            {
                evt.menu.AppendAction(
                    "Delete",
                    _ => this.DeleteCard(card),
                    _ => card.IsLocked ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);

                evt.menu.AppendAction(
                    "Duplicate",
                    _ => this.DuplicateCard(card),
                    _ => DropdownMenuAction.Status.Normal);

                evt.menu.AppendSeparator();
                evt.menu.AppendAction(
                    card.IsLocked ? "Unlock" : "Lock",
                    _ => this.ToggleCardLock(card, !card.IsLocked),
                    _ => DropdownMenuAction.Status.Normal);
            }

            private void DeleteCard(KanbanCardAsset card)
            {
                if (this.m_Plan == null || card == null)
                {
                    return;
                }

                Undo.RecordObject(this.m_Plan, "Remove Kanban Card");
                if (!this.m_Plan.RemoveCard(card.Id))
                {
                    return;
                }

                if (Selection.activeObject == card)
                {
                    Selection.activeObject = this.m_Plan;
                }

                EditorUtility.SetDirty(this.m_Plan);
                AssetDatabase.SaveAssets();
                this.Render();
            }

            private void ConfirmDeleteCard(KanbanCardAsset card)
            {
                if (card == null || card.IsLocked)
                {
                    return;
                }

                string title = string.IsNullOrWhiteSpace(card.Title) ? "this card" : card.Title.Trim();
                bool shouldDelete = EditorUtility.DisplayDialog(
                    "Delete Kanban Card",
                    "Delete '" + title + "'? This cannot be undone from the board view.",
                    "Delete",
                    "Cancel");

                if (!shouldDelete)
                {
                    return;
                }

                this.DeleteCard(card);
            }

            private void DuplicateCard(KanbanCardAsset card)
            {
                if (this.m_Plan == null || card == null)
                {
                    return;
                }

                Undo.RecordObject(this.m_Plan, "Duplicate Kanban Card");
                KanbanCardAsset duplicate = this.m_Plan.DuplicateCard(card.Id);
                if (duplicate == null)
                {
                    return;
                }

                EditorUtility.SetDirty(this.m_Plan);
                AssetDatabase.SaveAssets();
                this.SelectCardAsset(duplicate);
                this.Render();
            }

            private void ToggleCardLock(KanbanCardAsset card, bool isLocked)
            {
                if (this.m_Plan == null || card == null)
                {
                    return;
                }

                Undo.RecordObject(this.m_Plan, isLocked ? "Lock Kanban Card" : "Unlock Kanban Card");
                if (!this.m_Plan.SetCardLocked(card.Id, isLocked))
                {
                    return;
                }

                EditorUtility.SetDirty(this.m_Plan);
                AssetDatabase.SaveAssets();
                this.SelectCardAsset(card);
                this.Render();
            }

            private void HandleCollapseToggleChanged(ChangeEvent<bool> evt)
            {
                this.m_CardsCollapsed = evt.newValue;
                this.m_CollapseStateChanged?.Invoke(this.m_CardsCollapsed);
                this.UpdateFoldButtonText();
                this.Render();
            }

            private void HandleSearchChanged(ChangeEvent<string> evt)
            {
                string nextValue = (evt.newValue ?? string.Empty).Trim();
                if (string.Equals(this.m_SearchText, nextValue, StringComparison.Ordinal))
                {
                    return;
                }

                this.m_SearchText = nextValue;
                this.Render(preserveScrollOffset: true);
            }

            private void HandleUnreadToggleChanged(ChangeEvent<bool> evt)
            {
                this.m_ShowUnreadOnly = evt.newValue;
                this.UpdateUnreadToolbarState();
                this.Render(preserveScrollOffset: true);
            }

            private void UpdateFoldButtonText()
            {
                if (this.m_CollapseToggle != null)
                {
                    this.m_CollapseToggle.SetValueWithoutNotify(this.m_CardsCollapsed);
                }
            }

            private bool IsSearchActive => !string.IsNullOrWhiteSpace(this.m_SearchText);

            private bool IsUnreadFilterActive => this.m_ShowUnreadOnly;

            private bool HasActiveCardFilter => this.IsSearchActive || this.IsUnreadFilterActive;

            private void ApplyCardContent(
                VisualElement cardRoot,
                VisualElement bodyContainer,
                VisualElement badgeContainer,
                VisualElement commentsFooter,
                VisualElement unreadBadge,
                Label commentCountLabel,
                VisualElement createdByContainer,
                Label createdByInitialsLabel,
                Label createdByNameLabel,
                Label titleLabel,
                Label categoryLabel,
                Label descriptionLabel,
                KanbanCardAsset card)
            {
                KanbanCategoryDefinition category = this.m_Plan != null ? this.m_Plan.GetCategoryById(card.CategoryId) : null;
                VisualElement colorBar = cardRoot.Q<VisualElement>("Colorbar");

                if (titleLabel != null)
                {
                    titleLabel.text = card.Title;
                }

                if (categoryLabel != null)
                {
                    if (category != null)
                    {
                        categoryLabel.text = category.Name;
                        categoryLabel.style.display = DisplayStyle.Flex;
                        ApplyLabelTint(categoryLabel, category.Color, 0.28f);
                    }
                    else
                    {
                        categoryLabel.style.display = DisplayStyle.None;
                        categoryLabel.text = string.Empty;
                    }
                }

                if (colorBar != null)
                {
                    if (category != null)
                    {
                        colorBar.style.display = DisplayStyle.Flex;
                        colorBar.style.backgroundColor = new StyleColor(category.Color);
                    }
                    else
                    {
                        colorBar.style.display = DisplayStyle.None;
                    }
                }

                if (descriptionLabel != null)
                {
                    descriptionLabel.text = BuildDescriptionPreview(card.Description);
                }

                if (badgeContainer != null)
                {
                    this.PopulateCardBadges(badgeContainer, card);
                }

                this.ApplyCommentFooter(commentsFooter, unreadBadge, commentCountLabel, card);
                this.ApplyCreatedByFooter(createdByContainer, createdByInitialsLabel, createdByNameLabel, card);

                if (bodyContainer != null)
                {
                    bool keepExpandedForFilter = this.HasActiveCardFilter && this.CardMatchesCurrentFilters(card);
                    bool showBody = !this.m_CardsCollapsed || keepExpandedForFilter;
                    bodyContainer.style.display = showBody ? DisplayStyle.Flex : DisplayStyle.None;
                }

                cardRoot.EnableInClassList("mini-mcp-kanban-card--collapsed", bodyContainer != null && bodyContainer.style.display == DisplayStyle.None);
            }

            private bool CardMatchesCurrentFilters(KanbanCardAsset card)
            {
                return this.CardMatchesSearch(card) && this.CardMatchesUnreadFilter(card);
            }

            private bool CardMatchesSearch(KanbanCardAsset card)
            {
                if (!this.IsSearchActive || card == null)
                {
                    return true;
                }

                string needle = this.m_SearchText.Trim();
                if (needle.Length == 0)
                {
                    return true;
                }

                KanbanCategoryDefinition category = this.m_Plan != null ? this.m_Plan.GetCategoryById(card.CategoryId) : null;
                if (ContainsIgnoreCase(card.Title, needle)
                    || ContainsIgnoreCase(card.Description, needle)
                    || ContainsIgnoreCase(category != null ? category.Name : string.Empty, needle))
                {
                    return true;
                }

                foreach (string tagId in card.TagIds)
                {
                    KanbanTagDefinition tag = this.m_Plan != null ? this.m_Plan.GetTagById(tagId) : null;
                    if (tag != null && ContainsIgnoreCase(tag.Name, needle))
                    {
                        return true;
                    }
                }

                foreach (KanbanCardCommentData comment in card.Comments)
                {
                    if (ContainsIgnoreCase(comment.Text, needle) || ContainsIgnoreCase(comment.AuthorName, needle))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool CardMatchesUnreadFilter(KanbanCardAsset card)
            {
                if (!this.IsUnreadFilterActive || card == null)
                {
                    return true;
                }

                return this.GetUnreadCommentCount(card) > 0;
            }

            private void PopulateCardBadges(VisualElement badgeContainer, KanbanCardAsset card)
            {
                badgeContainer.Clear();

                if (this.m_Plan == null || card == null)
                {
                    return;
                }

                foreach (string tagId in card.TagIds)
                {
                    KanbanTagDefinition tag = this.m_Plan.GetTagById(tagId);
                    if (tag != null)
                    {
                        badgeContainer.Add(CreateBadge("#" + tag.Name, tag.Color));
                    }
                }

                badgeContainer.style.display = badgeContainer.childCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            private KanbanCardAsset FindCard(string cardId)
            {
                return this.m_Plan?.GetCard(cardId);
            }

            private void UpdateHoveredColumn(Vector2 pointerPosition)
            {
                if (this.TryGetDropTarget(pointerPosition, out ColumnDropTarget target))
                {
                    bool changedColumn = this.m_DragState.HoverColumnRoot != target.Root;
                    if (changedColumn)
                    {
                        this.ClearHoveredColumn();
                        this.m_DragState.HoverStatus = target.Status;
                        this.m_DragState.HoverColumnRoot = target.Root;
                        this.m_DragState.HoverColumnRoot.AddToClassList("mini-mcp-kanban-column--drop-target");
                    }

                    target.UpdateInsertionMarker(pointerPosition, this.m_DragState.CardId);
                    this.m_DragState.HoverStatus = target.Status;
                    this.m_DragState.BeforeCardId = target.BeforeCardId;
                    this.SetStatusMessage($"Insert into '{KanbanPlan.GetColumnTitle(target.Status)}'.");
                    return;
                }

                this.ClearHoveredColumn();
                this.ClearStatusMessage();
            }

            private void ClearHoveredColumn()
            {
                if (this.m_DragState?.HoverColumnRoot != null)
                {
                    ColumnDropTarget dropTarget = this.m_DragState.HoverColumnRoot.userData as ColumnDropTarget;
                    dropTarget?.ClearInsertionMarker();
                    this.m_DragState.HoverColumnRoot.RemoveFromClassList("mini-mcp-kanban-column--drop-target");
                    this.m_DragState.HoverColumnRoot = null;
                    this.m_DragState.HoverStatus = null;
                    this.m_DragState.BeforeCardId = null;
                }
            }

            internal void CancelDrag()
            {
                if (this.m_DragState == null)
                {
                    return;
                }

                this.ClearHoveredColumn();

                if (this.m_DragState.SourceCardElement != null)
                {
                    this.m_DragState.SourceCardElement.RemoveFromClassList("mini-mcp-kanban-card--drag-source");
                    this.m_DragState.SourceCardElement.pickingMode = PickingMode.Position;
                }

                if (this.m_DragState.ProxyElement != null)
                {
                    this.m_DragState.ProxyElement.RemoveFromHierarchy();
                }

                this.m_DragLayer.style.display = DisplayStyle.None;
                this.m_DragState = null;
                if (this.m_Plan == null)
                {
                    this.ClearStatusMessage();
                }
                else
                {
                    this.ClearStatusMessage();
                }
            }

            private bool TryGetDropTarget(Vector2 pointerPosition, out ColumnDropTarget dropTarget)
            {
                dropTarget = null;

                Rect viewportBounds = this.m_BoardScrollView.contentViewport != null
                    ? this.m_BoardScrollView.contentViewport.worldBound
                    : this.m_BoardScrollView.worldBound;

                if (viewportBounds.width <= 0f || viewportBounds.height <= 0f)
                {
                    return false;
                }

                foreach (ColumnDropTarget target in this.m_DropTargets)
                {
                    if (target.Contains(pointerPosition, viewportBounds))
                    {
                        dropTarget = target;
                        return true;
                    }
                }

                return false;
            }

            private static string BuildDescriptionPreview(string description)
            {
                string normalized = (description ?? string.Empty)
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Trim();

                if (normalized.Length == 0)
                {
                    return "No description.";
                }

                if (normalized.Length <= DescriptionPreviewCharacterLimit)
                {
                    return normalized;
                }

                return normalized.Substring(0, DescriptionPreviewCharacterLimit).TrimEnd() + "...";
            }

            private void ApplyCommentFooter(VisualElement commentsFooter, VisualElement unreadBadge, Label commentCountLabel, KanbanCardAsset card)
            {
                if (commentsFooter == null || unreadBadge == null || commentCountLabel == null)
                {
                    return;
                }

                int commentCount = card?.Comments?.Count ?? 0;
                int unreadCount = this.GetUnreadCommentCount(card);
                commentCountLabel.text = commentCount.ToString();
                unreadBadge.EnableInClassList("unread", unreadCount > 0);
                unreadBadge.tooltip = unreadCount > 0
                    ? unreadCount + (unreadCount == 1 ? " unread comment" : " unread comments")
                    : "No unread comments";
                commentsFooter.tooltip = commentCount == 0
                    ? "No comments"
                    : commentCount + (commentCount == 1 ? " comment" : " comments") + (unreadCount > 0 ? " | " + unreadCount + " unread" : string.Empty);
            }

            private void ApplyCreatedByFooter(VisualElement createdByContainer, Label createdByInitialsLabel, Label createdByNameLabel, KanbanCardAsset card)
            {
                if (createdByContainer == null || createdByInitialsLabel == null || createdByNameLabel == null)
                {
                    return;
                }

                string displayName = GetCreatedByDisplayName(card);
                createdByInitialsLabel.text = GetInitials(displayName);
                createdByNameLabel.text = displayName;
                createdByContainer.tooltip = displayName;
            }

            private void UpdateUnreadToolbarState()
            {
                if (this.m_UnreadToggle == null || this.m_UnreadBadge == null || this.m_UnreadCountLabel == null)
                {
                    return;
                }

                this.m_UnreadToggle.SetValueWithoutNotify(this.m_ShowUnreadOnly);
                int unreadCardCount = 0;
                int unreadCommentCount = 0;

                if (this.m_Plan != null)
                {
                    this.m_Plan.EnsureInitialized();
                    foreach (KanbanCardAsset card in this.m_Plan.Cards)
                    {
                        int cardUnreadCount = this.GetUnreadCommentCount(card);
                        if (cardUnreadCount <= 0)
                        {
                            continue;
                        }

                        unreadCardCount++;
                        unreadCommentCount += cardUnreadCount;
                    }
                }

                this.m_UnreadCountLabel.text = unreadCardCount.ToString();
                this.m_UnreadBadge.style.display = unreadCardCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                this.m_UnreadToggle.tooltip = unreadCardCount > 0
                    ? unreadCardCount + (unreadCardCount == 1 ? " card has unread comments" : " cards have unread comments") + " | " + unreadCommentCount + (unreadCommentCount == 1 ? " unread comment" : " unread comments")
                    : "No unread comments";
            }

            private int GetUnreadCommentCount(KanbanCardAsset card)
            {
                if (card == null)
                {
                    return 0;
                }

                return card.GetUnreadCommentCount(this.m_CurrentIdentity.DisplayName, this.m_CurrentIdentity.Id, this.m_CurrentIdentity.Kind);
            }

            private static string GetCreatedByDisplayName(KanbanCardAsset card)
            {
                if (card == null)
                {
                    return "Unknown";
                }

                if (!string.IsNullOrWhiteSpace(card.CreatedByName))
                {
                    return card.CreatedByName.Trim();
                }

                if (string.Equals(card.CreatedByKind, "mcp", StringComparison.OrdinalIgnoreCase))
                {
                    return "MCP";
                }

                if (!string.IsNullOrWhiteSpace(card.CreatedById))
                {
                    return card.CreatedById.Trim();
                }

                return "Unknown";
            }

            private static string GetInitials(string displayName)
            {
                string normalized = (displayName ?? string.Empty).Trim();
                if (normalized.Length == 0)
                {
                    return "?";
                }

                string[] parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    return parts[0].Length >= 2 ? parts[0].Substring(0, 2).ToUpperInvariant() : parts[0].ToUpperInvariant();
                }

                return (parts[0][0].ToString() + parts[parts.Length - 1][0].ToString()).ToUpperInvariant();
            }

            private void RenderPlanSelectionState()
            {
                this.ClearStatusMessage();

                VisualElement emptyState = new VisualElement();
                emptyState.AddToClassList("mini-mcp-kanban-board__empty-state");

                VisualElement planList = new VisualElement();
                planList.AddToClassList("mini-mcp-kanban-board__plan-list");

                List<KanbanPlan> plans = FindPlans();
                foreach (KanbanPlan plan in plans)
                {
                    string planPath = AssetDatabase.GetAssetPath(plan);
                    Button button = new Button(() =>
                    {
                        this.m_Window.Focus();
                        Selection.activeObject = plan;
                        EditorGUIUtility.PingObject(plan);
                        ((KanbanBoardWindow)this.m_Window).SetPlan(plan);
                    });
                    button.AddToClassList("mini-mcp-kanban-board__plan-button");

                    VisualElement buttonContent = new VisualElement();
                    buttonContent.AddToClassList("mini-mcp-kanban-board__plan-button-content");

                    Label nameLabel = new Label(plan.name);
                    nameLabel.AddToClassList("mini-mcp-kanban-board__plan-name");
                    buttonContent.Add(nameLabel);

                    Label pathLabel = new Label(planPath ?? string.Empty);
                    pathLabel.AddToClassList("mini-mcp-kanban-board__plan-path");
                    buttonContent.Add(pathLabel);

                    button.Add(buttonContent);
                    planList.Add(button);
                }

                emptyState.Add(planList);

                this.m_BoardScrollView.contentContainer.Add(emptyState);
            }

            private static List<KanbanPlan> FindPlans()
            {
                string[] guids = AssetDatabase.FindAssets("t:KanbanPlan");
                List<KanbanPlan> plans = new List<KanbanPlan>(guids.Length);
                for (int index = 0; index < guids.Length; index++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[index]);
                    KanbanPlan plan = AssetDatabase.LoadAssetAtPath<KanbanPlan>(path);
                    if (plan != null)
                    {
                        plans.Add(plan);
                    }
                }

                plans.Sort((left, right) => string.Compare(left != null ? left.name : string.Empty, right != null ? right.name : string.Empty, StringComparison.OrdinalIgnoreCase));
                return plans;
            }

            private void SetStatusMessage(string message)
            {
                if (this.m_StatusLabel == null)
                {
                    return;
                }

                string normalized = (message ?? string.Empty).Trim();
                this.m_StatusLabel.text = normalized;
                this.m_StatusLabel.style.display = normalized.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            private void ClearStatusMessage()
            {
                this.SetStatusMessage(string.Empty);
            }

            private static bool ContainsIgnoreCase(string text, string value)
            {
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
                {
                    return false;
                }

                return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static VisualElement CreateBadge(string text, Color color)
            {
                Label badge = new Label(text);
                badge.AddToClassList("mini-mcp-kanban-card__badge");
                ApplyLabelTint(badge, color, 0.28f);
                return badge;
            }

            private static void ApplyLabelTint(VisualElement element, Color color, float backgroundAlpha)
            {
                element.style.backgroundColor = new StyleColor(new Color(color.r, color.g, color.b, backgroundAlpha));
                element.style.borderTopColor = new StyleColor(color);
                element.style.borderRightColor = new StyleColor(color);
                element.style.borderBottomColor = new StyleColor(color);
                element.style.borderLeftColor = new StyleColor(color);
                element.style.borderTopWidth = 1f;
                element.style.borderRightWidth = 1f;
                element.style.borderBottomWidth = 1f;
                element.style.borderLeftWidth = 1f;

                float luminance = (color.r * 0.299f) + (color.g * 0.587f) + (color.b * 0.114f);
                element.style.color = new StyleColor(luminance > 0.62f
                    ? new Color(0.11f, 0.12f, 0.13f)
                    : new Color(0.94f, 0.95f, 0.97f));
            }

            private static bool IsWithinElement(VisualElement candidate, VisualElement ancestor)
            {
                if (candidate == null || ancestor == null)
                {
                    return false;
                }

                VisualElement current = candidate;
                while (current != null)
                {
                    if (current == ancestor)
                    {
                        return true;
                    }

                    current = current.parent;
                }

                return false;
            }

            private void SelectCardAsset(KanbanCardAsset card)
            {
                if (card == null)
                {
                    return;
                }

                Selection.activeObject = card;
                EditorGUIUtility.PingObject(card);
            }

            private string GetSelectedCardId()
            {
                if (!(Selection.activeObject is KanbanCardAsset selectedCard))
                {
                    return null;
                }

                if (this.m_Plan == null)
                {
                    return null;
                }

                KanbanCardAsset planCard = this.m_Plan.GetCard(selectedCard.Id);
                return planCard != null ? selectedCard.Id : null;
            }

            private void CaptureColumnScrollOffsets()
            {
                foreach (KeyValuePair<KanbanCardStatus, ScrollView> entry in this.m_ColumnScrollViews)
                {
                    this.StoreColumnScrollOffset(entry.Key, entry.Value.scrollOffset);
                }
            }

            private void RestoreColumnScrollOffsets()
            {
                foreach (KeyValuePair<KanbanCardStatus, ScrollView> entry in this.m_ColumnScrollViews)
                {
                    if (this.m_ColumnScrollOffsets.TryGetValue(entry.Key, out Vector2 scrollOffset))
                    {
                        entry.Value.scrollOffset = scrollOffset;
                    }
                }
            }

            private void StoreColumnScrollOffset(KanbanCardStatus status, Vector2 scrollOffset)
            {
                this.m_ColumnScrollOffsets[status] = scrollOffset;
            }

            private void ScrollSelectedCardIntoView(string cardId)
            {
                if (string.IsNullOrWhiteSpace(cardId))
                {
                    return;
                }

                if (!this.m_CardElementsById.TryGetValue(cardId, out VisualElement cardElement) || cardElement == null)
                {
                    return;
                }

                if (this.m_CardStatusesById.TryGetValue(cardId, out KanbanCardStatus status)
                    && this.m_ColumnScrollViews.TryGetValue(status, out ScrollView columnScrollView)
                    && columnScrollView != null)
                {
                    columnScrollView.ScrollTo(cardElement);
                }

                VisualElement columnRoot = cardElement.GetFirstAncestorOfType<ScrollView>()?.parent;
                if (columnRoot != null)
                {
                    this.m_BoardScrollView.ScrollTo(columnRoot);
                }
            }

            private sealed class ColumnDropTarget
            {
                private readonly List<CardPlacement> m_Cards = new List<CardPlacement>();
                private readonly VisualElement m_InsertionMarker = new VisualElement();
                private VisualElement m_CardsContainer;

                public ColumnDropTarget(KanbanCardStatus status, VisualElement root)
                {
                    this.Status = status;
                    this.Root = root;
                    this.m_InsertionMarker.AddToClassList("mini-mcp-kanban-insert-marker");
                }

                public KanbanCardStatus Status { get; }

                public VisualElement Root { get; }

                public string BeforeCardId { get; private set; }

                public void SetCardsContainer(VisualElement cardsContainer)
                {
                    this.m_CardsContainer = cardsContainer;
                }

                public void RegisterCard(string cardId, VisualElement cardElement)
                {
                    this.m_Cards.Add(new CardPlacement(cardId, cardElement));
                }

                public bool Contains(Vector2 pointerPosition, Rect viewportBounds)
                {
                    Rect columnBounds = this.Root.worldBound;
                    if (columnBounds.width <= 0f)
                    {
                        return false;
                    }

                    Rect dropBounds = new Rect(
                        columnBounds.xMin,
                        viewportBounds.yMin,
                        columnBounds.width,
                        viewportBounds.height);

                    return dropBounds.Contains(pointerPosition);
                }

                public void UpdateInsertionMarker(Vector2 pointerPosition, string draggedCardId)
                {
                    if (this.m_CardsContainer == null)
                    {
                        this.BeforeCardId = null;
                        return;
                    }

                    this.BeforeCardId = null;

                    foreach (CardPlacement card in this.m_Cards)
                    {
                        if (card.CardId == draggedCardId || card.Element == null)
                        {
                            continue;
                        }

                        Rect bounds = card.Element.worldBound;
                        if (pointerPosition.y < bounds.center.y)
                        {
                            this.BeforeCardId = card.CardId;
                            this.ShowInsertionMarkerBefore(card.Element);
                            return;
                        }
                    }

                    this.ShowInsertionMarkerAtEnd();
                }

                public void ClearInsertionMarker()
                {
                    this.BeforeCardId = null;
                    if (this.m_InsertionMarker.parent != null)
                    {
                        this.m_InsertionMarker.RemoveFromHierarchy();
                    }
                }

                private void ShowInsertionMarkerBefore(VisualElement cardElement)
                {
                    if (this.m_CardsContainer == null)
                    {
                        return;
                    }

                    if (this.m_InsertionMarker.parent == this.m_CardsContainer)
                    {
                        this.m_InsertionMarker.RemoveFromHierarchy();
                    }

                    int targetIndex = this.m_CardsContainer.IndexOf(cardElement);
                    if (targetIndex < 0)
                    {
                        this.m_CardsContainer.Add(this.m_InsertionMarker);
                    }
                    else
                    {
                        this.m_CardsContainer.Insert(targetIndex, this.m_InsertionMarker);
                    }
                }

                private void ShowInsertionMarkerAtEnd()
                {
                    if (this.m_CardsContainer == null)
                    {
                        return;
                    }

                    if (this.m_InsertionMarker.parent == this.m_CardsContainer)
                    {
                        this.m_InsertionMarker.RemoveFromHierarchy();
                    }

                    this.m_CardsContainer.Add(this.m_InsertionMarker);
                }

                private readonly struct CardPlacement
                {
                    public CardPlacement(string cardId, VisualElement element)
                    {
                        this.CardId = cardId;
                        this.Element = element;
                    }

                    public string CardId { get; }

                    public VisualElement Element { get; }
                }
            }

            private sealed class DragState
            {
                public DragState(KanbanCardStatus sourceStatus, string cardId, VisualElement sourceCardElement, VisualElement proxyElement, Vector2 pointerOffset)
                {
                    this.SourceStatus = sourceStatus;
                    this.CardId = cardId;
                    this.SourceCardElement = sourceCardElement;
                    this.ProxyElement = proxyElement;
                    this.PointerOffset = pointerOffset;
                }

                public KanbanCardStatus SourceStatus { get; }

                public string CardId { get; }

                public VisualElement SourceCardElement { get; }

                public VisualElement ProxyElement { get; }

                public Vector2 PointerOffset { get; }

                public KanbanCardStatus? HoverStatus { get; set; }

                public string BeforeCardId { get; set; }

                public VisualElement HoverColumnRoot { get; set; }
            }
        }

        private sealed class KanbanCardDragManipulator : PointerManipulator
        {
            private const float DragThreshold = 8f;

            private readonly KanbanBoardController m_Controller;
            private readonly VisualElement m_CardElement;
            private readonly KanbanCardStatus m_SourceStatus;
            private readonly string m_CardId;

            private int m_PointerId;
            private bool m_Active;
            private bool m_IsDragging;
            private Vector2 m_StartPosition;

            public KanbanCardDragManipulator(KanbanBoardController controller, VisualElement cardElement, KanbanCardStatus sourceStatus, string cardId)
            {
                this.m_Controller = controller;
                this.m_CardElement = cardElement;
                this.m_SourceStatus = sourceStatus;
                this.m_CardId = cardId;
                this.m_PointerId = -1;
                this.activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
            }

            protected override void RegisterCallbacksOnTarget()
            {
                this.target.RegisterCallback<PointerDownEvent>(this.OnPointerDown);
                this.target.RegisterCallback<PointerMoveEvent>(this.OnPointerMove);
                this.target.RegisterCallback<PointerUpEvent>(this.OnPointerUp);
                this.target.RegisterCallback<PointerCaptureOutEvent>(this.OnPointerCaptureOut);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                this.target.UnregisterCallback<PointerDownEvent>(this.OnPointerDown);
                this.target.UnregisterCallback<PointerMoveEvent>(this.OnPointerMove);
                this.target.UnregisterCallback<PointerUpEvent>(this.OnPointerUp);
                this.target.UnregisterCallback<PointerCaptureOutEvent>(this.OnPointerCaptureOut);
            }

            private void OnPointerDown(PointerDownEvent evt)
            {
                if (this.m_Active)
                {
                    evt.StopImmediatePropagation();
                    return;
                }

                if (!this.CanStartManipulation(evt))
                {
                    return;
                }

                this.m_Active = true;
                this.m_IsDragging = false;
                this.m_PointerId = evt.pointerId;
                this.m_StartPosition = new Vector2(evt.position.x, evt.position.y);
                this.target.CapturePointer(this.m_PointerId);
                evt.StopPropagation();
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (!this.m_Active || !this.target.HasPointerCapture(this.m_PointerId))
                {
                    return;
                }

                Vector2 pointerPosition = new Vector2(evt.position.x, evt.position.y);

                if (!this.m_IsDragging)
                {
                    if ((pointerPosition - this.m_StartPosition).sqrMagnitude < DragThreshold * DragThreshold)
                    {
                        return;
                    }

                    this.m_IsDragging = true;
                    this.m_Controller.BeginCardDrag(this.m_CardElement, this.m_SourceStatus, this.m_CardId, pointerPosition);
                }

                this.m_Controller.UpdateCardDrag(pointerPosition);
                evt.StopPropagation();
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (!this.m_Active || !this.target.HasPointerCapture(this.m_PointerId) || !this.CanStopManipulation(evt))
                {
                    return;
                }

                if (this.m_IsDragging)
                {
                    this.m_Controller.EndCardDrag(new Vector2(evt.position.x, evt.position.y));
                }

                this.ResetState();
                evt.StopPropagation();
            }

            private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
            {
                if (!this.m_Active)
                {
                    return;
                }

                if (this.m_IsDragging)
                {
                    this.m_Controller.CancelDrag();
                    this.m_Controller.Render();
                }

                this.ResetState();
                evt.StopPropagation();
            }

            private void ResetState()
            {
                if (this.m_PointerId >= 0 && this.target.HasPointerCapture(this.m_PointerId))
                {
                    this.target.ReleasePointer(this.m_PointerId);
                }

                this.m_PointerId = -1;
                this.m_Active = false;
                this.m_IsDragging = false;
            }
        }
    }
}