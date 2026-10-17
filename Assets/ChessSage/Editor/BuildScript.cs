#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ChessSage.Build
{
    /// <summary>
    /// 无 GUI 环境下的构建入口（供 tools/build.sh 通过 -executeMethod 调用）。
    /// 负责补齐启动场景并加入 Build Settings，再构建对应平台播放器。
    /// </summary>
    public static class BuildScript
    {
        const string SceneDir = "Assets/ChessSage/Presentation/Scenes";
        const string ScenePath = SceneDir + "/Main.unity";
        const string OverworldScenePath = SceneDir + "/Overworld.unity";

        public static void BuildLinux() => Build(BuildTarget.StandaloneLinux64, "Linux");
        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Windows");

        static void Build(BuildTarget target, string folder)
        {
            int exitCode = 0;
            try
            {
                EnsureStartupScene();

                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                var outRoot = Environment.GetEnvironmentVariable("OUT");
                if (string.IsNullOrEmpty(outRoot)) outRoot = Path.Combine(projectRoot, "Builds");
                var outDir = Path.Combine(outRoot, folder);
                Directory.CreateDirectory(outDir);

                string ext = target == BuildTarget.StandaloneWindows64 ? ".exe" : ".x86_64";
                var options = new BuildPlayerOptions
                {
                    // P2-11：构建场景 = Main（启动场景，index 0）+ Overworld（六道大陆）。
                    // 此前只打 Main 一场景，发布包里大地图是死内容，hub 的入口按钮
                    // 点下去只会黑屏——两场景必须同时进包。
                    scenes = BuildScenes(),
                    locationPathName = Path.Combine(outDir, "ChessSage" + ext),
                    target = target,
                    options = BuildOptions.None,
                };

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError($"构建失败：{summary.result}（{summary.totalErrors} 个错误）");
                    exitCode = 1;
                }
                else
                {
                    Debug.Log($"构建成功：{options.locationPathName}（{summary.totalSize} 字节）");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"构建异常：{e}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>启动场景缺失时创建空场景并写入 Build Settings，保证 batchmode 构建不因「无场景」失败。</summary>
        static void EnsureStartupScene()
        {
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory(SceneDir);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.Refresh();
            }

            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(s => s.path == ScenePath))
            {
                scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
        }

        /// <summary>
        /// 构建场景列表：真源为 EditorBuildSettings 里 enabled 的场景，外加
        /// Overworld（若磁盘存在但未登记则补入）。Main 强制置顶（index 0）——
        /// 发布包的启动场景必须是主界面，否则进游戏直接播大地图。
        /// </summary>
        static string[] BuildScenes()
        {
            var paths = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled && !string.IsNullOrEmpty(s.path) && !paths.Contains(s.path))
                    paths.Add(s.path);

            if (File.Exists(OverworldScenePath) && !paths.Contains(OverworldScenePath))
                paths.Add(OverworldScenePath);

            paths.Remove(ScenePath);
            paths.Insert(0, ScenePath);   // Main 恒为启动场景（index 0）
            return paths.ToArray();
        }
    }
}
#endif