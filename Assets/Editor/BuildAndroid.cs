using UnityEditor;
using UnityEngine;

namespace VRSim.EditorTools
{
    /// <summary>
    /// 一键打包 Android APK（PICO 侧载用）。
    /// 菜单：Tools/Build Android APK
    /// 输出：Builds/VRSim.apk
    /// </summary>
    public static class BuildAndroid
    {
        const string ScenePath = "Assets/Scenes/Test.unity";
        const string OutputPath = "Builds/VRSim.apk";

        [MenuItem("Tools/Build Android APK")]
        public static void BuildApk()
        {
            // 体验版用 Unity 自带 debug keystore 签名，不需要正式证书
            PlayerSettings.Android.useCustomKeystore = false;

            // 包名：原来用的是 Unity 模板默认值（com.UnityTechnologies.com.unity.template.urpblank），
            // 非规范且含多级 com.，部分安装器会拒绝，改成本项目自己的包名
            PlayerSettings.SetApplicationIdentifier(
                UnityEditor.Build.NamedBuildTarget.Android, "com.vrsim.robotvr");

            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;

            AssetDatabase.SaveAssets();

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };

            Debug.Log("[BuildAndroid] 开始打包 -> " + OutputPath);
            var report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;

            if (s.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                Debug.Log(string.Format("[BuildAndroid] 成功 大小={0:F1}MB 耗时={1:F0}s 输出={2}",
                    s.totalSize / 1048576f, s.totalTime.TotalSeconds, OutputPath));
            else
                Debug.LogError(string.Format("[BuildAndroid] 失败 result={0} errors={1} 输出={2}",
                    s.result, s.totalErrors, OutputPath));
        }

        [MenuItem("Tools/Disable Custom Keystore")]
        public static void DisableCustomKeystore()
        {
            PlayerSettings.Android.useCustomKeystore = false;
            AssetDatabase.SaveAssets();
            Debug.Log("[BuildAndroid] useCustomKeystore = " + PlayerSettings.Android.useCustomKeystore);
        }
    }
}
