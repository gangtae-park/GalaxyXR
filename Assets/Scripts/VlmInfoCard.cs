using TMPro;
using UnityEngine;

/// <summary>
/// Component placed on the root of the VLM info card prefab.
/// Manages three visual states: Loading -> Content (or Error) -> Auto-destroy.
/// Also billboards toward the camera every frame.
/// </summary>
public class VlmInfoCard : MonoBehaviour
{
    [Header("Targets")]
    [Tooltip("If null, Camera.main is used.")]
    public Camera billboardCamera;
    public TMP_Text titleText;
    public TMP_Text bodyText;
    public GameObject loadingIndicator;   // shown while waiting for VLM
    public GameObject contentRoot;        // shown when content/error arrives
    [Tooltip("Optional: child Transform to spin while loading.")]
    public Transform spinningTransform;

    [Header("Behavior")]
    public bool billboard = true;
    [Tooltip("Disable to keep the card alive indefinitely (e.g. while debugging).")]
    public bool autoDestroy = true;
    [Tooltip("Seconds the card stays visible after content/error is set.")]
    public float autoDestroyAfterContent = 30f;
    [Tooltip("Seconds to wait for VLM result before showing a 'Timed out' error.")]
    public float loadingTimeoutSec = 60f;
    public float spinSpeedDegPerSec = 240f;

    [Header("Drag/Close")]
    [Tooltip("If true, the card stops auto-destroying once the user has interacted " +
             "(grabbed or pressed any button). Lets the user keep it as long as they want.")]
    public bool stopAutoDestroyOnInteract = true;

    private float _destroyAt = float.PositiveInfinity;
    private float _loadingStartedAt = -1f;
    private bool _contentArrived = false;

    public void SetLoading(string title = "Searching...")
    {
        _contentArrived = false;
        _loadingStartedAt = Time.time;
        if (loadingIndicator != null) loadingIndicator.SetActive(true);
        if (contentRoot != null) contentRoot.SetActive(true); // keep title visible
        if (titleText != null) titleText.text = title;
        if (bodyText != null) bodyText.text = "";
    }

    public void SetContentSearch(string objName, string description, string typicalUse, string info)
    {
        _contentArrived = true;
        if (loadingIndicator != null) loadingIndicator.SetActive(false);
        if (contentRoot != null) contentRoot.SetActive(true);
        if (titleText != null)
            titleText.text = string.IsNullOrEmpty(objName) ? "Unknown Object" : objName;

        if (bodyText != null)
        {
            string body = "";
            if (!string.IsNullOrEmpty(description)) body = description;
            if (!string.IsNullOrEmpty(typicalUse))  body = JoinPara(body, typicalUse);
            if (!string.IsNullOrEmpty(info))        body = JoinPara(body, info);
            bodyText.text = body;
        }

        if (autoDestroy) _destroyAt = Time.time + autoDestroyAfterContent;
    }

    public void SetContentAsk(string objName, string answer)
    {
        _contentArrived = true;
        if (loadingIndicator != null) loadingIndicator.SetActive(false);
        if (contentRoot != null) contentRoot.SetActive(true);
        if (titleText != null)
            titleText.text = string.IsNullOrEmpty(objName) ? "Unknown Object" : objName;

        if (bodyText != null)
        {
            string body = "";
            if (!string.IsNullOrEmpty(answer)) body = answer;
            bodyText.text = body;
        }

        if (autoDestroy) _destroyAt = Time.time + autoDestroyAfterContent;
    }

    public void SetError(string title, string message)
    {
        _contentArrived = true;
        if (loadingIndicator != null) loadingIndicator.SetActive(false);
        if (contentRoot != null) contentRoot.SetActive(true);
        if (titleText != null) titleText.text = string.IsNullOrEmpty(title) ? "Error" : title;
        if (bodyText != null) bodyText.text = message ?? "";
        if (autoDestroy) _destroyAt = Time.time + autoDestroyAfterContent;
    }

    static string JoinPara(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return a + b;
    }

    // ---- Public API for UI buttons / XRGrabInteractable UnityEvents ----

    /// <summary>Wire the close button's OnClick to this. Destroys the card.</summary>
    public void Close()
    {
        Destroy(gameObject);
    }

    /// <summary>Toggle billboard (called from XRGrabInteractable.selectEntered/selectExited
    /// or any other UnityEvent source). When the user grabs the card, billboard should be
    /// disabled so the card keeps the orientation the user puts it in.</summary>
    public void SetBillboardEnabled(bool enabled)
    {
        billboard = enabled;
    }

    public void DisableBillboard() { billboard = false; }
    public void EnableBillboard()  { billboard = true; }

    /// <summary>Call this whenever the user interacts (grab/click) so the auto-destroy
    /// timer doesn't yank the card out from under them. Wire to XRGrabInteractable.
    /// selectEntered and any button OnClick if you want this safety.</summary>
    public void NotifyInteracted()
    {
        if (stopAutoDestroyOnInteract)
        {
            autoDestroy = false;
            _destroyAt = float.PositiveInfinity;
        }
    }

    void LateUpdate()
    {
        // Billboard toward camera so the card is always readable.
        if (billboard)
        {
            Camera cam = billboardCamera != null ? billboardCamera : Camera.main;
            if (cam != null)
            {
                Vector3 toCam = transform.position - cam.transform.position;
                if (toCam.sqrMagnitude > 0.000001f)
                {
                    transform.rotation = Quaternion.LookRotation(toCam, cam.transform.up);
                }
            }
        }

        // Spin loading indicator
        if (spinningTransform != null && loadingIndicator != null && loadingIndicator.activeInHierarchy)
        {
            spinningTransform.Rotate(0f, 0f, -spinSpeedDegPerSec * Time.deltaTime, Space.Self);
        }

        // Loading timeout
        if (!_contentArrived && _loadingStartedAt > 0f &&
            Time.time - _loadingStartedAt > loadingTimeoutSec)
        {
            SetError("Timed out", $"No VLM response within {loadingTimeoutSec:F0}s.");
        }

        // Auto destroy
        if (autoDestroy && Time.time >= _destroyAt)
        {
            Destroy(gameObject);
        }
    }
}
