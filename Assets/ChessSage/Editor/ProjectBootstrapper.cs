#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ChessSage.EditorTools
{
    /// <summary>
    /// 工程引导器：在首次导入 / 菜单触发时，把「手工才能完成的工程配置」全部自动化。
    ///
    /// 存在的理由：URP 资产、.unity 场景、Build Settings 都必须由 Unity 序列化器生成 YAML，
    /// 纯文本无法可靠手写。本脚本把这一步固化为可重复执行的幂等操作，
    /// 使 CI 与新同事 clone 后无需任何手工点击即可得到可构建工程。
    ///
    /// 设计约束：<b>不引用任何 URP / 包程序集</b>。URP 资产类型通过反射解析，
    /// 因此本文件在「只有内置 UnityEngine/UnityEditor 模块」的环境下也能编译通过
    /// （见 tools/verify/compile-check.sh 的 L2 编译门禁）。
    /// </summary>
    public static class ProjectBootstrapper
    {
        public const string SceneDir = "Assets/ChessSage/Presentation/Scenes";
        public const string ScenePath = SceneDir + "/Main.unity";
        public const string OverworldScenePath = SceneDir + "/Overworld.unity";
        public const string UrpAssetDir = "Assets/ChessSage/Presentation/Art/Pipeline";
        public const string UrpAssetPath = UrpAssetDir + "/UniversalRenderPipelineAsset.asset";
        public const string OverlayAssetPath = UrpAssetDir + "/UniversalRenderer2D.asset";

        const string MenuRoot = "ChessSage/工程引导";

        [MenuItem(MenuRoot + "/一键引导（幂等）", priority = 0)]
        public static void BootstrapAll()
        {
            EnsureFolder("Assets/ChessSage/Presentation");
            EnsureFolder(SceneDir);
            EnsureFolder("Assets/ChessSage/Presentation/Art");
            EnsureFolder(UrpAssetDir);

            bool urp = EnsureRenderPipeline();
            bool scene = EnsureStartupScene();
            bool overworld = EnsureOverworldScene();
            EnsureBuildSettings();
            EnsureQualitySettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ChessSage] 工程引导完成：URP={(urp ? "已配置" : "未变动")}，" +
                      $"主场景={(scene ? "已创建" : "已存在")}，大地图场景={(overworld ? "已创建" : "已存在")}");
        }

        // ── URP ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建并挂载 URP 资产。URP 包缺席时降级为 Built-in 管线并给出明确提示，不抛异常。
        /// </summary>
        public static bool EnsureRenderPipeline()
        {
            var pipelineType = FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");
            if (pipelineType == null)
            {
                Debug.LogWarning("[ChessSage] 未安装 com.unity.render-pipelines.universal，" +
                                 "已保留 Built-in 渲染管线。大地图着色器需 URP 就位后生效。");
                return false;
            }

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(UrpAssetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance(pipelineType);
                asset.name = "UniversalRenderPipelineAsset";
                AssetDatabase.CreateAsset(asset, UrpAssetPath);
            }

            TryAssignRenderer(asset);

            // GraphicsSettings.renderPipelineAsset 是静态属性，反射写入避免编译期依赖。
            var gs = typeof(GraphicsSettings);
            var prop = gs.GetProperty("renderPipelineAsset",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (prop != null && prop.CanWrite)
            {
                var current = prop.GetValue(null) as RenderPipelineAsset;
                if (current == null || current != asset)
                {
                    prop.SetValue(null, asset);
                    return true;
                }
            }
            else
            {
                Debug.LogWarning("[ChessSage] GraphicsSettings.renderPipelineAsset 不可写，请手动指定 URP 资产。");
            }
            return false;
        }

        /// <summary>为 URP 资产挂载 2D Renderer（URP 12+ 必配，大地图依赖）。失败不阻断引导。</summary>
        static void TryAssignRenderer(ScriptableObject urpAsset)
        {
            var rendererType = FindType("UnityEngine.Rendering.Universal.UniversalRendererData");
            if (rendererType == null) return;

            var dataType = FindType("UnityEngine.Rendering.Universal.UniversalRendererData");
            if (dataType == null) dataType = rendererType;

            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(OverlayAssetPath);
            ScriptableObject rendererData = existing;
            if (rendererData == null)
            {
                try
                {
                    rendererData = ScriptableObject.CreateInstance(dataType);
                    rendererData.name = "UniversalRenderer2D";
                    AssetDatabase.CreateAsset(rendererData, OverlayAssetPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ChessSage] 创建 2D Renderer 失败（可忽略）：{e.Message}");
                    return;
                }
            }

            // rendererDataList 是 List<ScriptableRendererData> 类型的私有字段。
            var f = urpAsset.GetType().GetField("m_RendererDataList",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var list = f?.GetValue(urpAsset) as System.Collections.IList;
            if (f == null || list == null) return;

            bool already = list.Cast<object>().Any(o => o == rendererData);
            if (already) return;

            var add = f.FieldType.GetMethod("Add");
            add?.Invoke(list, new[] { rendererData });
        }

        // ── 启动场景 ──────────────────────────────────────────────────────────

        /// <summary>创建启动场景（含 Bootstrap 宿主对象），已存在则原样保留，绝不覆盖用户改动。</summary>
        public static bool EnsureStartupScene()
        {
            if (File.Exists(ScenePath)) return false;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var go = new GameObject("ChessSage");
            try
            {
                var bootstrapType = FindType("ChessSage.Presentation.Bootstrap");
                if (bootstrapType != null)
                {
                    go.AddComponent(bootstrapType);
                }
                else
                {
                    Debug.LogWarning("[ChessSage] 未找到 ChessSage.Presentation.Bootstrap，场景将只有空宿主对象。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ChessSage] 挂载 Bootstrap 失败（可忽略）：{e.Message}");
            }

            // 2D 正交相机：大地图与棋盘共用，默认分辨率 1920x1080。
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 5.4f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.06f, 0.08f, 1f);
            camGo.AddComponent<AudioListener>();
            cam.transform.position = new Vector3(0f, 0f, -10f);

            Directory.CreateDirectory(SceneDir);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[ChessSage] 已生成启动场景：{ScenePath}");
            return true;
        }

        /// <summary>
        /// 创建六道大陆大地图场景：正交相机 + <c>OverworldBootstrap</c>宿主对象。
        /// 大地图的所有子系统都由该组件在 Awake 里自建，因此场景本身只需一个空壳。
        /// </summary>
        public static bool EnsureOverworldScene()
        {
            if (File.Exists(OverworldScenePath)) return false;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 8f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.10f, 0.16f, 1f);
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(0f, 0f, -30f);

            var host = new GameObject("Overworld");
            var type = FindType("ChessSage.Presentation.Overworld.OverworldBootstrap");
            if (type != null) host.AddComponent(type);
            else Debug.LogWarning("[ChessSage] 未找到 OverworldBootstrap，大地图场景将缺少引导组件。");

            Directory.CreateDirectory(SceneDir);
            EditorSceneManager.SaveScene(scene, OverworldScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[ChessSage] 已生成大地图场景：{OverworldScenePath}");
            return true;
        }

        // ── Build Settings / Quality ───────────────────────────────────────────

        public static void EnsureBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(s => s.path == ScenePath))
                scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            if (File.Exists(OverworldScenePath) && !scenes.Exists(s => s.path == OverworldScenePath))
                scenes.Insert(1, new EditorBuildSettingsScene(OverworldScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>关闭 VSync 之外的质量开关，保证低端机与批处理构建帧率稳定。</summary>
        public static void EnsureQualitySettings()
        {
            try
            {
                QualitySettings.vSyncCount = 0;
                QualitySettings.antiAliasing = 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ChessSage] QualitySettings 设置失败（可忽略）：{e.Message}");
            }
        }

        // ── 工具 ──────────────────────────────────────────────────────────────

        static Type FindType(string fullName)
        {
            var direct = Type.GetType(fullName, false);
            if (direct != null) return direct;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>逐级创建 Asset 目录（AssetDatabase 无 mkdir，需自建）。</summary>
        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            var parts = assetFolder.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
#endif