using MiniMCP;
using MiniMCP.Kanban;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace MiniMCP.Kanban.Editor
{
    [CustomEditor(typeof(KanbanPlan))]
    public sealed class KanbanPlanEditor : UnityEditor.Editor
    {
        [OnOpenAsset]
        public static bool OpenKanbanPlan(int instanceId, int line)
        {
            KanbanPlan plan = EditorUtility.InstanceIDToObject(instanceId) as KanbanPlan;
            if (plan == null)
            {
                return false;
            }

            KanbanBoardWindow.Open(plan);
            return true;
        }

        public override void OnInspectorGUI()
        {
            KanbanPlan plan = this.target as KanbanPlan;
            if (plan == null)
            {
                return;
            }

            serializedObject.Update();

            if (GUILayout.Button("Open Kanban Board", GUILayout.Height(26f)))
            {
                KanbanBoardWindow.Open(plan);
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Board Description", EditorStyles.boldLabel);
            SerializedProperty descriptionProperty = serializedObject.FindProperty("m_Description");
            EditorGUILayout.PropertyField(descriptionProperty, GUIContent.none, GUILayout.MinHeight(120f));

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Categories", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Define one primary category per card. Each category can have its own color, optional MCP rule text and is queryable through MCP.", MessageType.None);
            SerializedProperty categoriesProperty = serializedObject.FindProperty("m_Categories");
            EditorGUILayout.PropertyField(categoriesProperty, includeChildren: true);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Tags", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Tags are multi-select labels for each card. Use them for cross-cutting attributes that should remain separate from categories. Tags can also carry optional MCP rule text.", MessageType.None);
            SerializedProperty tagsProperty = serializedObject.FindProperty("m_Tags");
            EditorGUILayout.PropertyField(tagsProperty, includeChildren: true);

            serializedObject.ApplyModifiedProperties();
        }
    }
}