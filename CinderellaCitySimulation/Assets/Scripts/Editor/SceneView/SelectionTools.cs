using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

/// <summary>
/// Scene view selection tools — clearing and modifying the active Selection while editing.
/// </summary>
[InitializeOnLoad]
public static class SelectionTools
{
    static SelectionTools()
    {
        SceneView.duringSceneGui += OnDuringSceneGui;
    }

    [Shortcut("Cinderella City Project/Clear Selection", KeyCode.Escape)]
    static void ClearSelectionShortcut(ShortcutArguments args)
    {
        ClearSelection();
    }

    static void OnDuringSceneGui(SceneView view)
    {
        Event evt = Event.current;
        if (evt == null || evt.type != EventType.KeyDown || evt.keyCode != KeyCode.Escape || GUIUtility.hotControl != 0)
            return;

        if (ClearSelection())
            evt.Use();
    }

    public static bool ClearSelection()
    {
        if (Selection.activeObject == null)
            return false;

        Selection.activeObject = null;
        return true;
    }
}
