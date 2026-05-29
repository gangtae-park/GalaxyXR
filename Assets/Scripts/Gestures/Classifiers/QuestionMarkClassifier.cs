using System.Collections.Generic;
using UnityEngine;
using PDollarGestureRecognizer;
using QDollarGestureRecognizer;

/// <summary>
/// $Q-based classifier for the "?" gesture.
/// Projects the world-space stroke to a 2D camera-aligned plane, builds a $Q
/// candidate, then runs greedy point-cloud matching against a small set of
/// hardcoded "?" templates (tweakable in Inspector).
///
/// To improve accuracy: add more user-recorded templates over time. They can be
/// dropped into the `templateSamples` list as raw 2D point sequences.
/// </summary>
public class QuestionMarkClassifier : GestureClassifierComponent
{
    [Header("Identity")]
    [SerializeField] private string gestureName = "Ask";
    public override string GestureName => gestureName;

    [Header("Sampling")]
    public int minPointCount = 12;

    [Header("Matching")]
    [Tooltip("Looser distance threshold = more lenient match. $Q's distance is roughly " +
             "0..a few hundred for normalized 64-point clouds. Tune by watching the log.")]
    public float maxMatchDistance = 4.0f;
    [Tooltip("If true, also try every other registered $Q template (e.g. circle as decoy) " +
             "and only accept '?' if it wins by a margin.")]
    public bool useNegativeTemplates = true;
    [Tooltip("Confidence margin: '?' distance must be at least this much smaller than " +
             "the best non-? template. Only used if useNegativeTemplates=true.")]
    public float requiredMargin = 0.5f;

    [Header("Templates")]
    [Tooltip("Each entry is one drawn instance of '?'. Multiple variations help recall.")]
    public List<TemplateSample> templateSamples = new List<TemplateSample>();
    [Tooltip("Optional decoy templates of NOT-question-mark shapes (e.g., circle, line). " +
             "Used only when useNegativeTemplates=true.")]
    public List<TemplateSample> decoyTemplates = new List<TemplateSample>();

    [System.Serializable]
    public class TemplateSample
    {
        public string label = "qmark";
        // Each sample is a sequence of 2D points (any units; will be normalized).
        public List<Vector2> points = new List<Vector2>();
    }

    private Gesture[] _builtTemplates;
    private bool _templatesBuilt = false;

    void Awake()
    {
        BuildDefaultTemplatesIfEmpty();
        BuildQTemplates();
    }

    /// <summary>
    /// Append a user-recorded template at runtime (called by QuestionMarkTemplateRecorder).
    /// Rebuilds the internal $Q template set so the new sample takes effect immediately.
    /// </summary>
    public void AppendUserTemplate(string label, List<Vector2> points)
    {
        if (points == null || points.Count < 2) return;
        templateSamples.Add(new TemplateSample
        {
            label = string.IsNullOrEmpty(label) ? "user" : label,
            points = new List<Vector2>(points),
        });
        BuildQTemplates();
    }

    /// <summary>Reset all user templates back to just the synthetic defaults and rebuild.</summary>
    public void ResetTemplates()
    {
        templateSamples.Clear();
        BuildDefaultTemplatesIfEmpty();
        BuildQTemplates();
    }

    public override bool TryClassify(Stroke stroke, out float confidence)
    {
        confidence = 0f;
        if (stroke == null || stroke.WorldPoints.Count < minPointCount) return false;
        if (!_templatesBuilt || _builtTemplates == null || _builtTemplates.Length == 0)
        {
            Debug.LogWarning("[QuestionMarkClassifier] no templates built; skipping.");
            return false;
        }

        // Project world stroke to 2D camera-plane, then build a $Q Gesture.
        var projected = stroke.ProjectTo2DCameraPlane();
        if (projected.Count < minPointCount) return false;

        Point[] candidatePts = new Point[projected.Count];
        for (int i = 0; i < projected.Count; i++)
            candidatePts[i] = new Point(projected[i].x, projected[i].y, 0);
        var candidate = new Gesture(candidatePts, "candidate");

        // Find closest "?" template + closest decoy.
        float bestQDist = float.MaxValue;
        float bestDecoyDist = float.MaxValue;
        string bestQLabel = null;
        foreach (var t in _builtTemplates)
        {
            float d = ComputeDistance(candidate, t);
            bool isQ = t.Name.StartsWith("Q__");
            if (isQ)
            {
                if (d < bestQDist) { bestQDist = d; bestQLabel = t.Name; }
            }
            else
            {
                if (d < bestDecoyDist) bestDecoyDist = d;
            }
        }

        Debug.Log(
            $"[QuestionMarkClassifier] best Q dist={bestQDist:F3} ({bestQLabel}), " +
            $"best decoy dist={(bestDecoyDist == float.MaxValue ? -1f : bestDecoyDist):F3}, " +
            $"thresh={maxMatchDistance:F3}"
        );

        if (bestQDist > maxMatchDistance) return false;
        if (useNegativeTemplates && bestDecoyDist != float.MaxValue)
        {
            if (bestDecoyDist - bestQDist < requiredMargin)
            {
                Debug.Log("[QuestionMarkClassifier] decoy too close; reject.");
                return false;
            }
        }

        // Confidence: tighter match -> higher conf. Linearly map [0..maxMatchDistance] -> [1..0].
        confidence = Mathf.Clamp01(1f - bestQDist / maxMatchDistance);
        return true;
    }

