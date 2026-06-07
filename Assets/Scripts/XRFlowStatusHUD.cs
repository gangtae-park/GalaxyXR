using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Small head-locked status panel for validating the Galaxy XR gesture/YOLO flow.
/// Created automatically in GestureScene so it also confirms that XR rendering
/// has entered the Unity scene.
/// </summary>
public sealed class XRFlowStatusHUD : MonoBehaviour
{
    private const string GestureSceneName = "GestureScene";

    private TMP_Text _text;
    private PinchStrokeCapture _strokeCapture;
    private GestureRouter _gestureRouter;
    private YoloSegLogger _yoloLogger;
    private LocalYoloSearchController _localSearch;
    private Camera2FrameReceiver _frameReceiver;
    private string _interactionStatus = "Waiting for pinch";
    private string _yoloStatus;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForGestureScene()
    {
        if (SceneManager.GetActiveScene().name != GestureSceneName)
            return;

        var host = new GameObject("XR Flow Status HUD");
        DontDestroyOnLoad(host);
        host.AddComponent<XRFlowStatusHUD>();
    }

    private IEnumerator Start()
    {
        Camera camera = null;
        while (camera == null)
        {
            camera = Camera.main;
            yield return null;
        }

        BuildPanel(camera.transform);
        BindFlow();
        RefreshText();
        Debug.Log("[XRFlowStatus] GestureScene entered; head-locked HUD is active.");
    }

    private void OnDestroy()
    {
        if (_strokeCapture != null)
        {
            _strokeCapture.OnStrokeStarted -= HandleStrokeStarted;
            _strokeCapture.OnStrokeCompleted -= HandleStrokeCompleted;
            _strokeCapture.OnStrokeCancelled -= HandleStrokeCancelled;
        }

        if (_gestureRouter != null)
        {
            _gestureRouter.OnGestureRecognized -= HandleGestureRecognized;
            _gestureRouter.OnGestureFailed -= HandleGestureFailed;
        }

        if (_yoloLogger != null)
            _yoloLogger.OnResultsUpdated -= HandleYoloResults;

        if (_localSearch != null)
            _localSearch.OnSearchStatusChanged -= HandleSearchStatus;

        if (_frameReceiver != null)
            _frameReceiver.OnCameraStatusChanged -= HandleCameraStatus;
    }

    private void BuildPanel(Transform cameraTransform)
    {
        var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        canvasObject.transform.SetParent(cameraTransform, false);
        canvasObject.transform.localPosition = new Vector3(0f, 0.27f, 1.1f);
        canvasObject.transform.localRotation = Quaternion.identity;
        canvasObject.transform.localScale = Vector3.one * 0.00125f;

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 1000;

        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(520f, 116f);

        var panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panelObject.transform.SetParent(canvasObject.transform, false);
        RectTransform panelRect = panelObject.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        panelObject.GetComponent<Image>().color = new Color(0.02f, 0.06f, 0.09f, 0.82f);

        var textObject = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(panelObject.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(18f, 10f);
        textRect.offsetMax = new Vector2(-18f, -10f);

        _text = textObject.GetComponent<TextMeshProUGUI>();
        _text.fontSize = 27f;
        _text.alignment = TextAlignmentOptions.MidlineLeft;
        _text.color = new Color(0.84f, 0.96f, 1f, 1f);
        _text.textWrappingMode = TextWrappingModes.NoWrap;
    }

    private void BindFlow()
    {
        _strokeCapture = FindAnyObjectByType<PinchStrokeCapture>();
        _gestureRouter = FindAnyObjectByType<GestureRouter>();
        _yoloLogger = FindAnyObjectByType<YoloSegLogger>();
        _localSearch = FindAnyObjectByType<LocalYoloSearchController>();
        _frameReceiver = FindAnyObjectByType<Camera2FrameReceiver>();

        if (_strokeCapture != null)
        {
            _strokeCapture.OnStrokeStarted += HandleStrokeStarted;
            _strokeCapture.OnStrokeCompleted += HandleStrokeCompleted;
            _strokeCapture.OnStrokeCancelled += HandleStrokeCancelled;
        }

        if (_gestureRouter != null)
        {
            _gestureRouter.OnGestureRecognized += HandleGestureRecognized;
            _gestureRouter.OnGestureFailed += HandleGestureFailed;
        }

        if (_yoloLogger != null)
            _yoloLogger.OnResultsUpdated += HandleYoloResults;

        if (_localSearch != null)
            _localSearch.OnSearchStatusChanged += HandleSearchStatus;

        if (_frameReceiver != null)
        {
            _frameReceiver.OnCameraStatusChanged += HandleCameraStatus;
            _yoloStatus = $"Camera: {_frameReceiver.ActiveSource}";
        }
    }

    private void HandleStrokeStarted(Stroke stroke)
    {
        _interactionStatus = "Pinch detected - draw a circle";
        RefreshText();
    }

    private void HandleStrokeCompleted(Stroke stroke)
    {
        _interactionStatus = $"Stroke complete ({stroke.PointCount} points) - classifying";
        RefreshText();
    }

    private void HandleStrokeCancelled()
    {
        _interactionStatus = "Pinch cancelled or reserved by Android XR";
        RefreshText();
    }

    private void HandleGestureRecognized(string gestureName)
    {
        _interactionStatus = $"Gesture recognized: {gestureName}";
        RefreshText();
    }

    private void HandleGestureFailed()
    {
        _interactionStatus = "Gesture failed - draw larger and slower";
        RefreshText();
    }

    private void HandleYoloResults(System.Collections.Generic.IReadOnlyList<YoloSegLogger.SegResult> results)
    {
        _yoloStatus = $"YOLO: {results?.Count ?? 0} results updated";
        RefreshText();
    }

    private void HandleSearchStatus(string status)
    {
        _interactionStatus = status;
        RefreshText();
    }

    private void HandleCameraStatus(string status)
    {
        _yoloStatus = status;
        RefreshText();
    }

    private void RefreshText()
    {
        if (_text == null)
            return;

        _text.text = $"GALAXY XR SCENE ACTIVE\n{_interactionStatus} | {_yoloStatus}";
    }
}
