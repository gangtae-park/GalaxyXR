using UnityEngine;

/// <summary>
/// Anything that can answer "does this stroke look like my gesture?".
/// </summary>
public interface IGestureClassifier
{
    /// <summary>The gesture name to emit when this classifier matches (must match
    /// the @register("...") name on the Python handler side).</summary>
    string GestureName { get; }

    /// <summary>Return true if the stroke matches. confidence is a 0..1 score
    /// (rule-based classifiers may return 1.0 on match / 0.0 on miss; template
    /// classifiers return a similarity score).</summary>
    bool TryClassify(Stroke stroke, out float confidence);
}

/// <summary>
/// MonoBehaviour-friendly base. Subclass this so each classifier can be
/// configured via the Inspector and registered into GestureRouter's list.
/// </summary>
public abstract class GestureClassifierComponent : MonoBehaviour, IGestureClassifier
{
    public abstract string GestureName { get; }
    public abstract bool TryClassify(Stroke stroke, out float confidence);
}
