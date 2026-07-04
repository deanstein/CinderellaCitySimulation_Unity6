using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

delegate bool IntersectRayMeshDelegate(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit);

/// <summary>
/// Scene view camera navigation — pivot relocalization, zoom-to-fit, scroll boost, and cursor picking.
/// </summary>
[InitializeOnLoad]
public static class NavigationTools
{
    const string ZoomMultiplierPrefKey = "CCP.SceneViewZoomMultiplier";
    const string BoostEnabledPrefKey = "CCP.SceneViewZoomBoostEnabled";
    const string MinWorldStepPrefKey = "CCP.SceneViewZoomMinWorldStep";

    const float DefaultZoomMultiplier = 4f;
    const float DefaultMinWorldStep = 0.35f;
    const float FramePaddingFactor = 0.25f;
    const float MinFrameExtent = 0.5f;
    const float UnityZoomFactor = 0.015f;
    const float UnityMinZoomDelta = 0.0001f;
    const float MaxSceneViewSize = 2.5E+7f;

    static Vector2 s_LastMousePositionInSceneView;
    static int s_LastSceneViewInstanceId;
    static PendingSceneAction s_PendingAction = PendingSceneAction.None;
    static int s_PendingViewInstanceId;
    static readonly IntersectRayMeshDelegate s_IntersectRayMesh = CreateIntersectRayMeshDelegate();

    enum PendingSceneAction
    {
        None,
        RelocalizePivot,
        ZoomToFitAtCursor,
    }

    static NavigationTools()
    {
        SceneView.duringSceneGui += OnDuringSceneGui;
    }

    [Shortcut("Cinderella City Project/Relocalize Zoom Pivot", typeof(SceneView), KeyCode.F, ShortcutModifiers.Action | ShortcutModifiers.Shift)]
    static void RelocalizeZoomPivotShortcut(ShortcutArguments args)
    {
        RequestRelocalizeZoomPivot(args.context as SceneView);
    }

    [Shortcut("Cinderella City Project/Zoom to Fit At Cursor/Ctrl+F", typeof(SceneView), KeyCode.F, ShortcutModifiers.Action)]
    static void ZoomToFitAtCursorCtrlFShortcut(ShortcutArguments args)
    {
        RequestZoomToFitAtCursor(args.context as SceneView);
    }

    [Shortcut("Cinderella City Project/Zoom to Fit At Cursor/F", typeof(SceneView), KeyCode.F)]
    static void ZoomToFitAtCursorFShortcut(ShortcutArguments args)
    {
        RequestZoomToFitAtCursor(args.context as SceneView);
    }

    [Shortcut("Cinderella City Project/Zoom to Fit At Cursor/Space", typeof(SceneView), KeyCode.Space)]
    static void ZoomToFitAtCursorSpaceShortcut(ShortcutArguments args)
    {
        RequestZoomToFitAtCursor(args.context as SceneView);
    }

    public static bool BoostEnabled
    {
        get => EditorPrefs.GetBool(BoostEnabledPrefKey, true);
        set => EditorPrefs.SetBool(BoostEnabledPrefKey, value);
    }

    public static float ZoomMultiplier
    {
        get => EditorPrefs.GetFloat(ZoomMultiplierPrefKey, DefaultZoomMultiplier);
        set => EditorPrefs.SetFloat(ZoomMultiplierPrefKey, Mathf.Clamp(value, 1f, 20f));
    }

    public static float MinWorldStep
    {
        get => EditorPrefs.GetFloat(MinWorldStepPrefKey, DefaultMinWorldStep);
        set => EditorPrefs.SetFloat(MinWorldStepPrefKey, Mathf.Max(0.01f, value));
    }

    public static void ToggleBoostedScrollZoom()
    {
        BoostEnabled = !BoostEnabled;
        Debug.Log($"CCP Scene view boosted scroll zoom: {(BoostEnabled ? "ON" : "OFF")} (multiplier {ZoomMultiplier:0.#}, min step {MinWorldStep:0.##}m)");
    }

    public static void SetZoomPreset(float multiplier)
    {
        ZoomMultiplier = multiplier;
        BoostEnabled = true;
        Debug.Log($"CCP Scene view scroll zoom: ON at {multiplier:0.#}x (min step {MinWorldStep:0.##}m). Use Relocalize Zoom Pivot (Ctrl+Shift+F) when wheel still feels stuck.");
    }

