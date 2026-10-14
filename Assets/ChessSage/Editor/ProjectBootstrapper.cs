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

        /// <summary>URP 包内资源根路径（ResourceReloader.ReloadAllNullIn 的 basePath）。</summary>
        const string UrpPackagePath = "Packages/com.unity.render-pipelines.universal";

        /// <summary>
        /// 创建并挂载 URP 资产。URP 包缺席时降级为 Built-in 管线并给出明确提示，不抛异常。
        ///
        /// <para><b>D-04 缺陷修复</b>：旧实现用 <c>ScriptableObject.CreateInstance</c> 裸建 URP 资产，
        /// 再往 <c>m_RendererDataList</c> 里 <c>Add</c>——但该字段是 <c>ScriptableRendererData[]</c>
        /// 数组（非 List），数组没有 <c>Add</c> 方法，反射拿到的 <c>add</c> 为 null，<c>?.Invoke</c>
        /// 静默跳过：renderer 从未挂上；同时被 CreateInstance 出来的 renderer data 内所有 shader /
        /// postProcessData 引用全为 null。运行期 <c>UniversalRenderPipelineAsset.CreatePipeline()</c>
        /// 因此抛 <see cref="NullReferenceException"/>，相机无内容可渲染 → 试玩截图纯色。</para>
        ///
        /// <para>现在：① renderer 用 URP 官方路径创建（反射调用 <c>CreateRendererAsset</c>，内部
        /// <c>CreateInstance</c> + 设 postProcessData + <c>CreateAsset</c> + <c>ResourceReloader.ReloadAllNullIn</c>）；
        /// ② 对已存在的空壳 / 类型错配资产做检测并重建（幂等自愈）；③ 全流程仅反射，不引用 URP 程序集。</para>
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

            // 2D 工程（sprite / tilemap / 等距地图）应使用 URP 的 2D Renderer（Renderer2DData）。
            // 历史资产名虽为 UniversalRenderer2D、类型却是 3D 前向 UniversalRendererData，属类型错配。
            // 找不到 Renderer2DData 时退回 3D 前向 renderer，保证引导不中断。
            var renderer2DType = FindType("UnityEngine.Rendering.Universal.Renderer2DData");
            var rendererType = renderer2DType ?? FindType("UnityEngine.Rendering.Universal.UniversalRendererData");
            if (rendererType == null)
            {
                Debug.LogWarning("[ChessSage] 未找到 URP Renderer 数据类型，跳过 renderer 挂载。");
                return false;
            }

            EnsureFolder(UrpAssetDir);
            bool changed = false;

            // ① renderer data：缺失 / 类型错配 / 资源引用为空壳 → 用官方路径重建（自愈）
            var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableObject>(OverlayAssetPath);
            bool rendererBad =
                rendererData == null ||
                rendererData.GetType() != rendererType ||
                HasNullReloadField(rendererData) ||
                GetMember(rendererData, "m_PostProcessData", "postProcessData") == null;

            if (rendererBad)
            {
                if (rendererData != null) AssetDatabase.DeleteAsset(OverlayAssetPath);
                rendererData = BuildRendererData(pipelineType, renderer2DType, rendererType);
                if (rendererData == null)
                {
                    Debug.LogWarning("[ChessSage] URP renderer data 创建失败，保留 Built-in 管线。");
                    return false;
                }
                changed = true;
            }

            // ② pipeline asset：缺失 / 类型错配则新建；已存在则「原地补齐」以保留 GUID——
            //    ProjectSettings/GraphicsSettings.asset 通过 GUID 引用本资产，删档重建会断开引用。
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(UrpAssetPath);
            if (asset == null || asset.GetType() != pipelineType)
            {
                if (asset != null) AssetDatabase.DeleteAsset(UrpAssetPath);
                asset = ScriptableObject.CreateInstance(pipelineType);
                asset.name = "UniversalRenderPipelineAsset";
                AssetDatabase.CreateAsset(asset, UrpAssetPath);
                changed = true;
            }

            // ③ 补齐 URP 资产与 renderer 的资源引用（ReloadAllNullIn 幂等：只填 null）
            if (ReloadNullResources(asset)) { EditorUtility.SetDirty(asset); changed = true; }
            if (ReloadNullResources(rendererData)) { EditorUtility.SetDirty(rendererData); changed = true; }

            // ④ 把 renderer 写进 m_RendererDataList[0]（数组下标赋值——旧代码用 List.Add 静默失败）
            if (SetRendererAt(asset, 0, rendererData))
            {
                EditorUtility.SetDirty(asset);
                changed = true;
            }

            // ⑤ 挂到 GraphicsSettings（静态属性，反射写入避免编译期依赖）
            changed |= AssignGraphicsPipeline(asset);

            return changed;
        }

        /// <summary>
        /// 用 URP 官方路径创建 renderer data（含资源填充）；官方 API 不可用时退化为
        /// 「CreateInstance + 手工补 postProcessData + ReloadAllNullIn」。返回 null 表示彻底失败。
        /// </summary>
        static ScriptableObject BuildRendererData(Type pipelineType, Type renderer2DType, Type rendererType)
        {
            try
            {
                var enumType = pipelineType.GetNestedType("RendererType",
                    BindingFlags.Public | BindingFlags.NonPublic);
                var createRendererAsset = pipelineType.GetMethod("CreateRendererAsset",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (enumType != null && createRendererAsset != null)
                {
                    // relativePath:false → 直接写到 OverlayAssetPath。
                    var kind = Enum.Parse(enumType, renderer2DType != null ? "_2DRenderer" : "UniversalRenderer");
                    var created = createRendererAsset.Invoke(null,
                        new object[] { OverlayAssetPath, kind, false, "Renderer" }) as ScriptableObject;
                    if (created != null) return created;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ChessSage] URP 官方 CreateRendererAsset 调用失败，改用兜底创建：{e.Message}");
            }

            // 兜底：裸建 + 手工补 postProcessData + ReloadAllNullIn（URP 包结构变化时的保险）
            var data = ScriptableObject.CreateInstance(rendererType);
            if (data == null) return null;
            data.name = "UniversalRenderer2D";
            SetMemberIfNull(data, "m_PostProcessData", "postProcessData", LoadDefaultPostProcessData());
            AssetDatabase.CreateAsset(data, OverlayAssetPath);
            ReloadNullResources(data);
            return data;
        }

        /// <summary>加载 URP 包内的默认 PostProcessData 资产（反射，避免编译期依赖 URP 程序集）。</summary>
        static UnityEngine.Object LoadDefaultPostProcessData()
        {
            var ppType = FindType("UnityEngine.Rendering.Universal.PostProcessData");
            if (ppType == null) return null;
            var byPath = AssetDatabase.LoadAssetAtPath(UrpPackagePath + "/Runtime/Data/PostProcessData.asset", ppType);
            if (byPath != null) return byPath;
            var getter = ppType.GetMethod("GetDefaultPostProcessData",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return getter?.Invoke(null, null) as UnityEngine.Object;
        }

        /// <summary>
        /// 反射调用 <c>UnityEngine.Rendering.ResourceReloader.ReloadAllNullIn(asset, packagePath)</c>，
        /// 把资产里所有带 <c>[Reload]</c> / <c>[ReloadGroup]</c> 标记的空引用从 URP 包内补全。
        /// 返回 true 表示确有字段被填充（即原资产原本是空壳）。
        /// </summary>
        static bool ReloadNullResources(ScriptableObject asset)
        {
            if (asset == null) return false;
            var reloaderType = FindType("UnityEngine.Rendering.ResourceReloader");
            var method = reloaderType?.GetMethod("ReloadAllNullIn",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Debug.LogWarning("[ChessSage] 未找到 ResourceReloader.ReloadAllNullIn，URP 资源引用可能仍为空。");
                return false;
            }
            try
            {
                return method.Invoke(null, new object[] { asset, UrpPackagePath }) is bool b && b;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ChessSage] ReloadAllNullIn 调用失败：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 递归扫描对象中所有带 <c>[Reload]</c> 标记的资源字段（含 <c>[ReloadGroup]</c> 子容器），
        /// 判断是否存在 null 引用。用于识别「CreateInstance 裸建出来的空壳资产」。
        /// 纯反射（按特性名匹配），不引用 URP / core 程序集。
        /// </summary>
        static bool HasNullReloadField(object container, int depth = 0)
        {
            if (container == null || depth > 8) return false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in container.GetType().GetFields(flags))
            {
                bool isReload = false, isGroup = false;
                foreach (var attr in field.GetCustomAttributes(false))
                {
                    var name = attr.GetType().Name;
                    if (name == "ReloadAttribute") isReload = true;
                    else if (name == "ReloadGroupAttribute") isGroup = true;
                }
                if (!isGroup && field.FieldType != null)
                    foreach (var attr in field.FieldType.GetCustomAttributes(false))
                        if (attr.GetType().Name == "ReloadGroupAttribute") { isGroup = true; break; }

                if (isGroup)
                {
                    var child = field.GetValue(container);
                    if (child != null && HasNullReloadField(child, depth + 1)) return true;
                }
                else if (isReload && typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                {
                    if (field.GetValue(container) == null) return true;
                }
            }
            return false;
        }

        /// <summary>把 renderer 写入 <c>m_RendererDataList</c> 的第 index 项（该字段是数组，非 List）。</summary>
        static bool SetRendererAt(ScriptableObject urpAsset, int index, ScriptableObject renderer)
        {
            var field = urpAsset.GetType().GetField("m_RendererDataList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || renderer == null) return false;

            var elementType = field.FieldType.GetElementType() ?? renderer.GetType();
            var array = field.GetValue(urpAsset) as Array;
            if (array == null || array.Length <= index)
            {
                var grown = Array.CreateInstance(elementType, Math.Max(index + 1, 1));
                if (array != null) Array.Copy(array, grown, array.Length);
                field.SetValue(urpAsset, grown);
                array = grown;
            }
            if (ReferenceEquals(array.GetValue(index), renderer)) return false;
            array.SetValue(renderer, index);
            return true;
        }

        /// <summary>把 URP 资产挂到 GraphicsSettings.renderPipelineAsset（反射，静态属性）。</summary>
        static bool AssignGraphicsPipeline(ScriptableObject asset)
        {
            var prop = typeof(GraphicsSettings).GetProperty("renderPipelineAsset",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (prop == null || !prop.CanWrite)
            {
                Debug.LogWarning("[ChessSage] GraphicsSettings.renderPipelineAsset 不可写，请手动指定 URP 资产。");
                return false;
            }
            if (ReferenceEquals(prop.GetValue(null), asset)) return false;
            prop.SetValue(null, asset);
            return true;
        }

        // ── 反射小工具 ──────────────────────────────────────────────────────────

        /// <summary>按字段名（优先）或属性名读取成员值；不存在返回 null。</summary>
        static object GetMember(object target, string fieldName, string propName)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = target.GetType().GetField(fieldName, flags);
            if (field != null) return field.GetValue(target);
            return target.GetType().GetProperty(propName, flags)?.GetValue(target);
        }

        /// <summary>成员为 null 时写入 value（字段优先，其次可写属性）。</summary>
        static void SetMemberIfNull(object target, string fieldName, string propName, object value)
        {
            if (target == null || value == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = target.GetType().GetField(fieldName, flags);
            if (field != null && field.GetValue(target) == null) { field.SetValue(target, value); return; }
            var prop = target.GetType().GetProperty(propName, flags);
            if (prop != null && prop.CanWrite && prop.GetValue(target) == null) prop.SetValue(target, value);
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