    float ComputeDistance(Gesture candidate, Gesture template)
    {
        // Classify against single template by passing it as the only one in templateSet
        // is wasteful; instead reuse $Q's internal greedy matcher logic by classifying
        // against an array of one, but Classify returns the name only. Easier: we'll
        // bypass and use QPointCloudRecognizer.Classify with both templates in array.
        // For per-template distance we just call Classify for each pair; since we want
        // raw distance, we hand-roll a thin wrapper.
        Gesture[] one = new Gesture[] { template };
        // Unfortunately Classify() returns name only. Re-implement a minimal version
        // by reading the public greedy match through a one-element classification with
        // a sentinel label.
        // Simpler: replicate the lower-bounding match here would require reflection.
        // Instead: do a coarse match by direct point-cloud sum (slower, simpler, fine
        // for small template counts).
        return DirectCloudDistance(candidate, template);
    }

    /// <summary>
    /// Simple greedy cloud distance (no early-abandon / lower-bound). Adequate when
    /// template count is small (5-20). Gives a comparable distance scalar for ranking.
    /// </summary>
    static float DirectCloudDistance(Gesture a, Gesture b)
    {
        Point[] pa = a.Points, pb = b.Points;
        int n = Mathf.Min(pa.Length, pb.Length);
        if (n == 0) return float.MaxValue;
        bool[] used = new bool[n];
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float minD = float.MaxValue; int minJ = -1;
            for (int j = 0; j < n; j++)
            {
                if (used[j]) continue;
                float d = (pa[i].X - pb[j].X) * (pa[i].X - pb[j].X) +
                          (pa[i].Y - pb[j].Y) * (pa[i].Y - pb[j].Y);
                if (d < minD) { minD = d; minJ = j; }
            }
            if (minJ < 0) break;
            used[minJ] = true;
            sum += (n - i) * minD; // weight decreases like in $Q
        }
        // Normalize by n*(n+1)/2 so ranges are comparable across n sizes.
        return sum / (n * (n + 1f) / 2f);
    }

    void BuildQTemplates()
    {
        var list = new List<Gesture>();
        for (int i = 0; i < templateSamples.Count; i++)
        {
            var s = templateSamples[i];
            if (s == null || s.points.Count < 2) continue;
            list.Add(BuildGesture(s.points, $"Q__{s.label}_{i}"));
        }
        if (useNegativeTemplates)
        {
            for (int i = 0; i < decoyTemplates.Count; i++)
            {
                var s = decoyTemplates[i];
                if (s == null || s.points.Count < 2) continue;
                list.Add(BuildGesture(s.points, $"D__{s.label}_{i}"));
            }
        }
        _builtTemplates = list.ToArray();
        _templatesBuilt = true;
        Debug.Log($"[QuestionMarkClassifier] built {_builtTemplates.Length} templates " +
                  $"(Q={templateSamples.Count}, D={decoyTemplates.Count})");
    }

    static Gesture BuildGesture(List<Vector2> pts, string name)
    {
        Point[] arr = new Point[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            arr[i] = new Point(pts[i].x, pts[i].y, 0);
        return new Gesture(arr, name);
    }

    /// <summary>
    /// If user hasn't recorded any templates, seed with synthetic "?" curves so the
    /// system at least runs. These are coarse approximations -- replace with real
    /// recorded strokes for production accuracy.
    /// </summary>
    void BuildDefaultTemplatesIfEmpty()
    {
        if (templateSamples.Count > 0) return;

        // Single-stroke "?" approximations, drawn in unit-ish coords (any units OK,
        // Gesture.Normalize() rescales).
        // Variation A: classical right-handed hook + stem + small bottom loop
        var a = new TemplateSample { label = "synthA" };
        AppendArc(a.points, new Vector2(0.4f, 0.0f), new Vector2(0.6f, 0.2f), 12, false); // top arc
        AppendArc(a.points, new Vector2(0.6f, 0.2f), new Vector2(0.5f, 0.45f), 8, false); // curve down
        AppendLine(a.points, new Vector2(0.5f, 0.45f), new Vector2(0.5f, 0.65f), 8);       // stem
        AppendArc(a.points, new Vector2(0.5f, 0.65f), new Vector2(0.55f, 0.85f), 6, false); // dot region
        templateSamples.Add(a);

        // Variation B: more rounded top
        var b = new TemplateSample { label = "synthB" };
        AppendArc(b.points, new Vector2(0.35f, 0.05f), new Vector2(0.65f, 0.05f), 16, true);
        AppendLine(b.points, new Vector2(0.65f, 0.05f), new Vector2(0.5f, 0.55f), 12);
        AppendLine(b.points, new Vector2(0.5f, 0.55f), new Vector2(0.5f, 0.9f), 6);
        templateSamples.Add(b);

        if (decoyTemplates.Count == 0)
        {
            // Decoy: closed circle (so circle gesture won't accidentally match "?")
            var circle = new TemplateSample { label = "circle" };
            AppendCircle(circle.points, new Vector2(0.5f, 0.5f), 0.4f, 32);
            decoyTemplates.Add(circle);
        }
    }

    static void AppendLine(List<Vector2> dst, Vector2 a, Vector2 b, int steps)
    {
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            dst.Add(Vector2.Lerp(a, b, t));
        }
    }

    static void AppendArc(List<Vector2> dst, Vector2 a, Vector2 b, int steps, bool concaveDown)
    {
        Vector2 mid = (a + b) * 0.5f;
        Vector2 ab = b - a;
        Vector2 perp = new Vector2(-ab.y, ab.x).normalized * ab.magnitude * 0.4f;
        if (concaveDown) perp = -perp;
        Vector2 ctrl = mid + perp;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            // Quadratic Bezier
            Vector2 p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * ctrl + t * t * b;
            dst.Add(p);
        }
    }

    static void AppendCircle(List<Vector2> dst, Vector2 c, float r, int steps)
    {
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps * Mathf.PI * 2f;
            dst.Add(c + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r);
        }
    }
}