    public static void ResetZoomToUnityDefault()
    {
        ZoomMultiplier = 1f;
        BoostEnabled = false;
        Debug.Log("CCP Scene view scroll zoom reset to Unity default.");
    }

    public static void LogNavigationState()
    {
        SceneView view = SceneView.lastActiveSceneView;
        if (view == null)
        {
            Debug.LogWarning("No active Scene view.");
            return;
        }

        Vector3 camPos = view.camera.transform.position;
        float pivotDistance = Vector3.Distance(camPos, view.pivot);
        Debug.Log(
            $"Scene view: size={view.size:0.###}, pivotDistance={pivotDistance:0.###}, " +
            $"cameraDistance={view.cameraDistance:0.###}, orthographic={view.orthographic}, " +
            $"boost={(BoostEnabled ? "ON" : "OFF")} x{ZoomMultiplier:0.#}");
    }

    /// <summary>
    /// Queues a pivot relocalization for the next Scene view Layout pass so picking runs inside OnGUI.
    /// </summary>
    public static void RequestRelocalizeZoomPivot(SceneView view)
    {
        QueueSceneAction(view, PendingSceneAction.RelocalizePivot);
    }

    /// <summary>
    /// Queues a zoom-to-fit for the next Scene view Layout pass so picking runs inside OnGUI.
    /// </summary>
    public static void RequestZoomToFitAtCursor(SceneView view)
    {
        QueueSceneAction(view, PendingSceneAction.ZoomToFitAtCursor);
    }

    /// <summary>
    /// Picks scene geometry at the tracked cursor position for the given Scene view.
    /// Must be called during a Scene view OnGUI Layout/Repaint pass.
    /// </summary>
    public static bool TryPickAtViewCursor(SceneView view, out SceneGeometryPick pick)
    {
        pick = default;
        if (view == null)
            return false;

        return TryPickSceneGeometry(GetGuiPointForSceneView(view), out pick);
    }

    static void OnDuringSceneGui(SceneView view)
    {
        Event evt = Event.current;
        if (evt != null)
        {
            s_LastMousePositionInSceneView = evt.mousePosition;
            s_LastSceneViewInstanceId = view.GetInstanceID();

            if (evt.type == EventType.Layout)
                ProcessPendingSceneAction(view);
        }

        if (!BoostEnabled || ZoomMultiplier <= 1f)
            return;

        if (evt == null || evt.type != EventType.ScrollWheel || evt.alt)
            return;

        if (view == null || view.in2DMode)
            return;

        ApplyBoostedScrollZoom(view, evt);
        evt.Use();
        view.Repaint();
    }

    static void ApplyBoostedScrollZoom(SceneView view, Event evt)
    {
        float scrollDelta = evt.delta.y;
        if (Mathf.Approximately(scrollDelta, 0f))
            return;

        float multiplier = ZoomMultiplier;
        float minWorldStep = MinWorldStep * multiplier;
        float targetSize;

        if (view.orthographic)
        {
            targetSize = Mathf.Abs(view.size) * (scrollDelta * UnityZoomFactor * multiplier + 1f);
        }
        else
        {
            float relativeDelta = Mathf.Abs(view.size) * scrollDelta * UnityZoomFactor * multiplier;
            float minDelta = Mathf.Sign(scrollDelta) * minWorldStep;
            if (Mathf.Abs(relativeDelta) < Mathf.Abs(minDelta))
                relativeDelta = minDelta;

            if (relativeDelta > 0f && relativeDelta < UnityMinZoomDelta)
                relativeDelta = UnityMinZoomDelta;
            else if (relativeDelta < 0f && relativeDelta > -UnityMinZoomDelta)
                relativeDelta = -UnityMinZoomDelta;

            targetSize = view.size + relativeDelta;
        }

        if (float.IsNaN(targetSize) || float.IsInfinity(targetSize))
            return;

        float initialDistance = view.cameraDistance;
        targetSize = Mathf.Min(MaxSceneViewSize, targetSize);
        view.size = targetSize;

        if (!evt.alt && Mathf.Abs(view.cameraDistance) < 1.0e7f)
        {
            float percentage = 1f - (view.cameraDistance / initialDistance);
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            Vector3 mousePivot = mouseRay.origin + mouseRay.direction * initialDistance;
            Vector3 pivotVector = mousePivot - view.pivot;
            view.pivot += pivotVector * percentage;
        }
    }

