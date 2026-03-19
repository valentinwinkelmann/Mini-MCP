using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniMCP.Kanban
{
    public sealed class KanbanCardAsset : ScriptableObject
    {
        [SerializeField, HideInInspector] private string m_Id;
        [SerializeField] private string m_Title;
        [SerializeField, TextArea(4, 12)] private string m_Description;
        [SerializeField] private KanbanCardStatus m_Status;
        [SerializeField] private string m_CreatedByName;
        [SerializeField] private string m_CreatedById;
        [SerializeField] private string m_CreatedByKind;
        [SerializeField] private string m_CategoryId;
        [SerializeField] private List<string> m_TagIds = new List<string>();
        [SerializeField] private bool m_IsLocked;
        [SerializeField] private List<KanbanCardCommentData> m_Comments = new List<KanbanCardCommentData>();

        public string Id => this.m_Id;

        public string Title
        {
            get => this.m_Title;
            set => this.m_Title = value;
        }

        public string Description
        {
            get => this.m_Description;
            set => this.m_Description = value;
        }

        public KanbanCardStatus Status
        {
            get => this.m_Status;
            set => this.m_Status = value;
        }

        public string CreatedByName
        {
            get => this.m_CreatedByName;
            set => this.m_CreatedByName = value ?? string.Empty;
        }

        public string CreatedById
        {
            get => this.m_CreatedById;
            set => this.m_CreatedById = value ?? string.Empty;
        }

        public string CreatedByKind
        {
            get => this.m_CreatedByKind;
            set => this.m_CreatedByKind = value ?? string.Empty;
        }

        public string CategoryId
        {
            get => this.m_CategoryId;
            set => this.m_CategoryId = value ?? string.Empty;
        }

        public List<string> TagIds => this.m_TagIds;

        public bool IsLocked
        {
            get => this.m_IsLocked;
            set => this.m_IsLocked = value;
        }

        public List<KanbanCardCommentData> Comments => this.m_Comments;

        public int GetUnreadCommentCount(string readerName, string readerId, string readerKind)
        {
            this.EnsureInitialized();

            int count = 0;
            foreach (KanbanCardCommentData comment in this.m_Comments)
            {
                if (comment == null)
                {
                    continue;
                }

                comment.EnsureInitialized();
                if (comment.IsUnreadFor(readerName, readerId, readerKind))
                {
                    count++;
                }
            }

            return count;
        }

        public int MarkCommentsAsRead(string readerName, string readerId, string readerKind)
        {
            this.EnsureInitialized();

            int markedCount = 0;
            string readAtUtc = DateTime.UtcNow.ToString("o");
            foreach (KanbanCardCommentData comment in this.m_Comments)
            {
                if (comment == null)
                {
                    continue;
                }

                comment.EnsureInitialized();
                if (!comment.IsUnreadFor(readerName, readerId, readerKind))
                {
                    continue;
                }

                if (comment.MarkAsReadBy(readerName, readerId, readerKind, readAtUtc))
                {
                    markedCount++;
                }
            }

            return markedCount;
        }

        public void Initialize(string title, string description, KanbanCardStatus status, bool isLocked = false, string createdByName = "", string createdById = "", string createdByKind = "")
        {
            this.EnsureInitialized();
            this.m_Title = title ?? string.Empty;
            this.m_Description = description ?? string.Empty;
            this.m_Status = status;
            this.m_IsLocked = isLocked;
            this.m_CreatedByName = createdByName ?? string.Empty;
            this.m_CreatedById = createdById ?? string.Empty;
            this.m_CreatedByKind = createdByKind ?? string.Empty;
        }

        public void EnsureInitialized()
        {
            if (string.IsNullOrWhiteSpace(this.m_Id))
            {
                this.m_Id = Guid.NewGuid().ToString("N");
            }

            this.m_Title ??= string.Empty;
            this.m_Description ??= string.Empty;
            this.m_CreatedByName ??= string.Empty;
            this.m_CreatedById ??= string.Empty;
            this.m_CreatedByKind ??= string.Empty;
            this.m_CategoryId ??= string.Empty;

            if (this.m_TagIds == null)
            {
                this.m_TagIds = new List<string>();
            }

            if (this.m_Comments == null)
            {
                this.m_Comments = new List<KanbanCardCommentData>();
            }

            foreach (KanbanCardCommentData comment in this.m_Comments)
            {
                comment?.EnsureInitialized();
            }
        }

        internal void SetId(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                this.m_Id = id;
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
    }
}