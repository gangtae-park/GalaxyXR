using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

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

    // Generates a sphere-based bubble prefab that matches ObjectDetectionBubble's
    // current shape (no Canvas / Button / Text). Click handling rides on
    // SphereCollider + EventSystem (IPointerClickHandler) and, when XR
    // Interaction Toolkit is present, an optional XRSimpleInteractable.
    static ObjectDetectionBubble LoadOrCreateBubblePrefab()
    {
        ObjectDetectionBubble existing = AssetDatabase.LoadAssetAtPath<ObjectDetectionBubble>(PrefabPath);
        if (existing != null) return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));

        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        root.name = "ObjectDetectionBubblePrefab";
        root.transform.localScale = Vector3.one * 0.05f; // 5 cm diameter

        MeshRenderer mr = root.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Standard");
            Material mat = new Material(shader);
            mat.color = new Color(0.08f, 0.43f, 1f, 0.88f);
            mr.sharedMaterial = mat;
        }

        root.AddComponent<ObjectDetectionBubble>();
        AddXrSimpleInteractableIfAvailable(root);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.ImportAsset(PrefabPath);
        return prefab != null ? prefab.GetComponent<ObjectDetectionBubble>() : AssetDatabase.LoadAssetAtPath<ObjectDetectionBubble>(PrefabPath);
    }

    // Reflection-based add so the editor utility compiles even when XR
    // Interaction Toolkit isn't in the project. When present, the bubble
    // script auto-hooks selectEntered at runtime.
    static void AddXrSimpleInteractableIfAvailable(GameObject root)
    {
        Type t = Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable, Unity.XR.Interaction.Toolkit");
        if (t == null || root.GetComponent(t) != null) return;
        root.AddComponent(t);
    }

    static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;

        GameObject eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        Undo.RegisterCreatedObjectUndo(eventSystem, "Create EventSystem");
        EditorUtility.SetDirty(eventSystem);
    }
}