    static void QueueSceneAction(SceneView view, PendingSceneAction action)
    {
        if (view == null)
            view = SceneView.lastActiveSceneView;

        if (view == null)
        {
            Debug.LogWarning("CCP Scene view navigation: no Scene view is open.");
            return;
        }

        s_PendingAction = action;
        s_PendingViewInstanceId = view.GetInstanceID();
        view.Repaint();
    }

    static void ProcessPendingSceneAction(SceneView view)
    {
        if (s_PendingAction == PendingSceneAction.None || view.GetInstanceID() != s_PendingViewInstanceId)
            return;

        PendingSceneAction action = s_PendingAction;
        s_PendingAction = PendingSceneAction.None;

        switch (action)
        {
            case PendingSceneAction.RelocalizePivot:
                RelocalizeZoomPivot(view);
                break;
            case PendingSceneAction.ZoomToFitAtCursor:
                ZoomToFitAtCursor(view);
                break;
        }
    }

    static void RelocalizeZoomPivot(SceneView view)
    {
        if (view == null)
            view = SceneView.lastActiveSceneView;

        if (view == null)
        {
            Debug.LogWarning("CCP Relocalize Zoom Pivot: no Scene view is open.");
            return;
        }

        Vector3 camPos = view.camera.transform.position;
        Vector3 forward = view.camera.transform.forward;
        Vector2 guiPoint = GetGuiPointForSceneView(view);

        Vector3 newPivot;
        if (TryPickSceneGeometry(guiPoint, out SceneGeometryPick pick))
        {
            newPivot = pick.hitPoint;
        }
        else
        {
            newPivot = camPos + forward * Mathf.Max(view.size, 5f);
        }

        float distance = Vector3.Distance(camPos, newPivot);
        if (distance < 0.05f)
            newPivot = camPos + forward * 5f;

        distance = Vector3.Distance(camPos, newPivot);
        view.LookAt(newPivot, view.rotation, distance);
        view.Repaint();
        Debug.Log($"CCP Relocalize Zoom Pivot: pivot reset at {newPivot} (distance {distance:0.##}).");
    }

    static void ZoomToFitAtCursor(SceneView view)
    {
        if (view == null)
            view = SceneView.lastActiveSceneView;

        if (view == null)
        {
            Debug.LogWarning("CCP Zoom to Fit At Cursor: no Scene view is open.");
            return;
        }

        Vector2 guiPoint = GetGuiPointForSceneView(view);
        if (!TryGetFrameBounds(guiPoint, out Bounds frameBounds))
        {
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(guiPoint);
            Vector3 fallbackCenter = mouseRay.origin + mouseRay.direction * Mathf.Max(view.size * 0.5f, 5f);
            frameBounds = new Bounds(fallbackCenter, Vector3.one * 2f);
        }

        ApplyPaddedFrame(view, frameBounds);
    }

    static void ApplyPaddedFrame(SceneView view, Bounds bounds)
    {
        Vector3 padding = bounds.size * FramePaddingFactor;
        float minPadding = MinFrameExtent * FramePaddingFactor;
        padding.x = Mathf.Max(padding.x, minPadding);
        padding.y = Mathf.Max(padding.y, minPadding);
        padding.z = Mathf.Max(padding.z, minPadding);
        bounds.Expand(padding);

        if (!view.Frame(bounds, instant: false))
            view.LookAt(bounds.center, view.rotation, bounds.extents.magnitude * 2f);

        view.Repaint();
    }

    static bool TryGetFrameBounds(Vector2 guiPoint, out Bounds bounds)
    {
        bounds = default;

        if (!TryPickSceneGeometry(guiPoint, out SceneGeometryPick pick))
            return false;

        if (pick.hasMeshHit && TryGetTriangleBounds(pick.meshFilter, pick.meshHit, out bounds))
            return true;

        if (pick.meshFilter != null && TryGetMeshFilterBounds(pick.meshFilter, out bounds))
            return true;

        Renderer renderer = pick.gameObject != null ? pick.gameObject.GetComponent<Renderer>() : null;
        if (renderer != null)
        {
            bounds = renderer.bounds;
            return true;
        }

        bounds = new Bounds(pick.hitPoint, Vector3.one * MinFrameExtent);
        return true;
    }

    static bool TryGetTriangleBounds(MeshFilter meshFilter, RaycastHit meshHit, out Bounds bounds)
    {
        bounds = default;
        if (meshFilter == null)
            return false;

        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null || meshHit.triangleIndex < 0)
            return false;

