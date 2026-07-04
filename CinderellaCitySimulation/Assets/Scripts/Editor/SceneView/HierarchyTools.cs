using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Hierarchy parenting tools for the active editor selection.
/// </summary>
public static class HierarchyTools
{
    public static bool HasSelection => Selection.transforms.Length > 0;

    public static bool CanSetParent()
    {
        return HasSelection && TryGetSceneContainerForSelection(out GameObject sceneContainer)
            && ManageSceneObjects.GetAllTopLevelChildrenInObject(sceneContainer).Length > 0;
    }

    public static bool CanReorderActiveSibling()
    {
        return HasSelection && Selection.activeTransform != null && Selection.activeTransform.parent != null;
    }

    public static bool CanMoveUp()
    {
        if (!CanReorderActiveSibling())
            return false;

        return Selection.activeTransform.GetSiblingIndex() > 0;
    }

    public static bool CanMoveDown()
    {
        if (!CanReorderActiveSibling())
            return false;

        Transform activeTransform = Selection.activeTransform;
        return activeTransform.GetSiblingIndex() < activeTransform.parent.childCount - 1;
    }

    public static void ShowSetParentMenu()
    {
        if (!TryGetSceneContainerForSelection(out GameObject sceneContainer))
            return;

        GameObject[] containerChildren = ManageSceneObjects.GetAllTopLevelChildrenInObject(sceneContainer);
        GenericMenu menu = new GenericMenu();

        if (containerChildren == null || containerChildren.Length == 0)
        {
            menu.AddDisabledItem(new GUIContent("(No container children)"));
        }
        else
        {
            Array.Sort(containerChildren, (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            foreach (GameObject targetParent in containerChildren)
            {
                GameObject capturedTarget = targetParent;
                string menuPath = BuildSetParentMenuPath(capturedTarget.name);
                menu.AddItem(new GUIContent(menuPath), false, () => ReparentSelectionUnder(capturedTarget));
            }
        }

        menu.DropDown(GetMenuDropDownRect());
    }

    public static void MoveUp()
    {
        Transform activeTransform = Selection.activeTransform;
        if (activeTransform == null || activeTransform.parent == null)
            return;

        SetActiveSelectionSiblingIndex(activeTransform.GetSiblingIndex() - 1);
    }

    public static void MoveDown()
    {
        Transform activeTransform = Selection.activeTransform;
        if (activeTransform == null || activeTransform.parent == null)
            return;

        SetActiveSelectionSiblingIndex(activeTransform.GetSiblingIndex() + 1);
    }

    public static void SetAsFirstSibling()
    {
        SetActiveSelectionSiblingIndex(0);
    }

    public static void SetAsLastSibling()
    {
        Transform activeTransform = Selection.activeTransform;
        if (activeTransform == null || activeTransform.parent == null)
            return;

        SetActiveSelectionSiblingIndex(activeTransform.parent.childCount - 1);
    }

    static Rect GetMenuDropDownRect()
    {
        Vector2 mousePosition = Event.current != null ? Event.current.mousePosition : Vector2.zero;
        return new Rect(GUIUtility.GUIToScreenPoint(mousePosition), Vector2.zero);
    }

    static string BuildSetParentMenuPath(string objectName)
    {
        string prefix = GetNamePrefix(objectName);
        return SanitizeMenuSegment(prefix) + "/" + SanitizeMenuSegment(objectName);
    }

    static string GetNamePrefix(string objectName)
    {
        int dashIndex = objectName.IndexOf('-');
        if (dashIndex <= 0)
            return "other";

        return objectName.Substring(0, dashIndex);
    }

    static string SanitizeMenuSegment(string segment)
    {
        return segment.Replace("/", "-");
    }

    static void SetActiveSelectionSiblingIndex(int siblingIndex)
    {
        Transform activeTransform = Selection.activeTransform;
        if (activeTransform == null || activeTransform.parent == null)
            return;

        Undo.RecordObject(activeTransform, "Set Sibling Index");
        activeTransform.SetSiblingIndex(siblingIndex);
        MarkSelectionScenesDirty();
    }

    static bool TryGetSceneContainerForSelection(out GameObject sceneContainer)
    {
        sceneContainer = null;
        if (Selection.activeGameObject == null)
            return false;

        Scene scene = Selection.activeGameObject.scene;
        if (!scene.IsValid())
            scene = SceneManager.GetActiveScene();

        sceneContainer = ManageSceneObjects.GetSceneContainerObject(scene);
        return sceneContainer != null;
    }

    static void ReparentSelectionUnder(GameObject newParent)
    {
        if (newParent == null)
            return;

        Transform[] selectedTransforms = Selection.transforms;
        if (selectedTransforms.Length == 0)
            return;

        Undo.SetCurrentGroupName("Set Parent");
        int undoGroup = Undo.GetCurrentGroup();
        bool anyReparented = false;

        foreach (Transform transform in selectedTransforms)
        {
            if (transform == null || WouldCreateCycle(transform, newParent.transform))
                continue;

            Undo.SetTransformParent(transform, newParent.transform, "Set Parent");
            anyReparented = true;
        }

        if (!anyReparented)
            return;

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(newParent.scene);
    }

    static bool WouldCreateCycle(Transform child, Transform proposedParent)
    {
        if (child == proposedParent)
            return true;

        Transform ancestor = proposedParent;
        while (ancestor != null)
        {
            if (ancestor == child)
                return true;
            ancestor = ancestor.parent;
        }

        return false;
    }

    static void MarkSelectionScenesDirty()
    {
        if (Selection.activeGameObject == null)
            return;

        Scene scene = Selection.activeGameObject.scene;
        if (scene.IsValid())
            EditorSceneManager.MarkSceneDirty(scene);
    }
}
