using System.Linq;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features.Android;

internal static class AndroidXRDirectPreviewSetup
{
    private const string AndroidXRFeatureSetId = "com.unity.openxr.featureset.android";
    private const string PendingConfigurationKey =
        "GalaxyXR.PendingDirectPreviewConfiguration";

    [InitializeOnLoadMethod]
    private static void ContinuePendingConfiguration()
    {
        if (!SessionState.GetBool(PendingConfigurationKey, false))
            return;

        SessionState.EraseBool(PendingConfigurationKey);
        EditorApplication.delayCall += ConfigureStandaloneFeatures;
    }

    [MenuItem("Galaxy XR/Configure Direct Preview Passthrough")]
    private static void ConfigureStandaloneFeatures()
    {
        const BuildTargetGroup target = BuildTargetGroup.Standalone;

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
        {
            SessionState.SetBool(PendingConfigurationKey, true);
            Debug.Log(
                "[Android XR Setup] Switching the active build target from Android " +
                "to Windows/Standalone for Direct Preview.");
            EditorUserBuildSettings.SwitchActiveBuildTargetAsync(
                target,
                BuildTarget.StandaloneWindows64);
            return;
        }

        // Package updates can leave newly introduced feature instances absent from
        // the serialized OpenXR settings until its Project Settings UI is opened.
        FeatureHelpers.RefreshFeatures(target);

        var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(
            target,
            AndroidXRFeatureSetId);

        if (featureSet == null)
        {
            Debug.LogError(
                "[Android XR Setup] Android XR feature group was not found. " +
                "Check that the Android XR OpenXR package is installed.");
            return;
        }

        var changed = !featureSet.isEnabled;
        featureSet.isEnabled = true;
        OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(target);

        var requiredFeatureIds = new[]
        {
            AndroidXRSupportFeature.featureId,
            ARSessionFeature.featureId,
            ARCameraFeature.featureId,
            "com.unity.openxr.feature.input.handinteraction",
            "com.unity.openxr.feature.input.handtracking",
            "com.unity.openxr.feature.input.metahandtrackingaim",
        };

        var requiredFeatures = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(
            target,
            requiredFeatureIds).Where(feature => feature != null).ToArray();

        if (requiredFeatures.Length != requiredFeatureIds.Length)
        {
            Debug.LogError(
                $"[Android XR Setup] Expected {requiredFeatureIds.Length} Direct Preview " +
                $"features but found {requiredFeatures.Length}. Open Project Settings > " +
                "XR Plug-in Management > OpenXR and select the Windows/Standalone tab once.");
            return;
        }

        foreach (var feature in requiredFeatures)
        {
            if (feature.enabled)
                continue;

            feature.enabled = true;
            changed = true;
        }

        if (!changed)
            return;

        AssetDatabase.SaveAssets();
        Debug.Log(
            "[Android XR Setup] Enabled Android XR Support, AR Session, and AR Camera " +
            "for Windows/Standalone Direct Preview.");
    }
}