        int[] triangles = mesh.triangles;
        int triangleVertexIndex = meshHit.triangleIndex * 3;
        if (triangleVertexIndex + 2 >= triangles.Length)
            return false;

        Vector3[] vertices = mesh.vertices;
        Transform transform = meshFilter.transform;
        Vector3 v0 = transform.TransformPoint(vertices[triangles[triangleVertexIndex]]);
        Vector3 v1 = transform.TransformPoint(vertices[triangles[triangleVertexIndex + 1]]);
        Vector3 v2 = transform.TransformPoint(vertices[triangles[triangleVertexIndex + 2]]);

        bounds = new Bounds(v0, Vector3.zero);
        bounds.Encapsulate(v1);
        bounds.Encapsulate(v2);
        return true;
    }

    static bool TryGetMeshFilterBounds(MeshFilter meshFilter, out Bounds bounds)
    {
        bounds = default;
        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null)
            return false;

        bounds = mesh.bounds;
        bounds = TransformBounds(meshFilter.transform.localToWorldMatrix, bounds);
        return true;
    }

    static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
    {
        Vector3 center = matrix.MultiplyPoint3x4(localBounds.center);
        Vector3 extents = localBounds.extents;
        Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
        extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
        extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
        extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);
        return new Bounds(center, extents * 2f);
    }

    public struct SceneGeometryPick
    {
        public Vector3 hitPoint;
        public GameObject gameObject;
        public MeshFilter meshFilter;
        public RaycastHit meshHit;
        public bool hasMeshHit;
    }

    static bool TryPickSceneGeometry(Vector2 guiPoint, out SceneGeometryPick pick)
    {
        pick = default;

        GameObject picked = HandleUtility.PickGameObject(guiPoint, false);
        if (picked == null)
            return false;

        pick.gameObject = picked;
        Ray mouseRay = HandleUtility.GUIPointToWorldRay(guiPoint);
        MeshFilter[] meshFilters = picked.GetComponentsInChildren<MeshFilter>();
        float minDistance = float.PositiveInfinity;

        foreach (MeshFilter meshFilter in meshFilters)
        {
            Mesh mesh = meshFilter.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0)
                continue;

            if (IntersectRayMesh(
                    mouseRay,
                    mesh,
                    meshFilter.transform.localToWorldMatrix,
                    out RaycastHit localHit)
                && localHit.distance < minDistance)
            {
                pick.hitPoint = localHit.point;
                pick.meshFilter = meshFilter;
                pick.meshHit = localHit;
                pick.hasMeshHit = true;
                minDistance = localHit.distance;
            }
        }

        if (pick.hasMeshHit)
            return true;

        Collider[] colliders = picked.GetComponentsInChildren<Collider>();
        foreach (Collider collider in colliders)
        {
            if (collider.Raycast(mouseRay, out RaycastHit colliderHit, float.PositiveInfinity)
                && colliderHit.distance < minDistance)
            {
                pick.hitPoint = colliderHit.point;
                minDistance = colliderHit.distance;
            }
        }

        if (minDistance < float.PositiveInfinity)
            return true;

        pick.hitPoint = Vector3.Project(picked.transform.position - mouseRay.origin, mouseRay.direction) + mouseRay.origin;
        return true;
    }

    static Vector2 GetGuiPointForSceneView(SceneView view)
    {
        if (view.GetInstanceID() == s_LastSceneViewInstanceId)
            return s_LastMousePositionInSceneView;

        return new Vector2(view.camera.pixelWidth * 0.5f, view.camera.pixelHeight * 0.5f);
    }

    static IntersectRayMeshDelegate CreateIntersectRayMeshDelegate()
    {
        MethodInfo method = typeof(HandleUtility).GetMethod(
            "IntersectRayMesh",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (method == null)
        {
            Debug.LogWarning("CCP Scene view navigation: HandleUtility.IntersectRayMesh is unavailable in this Unity version.");
            return null;
        }

        return (IntersectRayMeshDelegate)Delegate.CreateDelegate(typeof(IntersectRayMeshDelegate), method);
    }

    static bool IntersectRayMesh(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit)
    {
        hit = default;
        if (s_IntersectRayMesh == null)
            return false;

        return s_IntersectRayMesh(ray, mesh, matrix, out hit);
    }
}
