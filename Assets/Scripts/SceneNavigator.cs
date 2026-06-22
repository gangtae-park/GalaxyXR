using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneNavigator : MonoBehaviour
{
    public void LoadSceneByName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return;
        SceneManager.LoadScene(sceneName);
    }

    public void LoadSceneByIndex(int buildIndex)
    {
        if (buildIndex < 0 || buildIndex >= SceneManager.sceneCountInBuildSettings) return;
        SceneManager.LoadScene(buildIndex);
    }

    public void LoadNextScene()
    {
        int next = SceneManager.GetActiveScene().buildIndex + 1;
        if (next >= SceneManager.sceneCountInBuildSettings)
        {
            Debug.LogWarning("[SceneNavigator] No next scene in Build Settings.");
            return;
        }
        SceneManager.LoadScene(next);
    }

    public void LoadPreviousScene()
    {
        int prev = SceneManager.GetActiveScene().buildIndex - 1;
        if (prev < 0)
        {
            Debug.LogWarning("[SceneNavigator] No previous scene in Build Settings.");
            return;
        }
        SceneManager.LoadScene(prev);
    }

    public void ReloadCurrentScene()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
