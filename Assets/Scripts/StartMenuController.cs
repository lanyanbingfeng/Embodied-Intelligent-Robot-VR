using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 开始场景（StartScene）的按钮逻辑：开始游戏加载 GameScene，退出游戏关闭应用。
/// 挂在场景里的 StartMenuController 物体上，由两个 Button 的 OnClick 调用。
/// </summary>
public class StartMenuController : MonoBehaviour
{
    [Header("场景")]
    [Tooltip("点击“开始游戏”后加载的场景名，需已加入 Build Settings")]
    public string gameSceneName = "GameScene";

    /// <summary>开始游戏：加载游戏场景。</summary>
    public void StartGame()
    {
        if (string.IsNullOrEmpty(gameSceneName))
        {
            Debug.LogWarning("[StartMenu] gameSceneName 为空，无法进入游戏场景。");
            return;
        }

        SceneManager.LoadScene(gameSceneName);
    }

    /// <summary>退出游戏：编辑器里结束播放，打包后退出应用。</summary>
    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
