using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class ObjectDetectionBubbleSetupEditor
{
    const string PrefabPath = "Assets/Prefabs/UI/ObjectDetectionBubblePrefab.prefab";

    [MenuItem("Tools/Object UI/Setup Detection Bubbles")]
    public static void SetupDetectionBubbles()
    {
        GameObject uiManager = FindOrCreateRoot("UIManager");
        ObjectDetectionBubbleSpawner bubbleSpawner = EnsureComponent<ObjectDetectionBubbleSpawner>(uiManager);
        ObjectUiRequestManager requestManager = UnityEngine.Object.FindObjectOfType<ObjectUiRequestManager>();
        if (requestManager == null)
            requestManager = EnsureComponent<ObjectUiRequestManager>(uiManager);

        Transform bubbleRoot = FindOrCreateChild(uiManager.transform, "BubbleRoot");
        ObjectActionRadialMenuSpawner radialSpawner = uiManager.GetComponent<ObjectActionRadialMenuSpawner>();
        if (radialSpawner == null)
            radialSpawner = UnityEngine.Object.FindObjectOfType<ObjectActionRadialMenuSpawner>();
        if (radialSpawner == null)
            radialSpawner = EnsureComponent<ObjectActionRadialMenuSpawner>(uiManager);

        Camera camera = FindSceneCamera();
        ObjectDetectionBubble bubblePrefab = LoadOrCreateBubblePrefab();

        Undo.RecordObject(bubbleSpawner, "Setup Object Detection Bubble Spawner");
        bubbleSpawner.BubblePrefab = bubblePrefab;
        bubbleSpawner.BubbleRoot = bubbleRoot;
        bubbleSpawner.RadialMenuSpawner = radialSpawner;
        if (radialSpawner.referenceCamera == null)
            radialSpawner.referenceCamera = camera;
        if (radialSpawner.anchorResolver == null)
            radialSpawner.anchorResolver = EnsureComponent<DetectedObjectAnchorResolver>(radialSpawner.gameObject);
        if (radialSpawner.commandBridge == null)
            radialSpawner.commandBridge = EnsureComponent<ObjectActionCommandBridge>(radialSpawner.gameObject);
        EditorUtility.SetDirty(radialSpawner);

        bubbleSpawner.AnchorResolver = radialSpawner != null ? radialSpawner.anchorResolver : UnityEngine.Object.FindObjectOfType<DetectedObjectAnchorResolver>();
        bubbleSpawner.ReferenceCamera = camera;
        EditorUtility.SetDirty(bubbleSpawner);

        Undo.RecordObject(requestManager, "Connect Object Detection Bubble Spawner");
        requestManager.bubbleSpawner = bubbleSpawner;
        if (requestManager.radialMenuSpawner == null)
            requestManager.radialMenuSpawner = radialSpawner;
        if (requestManager.referenceCamera == null)
            requestManager.referenceCamera = camera;
        EditorUtility.SetDirty(requestManager);

        EnsureEventSystem();

        EditorUtility.SetDirty(uiManager);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[OBJECT_BUBBLE][SETUP] Detection bubble setup complete. Save the scene if Unity prompts for changes.");
    }

    static GameObject FindOrCreateRoot(string name)
    {
        GameObject existing = GameObject.Find(name);
        if (existing != null) return existing;

        GameObject go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        return go;
    }

    static Transform FindOrCreateChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing;

        GameObject child = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(child, $"Create {name}");
        child.transform.SetParent(parent, false);
        return child.transform;
    }

    static T EnsureComponent<T>(GameObject go) where T : Component
    {
        T component = go.GetComponent<T>();
        if (component != null) return component;
        component = Undo.AddComponent<T>(go);
        return component;
    }

    static Camera FindSceneCamera()
    {
        Camera main = Camera.main;
        if (main != null) return main;

        Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] != null && cameras[i].enabled)
                return cameras[i];
        }
        return cameras.Length > 0 ? cameras[0] : null;
    }

    static ObjectDetectionBubble LoadOrCreateBubblePrefab()
    {
        ObjectDetectionBubble existing = AssetDatabase.LoadAssetAtPath<ObjectDetectionBubble>(PrefabPath);
        if (existing != null) return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));

        GameObject root = new GameObject("ObjectDetectionBubblePrefab", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(150f, 72f);
        root.transform.localScale = Vector3.one * 0.0012f;

        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.pixelPerfect = false;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        AddTrackedDeviceGraphicRaycasterIfAvailable(root);

        GameObject buttonGo = new GameObject("BubbleButton", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGo.transform.SetParent(root.transform, false);
        RectTransform buttonRect = buttonGo.GetComponent<RectTransform>();
        buttonRect.anchorMin = Vector2.zero;
        buttonRect.anchorMax = Vector2.one;
        buttonRect.offsetMin = Vector2.zero;
        buttonRect.offsetMax = Vector2.zero;

        Image image = buttonGo.GetComponent<Image>();
        image.color = new Color(0.08f, 0.43f, 1f, 0.88f);

        Button button = buttonGo.GetComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.08f, 0.43f, 1f, 0.88f);
        colors.highlightedColor = new Color(0.24f, 0.58f, 1f, 0.96f);
        colors.pressedColor = new Color(0.02f, 0.24f, 0.82f, 1f);
        button.colors = colors;

        Text label = CreateText(buttonGo.transform, "Label", new Vector2(0f, 10f), 20, FontStyle.Bold);
        Text confidence = CreateText(buttonGo.transform, "Confidence", new Vector2(0f, -16f), 14, FontStyle.Normal);

        ObjectDetectionBubble bubble = root.AddComponent<ObjectDetectionBubble>();
        bubble.Button = button;
        bubble.LabelText = label;
        bubble.ConfidenceText = confidence;

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.ImportAsset(PrefabPath);
        return prefab != null ? prefab.GetComponent<ObjectDetectionBubble>() : AssetDatabase.LoadAssetAtPath<ObjectDetectionBubble>(PrefabPath);
    }

    static Text CreateText(Transform parent, string name, Vector2 anchoredPosition, int fontSize, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = new Vector2(136f, 24f);

        Text text = go.GetComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 8;
        text.resizeTextMaxSize = fontSize;
        text.raycastTarget = false;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return text;
    }

    static void AddTrackedDeviceGraphicRaycasterIfAvailable(GameObject root)
    {
        Type raycasterType = Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit");
        if (raycasterType == null || root.GetComponent(raycasterType) != null) return;
        root.AddComponent(raycasterType);
    }

    static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;

        GameObject eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        Undo.RegisterCreatedObjectUndo(eventSystem, "Create EventSystem");
        EditorUtility.SetDirty(eventSystem);
    }
}
