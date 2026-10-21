using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Playables;
using AnimatorController = UnityEditor.Animations.AnimatorController;
using AnimatorControllerLayer = UnityEditor.Animations.AnimatorControllerLayer;


    public sealed class ArtResourceSheetExporterGitHub : EditorWindow
    {
        private const string EditionLabel = "v0.4.1";
        private const string LanguagePreferenceKey = "ArtResourceSheetExporterGitHub.UiEnglish";
        private static bool uiEnglish;
        private static string[] cachedCategoryNames;
        private static string[] cachedCategoryDisplayNames;
        private bool legacyCategoryUpgradePendingSave;
        private static System.Reflection.MethodInfo assetGuidLocalIdMethod;
        private static bool assetGuidLocalIdMethodResolved;

        private static string L(string chinese, string english)
        {
            return uiEnglish ? english : chinese;
        }
        [Serializable]
        private sealed class ResourceRow
        {
            public UnityEngine.Object asset;
            public string type;
            public string gameCategory;
            public string displayName;
            public string outputName;
            public string description;
            public Texture2D referenceImage;
            public float effectPreviewTime = 1f;
            [NonSerialized] public Texture2D generatedEffectPreview;
            [NonSerialized] public bool selected;
            public bool enabled = true;
        }

        private sealed class ExportImage
        {
            public byte[] bytes;
            public int width;
            public int height;
        }

        private sealed class ImagePreviewWindow : EditorWindow
        {
            private static ImagePreviewWindow activeWindow;
            private Texture previewTexture;
            private string previewName;

            public static void Open(Texture texture, string displayName)
            {
                if (texture == null) return;
                ImagePreviewWindow window = activeWindow;
                if (window == null)
                    window = Resources.FindObjectsOfTypeAll<ImagePreviewWindow>().FirstOrDefault();
                bool isNewWindow = window == null;
                if (isNewWindow) window = CreateInstance<ImagePreviewWindow>();
                activeWindow = window;
                window.previewTexture = texture;
                window.previewName = string.IsNullOrEmpty(displayName) ? L("资源预览", "Asset Preview") : displayName;
                window.titleContent = new GUIContent(L("预览 - ", "Preview - ") + window.previewName);
                window.minSize = new Vector2(360f, 360f);
                if (isNewWindow)
                    window.position = new Rect(
                        Mathf.Max(0f, (Screen.currentResolution.width - 720f) * 0.5f),
                        Mathf.Max(0f, (Screen.currentResolution.height - 720f) * 0.5f),
                        720f,
                        720f);
                // 普通独立窗口不会在点击主工具或其他 Unity 面板时自动关闭。
                window.Show();
                window.Focus();
                window.Repaint();
            }

            private void OnDisable()
            {
                if (activeWindow == this) activeWindow = null;
            }

            private void OnGUI()
            {
                if (previewTexture == null)
                {
                    EditorGUILayout.HelpBox(L("预览图已刷新或释放，请关闭窗口后重新点击。", "The preview was refreshed or released. Close this window and open it again."), MessageType.Info);
                    return;
                }

                EditorGUILayout.LabelField(previewName + "    " + previewTexture.width + " × " + previewTexture.height, EditorStyles.boldLabel);
                Rect previewRect = GUILayoutUtility.GetRect(1f, 10000f, 1f, 10000f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                DrawCheckerboard(previewRect);
                GUI.DrawTexture(previewRect, previewTexture, ScaleMode.ScaleToFit, true);
            }

            private static void DrawCheckerboard(Rect rect)
            {
                EditorGUI.DrawRect(rect, new Color(0.22f, 0.22f, 0.22f));
                const float size = 16f;
                Color light = new Color(0.34f, 0.34f, 0.34f);
                int columns = Mathf.CeilToInt(rect.width / size);
                int rows = Mathf.CeilToInt(rect.height / size);
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < columns; x++)
                        if ((x + y) % 2 == 0)
                            EditorGUI.DrawRect(new Rect(rect.x + x * size, rect.y + y * size, Mathf.Min(size, rect.xMax - rect.x - x * size), Mathf.Min(size, rect.yMax - rect.y - y * size)), light);
            }
        }

        [Serializable]
        private sealed class SavedOrderData
        {
            public List<string> assetKeys = new List<string>();
        }

        [Serializable]
        private sealed class CategoryDefinition
        {
            public string name;
            public string folderName;
            public string prefix;
        }

        [Serializable]
        private sealed class AnimationNameRule
        {
            public string keyword;
            public string displayName;
        }

        private sealed class ControllerGraphNode
        {
            public UnityEditor.Animations.AnimatorState state;
            public string label;
            public Vector2 sourcePosition;
            public Color color;
        }

        [Serializable]
        private sealed class SavedRowConfiguration
        {
            public string assetKey;
            public string assetPath;
            public bool enabled;
            public string type;
            public string gameCategory;
            public string displayName;
            public string outputName;
            public string description;
            public float effectPreviewTime;
            public string referenceImagePath;
        }

        [Serializable]
        private sealed class SavedConfiguration
        {
            public string sectionName;
            public bool includeSectionRow;
            public bool enableGameClassification;
            public bool autoDetectCategoryByPrefix;
            public float defaultEffectPreviewTime;
            public List<CategoryDefinition> categories = new List<CategoryDefinition>();
            public List<AnimationNameRule> animationNameRules = new List<AnimationNameRule>();
            public List<SavedRowConfiguration> rows = new List<SavedRowConfiguration>();
        }

        [SerializeField] private List<ResourceRow> rows = new List<ResourceRow>();
        private ReorderableList resourceList;
        private ReorderableList categoryList;
        private ReorderableList animationRuleList;
        private Vector2 scroll;
        [SerializeField] private string sectionName = "游戏美术资源";
        [SerializeField] private bool includeSectionRow = true;
        [SerializeField] private bool enableGameClassification = true;
        [SerializeField] private bool autoDetectCategoryByPrefix = true;
        [SerializeField] private List<CategoryDefinition> categoryDefinitions = new List<CategoryDefinition>();
        [SerializeField] private List<AnimationNameRule> animationNameRules = new List<AnimationNameRule>();
        [SerializeField] private float defaultEffectPreviewTime = 1f;
        private string batchType = string.Empty;
        private string batchDisplayName = string.Empty;
        private string batchDescription = string.Empty;
        private float batchEffectPreviewTime = 1f;
        private int batchCategoryIndex;
        private int selectionAnchor = -1;
        private int manualCategoryIndex;
        [SerializeField] private int settingsTab;
        [SerializeField] private bool showRecognitionSettings;
        [SerializeField] private bool showBatchEdit = true;
        [SerializeField] private List<UnityEngine.Object> scanSources = new List<UnityEngine.Object>();
        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle sectionTitleStyle;
        private GUIStyle dropAreaStyle;
        private ResourceRow pendingAnimatorScreenshotRow;
        private EditorWindow pendingAnimatorWindow;
        private static EditorWindow animatorCaptureWindow;
        // Spine-Unity 是可选插件：这里只保存 Type，不在编译期引用 Spine.Unity 类型。
        private static Type spineSkeletonDataAssetType;
        private static Type spineSkeletonAnimationType;
        private static bool spineTypesResolved;
        private int pendingAnimatorScreenshotStage;
        private double pendingAnimatorScreenshotTime;
        private double pendingAnimatorScreenshotDeadline;
        private readonly Queue<ResourceRow> animatorScreenshotQueue = new Queue<ResourceRow>();
        private int animatorScreenshotTotal;
        private int animatorScreenshotCompleted;
        private int animatorScreenshotFailed;
        private string animatorScreenshotStatus = string.Empty;

        [MenuItem("Tools/美术资源表导出工具 v0.4.1")]
        [MenuItem("Tools/Art Resource Sheet Exporter v0.4.1")]
        private static void Open()
        {
            ArtResourceSheetExporterGitHub window = GetWindow<ArtResourceSheetExporterGitHub>();
            window.minSize = new Vector2(1240, 680);
            window.UpdateWindowTitle();
        }

        private void UpdateWindowTitle()
        {
            titleContent = new GUIContent(L("美术资源表 ", "Art Resource Sheet ") + EditionLabel);
        }

        private void OnEnable()
        {
            uiEnglish = EditorPrefs.GetBool(LanguagePreferenceKey, false);
            UpdateWindowTitle();
            minSize = new Vector2(1240, 680);
            if (rows == null) rows = new List<ResourceRow>();
            if (scanSources == null) scanSources = new List<UnityEngine.Object>();
            EnsureCategoryDefinitions();
            if (ReplaceExactLegacyCategoryPreset())
            {
                legacyCategoryUpgradePendingSave = true;
                ReclassifyRowsWithCurrentRules();
                ShowNotification(new GUIContent(L("旧版默认分类已升级为通用预设；自定义规则未改动", "Old default categories upgraded; custom rules were kept")));
            }
            EnsureAnimationNameRules();
            InitializeResourceList();
            InitializeCategoryList();
            InitializeAnimationRuleList();
            Undo.undoRedoPerformed += HandleUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= HandleUndoRedo;
            EditorApplication.update -= UpdateAnimatorScreenshot;
            EditorApplication.delayCall -= BeginAnimatorWindowScreenshot;
            EditorApplication.delayCall -= StartNextAnimatorWindowScreenshot;
            animatorScreenshotQueue.Clear();
            pendingAnimatorScreenshotRow = null;
            if (animatorCaptureWindow != null) animatorCaptureWindow.Close();
            animatorCaptureWindow = null;
            ClearGeneratedEffectPreviews();
        }

        private void OnGUI()
        {
            EnsureStyles();
            RecordUndoForCurrentInput();
            GUILayout.Space(6);
            DrawTitleArea();
            GUILayout.Space(6);
            DrawMainToolbar();
            DrawDropArea();
            DrawSettingsTabs();
            DrawSortingControls();
            DrawBatchEditControls();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(L("04 / 资源列表", "04 / Assets"), sectionTitleStyle, GUILayout.Width(uiEnglish ? 105 : 100));
                EditorGUILayout.LabelField(L("单击选择 · Shift 连选 · Ctrl 增减选择 · 拖动行调整顺序", "Click to select · Shift range · Ctrl toggle · Drag to reorder"), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(rows.Count + L(" 项", " items"), EditorStyles.miniBoldLabel, GUILayout.Width(75));
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (resourceList == null) InitializeResourceList();
            resourceList.DoLayoutList();
            EditorGUILayout.EndScrollView();

            GUILayout.Space(5);
            using (new EditorGUI.DisabledScope(rows.All(r => !r.enabled)))
            {
                Color previousColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.35f, 0.78f, 0.48f);
                if (GUILayout.Button(L("导出 Excel  ·  ", "Export Excel  ·  ") + rows.Count(row => row.enabled) + L(" 项资源", " assets"), GUILayout.Height(38))) Export();
                GUI.backgroundColor = previousColor;
            }
            GUILayout.Space(6);
        }

        private void DrawSettingsTabs()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                showRecognitionSettings = EditorGUILayout.Foldout(showRecognitionSettings,
                    L("02 / 识别与预览设置", "02 / Recognition & previews"), true);
                if (!showRecognitionSettings) return;
                GUILayout.Space(4);
                settingsTab = GUILayout.Toolbar(Mathf.Clamp(settingsTab, 0, 2), new[]
                {
                    L("游戏分类", "Categories"),
                    L("动画名称规则", "Animation rules"),
                    L("Animator/特效截图", "Animator / FX previews")
                }, GUILayout.Height(26));
            }

            switch (settingsTab)
            {
                case 0: DrawClassificationSettings(); break;
                case 1: DrawAnimationRuleSettings(); break;
                default: DrawEffectPreviewSettings(); break;
            }
        }

        private void RecordUndoForCurrentInput()
        {
            Event currentEvent = Event.current;
            if (currentEvent == null) return;
            bool undoShortcut = currentEvent.type == EventType.KeyDown &&
                                (currentEvent.control || currentEvent.command) &&
                                (currentEvent.keyCode == KeyCode.Z || currentEvent.keyCode == KeyCode.Y);
            if (undoShortcut) return;
            if (currentEvent.type != EventType.MouseDown &&
                currentEvent.type != EventType.MouseUp &&
                currentEvent.type != EventType.MouseDrag &&
                currentEvent.type != EventType.KeyDown) return;
            Undo.RecordObject(this, "编辑美术资源表");
        }

        private void HandleUndoRedo()
        {
            ClearGeneratedEffectPreviews();
            if (rows == null) rows = new List<ResourceRow>();
            if (scanSources == null) scanSources = new List<UnityEngine.Object>();
            EnsureCategoryDefinitions();
            EnsureAnimationNameRules();
            InitializeResourceList();
            InitializeCategoryList();
            InitializeAnimationRuleList();
            Repaint();
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
            titleStyle.normal.textColor = Color.white;
            subtitleStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = 11, wordWrap = true };
            subtitleStyle.normal.textColor = new Color(0.78f, 0.86f, 0.95f);
            sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            dropAreaStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                wordWrap = true
            };
        }

        private void DrawTitleArea()
        {
            Rect rect = GUILayoutUtility.GetRect(0f, 82f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.11f, 0.18f, 0.27f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 5f, rect.height), new Color(0.29f, 0.68f, 0.96f));
            GUI.Label(new Rect(rect.x + 18f, rect.y + 11f, rect.width - 390f, 29f),
                L("美术资源表导出工具", "Art Resource Sheet Exporter"), titleStyle);
            GUI.Label(new Rect(rect.x + 19f, rect.y + 43f, rect.width - 390f, 18f),
                L("资源扫描  /  分类整理  /  预览生成  /  Excel 导出", "Scan  /  Organize  /  Preview  /  Export Excel"), subtitleStyle);

            GUIStyle metaStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
            metaStyle.normal.textColor = new Color(0.85f, 0.92f, 1f);
            GUI.Label(new Rect(rect.xMax - 352f, rect.y + 49f, 334f, 18f),
                L("资源 ", "Assets ") + rows.Count + L("   启用 ", "   Active ") + rows.Count(row => row.enabled) +
                L("   已选 ", "   Selected ") + rows.Count(row => row.selected), metaStyle);
            GUI.Label(new Rect(rect.xMax - 352f, rect.y + 13f, 150f, 23f), EditionLabel, metaStyle);
            int selectedLanguage = GUI.Toolbar(new Rect(rect.xMax - 188f, rect.y + 12f, 170f, 24f),
                uiEnglish ? 1 : 0, new[] { "中文", "English" });
            if ((selectedLanguage == 1) != uiEnglish)
            {
                uiEnglish = selectedLanguage == 1;
                EditorPrefs.SetBool(LanguagePreferenceKey, uiEnglish);
                UpdateWindowTitle();
                Repaint();
            }
        }

        private void DrawMainToolbar()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L("01 / 项目与导入", "01 / Project & import"), sectionTitleStyle);
                using (new EditorGUILayout.HorizontalScope())
                {
                    sectionName = EditorGUILayout.TextField(L("资源分组", "Sheet section"), sectionName);
                    includeSectionRow = EditorGUILayout.ToggleLeft(L("导出分组标题行", "Include section header"), includeSectionRow, GUILayout.Width(uiEnglish ? 160 : 130));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    Color previousColor = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.45f, 0.68f, 0.95f);
                    if (GUILayout.Button(L("＋ 添加当前选择", "+ Add selection"), GUILayout.Width(145), GUILayout.Height(26))) AddObjects(Selection.objects);
                    GUI.backgroundColor = previousColor;
                    if (GUILayout.Button(L("↻ 刷新资源", "↻ Refresh assets"), GUILayout.Width(130), GUILayout.Height(26))) RefreshResources();
                    if (GUILayout.Button(L("清空列表", "Clear list"), GUILayout.Width(90), GUILayout.Height(26)) &&
                        EditorUtility.DisplayDialog(L("清空列表", "Clear list"), L("确定清空当前资源列表？不会删除 Unity 项目中的资源。", "Clear the current list? Assets in the Unity project will not be deleted."), L("清空", "Clear"), L("取消", "Cancel")))
                    {
                        ClearGeneratedEffectPreviews();
                        rows.Clear();
                        scanSources.Clear();
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(L("也可以将资源直接拖入下方区域", "You can also drag assets into the area below"), EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField(IsSpinePluginAvailable()
                    ? L("支持 Texture、特效 Prefab、Spine SkeletonData、FBX 模型/动画、Animator Controller 和文件夹。", "Supports Textures, FX Prefabs, Spine SkeletonData, FBX models/animations, Animator Controllers and folders.")
                    : L("未检测到 Spine-Unity：Spine 功能停用，其他资源仍可正常使用。", "Spine-Unity not detected: Spine features are disabled; other assets remain available."), EditorStyles.miniLabel);
            }
        }

        private void DrawSortingControls()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(L("03 / 整理与保存", "03 / Organize & save"), sectionTitleStyle, GUILayout.Width(uiEnglish ? 150 : 120));
                    if (GUILayout.Button(L("按分类整理", "Sort by category"), GUILayout.Width(140))) SortRowsByCategory();
                    if (GUILayout.Button(L("保存全部配置", "Save configuration"), GUILayout.Width(145))) SaveCurrentConfiguration();
                    if (GUILayout.Button(L("恢复全部配置", "Restore configuration"), GUILayout.Width(155))) ApplySavedConfiguration(true);
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Label(L("保存排序、名称、类型、分类、说明、启用状态、特效时间和手动参考图。", "Saves order, names, types, categories, notes, enabled state, FX time and manual images."), EditorStyles.miniLabel);
            }
        }

        private void DrawEffectPreviewSettings()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(L("特效 Prefab", "FX Prefabs"), sectionTitleStyle, GUILayout.Width(95));
                    defaultEffectPreviewTime = Mathf.Max(0f, EditorGUILayout.FloatField(L("默认时间（秒）", "Default time (s)"), defaultEffectPreviewTime, GUILayout.Width(uiEnglish ? 205 : 180)));
                    if (GUILayout.Button(L("应用时间到全部", "Apply time to all"), GUILayout.Width(135)))
                    {
                        foreach (ResourceRow row in rows.Where(row => IsEffectPrefab(row.asset)))
                        {
                            row.effectPreviewTime = defaultEffectPreviewTime;
                            DestroyGeneratedPreview(row);
                        }
                        Repaint();
                    }
                    if (GUILayout.Button(L("刷新全部特效预览", "Refresh all FX previews"), GUILayout.Width(165)))
                    {
                        try
                        {
                            List<ResourceRow> effectRows = rows.Where(row => IsEffectPrefab(row.asset)).ToList();
                            for (int i = 0; i < effectRows.Count; i++)
                            {
                                EditorUtility.DisplayProgressBar(L("生成特效预览", "Generating FX previews"), effectRows[i].outputName, (i + 1f) / Mathf.Max(1, effectRows.Count));
                                RefreshEffectPreview(effectRows[i]);
                            }
                        }
                        finally
                        {
                            EditorUtility.ClearProgressBar();
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Animator", sectionTitleStyle, GUILayout.Width(90));
                    if (GUILayout.Button(L("一键绘图", "Draw all"), GUILayout.Width(110))) RefreshAllAnimatorControllerPreviews();
                    if (GUILayout.Button(L("一键截图", "Capture all"), GUILayout.Width(110))) StartAllAnimatorWindowScreenshots();
                    if (pendingAnimatorScreenshotRow != null && GUILayout.Button(L("取消截图", "Cancel capture"), GUILayout.Width(110))) CancelAnimatorScreenshots();
                    int controllerCount = rows.Count(item => item.enabled && item.asset is AnimatorController);
                    string animatorStatus = pendingAnimatorScreenshotRow != null
                        ? L("正在截图 ", "Capturing ") + (animatorScreenshotCompleted + animatorScreenshotFailed + 1) + "/" + Mathf.Max(1, animatorScreenshotTotal)
                        : string.IsNullOrEmpty(animatorScreenshotStatus) ? L("已识别 ", "Detected ") + controllerCount + L(" 个已启用的 Animator Controller", " enabled Animator Controllers") : animatorScreenshotStatus;
                    GUILayout.Label(animatorStatus, EditorStyles.miniLabel);
                }
                GUILayout.Label(L("单行可调整特效截图时间；点击缩略图放大查看，手动参考图优先于自动预览。", "Adjust FX capture time per row. Click thumbnails to enlarge; manual images take priority over automatic previews."), EditorStyles.miniLabel);
            }
        }

        private void DrawBatchEditControls()
        {
            int selectedCount = rows.Count(row => row.selected);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                showBatchEdit = EditorGUILayout.Foldout(showBatchEdit,
                    L("批量编辑 · 已选 ", "Bulk edit · selected ") + selectedCount, true);
                if (!showBatchEdit) return;
                GUILayout.Space(3);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("全选", "Select all"), GUILayout.Width(90))) rows.ForEach(row => row.selected = true);
                    if (GUILayout.Button(L("取消选择", "Deselect"), GUILayout.Width(90))) rows.ForEach(row => row.selected = false);
                    if (GUILayout.Button(L("反选", "Invert"), GUILayout.Width(75))) rows.ForEach(row => row.selected = !row.selected);

                    using (new EditorGUI.DisabledScope(selectedCount == 0))
                    {
                        if (GUILayout.Button(L("启用", "Enable"), GUILayout.Width(80)))
                            foreach (ResourceRow row in rows.Where(row => row.selected)) row.enabled = true;
                        if (GUILayout.Button(L("禁用", "Disable"), GUILayout.Width(80)))
                            foreach (ResourceRow row in rows.Where(row => row.selected)) row.enabled = false;
                        Color previousColor = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.95f, 0.48f, 0.42f);
                        if (GUILayout.Button(new GUIContent(L("移出列表", "Remove rows"), L("只从当前界面移除，不删除项目资源", "Remove from this window only; project assets stay untouched")), GUILayout.Width(105)))
                            RemoveSelectedRows();
                        GUI.backgroundColor = previousColor;
                    }
                    GUILayout.FlexibleSpace();
                }

                if (selectedCount == 0)
                {
                    EditorGUILayout.LabelField(L("请先在资源列表中选择一项；Shift 连选，Ctrl 增减选择。", "Select assets in the list first. Shift selects a range; Ctrl toggles items."), EditorStyles.miniLabel);
                    return;
                }

                EditorGUILayout.LabelField(L("编辑任一已选行会同步到其他已选行；也可以使用下方批量字段。", "Editing one selected row updates the others, or use the bulk fields below."), EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(selectedCount == 0))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        batchType = EditorGUILayout.TextField(L("资源类型", "Asset type"), batchType, GUILayout.Width(225));
                        if (GUILayout.Button(L("应用类型", "Apply type"), GUILayout.Width(95)))
                            foreach (ResourceRow row in rows.Where(row => row.selected)) row.type = batchType;

                        if (enableGameClassification)
                        {
                            string[] categoryNames = GetCategoryNames();
                            batchCategoryIndex = Mathf.Clamp(batchCategoryIndex, 0, categoryNames.Length - 1);
                            batchCategoryIndex = EditorGUILayout.Popup(L("游戏分类", "Category"), batchCategoryIndex, GetCategoryDisplayNames(categoryNames), GUILayout.Width(205));
                            if (GUILayout.Button(L("应用分类", "Apply category"), GUILayout.Width(110)))
                                foreach (ResourceRow row in rows.Where(row => row.selected)) row.gameCategory = categoryNames[batchCategoryIndex];
                        }

                        batchEffectPreviewTime = Mathf.Max(0f, EditorGUILayout.FloatField(L("特效时间", "FX time"), batchEffectPreviewTime, GUILayout.Width(165)));
                        if (GUILayout.Button(L("应用时间", "Apply time"), GUILayout.Width(95)))
                        {
                            foreach (ResourceRow row in rows.Where(row => row.selected && IsEffectPrefab(row.asset)))
                            {
                                row.effectPreviewTime = batchEffectPreviewTime;
                                DestroyGeneratedPreview(row);
                            }
                        }
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        batchDisplayName = EditorGUILayout.TextField(L("名称", "Name"), batchDisplayName, GUILayout.Width(300));
                        if (GUILayout.Button(L("应用名称", "Apply name"), GUILayout.Width(105)))
                            foreach (ResourceRow row in rows.Where(row => row.selected)) row.displayName = batchDisplayName;

                        batchDescription = EditorGUILayout.TextField(L("说明", "Notes"), batchDescription);
                        if (GUILayout.Button(L("应用说明", "Apply notes"), GUILayout.Width(105)))
                            foreach (ResourceRow row in rows.Where(row => row.selected)) row.description = batchDescription;
                    }
                }
            }
        }

        private void DrawClassificationSettings()
        {
            GUILayout.Space(3);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L("分类规则", "Category rules"), sectionTitleStyle);
                enableGameClassification = EditorGUILayout.ToggleLeft(L("启用游戏大类分类", "Enable category groups"), enableGameClassification);
                if (!enableGameClassification) return;

                if (categoryList == null) InitializeCategoryList();
                categoryList.DoLayoutList();
                EditorGUILayout.LabelField(L("分类名称会写入 Excel；切换界面语言不会改写名称或已有数据。", "Category names are exported to Excel; changing the UI language does not rewrite names or existing data."), EditorStyles.miniLabel);
                if (legacyCategoryUpgradePendingSave)
                    EditorGUILayout.HelpBox(L("检测到旧版原样分类预设，已切换为通用预设并重新识别资源。确认后请点击“保存全部配置”。", "The untouched old preset was upgraded and assets were reclassified. Review the result, then save the configuration."), MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("载入通用预设", "Load general preset"), GUILayout.Width(155)) &&
                        EditorUtility.DisplayDialog(L("载入通用分类预设", "Load general category preset"),
                            L("这会替换当前分类规则，并按新规则重新识别列表中所有资源的分类；已手动指定的分类也会改变。名称、说明和列表手动顺序不变，但导出时会按新分类重新分组。是否继续？",
                                "This replaces category rules and reclassifies every row, including manually assigned categories. Names, notes and manual row order remain, but export groups may change. Continue?"),
                            L("载入并重新分类", "Load and reclassify"), L("取消", "Cancel")))
                    {
                        Undo.RecordObject(this, "载入通用分类预设");
                        categoryDefinitions = CreateDefaultCategoryDefinitions();
                        foreach (ResourceRow row in rows)
                            row.gameCategory = row.asset == null
                                ? GetFallbackCategoryName()
                                : DetectGameCategory(row.outputName, AssetDatabase.GetAssetPath(row.asset));
                        InitializeCategoryList();
                        ShowNotification(new GUIContent(L("已载入通用预设；点击保存全部配置可持久保存", "General preset loaded. Save configuration to keep it.")));
                    }
                    EditorGUILayout.LabelField(L("自定义规则只在主动点击时替换；旧版原样预设会自动升级。", "Custom rules change only when requested; the untouched old preset upgrades automatically."), EditorStyles.miniLabel);
                }
                autoDetectCategoryByPrefix = EditorGUILayout.ToggleLeft(L("新增资源时按 Textures / Spines 下级文件夹及名称前缀自动识别", "Auto-detect from direct child folders under Textures / Spines, then filename prefixes"), autoDetectCategoryByPrefix);
                if (autoDetectCategoryByPrefix)
                {
                    if (GUILayout.Button(L("按文件夹和当前前缀重新分类已有资源", "Reclassify existing assets"), GUILayout.Width(265)))
                    {
                        foreach (ResourceRow row in rows)
                            row.gameCategory = DetectGameCategory(row.outputName, AssetDatabase.GetAssetPath(row.asset));
                    }
                }
                else
                {
                    string[] categoryNames = GetCategoryNames();
                    manualCategoryIndex = Mathf.Clamp(manualCategoryIndex, 0, categoryNames.Length - 1);
                    manualCategoryIndex = EditorGUILayout.Popup(L("拖入资源时指定为", "Category for dropped assets"), manualCategoryIndex, GetCategoryDisplayNames(categoryNames));
                }
            }
        }

        private void EnsureCategoryDefinitions()
        {
            if (categoryDefinitions != null && categoryDefinitions.Count > 0) return;
            categoryDefinitions = CreateDefaultCategoryDefinitions();
        }

        private static List<CategoryDefinition> CreateDefaultCategoryDefinitions()
        {
            return new List<CategoryDefinition>
            {
                new CategoryDefinition { name = "游戏内", folderName = "Gameplay", prefix = "game_" },
                new CategoryDefinition { name = "主菜单", folderName = "MainMenu", prefix = "menu_" },
                new CategoryDefinition { name = "结算界面", folderName = "Results", prefix = "result_" },
                new CategoryDefinition { name = "加载界面", folderName = "Loading", prefix = "load_" },
                new CategoryDefinition { name = "通用资源", folderName = "Common", prefix = "common_" },
                new CategoryDefinition { name = "未分类", folderName = string.Empty, prefix = string.Empty }
            };
        }

        private bool ReplaceExactLegacyCategoryPreset()
        {
            // 只迁移旧版原样预设；用户改过名称、顺序、文件夹或前缀时一律保留。
            if (categoryDefinitions == null || categoryDefinitions.Count != 5) return false;
            string[] names = { "普通游戏", "免费游戏", "彩金游戏", "加载界面", "未分类" };
            string[] folders = { "NormalGame", "FreeGame", "SmallGame", "Loading", "" };
            string[] prefixes = { "ng_", "fg_", "jp_", "load_", "" };
            for (int i = 0; i < categoryDefinitions.Count; i++)
            {
                CategoryDefinition item = categoryDefinitions[i];
                if (item == null ||
                    !string.Equals(item.name, names[i], StringComparison.Ordinal) ||
                    !string.Equals(item.folderName ?? string.Empty, folders[i], StringComparison.Ordinal) ||
                    !string.Equals(item.prefix ?? string.Empty, prefixes[i], StringComparison.Ordinal))
                    return false;
            }
            categoryDefinitions = CreateDefaultCategoryDefinitions();
            return true;
        }

        private void ReclassifyRowsWithCurrentRules()
        {
            foreach (ResourceRow row in rows)
                row.gameCategory = row.asset == null
                    ? GetFallbackCategoryName()
                    : DetectGameCategory(row.outputName, AssetDatabase.GetAssetPath(row.asset));
        }

        private void InitializeCategoryList()
        {
            EnsureCategoryDefinitions();
            categoryList = new ReorderableList(categoryDefinitions, typeof(CategoryDefinition), true, true, true, true)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 5f,
                drawHeaderCallback = rect =>
                {
                    float nameWidth = rect.width * 0.34f;
                    float folderWidth = rect.width * 0.34f;
                    EditorGUI.LabelField(new Rect(rect.x, rect.y, nameWidth, rect.height), L("分类名称（同时决定导出顺序）", "Category name / export order"));
                    EditorGUI.LabelField(new Rect(rect.x + nameWidth + 4f, rect.y, folderWidth, rect.height), L("Textures / Spines 下级文件夹", "Direct folder under Textures / Spines"));
                    EditorGUI.LabelField(new Rect(rect.x + nameWidth + folderWidth + 8f, rect.y, rect.width - nameWidth - folderWidth - 8f, rect.height), L("名称前缀", "Filename prefix"));
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    if (index < 0 || index >= categoryDefinitions.Count) return;
                    CategoryDefinition definition = categoryDefinitions[index];
                    rect.y += 2f;
                    rect.height = EditorGUIUtility.singleLineHeight;
                    float nameWidth = rect.width * 0.34f;
                    float folderWidth = rect.width * 0.34f;
                    string oldName = definition.name;
                    definition.name = EditorGUI.TextField(new Rect(rect.x, rect.y, nameWidth, rect.height), definition.name);
                    definition.folderName = EditorGUI.TextField(new Rect(rect.x + nameWidth + 4f, rect.y, folderWidth, rect.height), definition.folderName);
                    definition.prefix = EditorGUI.TextField(new Rect(rect.x + nameWidth + folderWidth + 8f, rect.y, rect.width - nameWidth - folderWidth - 8f, rect.height), definition.prefix);
                    if (!string.Equals(oldName, definition.name, StringComparison.Ordinal))
                        foreach (ResourceRow row in rows.Where(row => string.Equals(row.gameCategory, oldName, StringComparison.Ordinal))) row.gameCategory = definition.name;
                },
                onAddCallback = list =>
                {
                    categoryDefinitions.Add(new CategoryDefinition { name = "新分类", folderName = string.Empty, prefix = string.Empty });
                    list.index = categoryDefinitions.Count - 1;
                },
                onRemoveCallback = list =>
                {
                    if (list.index < 0 || list.index >= categoryDefinitions.Count) return;
                    string removedName = categoryDefinitions[list.index].name;
                    categoryDefinitions.RemoveAt(list.index);
                    string fallback = GetFallbackCategoryName();
                    foreach (ResourceRow row in rows.Where(row => string.Equals(row.gameCategory, removedName, StringComparison.Ordinal))) row.gameCategory = fallback;
                },
                onReorderCallback = list => Repaint()
            };
        }

        private string[] GetCategoryNames()
        {
            EnsureCategoryDefinitions();
            string[] names = categoryDefinitions.Select((item, index) => string.IsNullOrWhiteSpace(item.name) ? "分类" + (index + 1) : item.name.Trim()).ToArray();
            return names.Length == 0 ? new[] { "未分类" } : names;
        }

        private static string[] GetCategoryDisplayNames(string[] names)
        {
            if (!uiEnglish) return names;
            if (cachedCategoryNames != null && cachedCategoryNames.SequenceEqual(names))
                return cachedCategoryDisplayNames;
            cachedCategoryNames = (string[])names.Clone();
            cachedCategoryDisplayNames = names.Select(name =>
            {
                switch (name)
                {
                    case "游戏内": return "Gameplay";
                    case "主菜单": return "Main menu";
                    case "结算界面": return "Results";
                    case "加载界面": return "Loading";
                    case "通用资源": return "Common";
                    case "未分类": return "Uncategorized";
                    case "普通游戏": return "Base game";
                    case "免费游戏": return "Free game";
                    case "彩金游戏": return "Jackpot game";
                    default: return name;
                }
            }).ToArray();
            return cachedCategoryDisplayNames;
        }

        private string GetFallbackCategoryName()
        {
            EnsureCategoryDefinitions();
            CategoryDefinition fallback = categoryDefinitions.FirstOrDefault(item => string.IsNullOrEmpty(item.folderName) && string.IsNullOrEmpty(item.prefix));
            return fallback == null || string.IsNullOrWhiteSpace(fallback.name) ? GetCategoryNames().Last() : fallback.name.Trim();
        }

        private void DrawAnimationRuleSettings()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L("Animator Controller 动画名称规则", "Animator Controller name rules"), sectionTitleStyle);
                if (animationRuleList == null) InitializeAnimationRuleList();
                animationRuleList.DoLayoutList();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L("按当前规则刷新控制器说明", "Refresh controller notes"), GUILayout.Width(205)))
                        foreach (ResourceRow row in rows.Where(row => row.asset is AnimatorController))
                            row.description = GetAnimatorControllerDescription(row.asset as AnimatorController);
                    EditorGUILayout.LabelField(L("状态名称包含关键词时显示对应名称；列表顺序也是说明输出顺序。", "State names containing a keyword use its display label; rule order controls note order."), EditorStyles.miniLabel);
                }
            }
        }

        private void EnsureAnimationNameRules()
        {
            if (animationNameRules != null && animationNameRules.Count > 0) return;
            animationNameRules = new List<AnimationNameRule>
            {
                new AnimationNameRule { keyword = "exit", displayName = "退出" },
                new AnimationNameRule { keyword = "start", displayName = "开始" },
                new AnimationNameRule { keyword = "pop", displayName = "出现" }
            };
        }

        private void InitializeAnimationRuleList()
        {
            EnsureAnimationNameRules();
            animationRuleList = new ReorderableList(animationNameRules, typeof(AnimationNameRule), true, true, true, true)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 5f,
                drawHeaderCallback = rect =>
                {
                    float width = (rect.width - 6f) * 0.5f;
                    EditorGUI.LabelField(new Rect(rect.x, rect.y, width, rect.height), L("动画名关键词", "Animation keyword"));
                    EditorGUI.LabelField(new Rect(rect.x + width + 6f, rect.y, width, rect.height), L("说明显示名称", "Display label in notes"));
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    if (index < 0 || index >= animationNameRules.Count) return;
                    AnimationNameRule rule = animationNameRules[index];
                    rect.y += 2f;
                    rect.height = EditorGUIUtility.singleLineHeight;
                    float width = (rect.width - 6f) * 0.5f;
                    rule.keyword = EditorGUI.TextField(new Rect(rect.x, rect.y, width, rect.height), rule.keyword);
                    rule.displayName = EditorGUI.TextField(new Rect(rect.x + width + 6f, rect.y, width, rect.height), rule.displayName);
                },
                onAddCallback = list =>
                {
                    animationNameRules.Add(new AnimationNameRule { keyword = "keyword", displayName = "新动画" });
                    list.index = animationNameRules.Count - 1;
                },
                onRemoveCallback = list =>
                {
                    if (list.index >= 0 && list.index < animationNameRules.Count) animationNameRules.RemoveAt(list.index);
                },
                onReorderCallback = list => Repaint()
            };
        }

        private void DrawDropArea()
        {
            GUILayout.Space(3);
            Rect rect = GUILayoutUtility.GetRect(0, 76, GUILayout.ExpandWidth(true));
            Event evt = Event.current;
            bool hovering = rect.Contains(evt.mousePosition);
            Color background = hovering ? new Color(0.22f, 0.52f, 0.82f, 0.28f) : new Color(0.22f, 0.52f, 0.82f, 0.13f);
            EditorGUI.DrawRect(rect, background);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), new Color(0.32f, 0.65f, 0.95f, 0.65f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), new Color(0.32f, 0.65f, 0.95f, 0.65f));
            GUI.Label(rect, L("↓  拖放资源或文件夹到这里\nTexture · 特效 Prefab · ", "↓  Drop assets or folders here\nTextures · FX Prefabs · ") +
                (IsSpinePluginAvailable() ? "Spine · " : "") + "FBX · Animator Controller", dropAreaStyle);
            if (!rect.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddObjects(DragAndDrop.objectReferences);
            }
            evt.Use();
        }

        private void InitializeResourceList()
        {
            resourceList = new ReorderableList(rows, typeof(ResourceRow), true, true, false, false)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 6f,
                drawHeaderCallback = DrawListHeader,
                drawElementCallback = DrawListElement,
                onReorderCallback = list =>
                {
                    selectionAnchor = list.index;
                    Repaint();
                }
            };
        }

        private void HandleListSelection(int index)
        {
            if (index < 0 || index >= rows.Count) return;
            Event currentEvent = Event.current;
            bool shift = currentEvent != null && currentEvent.shift;
            bool toggle = currentEvent != null && (currentEvent.control || currentEvent.command);

            if (shift && selectionAnchor >= 0 && selectionAnchor < rows.Count)
            {
                int first = Mathf.Min(selectionAnchor, index);
                int last = Mathf.Max(selectionAnchor, index);
                for (int i = 0; i < rows.Count; i++) rows[i].selected = i >= first && i <= last;
            }
            else if (toggle)
            {
                rows[index].selected = !rows[index].selected;
                selectionAnchor = index;
            }
            else
            {
                // 已经多选时，点击其中一行进行编辑不会取消其他选择。
                if (!rows[index].selected || rows.Count(row => row.selected) <= 1)
                    for (int i = 0; i < rows.Count; i++) rows[i].selected = i == index;
                selectionAnchor = index;
            }
            Repaint();
        }

        private void DrawListHeader(Rect rect)
        {
            EditorGUI.DrawRect(new Rect(rect.x - 4f, rect.y - 1f, rect.width + 8f, rect.height + 2f), new Color(0f, 0f, 0f, 0.12f));
            float x = rect.x;
            DrawHeaderLabel(ref x, rect, 34, L("启用", "On"));
            DrawHeaderLabel(ref x, rect, 180, L("资源", "Asset"));
            DrawHeaderLabel(ref x, rect, 70, L("类型", "Type"));
            DrawHeaderLabel(ref x, rect, 128, L("游戏分类", "Category"));
            DrawHeaderLabel(ref x, rect, 140, L("名称", "Name"));
            DrawHeaderLabel(ref x, rect, 165, L("输出名", "Output name"));
            float descriptionWidth = Mathf.Max(120, rect.xMax - x - 193);
            DrawHeaderLabel(ref x, rect, descriptionWidth, L("说明", "Notes"));
            DrawHeaderLabel(ref x, rect, 160, L("特效时间 / 参考图", "FX time / image"));
        }

        private static void DrawHeaderLabel(ref float x, Rect rowRect, float width, string label)
        {
            EditorGUI.LabelField(new Rect(x, rowRect.y, width, rowRect.height), label);
            x += width + 3;
        }

        private void DrawListElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= rows.Count) return;
            ResourceRow row = rows[index];
            Rect selectionRect = rect;
            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && selectionRect.Contains(currentEvent.mousePosition))
            {
                bool modifierSelection = currentEvent.shift || currentEvent.control || currentEvent.command;
                HandleListSelection(index);
                // Shift/Ctrl 点击只用于多选，不继续传递给 ObjectField，避免 Unity 展开资源大预览。
                if (modifierSelection) currentEvent.Use();
            }
            rect.y += 2;
            rect.height = EditorGUIUtility.singleLineHeight;
            float x = rect.x;

            if (index % 2 == 1)
                EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0f, 0f, 0f, 0.045f));
            if (row.selected)
            {
                EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0.20f, 0.48f, 0.82f, 0.28f));
                EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 2f, 3f, rect.height + 4f), new Color(0.25f, 0.65f, 1f));
            }
            EditorGUI.BeginChangeCheck();
            row.enabled = EditorGUI.Toggle(new Rect(x, rect.y, 30, rect.height), row.enabled);
            if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.enabled = row.enabled);
            x += 34;

            EditorGUI.BeginChangeCheck();
            row.asset = EditorGUI.ObjectField(new Rect(x, rect.y, 177, rect.height), row.asset, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck() && row.asset != null) FillDefaults(row, row.asset);
            x += 183;

            EditorGUI.BeginChangeCheck();
            row.type = EditorGUI.TextField(new Rect(x, rect.y, 67, rect.height), row.type);
            if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.type = row.type);
            x += 73;

            if (enableGameClassification)
            {
                string[] categoryNames = GetCategoryNames();
                string currentCategory = NormalizeLegacyCategoryName(string.IsNullOrEmpty(row.gameCategory) ? GetFallbackCategoryName() : row.gameCategory);
                int categoryIndex = Mathf.Max(0, Array.IndexOf(categoryNames, currentCategory));
                EditorGUI.BeginChangeCheck();
                row.gameCategory = categoryNames[EditorGUI.Popup(new Rect(x, rect.y, 125, rect.height), categoryIndex, GetCategoryDisplayNames(categoryNames))];
                if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.gameCategory = row.gameCategory);
            }
            else EditorGUI.LabelField(new Rect(x, rect.y, 125, rect.height), L("不分类", "No group"), EditorStyles.miniLabel);
            x += 131;

            EditorGUI.BeginChangeCheck();
            row.displayName = EditorGUI.TextField(new Rect(x, rect.y, 137, rect.height), row.displayName);
            if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.displayName = row.displayName);
            x += 143;
            EditorGUI.BeginChangeCheck();
            row.outputName = EditorGUI.TextField(new Rect(x, rect.y, 162, rect.height), row.outputName);
            if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.outputName = row.outputName);
            x += 168;

            float descriptionWidth = Mathf.Max(120, rect.xMax - x - 190);
            EditorGUI.BeginChangeCheck();
            row.description = EditorGUI.TextField(new Rect(x, rect.y, descriptionWidth, rect.height), row.description);
            if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.description = row.description);
            x += descriptionWidth + 3;

            if (IsEffectPrefab(row.asset))
            {
                EditorGUI.BeginChangeCheck();
                row.effectPreviewTime = Mathf.Max(0f, EditorGUI.FloatField(new Rect(x, rect.y, 38, rect.height), row.effectPreviewTime));
                if (EditorGUI.EndChangeCheck())
                {
                    DestroyGeneratedPreview(row);
                    ApplyToSelectedRows(row, target =>
                    {
                        if (!IsEffectPrefab(target.asset)) return;
                        target.effectPreviewTime = row.effectPreviewTime;
                        DestroyGeneratedPreview(target);
                    });
                }
                if (GUI.Button(new Rect(x + 41, rect.y, 38, rect.height), new GUIContent(L("刷新", "↻"), L("刷新特效预览", "Refresh FX preview")))) RefreshEffectPreview(row);
                if (row.generatedEffectPreview != null)
                {
                    Rect thumbnailRect = new Rect(x + 82, rect.y, 20, rect.height);
                    GUI.DrawTexture(thumbnailRect, row.generatedEffectPreview, ScaleMode.ScaleToFit, true);
                    if (GUI.Button(thumbnailRect, new GUIContent(string.Empty, L("点击放大预览", "Click to enlarge preview")), GUIStyle.none))
                        ImagePreviewWindow.Open(row.generatedEffectPreview, row.outputName);
                    EditorGUIUtility.AddCursorRect(thumbnailRect, MouseCursor.Zoom);
                }
                EditorGUI.BeginChangeCheck();
                row.referenceImage = (Texture2D)EditorGUI.ObjectField(new Rect(x + 105, rect.y, 52, rect.height), row.referenceImage, typeof(Texture2D), false);
                if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.referenceImage = row.referenceImage);
            }
            else if (row.asset is AnimatorController)
            {
                if (GUI.Button(new Rect(x, rect.y, 38, rect.height), new GUIContent(L("绘图", "Draw"), L("根据 Animator Controller 数据生成清晰关系图", "Draw a clear diagram from Animator Controller data"))))
                    RefreshAnimatorControllerPreview(row);
                if (GUI.Button(new Rect(x + 41, rect.y, 38, rect.height), new GUIContent(L("截图", "Shot"), L("打开 Unity Animator 窗口并截取当前原生界面", "Capture the native Unity Animator window"))))
                    StartAnimatorWindowScreenshot(row);
                if (row.generatedEffectPreview != null)
                {
                    Rect thumbnailRect = new Rect(x + 82, rect.y, 24, rect.height);
                    GUI.DrawTexture(thumbnailRect, row.generatedEffectPreview, ScaleMode.ScaleToFit, true);
                    if (GUI.Button(thumbnailRect, new GUIContent(string.Empty, L("点击放大 Animator 预览图", "Click to enlarge Animator preview")), GUIStyle.none))
                        ImagePreviewWindow.Open(row.generatedEffectPreview, row.outputName + " - Animator");
                    EditorGUIUtility.AddCursorRect(thumbnailRect, MouseCursor.Zoom);
                }
                EditorGUI.BeginChangeCheck();
                row.referenceImage = (Texture2D)EditorGUI.ObjectField(new Rect(x + 109, rect.y, 48, rect.height), row.referenceImage, typeof(Texture2D), false);
                if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.referenceImage = row.referenceImage);
            }
            else if (row.asset is AnimationClip)
            {
                row.referenceImage = null;
                EditorGUI.LabelField(new Rect(x, rect.y, 157, rect.height), L("无需参考图", "No image needed"), EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                row.referenceImage = (Texture2D)EditorGUI.ObjectField(new Rect(x, rect.y, 157, rect.height), row.referenceImage, typeof(Texture2D), false);
                if (EditorGUI.EndChangeCheck()) ApplyToSelectedRows(row, target => target.referenceImage = row.referenceImage);
            }
            x += 161;

            if (GUI.Button(new Rect(x, rect.y, 22, rect.height), new GUIContent("×", L("仅从当前界面移除，不删除项目资源", "Remove from this window only; project asset stays untouched"))))
            {
                DestroyGeneratedPreview(row);
                rows.RemoveAt(index);
                GUIUtility.ExitGUI();
            }
        }

        private void ApplyToSelectedRows(ResourceRow source, Action<ResourceRow> apply)
        {
            if (source == null || !source.selected || rows.Count(row => row.selected) <= 1) return;
            foreach (ResourceRow target in rows.Where(row => row.selected && row != source)) apply(target);
            Repaint();
        }

        private void RemoveSelectedRows()
        {
            List<ResourceRow> selectedRows = rows.Where(row => row.selected).ToList();
            if (selectedRows.Count == 0) return;
            if (!EditorUtility.DisplayDialog(
                    L("移出当前列表", "Remove from current list"),
                    L("确定把选中的 ", "Remove ") + selectedRows.Count + L(" 项从当前界面移除？\n\n不会删除 Unity 项目中的资源文件，可使用 Ctrl+Z 撤回。", " selected rows from this window?\n\nProject assets will not be deleted. Use Ctrl+Z to undo."),
                    L("移出列表", "Remove rows"),
                    L("取消", "Cancel"))) return;

            foreach (ResourceRow row in selectedRows) DestroyGeneratedPreview(row);
            rows.RemoveAll(row => row.selected);
            selectionAnchor = -1;
            if (resourceList != null) resourceList.index = -1;
            Repaint();
        }

        private void AddObjects(IEnumerable<UnityEngine.Object> objects, bool rememberSources = true)
        {
            UnityEngine.Object[] sourceObjects = objects.Where(o => o != null).ToArray();
            if (rememberSources)
            {
                foreach (UnityEngine.Object source in sourceObjects)
                    if (!scanSources.Contains(source)) scanSources.Add(source);
            }

            var paths = new List<string>();
            foreach (UnityEngine.Object obj in sourceObjects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (AssetDatabase.IsValidFolder(path))
                    paths.AddRange(AssetDatabase.FindAssets(string.Empty, new[] { path }).Select(AssetDatabase.GUIDToAssetPath));
                else paths.Add(path);
            }

            List<string> distinctPaths = paths.Distinct().ToList();
            List<string> spineFolders = distinctPaths
                .Where(p =>
                {
                    UnityEngine.Object candidate = AssetDatabase.LoadMainAssetAtPath(p);
                    return IsSpineSkeletonDataAsset(candidate);
                })
                .Select(Path.GetDirectoryName)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string path in distinctPaths)
            {
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (!IsSupported(asset, path)) continue;
                bool isSpineTexture = asset is Texture2D && spineFolders.Any(folder => IsPathInside(path, folder));
                AddAssetRow(asset, !isSpineTexture);

                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (AnimationClip clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
                    {
                        if (clip == null || clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) continue;
                        AddAssetRow(clip, true);
                    }
                }
            }
            if (!ApplySavedConfiguration(false) && !ApplySavedOrder()) SortRowsByCategory();
            Repaint();
        }

        private void RefreshResources()
        {
            scanSources.RemoveAll(source => source == null);
            if (scanSources.Count > 0)
            {
                UnityEngine.Object[] sources = scanSources.ToArray();
                rows.Clear();
                AddObjects(sources, false);
            }
            else
            {
                foreach (ResourceRow row in rows.Where(row => row.asset != null)) FillDefaults(row, row.asset);
                ApplySavedConfiguration(false);
                Repaint();
            }
        }

        private void SortRowsByCategory()
        {
            List<ResourceRow> sorted = rows
                .Select((row, index) => new { row, index })
                .OrderBy(x => enableGameClassification ? GameCategoryOrder(x.row.gameCategory) : 0)
                .ThenBy(x => CategoryOrder(x.row.type))
                .ThenBy(x => x.index)
                .Select(x => x.row)
                .ToList();
            rows.Clear();
            rows.AddRange(sorted);
            Repaint();
        }

        private void SaveCurrentOrder()
        {
            var data = new SavedOrderData();
            foreach (ResourceRow row in rows)
            {
                string key = GetAssetOrderKey(row.asset);
                if (!string.IsNullOrEmpty(key)) data.assetKeys.Add(key);
            }
            EditorPrefs.SetString(GetOrderPreferenceKey(), JsonUtility.ToJson(data));
            ShowNotification(new GUIContent(L("当前排序已保存", "Current order saved")));
        }

        private void SaveCurrentConfiguration()
        {
            var data = new SavedConfiguration
            {
                sectionName = sectionName,
                includeSectionRow = includeSectionRow,
                enableGameClassification = enableGameClassification,
                autoDetectCategoryByPrefix = autoDetectCategoryByPrefix,
                defaultEffectPreviewTime = defaultEffectPreviewTime
            };
            data.categories = categoryDefinitions.Select(item => new CategoryDefinition
            {
                name = item.name,
                folderName = item.folderName,
                prefix = item.prefix
            }).ToList();
            data.animationNameRules = animationNameRules.Select(item => new AnimationNameRule
            {
                keyword = item.keyword,
                displayName = item.displayName
            }).ToList();

            var orderData = new SavedOrderData();
            foreach (ResourceRow row in rows)
            {
                string key = GetAssetOrderKey(row.asset);
                if (string.IsNullOrEmpty(key)) continue;
                orderData.assetKeys.Add(key);
                data.rows.Add(new SavedRowConfiguration
                {
                    assetKey = key,
                    assetPath = AssetDatabase.GetAssetPath(row.asset),
                    enabled = row.enabled,
                    type = row.type,
                    gameCategory = row.gameCategory,
                    displayName = row.displayName,
                    outputName = row.outputName,
                    description = row.description,
                    effectPreviewTime = row.effectPreviewTime,
                    referenceImagePath = row.referenceImage == null ? string.Empty : AssetDatabase.GetAssetPath(row.referenceImage)
                });
            }

            EditorPrefs.SetString(GetConfigurationPreferenceKey(), JsonUtility.ToJson(data));
            EditorPrefs.SetString(GetOrderPreferenceKey(), JsonUtility.ToJson(orderData));
            legacyCategoryUpgradePendingSave = false;
            ShowNotification(new GUIContent(L("当前排序和资源配置已保存", "Order and asset configuration saved")));
        }

        private bool ApplySavedConfiguration(bool showNotification)
        {
            string json = EditorPrefs.GetString(GetConfigurationPreferenceKey(), string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                if (showNotification) ShowNotification(new GUIContent(L("还没有保存过配置", "No saved configuration yet")));
                return false;
            }

            SavedConfiguration data = JsonUtility.FromJson<SavedConfiguration>(json);
            if (data == null || data.rows == null)
            {
                if (showNotification) ShowNotification(new GUIContent(L("保存的配置无法读取", "Saved configuration could not be read")));
                return false;
            }

            sectionName = data.sectionName;
            includeSectionRow = data.includeSectionRow;
            enableGameClassification = data.enableGameClassification;
            autoDetectCategoryByPrefix = data.autoDetectCategoryByPrefix;
            defaultEffectPreviewTime = data.defaultEffectPreviewTime;
            if (data.categories != null && data.categories.Count > 0)
            {
                categoryDefinitions = data.categories.Select(item => new CategoryDefinition
                {
                    name = item.name,
                    folderName = item.folderName,
                    prefix = item.prefix
                }).ToList();
            }
            else
            {
                categoryDefinitions = null;
                EnsureCategoryDefinitions();
            }
            bool upgradedLegacyCategories = ReplaceExactLegacyCategoryPreset();
            if (upgradedLegacyCategories) legacyCategoryUpgradePendingSave = true;
            InitializeCategoryList();
            if (data.animationNameRules != null && data.animationNameRules.Count > 0)
            {
                animationNameRules = data.animationNameRules.Select(item => new AnimationNameRule
                {
                    keyword = item.keyword,
                    displayName = item.displayName
                }).ToList();
            }
            else
            {
                animationNameRules = null;
                EnsureAnimationNameRules();
            }
            InitializeAnimationRuleList();

            var savedRows = new Dictionary<string, SavedRowConfiguration>(StringComparer.Ordinal);
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < data.rows.Count; i++)
            {
                SavedRowConfiguration saved = data.rows[i];
                if (saved == null || string.IsNullOrEmpty(saved.assetKey) || savedRows.ContainsKey(saved.assetKey)) continue;
                savedRows.Add(saved.assetKey, saved);
                order.Add(saved.assetKey, i);
            }

            // 窗口重新打开或列表已清空时，根据保存路径恢复主资源和 FBX 子动画。
            var existingKeys = new HashSet<string>(rows.Select(row => GetAssetOrderKey(row.asset)), StringComparer.Ordinal);
            foreach (SavedRowConfiguration saved in data.rows)
            {
                if (saved == null || string.IsNullOrEmpty(saved.assetKey) || existingKeys.Contains(saved.assetKey) || string.IsNullOrEmpty(saved.assetPath)) continue;
                UnityEngine.Object asset = AssetDatabase.LoadAllAssetsAtPath(saved.assetPath)
                    .FirstOrDefault(candidate => GetAssetOrderKey(candidate) == saved.assetKey);
                if (asset == null)
                {
                    UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(saved.assetPath);
                    if (GetAssetOrderKey(mainAsset) == saved.assetKey) asset = mainAsset;
                }
                if (asset == null) continue;
                var restoredRow = new ResourceRow { asset = asset, enabled = saved.enabled };
                FillDefaults(restoredRow, asset);
                rows.Add(restoredRow);
                existingKeys.Add(saved.assetKey);
            }

            foreach (ResourceRow row in rows)
            {
                if (!savedRows.TryGetValue(GetAssetOrderKey(row.asset), out SavedRowConfiguration saved)) continue;
                DestroyGeneratedPreview(row);
                row.enabled = saved.enabled;
                row.type = saved.type;
                row.gameCategory = saved.gameCategory;
                row.displayName = saved.displayName;
                row.outputName = saved.outputName;
                row.description = saved.description;
                row.effectPreviewTime = saved.effectPreviewTime;
                row.referenceImage = string.IsNullOrEmpty(saved.referenceImagePath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<Texture2D>(saved.referenceImagePath);
            }
            if (upgradedLegacyCategories) ReclassifyRowsWithCurrentRules();

            List<ResourceRow> sorted = rows
                .Select((row, index) => new { row, index, key = GetAssetOrderKey(row.asset) })
                .OrderBy(item => order.TryGetValue(item.key, out int savedIndex) ? savedIndex : int.MaxValue)
                .ThenBy(item => item.index)
                .Select(item => item.row)
                .ToList();
            rows.Clear();
            rows.AddRange(sorted);
            if (showNotification) ShowNotification(new GUIContent(upgradedLegacyCategories
                ? L("配置已恢复；旧版默认分类已升级，请保存全部配置", "Configuration restored; old defaults upgraded. Save configuration.")
                : L("排序和资源配置已恢复", "Order and asset configuration restored")));
            Repaint();
            return true;
        }

        private bool ApplySavedOrder()
        {
            string json = EditorPrefs.GetString(GetOrderPreferenceKey(), string.Empty);
            if (string.IsNullOrEmpty(json)) return false;
            SavedOrderData data = JsonUtility.FromJson<SavedOrderData>(json);
            if (data == null || data.assetKeys == null || data.assetKeys.Count == 0) return false;

            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < data.assetKeys.Count; i++)
                if (!order.ContainsKey(data.assetKeys[i])) order.Add(data.assetKeys[i], i);

            List<ResourceRow> sorted = rows
                .Select((row, index) => new { row, index, key = GetAssetOrderKey(row.asset) })
                .OrderBy(x => order.TryGetValue(x.key, out int savedIndex) ? savedIndex : int.MaxValue)
                .ThenBy(x => x.index)
                .Select(x => x.row)
                .ToList();
            rows.Clear();
            rows.AddRange(sorted);
            return true;
        }

        private static string GetAssetOrderKey(UnityEngine.Object asset)
        {
            if (asset == null) return string.Empty;
            if (!assetGuidLocalIdMethodResolved)
            {
                assetGuidLocalIdMethod = typeof(AssetDatabase)
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "TryGetGUIDAndLocalFileIdentifier") return false;
                        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length == 3 &&
                               parameters[0].ParameterType == typeof(UnityEngine.Object) &&
                               parameters[1].ParameterType == typeof(string).MakeByRefType() &&
                               (parameters[2].ParameterType == typeof(long).MakeByRefType() ||
                                parameters[2].ParameterType == typeof(int).MakeByRefType());
                    });
                assetGuidLocalIdMethodResolved = true;
            }
            if (assetGuidLocalIdMethod != null)
            {
                try
                {
                    bool usesLongId = assetGuidLocalIdMethod.GetParameters()[2].ParameterType == typeof(long).MakeByRefType();
                    object[] arguments = { asset, string.Empty, usesLongId ? (object)0L : 0 };
                    if ((bool)assetGuidLocalIdMethod.Invoke(null, arguments))
                        return arguments[1] + ":" + arguments[2];
                }
                catch (Exception) { }
            }
            string path = AssetDatabase.GetAssetPath(asset);
            string guid = AssetDatabase.AssetPathToGUID(path);
            return (string.IsNullOrEmpty(guid) ? path : guid) + ":" + asset.name + ":" + asset.GetType().FullName;
        }

        private static string GetOrderPreferenceKey()
        {
            return "ArtResourceSheetExporter.GitHub.SavedOrder." + Application.dataPath.Replace('\\', '/').ToLowerInvariant();
        }

        private static string GetConfigurationPreferenceKey()
        {
            return "ArtResourceSheetExporter.GitHub.SavedConfiguration." + Application.dataPath.Replace('\\', '/').ToLowerInvariant();
        }

        private void AddAssetRow(UnityEngine.Object asset, bool enabled)
        {
            if (asset == null || rows.Any(r => r.asset == asset)) return;
            var row = new ResourceRow { asset = asset, enabled = enabled };
            FillDefaults(row, asset);
            rows.Add(row);
        }

        private static bool IsPathInside(string assetPath, string folder)
        {
            string normalizedPath = assetPath.Replace('\\', '/');
            string normalizedFolder = folder.Replace('\\', '/').TrimEnd('/') + "/";
            return normalizedPath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupported(UnityEngine.Object asset, string path)
        {
            if (asset is Texture2D) return true;
            if (asset is AnimatorController) return true;
            if (asset is GameObject && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return true;
            if (asset is GameObject && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return true;
            if (asset is AnimationClip && (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))) return true;
            // Spine 文件夹中只收集导入完成的 SkeletonDataAsset，避免把
            // .json、.skel、Atlas 和贴图作为独立资源重复导出。
            return IsSpineSkeletonDataAsset(asset);
        }

        private static void ResolveSpineTypes()
        {
            if (spineTypesResolved) return;
            spineTypesResolved = true;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (spineSkeletonDataAssetType == null)
                    spineSkeletonDataAssetType = assembly.GetType("Spine.Unity.SkeletonDataAsset", false);
                if (spineSkeletonAnimationType == null)
                    spineSkeletonAnimationType = assembly.GetType("Spine.Unity.SkeletonAnimation", false);
                if (spineSkeletonDataAssetType != null && spineSkeletonAnimationType != null) break;
            }
        }

        private static bool IsSpinePluginAvailable()
        {
            ResolveSpineTypes();
            return spineSkeletonDataAssetType != null && spineSkeletonAnimationType != null;
        }

        private static bool IsSpineSkeletonDataAsset(UnityEngine.Object asset)
        {
            if (asset == null) return false;
            ResolveSpineTypes();
            return spineSkeletonDataAssetType != null && spineSkeletonDataAssetType.IsInstanceOfType(asset);
        }

        private void FillDefaults(ResourceRow row, UnityEngine.Object asset)
        {
            DestroyGeneratedPreview(row);
            string path = AssetDatabase.GetAssetPath(asset);
            string name = asset is AnimationClip ? asset.name : Path.GetFileNameWithoutExtension(path);
            row.displayName = name;
            row.outputName = name;
            if (enableGameClassification)
            {
                if (autoDetectCategoryByPrefix)
                {
                    row.gameCategory = DetectGameCategory(name, path);
                    if (row.gameCategory == GetFallbackCategoryName()) row.gameCategory = DetectGameCategory(Path.GetFileNameWithoutExtension(path), path);
                }
                else
                {
                    string[] categoryNames = GetCategoryNames();
                    row.gameCategory = categoryNames[Mathf.Clamp(manualCategoryIndex, 0, categoryNames.Length - 1)];
                }
            }
            else row.gameCategory = string.Empty;
            if (asset is Texture2D)
            {
                row.type = "UI";
                row.referenceImage = asset as Texture2D;
            }
            else if (asset is AnimationClip)
            {
                row.type = "模型动画";
                row.description = "来源模型：" + Path.GetFileNameWithoutExtension(path);
            }
            else if (asset is AnimatorController)
            {
                row.type = "动画控制器";
                row.description = GetAnimatorControllerDescription(asset as AnimatorController);
            }
            else if (asset is GameObject && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) row.type = "模型";
            else if (asset is GameObject)
            {
                row.type = "特效";
                row.effectPreviewTime = defaultEffectPreviewTime;
            }
            else
            {
                row.type = "Spine动画";
                row.description = GetSpineAnimationDescription(asset);
            }
        }

        private string DetectGameCategory(string resourceName, string assetPath = null)
        {
            string folderCategory = DetectGameCategoryFromFolder(assetPath);
            if (!string.IsNullOrEmpty(folderCategory)) return folderCategory;
            foreach (CategoryDefinition definition in categoryDefinitions)
                if (StartsWithPrefix(resourceName, definition.prefix)) return definition.name;
            return GetFallbackCategoryName();
        }

        private string DetectGameCategoryFromFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string[] segments = assetPath.Replace('\\', '/').Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (!string.Equals(segments[i], "Textures", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(segments[i], "Spines", StringComparison.OrdinalIgnoreCase)) continue;

                // 只读取 Textures / Spines 的直接下一级，避免被更深层或项目根目录误分类。
                CategoryDefinition match = categoryDefinitions.FirstOrDefault(definition =>
                    !string.IsNullOrEmpty(definition.folderName) &&
                    string.Equals(segments[i + 1], definition.folderName, StringComparison.OrdinalIgnoreCase));
                return match == null ? null : match.name;
            }
            return null;
        }

        private static bool StartsWithPrefix(string value, string prefix)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(prefix) && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private string GetAnimatorControllerDescription(AnimatorController controller)
        {
            if (controller == null) return string.Empty;
            var stateNames = new List<string>();
            foreach (AnimatorControllerLayer layer in controller.layers)
                CollectAnimatorStateNames(layer.stateMachine, stateNames);
            stateNames = stateNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
            if (stateNames.Count == 0) return "动画：";

            EnsureAnimationNameRules();
            var lines = new List<string> { "动画：" };
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (AnimationNameRule rule in animationNameRules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.keyword) || string.IsNullOrWhiteSpace(rule.displayName)) continue;
                List<string> names = stateNames
                    .Where(name => name.IndexOf(rule.keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (names.Count == 0) continue;
                foreach (string name in names) matched.Add(name);
                lines.Add(rule.displayName.Trim() + "：" + string.Join("、", names));
            }

            List<string> other = stateNames.Where(name => !matched.Contains(name)).ToList();
            if (other.Count > 0) lines.Add("其他：" + string.Join("、", other));
            return string.Join("\n", lines);
        }

        private static void CollectAnimatorStateNames(AnimatorStateMachine stateMachine, List<string> names)
        {
            if (stateMachine == null) return;
            foreach (ChildAnimatorState childState in stateMachine.states)
                if (childState.state != null) names.Add(childState.state.name);
            foreach (ChildAnimatorStateMachine childMachine in stateMachine.stateMachines)
                CollectAnimatorStateNames(childMachine.stateMachine, names);
        }

        private static Texture2D RenderAnimatorControllerPreview(AnimatorController controller, int width, int height)
        {
            if (controller == null || controller.layers == null || controller.layers.Length == 0) return null;
            GameObject cameraObject = null;
            GameObject canvasObject = null;
            RenderTexture renderTexture = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                cameraObject = new GameObject("ArtResourceSheet_AnimatorCamera") { hideFlags = HideFlags.HideAndDontSave };
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.13f, 0.13f, 0.13f, 1f);
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.transform.position = new Vector3(0f, 0f, -10f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;

                renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                camera.targetTexture = renderTexture;

                canvasObject = new GameObject("ArtResourceSheet_AnimatorCanvas") { hideFlags = HideFlags.HideAndDontSave };
                Canvas canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
                canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
                canvasObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                RectTransform root = canvasObject.GetComponent<RectTransform>();
                CreateGraphRect(root, "Background", Vector2.zero, new Vector2(width, height), new Color(0.13f, 0.13f, 0.13f, 1f));
                const float parameterWidth = 185f;
                CreateGraphRect(root, "ParameterPanel", Vector2.zero, new Vector2(parameterWidth, height), new Color(0.19f, 0.19f, 0.19f, 1f));
                CreateGraphText(root, "Parameters", new Vector2(12f, height - 36f), new Vector2(parameterWidth - 24f, 24f), "Parameters", 16, TextAnchor.MiddleLeft, Color.white);
                float parameterY = height - 68f;
                foreach (UnityEngine.AnimatorControllerParameter parameter in controller.parameters)
                {
                    CreateGraphText(root, "Parameter", new Vector2(14f, parameterY), new Vector2(parameterWidth - 28f, 22f), parameter.name, 13, TextAnchor.MiddleLeft, new Color(0.85f, 0.85f, 0.85f));
                    parameterY -= 26f;
                    if (parameterY < 12f) break;
                }

                for (float x = parameterWidth; x < width; x += 32f)
                    CreateGraphRect(root, "GridV", new Vector2(x, 0f), new Vector2(1f, height), new Color(0.20f, 0.20f, 0.20f, 1f));
                for (float y = 0f; y < height; y += 32f)
                    CreateGraphRect(root, "GridH", new Vector2(parameterWidth, y), new Vector2(width - parameterWidth, 1f), new Color(0.20f, 0.20f, 0.20f, 1f));

                var nodes = new List<ControllerGraphNode>();
                var statePositions = new Dictionary<UnityEditor.Animations.AnimatorState, Vector2>();
                for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
                {
                    AnimatorStateMachine machine = controller.layers[layerIndex].stateMachine;
                    CollectAnimatorGraphNodes(machine, new Vector2(0f, layerIndex * 420f), nodes, statePositions);
                }

                AnimatorStateMachine rootMachine = controller.layers[0].stateMachine;
                var anyNode = new ControllerGraphNode { label = "Any State", sourcePosition = rootMachine.anyStatePosition, color = new Color(0.22f, 0.55f, 0.48f) };
                var entryNode = new ControllerGraphNode { label = "Entry", sourcePosition = rootMachine.entryPosition, color = new Color(0.03f, 0.52f, 0.18f) };
                var exitNode = new ControllerGraphNode { label = "Exit", sourcePosition = rootMachine.exitPosition, color = new Color(0.62f, 0.06f, 0.08f) };
                nodes.Add(anyNode);
                nodes.Add(entryNode);
                nodes.Add(exitNode);

                float minX = nodes.Min(node => node.sourcePosition.x);
                float maxX = nodes.Max(node => node.sourcePosition.x);
                float minY = nodes.Min(node => node.sourcePosition.y);
                float maxY = nodes.Max(node => node.sourcePosition.y);
                float graphLeft = parameterWidth + 36f;
                float graphBottom = 30f;
                float graphWidth = width - graphLeft - 30f;
                float graphHeight = height - 60f;
                float scaleX = graphWidth / Mathf.Max(1f, maxX - minX + 220f);
                float scaleY = graphHeight / Mathf.Max(1f, maxY - minY + 100f);
                float scale = Mathf.Min(scaleX, scaleY);
                var screenPositions = new Dictionary<ControllerGraphNode, Vector2>();
                foreach (ControllerGraphNode node in nodes)
                    screenPositions[node] = new Vector2(
                        graphLeft + (node.sourcePosition.x - minX + 110f) * scale,
                        graphBottom + (maxY - node.sourcePosition.y + 50f) * scale);

                var nodeByState = nodes.Where(node => node.state != null).ToDictionary(node => node.state, node => node);
                foreach (ControllerGraphNode node in nodes.Where(item => item.state != null))
                {
                    foreach (AnimatorStateTransition transition in node.state.transitions)
                        if (transition.destinationState != null && nodeByState.TryGetValue(transition.destinationState, out ControllerGraphNode target))
                            CreateGraphLine(root, screenPositions[node], screenPositions[target], new Color(0.72f, 0.72f, 0.72f), 2f);
                }
                foreach (AnimatorStateTransition transition in rootMachine.anyStateTransitions)
                    if (transition.destinationState != null && nodeByState.TryGetValue(transition.destinationState, out ControllerGraphNode target))
                        CreateGraphLine(root, screenPositions[anyNode], screenPositions[target], new Color(0.78f, 0.78f, 0.78f), 2f);
                if (rootMachine.defaultState != null && nodeByState.TryGetValue(rootMachine.defaultState, out ControllerGraphNode defaultNode))
                    CreateGraphLine(root, screenPositions[entryNode], screenPositions[defaultNode], new Color(0.78f, 0.48f, 0.02f), 3f);

                foreach (ControllerGraphNode node in nodes)
                {
                    Vector2 size = node.state == null ? new Vector2(160f, 36f) : new Vector2(205f, 42f);
                    Vector2 position = screenPositions[node] - size * 0.5f;
                    CreateGraphRect(root, "State", position, size, node.color);
                    CreateGraphText(root, "StateLabel", position, size, node.label, 13, TextAnchor.MiddleCenter, Color.white);
                }

                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = renderTexture;
                var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                result.Apply();
                return result;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("生成 Animator Controller 关系图失败：" + exception.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
                if (canvasObject != null) DestroyImmediate(canvasObject);
                if (cameraObject != null) DestroyImmediate(cameraObject);
            }
        }

        private static void CollectAnimatorGraphNodes(AnimatorStateMachine machine, Vector2 offset, List<ControllerGraphNode> nodes, Dictionary<UnityEditor.Animations.AnimatorState, Vector2> positions)
        {
            if (machine == null) return;
            foreach (ChildAnimatorState child in machine.states)
            {
                if (child.state == null || positions.ContainsKey(child.state)) continue;
                Vector2 position = offset + new Vector2(child.position.x, child.position.y);
                positions.Add(child.state, position);
                nodes.Add(new ControllerGraphNode
                {
                    state = child.state,
                    label = child.state.name,
                    sourcePosition = position,
                    color = machine.defaultState == child.state ? new Color(0.78f, 0.38f, 0.04f) : new Color(0.28f, 0.28f, 0.28f)
                });
            }
            foreach (ChildAnimatorStateMachine childMachine in machine.stateMachines)
                CollectAnimatorGraphNodes(childMachine.stateMachine, offset + new Vector2(childMachine.position.x, childMachine.position.y), nodes, positions);
        }

        private static RectTransform CreateGraphRect(RectTransform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var target = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            target.hideFlags = HideFlags.HideAndDontSave;
            RectTransform rect = target.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            target.GetComponent<UnityEngine.UI.Image>().color = color;
            return rect;
        }

        private static void CreateGraphText(RectTransform parent, string name, Vector2 position, Vector2 size, string text, int fontSize, TextAnchor alignment, Color color)
        {
            var target = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Text));
            target.hideFlags = HideFlags.HideAndDontSave;
            RectTransform rect = target.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            UnityEngine.UI.Text label = target.GetComponent<UnityEngine.UI.Text>();
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Truncate;
        }

        private static void CreateGraphLine(RectTransform parent, Vector2 from, Vector2 to, Color color, float thickness)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            RectTransform line = CreateGraphRect(parent, "Transition", (from + to) * 0.5f, new Vector2(length, thickness), color);
            line.pivot = new Vector2(0.5f, 0.5f);
            line.anchoredPosition = (from + to) * 0.5f;
            line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }

        private static string GetSpineAnimationDescription(UnityEngine.Object asset)
        {
            try
            {
                var method = asset.GetType().GetMethod("GetSkeletonData", new[] { typeof(bool) });
                object skeletonData = method == null ? null : method.Invoke(asset, new object[] { true });
                if (skeletonData == null) return string.Empty;
                var animationsProperty = skeletonData.GetType().GetProperty("Animations");
                IEnumerable animations = animationsProperty == null ? null : animationsProperty.GetValue(skeletonData, null) as IEnumerable;
                if (animations == null) return string.Empty;

                var start = new List<string>();
                var idle = new List<string>();
                var end = new List<string>();
                var win = new List<string>();
                var symbol = new List<string>();
                var other = new List<string>();
                foreach (object animation in animations)
                {
                    if (animation == null) continue;
                    var nameProperty = animation.GetType().GetProperty("Name");
                    string animationName = nameProperty == null ? null : nameProperty.GetValue(animation, null) as string;
                    if (string.IsNullOrEmpty(animationName)) continue;
                    if (animationName.IndexOf("win", StringComparison.OrdinalIgnoreCase) >= 0) win.Add(animationName);
                    else if (TryGetSymbolAnimationName(animationName, out string symbolName)) symbol.Add(symbolName);
                    else if (animationName.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0) start.Add(animationName);
                    else if (animationName.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) idle.Add(animationName);
                    else if (animationName.IndexOf("end", StringComparison.OrdinalIgnoreCase) >= 0) end.Add(animationName);
                    else other.Add(animationName);
                }

                var lines = new List<string> { "动画名称：" };
                if (start.Count > 0) lines.Add("开始：" + string.Join("、", start));
                if (idle.Count > 0) lines.Add("待机：" + string.Join("、", idle));
                if (end.Count > 0) lines.Add("结束：" + string.Join("、", end));
                if (win.Count > 0) lines.Add("中奖：" + string.Join("、", win));
                if (symbol.Count > 0) lines.Add("图标：" + string.Join("、", symbol.Distinct().ToArray()));
                if (other.Count > 0) lines.Add("其他：" + string.Join("、", other));
                return string.Join("\n", lines);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("读取 Spine 动画名称失败：" + exception.Message);
                return string.Empty;
            }
        }

        private static bool TryGetSymbolAnimationName(string animationName, out string symbolName)
        {
            symbolName = null;
            if (string.IsNullOrEmpty(animationName)) return false;
            string value = animationName.Trim();
            if (string.Equals(value, "10", StringComparison.OrdinalIgnoreCase)) symbolName = "10";
            else if (string.Equals(value, "A", StringComparison.OrdinalIgnoreCase)) symbolName = "A";
            else if (string.Equals(value, "J", StringComparison.OrdinalIgnoreCase)) symbolName = "J";
            else if (string.Equals(value, "Q", StringComparison.OrdinalIgnoreCase)) symbolName = "Q";
            else if (string.Equals(value, "K", StringComparison.OrdinalIgnoreCase)) symbolName = "K";
            return symbolName != null;
        }

        private static bool IsEffectPrefab(UnityEngine.Object asset)
        {
            return asset is GameObject && AssetDatabase.GetAssetPath(asset).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private void Export()
        {
            // 完全采用界面中的当前顺序。分类分隔行会在分类变化处自动插入。
            List<ResourceRow> exportRows = rows
                .Where(row => row.enabled && row.asset != null)
                .Select((row, index) => new { row, index })
                .OrderBy(item => enableGameClassification ? GameCategoryOrder(item.row.gameCategory) : 0)
                .ThenBy(item => CategoryOrder(item.row.type))
                .ThenBy(item => item.index)
                .Select(item => item.row)
                .ToList();
            string defaultName = SanitizeFileName(GetProjectName()) + "_美术资源表";
            string path = EditorUtility.SaveFilePanel(L("导出美术资源表", "Export art resource sheet"), Application.dataPath, defaultName, "xlsx");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var images = new List<ExportImage>();
                foreach (ResourceRow row in exportRows)
                {
                    bool destroyPreview;
                    Texture2D preview = GetExportPreview(row, out destroyPreview);
                    try
                    {
                        images.Add(preview == null ? null : EncodeTexture(preview));
                    }
                    finally
                    {
                        if (destroyPreview && preview != null) DestroyImmediate(preview);
                    }
                }
                XlsxWriter.Write(path, sectionName, includeSectionRow, enableGameClassification, exportRows, images);
                EditorUtility.RevealInFinder(path);
                EditorUtility.DisplayDialog(L("导出完成", "Export complete"), L("已导出：\n", "Exported:\n") + path, L("确定", "OK"));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(L("导出失败", "Export failed"), ex.Message, L("确定", "OK"));
            }
        }

        private static Texture2D GetExportPreview(ResourceRow row, out bool destroyAfterUse)
        {
            destroyAfterUse = false;
            if (row.asset is AnimationClip) return null;
            if (row.referenceImage != null) return row.referenceImage;
            if (row.asset is Texture2D) return row.asset as Texture2D;

            if (row.asset is AnimatorController)
            {
                if (row.generatedEffectPreview != null) return row.generatedEffectPreview;
                Texture2D controllerPreview = RenderAnimatorControllerPreview(row.asset as AnimatorController, 1200, 675);
                if (controllerPreview != null)
                {
                    destroyAfterUse = true;
                    return controllerPreview;
                }
                return null;
            }

            if (IsEffectPrefab(row.asset))
            {
                if (row.generatedEffectPreview != null) return row.generatedEffectPreview;
                Texture2D effectPreview = RenderEffectPreview(row.asset as GameObject, row.effectPreviewTime, 512, 512);
                if (effectPreview != null)
                {
                    destroyAfterUse = true;
                    return effectPreview;
                }
                return null;
            }

            // 按 Spine 实际渲染边界生成完整截图，避免 Inspector Preview 固定视角裁切。
            if (IsSpineSkeletonDataAsset(row.asset))
            {
                Texture2D spinePreview = RenderFullSpinePreview(row.asset, 512, 512);
                if (spinePreview != null)
                {
                    destroyAfterUse = true;
                    return spinePreview;
                }
            }

            return AssetPreview.GetAssetPreview(row.asset);
        }

        private void RefreshEffectPreview(ResourceRow row)
        {
            DestroyGeneratedPreview(row);
            if (!IsEffectPrefab(row.asset)) return;
            row.generatedEffectPreview = RenderEffectPreview(row.asset as GameObject, row.effectPreviewTime, 512, 512);
            Repaint();
        }

        private void RefreshAnimatorControllerPreview(ResourceRow row)
        {
            DestroyGeneratedPreview(row);
            if (!(row.asset is AnimatorController)) return;
            row.generatedEffectPreview = RenderAnimatorControllerPreview(row.asset as AnimatorController, 1200, 675);
            Repaint();
        }

        private void RefreshAllAnimatorControllerPreviews()
        {
            List<ResourceRow> controllerRows = rows.Where(item => item.enabled && item.asset is AnimatorController).ToList();
            if (controllerRows.Count == 0)
            {
                EditorUtility.DisplayDialog(L("一键绘图", "Draw all Animator diagrams"), L("当前列表中没有已启用的 Animator Controller。\n\n请先把 .controller 文件拖入工具，并确认该行处于启用状态。", "No enabled Animator Controllers are in the list.\n\nDrop .controller files into the tool and enable their rows first."), L("知道了", "OK"));
                return;
            }

            try
            {
                for (int i = 0; i < controllerRows.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(L("一键绘图", "Drawing Animator diagrams"), controllerRows[i].outputName, (i + 1f) / controllerRows.Count);
                    RefreshAnimatorControllerPreview(controllerRows[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            ShowNotification(new GUIContent(L("已生成 ", "Generated ") + controllerRows.Count + L(" 个 Animator 关系图", " Animator diagrams")));
            Repaint();
        }

        private void StartAllAnimatorWindowScreenshots()
        {
            if (pendingAnimatorScreenshotRow != null)
            {
                ShowNotification(new GUIContent(L("截图任务正在进行中", "A capture task is already running")));
                return;
            }

            animatorScreenshotQueue.Clear();
            foreach (ResourceRow row in rows.Where(item => item.enabled && item.asset is AnimatorController))
                animatorScreenshotQueue.Enqueue(row);

            animatorScreenshotTotal = animatorScreenshotQueue.Count;
            animatorScreenshotCompleted = 0;
            animatorScreenshotFailed = 0;
            animatorScreenshotStatus = string.Empty;
            if (animatorScreenshotTotal == 0)
            {
                EditorUtility.DisplayDialog(L("一键截图", "Capture all Animator windows"), L("当前列表中没有已启用的 Animator Controller。\n\n请先把 .controller 文件拖入工具，并确认该行处于启用状态。", "No enabled Animator Controllers are in the list.\n\nDrop .controller files into the tool and enable their rows first."), L("知道了", "OK"));
                return;
            }

            StartNextAnimatorWindowScreenshot();
        }

        private void StartNextAnimatorWindowScreenshot()
        {
            if (animatorScreenshotQueue.Count == 0)
            {
                animatorScreenshotStatus = L("截图完成：成功 ", "Capture complete: ") + animatorScreenshotCompleted + L(" 张，失败 ", " succeeded, ") + animatorScreenshotFailed + L(" 张", " failed");
                animatorScreenshotTotal = 0;
                EditorApplication.update -= UpdateAnimatorScreenshot;
                if (animatorCaptureWindow != null) animatorCaptureWindow.Close();
                animatorCaptureWindow = null;
                ShowNotification(new GUIContent(animatorScreenshotStatus));
                Debug.Log("美术资源表：" + animatorScreenshotStatus);
                Focus();
                Repaint();
                return;
            }
            StartAnimatorWindowScreenshot(animatorScreenshotQueue.Dequeue(), false);
        }

        private void StartAnimatorWindowScreenshot(ResourceRow row, bool clearBatchQueue = true)
        {
            AnimatorController controller = row == null ? null : row.asset as AnimatorController;
            if (controller == null) return;

            if (clearBatchQueue)
            {
                animatorScreenshotQueue.Clear();
                animatorScreenshotTotal = 0;
                animatorScreenshotCompleted = 0;
                animatorScreenshotFailed = 0;
            }

            pendingAnimatorScreenshotRow = row;
            pendingAnimatorWindow = null;
            pendingAnimatorScreenshotStage = -1;
            pendingAnimatorScreenshotTime = EditorApplication.timeSinceStartup;
            pendingAnimatorScreenshotDeadline = EditorApplication.timeSinceStartup + 5d;
            string progress = animatorScreenshotTotal > 0
                ? L("正在截图 ", "Capturing ") + (animatorScreenshotCompleted + animatorScreenshotFailed + 1) + "/" + animatorScreenshotTotal + ": " + row.outputName
                : L("正在打开 Animator 并截图…", "Opening Animator for capture…");
            ShowNotification(new GUIContent(progress));
            EditorApplication.update -= UpdateAnimatorScreenshot;
            EditorApplication.update += UpdateAnimatorScreenshot;
            // 所有窗口操作由下一次 EditorApplication.update 驱动，不依赖可被其他插件覆盖的 delayCall。
            Repaint();
        }

        private void BeginAnimatorWindowScreenshot()
        {
            if (pendingAnimatorScreenshotRow == null) return;
            try
            {
                AnimatorController controller = pendingAnimatorScreenshotRow.asset as AnimatorController;
                if (controller == null)
                {
                    FinishAnimatorScreenshot(false, L("Animator Controller 已失效。", "Animator Controller is no longer valid."));
                    return;
                }
                pendingAnimatorWindow = GetOrCreateAnimatorWindow();
                if (pendingAnimatorWindow == null)
                {
                    FinishAnimatorScreenshot(false, L("无法创建 Unity Animator 窗口。", "Could not create the Unity Animator window."));
                    return;
                }
                Selection.activeObject = controller;
                AssignAnimatorController(pendingAnimatorWindow, controller);
                pendingAnimatorWindow.Focus();
                pendingAnimatorWindow.Repaint();
                pendingAnimatorScreenshotStage = 0;
                pendingAnimatorScreenshotTime = EditorApplication.timeSinceStartup + 0.5d;
                Debug.Log("美术资源表：开始截取 Animator Controller：" + controller.name);
            }
            catch (Exception exception)
            {
                FinishAnimatorScreenshot(false, L("打开 Animator 窗口失败：", "Could not open the Animator window: ") + exception.Message);
            }
        }

        private void UpdateAnimatorScreenshot()
        {
            try
            {
                UpdateAnimatorScreenshotInternal();
            }
            catch (Exception exception)
            {
                FinishAnimatorScreenshot(false, L("Animator 截图流程异常：", "Animator capture failed: ") + exception.Message);
            }
        }

        private void UpdateAnimatorScreenshotInternal()
        {
            if (pendingAnimatorScreenshotRow != null && EditorApplication.timeSinceStartup > pendingAnimatorScreenshotDeadline)
            {
                FinishAnimatorScreenshot(false, L("Animator 截图超过 5 秒，已自动跳过：", "Animator capture exceeded 5 seconds; skipped: ") + pendingAnimatorScreenshotRow.outputName);
                return;
            }
            if (EditorApplication.timeSinceStartup < pendingAnimatorScreenshotTime) return;
            if (pendingAnimatorScreenshotStage < 0)
            {
                BeginAnimatorWindowScreenshot();
                return;
            }

            if (pendingAnimatorScreenshotRow == null || !(pendingAnimatorScreenshotRow.asset is AnimatorController))
            {
                FinishAnimatorScreenshot(false, L("待截图的 Animator Controller 已不存在。", "The Animator Controller to capture no longer exists."));
                return;
            }

            if (pendingAnimatorScreenshotStage == 0)
            {
                if (pendingAnimatorWindow == null)
                {
                    FinishAnimatorScreenshot(false, L("Animator 窗口已关闭，已跳过当前资源。", "Animator window closed; skipped this asset."));
                    return;
                }

                InvokeAnimatorFrameAll(pendingAnimatorWindow);
                pendingAnimatorWindow.Repaint();
                pendingAnimatorScreenshotStage = 1;
                pendingAnimatorScreenshotTime = EditorApplication.timeSinceStartup + 0.35d;
                return;
            }

            if (pendingAnimatorWindow == null) throw new InvalidOperationException(L("截图窗口已关闭。", "Capture window is closed."));
            if (EditorWindow.focusedWindow != pendingAnimatorWindow)
            {
                pendingAnimatorWindow.Focus();
                pendingAnimatorWindow.Repaint();
                pendingAnimatorScreenshotTime = EditorApplication.timeSinceStartup + 0.2d;
                return;
            }
            System.Reflection.PropertyInfo controllerProperty = pendingAnimatorWindow.GetType().GetProperty("animatorController",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (controllerProperty == null || controllerProperty.GetValue(pendingAnimatorWindow, null) as AnimatorController != pendingAnimatorScreenshotRow.asset)
                throw new InvalidOperationException(L("Animator 中显示的控制器与待截图资源不一致。", "The Animator window shows a different controller."));
            Texture2D screenshot = CaptureEditorWindow(pendingAnimatorWindow);
            if (screenshot == null)
            {
                FinishAnimatorScreenshot(false, L("Animator 窗口截图失败；请确保窗口没有被最小化。", "Animator capture failed; make sure the window is not minimized."));
                return;
            }

            DestroyGeneratedPreview(pendingAnimatorScreenshotRow);
            pendingAnimatorScreenshotRow.generatedEffectPreview = screenshot;
            Debug.Log("美术资源表：截图成功 " + pendingAnimatorScreenshotRow.outputName + " (" + screenshot.width + "×" + screenshot.height + ")");
            FinishAnimatorScreenshot(true, L("Animator 原生窗口截图完成", "Native Animator window captured"));
        }

        private static EditorWindow FindAnimatorWindow()
        {
            Type animatorWindowType = null;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                animatorWindowType = assembly.GetType("UnityEditor.Graphs.AnimatorControllerTool", false);
                if (animatorWindowType != null) break;
            }
            if (animatorWindowType == null) return null;
            return Resources.FindObjectsOfTypeAll(animatorWindowType).OfType<EditorWindow>().FirstOrDefault();
        }

        private static EditorWindow GetOrCreateAnimatorWindow()
        {
            if (animatorCaptureWindow != null) return animatorCaptureWindow;
            Type animatorWindowType = null;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                animatorWindowType = assembly.GetType("UnityEditor.Graphs.AnimatorControllerTool", false);
                if (animatorWindowType != null) break;
            }
            if (animatorWindowType == null)
            {
                try
                {
                    System.Reflection.Assembly graphAssembly = System.Reflection.Assembly.Load("UnityEditor.Graphs");
                    animatorWindowType = graphAssembly.GetType("UnityEditor.Graphs.AnimatorControllerTool", false);
                }
                catch { }
            }
            if (animatorWindowType == null) return null;
            // 停靠的 Animator 会被浮动资源表遮挡。专用 Utility 窗口始终浮在 Unity 主窗口之上。
            animatorCaptureWindow = (EditorWindow)CreateInstance(animatorWindowType);
            animatorCaptureWindow.titleContent = new GUIContent(L("Animator 截图", "Animator Capture"));
            animatorCaptureWindow.ShowUtility();
            animatorCaptureWindow.minSize = new Vector2(800f, 480f);
            animatorCaptureWindow.position = new Rect(80f, 80f, 1200f, 680f);
            return animatorCaptureWindow;
        }

        private static void AssignAnimatorController(EditorWindow animatorWindow, AnimatorController controller)
        {
            if (animatorWindow == null || controller == null) return;
            Type windowType = animatorWindow.GetType();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                                                         System.Reflection.BindingFlags.Public |
                                                         System.Reflection.BindingFlags.NonPublic;
            try
            {
                System.Reflection.MethodInfo setter = windowType.GetMethods(flags).FirstOrDefault(method =>
                {
                    if (method.Name != "SetAnimatorController") return false;
                    System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(AnimatorController));
                });
                if (setter != null)
                {
                    setter.Invoke(animatorWindow, new object[] { controller });
                    return;
                }
                System.Reflection.PropertyInfo property = windowType.GetProperty("animatorController", flags);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(animatorWindow, controller, null);
                    return;
                }
                System.Reflection.FieldInfo field = windowType.GetField("m_AnimatorController", flags);
                if (field == null) throw new MissingMemberException(L("没有找到 animatorController 赋值接口。", "Could not find the animatorController setter."));
                field.SetValue(animatorWindow, controller);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(L("指定 Animator Controller 失败。", "Could not assign the Animator Controller."), exception);
            }
        }

        private static void InvokeAnimatorFrameAll(EditorWindow animatorWindow)
        {
            if (animatorWindow == null) return;
            try
            {
                System.Reflection.MethodInfo method = animatorWindow.GetType().GetMethod(
                    "FrameAll",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method != null && method.GetParameters().Length == 0) method.Invoke(animatorWindow, null);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(L("Animator Frame All 失败。", "Animator Frame All failed."), exception);
            }
        }

        private static Texture2D CaptureEditorWindow(EditorWindow editorWindow)
        {
            if (editorWindow == null || editorWindow.position.width < 2f || editorWindow.position.height < 2f) return null;
            try
            {
                Rect position = editorWindow.position;
                int width = Mathf.Max(2, Mathf.RoundToInt(position.width));
                int height = Mathf.Max(2, Mathf.RoundToInt(position.height));
                System.Reflection.MethodInfo readScreenPixel = typeof(UnityEditorInternal.InternalEditorUtility).GetMethod(
                    "ReadScreenPixel",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Vector2), typeof(int), typeof(int) },
                    null);
                if (readScreenPixel == null) return null;
                Color[] pixels = readScreenPixel.Invoke(null, new object[] { new Vector2(position.x, position.y), width, height }) as Color[];
                if (pixels == null || pixels.Length != width * height) return null;

                Texture2D screenshot = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
                {
                    name = "AnimatorWindowScreenshot",
                    hideFlags = HideFlags.HideAndDontSave
                };
                screenshot.SetPixels(pixels);
                screenshot.Apply(false, false);
                return screenshot;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("截取 Animator 原生窗口失败：" + exception.Message);
                return null;
            }
        }

        private void FinishAnimatorScreenshot(bool success, string message)
        {
            EditorApplication.update -= UpdateAnimatorScreenshot;
            EditorApplication.delayCall -= BeginAnimatorWindowScreenshot;
            pendingAnimatorWindow = null;
            pendingAnimatorScreenshotStage = 0;
            pendingAnimatorScreenshotTime = 0d;
            pendingAnimatorScreenshotDeadline = 0d;
            pendingAnimatorScreenshotRow = null;
            if (!success) Debug.LogWarning(message);
            if (animatorScreenshotTotal > 0)
            {
                if (success) animatorScreenshotCompleted++;
                else animatorScreenshotFailed++;
                StartNextAnimatorWindowScreenshot();
            }
            else
            {
                animatorScreenshotStatus = message;
                if (animatorCaptureWindow != null) animatorCaptureWindow.Close();
                animatorCaptureWindow = null;
                ShowNotification(new GUIContent(message));
                Focus();
                Repaint();
            }
        }

        private void CancelAnimatorScreenshots()
        {
            EditorApplication.update -= UpdateAnimatorScreenshot;
            EditorApplication.delayCall -= BeginAnimatorWindowScreenshot;
            EditorApplication.delayCall -= StartNextAnimatorWindowScreenshot;
            animatorScreenshotQueue.Clear();
            pendingAnimatorScreenshotRow = null;
            pendingAnimatorWindow = null;
            animatorScreenshotTotal = 0;
            if (animatorCaptureWindow != null) animatorCaptureWindow.Close();
            animatorCaptureWindow = null;
            animatorScreenshotStatus = L("截图已取消，已完成 ", "Capture canceled; completed ") + animatorScreenshotCompleted + L(" 张", " captures");
            Repaint();
        }

        private void ClearGeneratedEffectPreviews()
        {
            foreach (ResourceRow row in rows) DestroyGeneratedPreview(row);
        }

        private static void DestroyGeneratedPreview(ResourceRow row)
        {
            if (row == null || row.generatedEffectPreview == null) return;
            DestroyImmediate(row.generatedEffectPreview);
            row.generatedEffectPreview = null;
        }

        private static Texture2D RenderEffectPreview(GameObject prefab, float previewTime, int width, int height)
        {
            if (prefab == null) return null;
            GameObject instance = null;
            PreviewRenderUtility previewUtility = null;
            try
            {
                instance = Instantiate(prefab);
                instance.name = "ArtResourceSheet_EffectPreview";
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = Vector3.zero;
                instance.SetActive(true);

                // 使用 Unity 自带的独立 Preview Scene，效果与 Prefab 预览窗口一致，
                // 不再把临时对象和相机放进当前编辑场景。
                previewUtility = new PreviewRenderUtility(true);
                previewUtility.AddSingleGO(instance);

                float time = Mathf.Max(0f, previewTime);
                foreach (PlayableDirector director in instance.GetComponentsInChildren<PlayableDirector>(true))
                {
                    if (director == null || director.playableAsset == null) continue;
                    director.time = time;
                    director.Evaluate();
                }
                foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
                {
                    if (animator == null) continue;
                    animator.Rebind();
                    animator.Update(0f);
                    animator.Update(time);
                }
                foreach (Animation animation in instance.GetComponentsInChildren<Animation>(true))
                {
                    if (animation == null) continue;
                    animation.Play();
                    foreach (AnimationState state in animation) state.time = time;
                    animation.Sample();
                }
                foreach (ParticleSystem particleSystem in instance.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (particleSystem == null || !particleSystem.gameObject.activeInHierarchy) continue;
                    particleSystem.gameObject.SetActive(true);
                    particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    // 分步推进可让拖尾、子发射器和延迟粒子的状态更接近运行时画面。
                    float simulated = 0f;
                    const float step = 1f / 30f;
                    bool restart = true;
                    while (simulated < time)
                    {
                        float delta = Mathf.Min(step, time - simulated);
                        particleSystem.Simulate(delta, false, restart, true);
                        restart = false;
                        simulated += delta;
                    }
                    if (time <= 0f) particleSystem.Simulate(0f, false, true, true);
                }
                Canvas.ForceUpdateCanvases();

                Bounds bounds;
                if (!TryGetRendererBounds(instance, out bounds)) bounds = new Bounds(Vector3.zero, Vector3.one * 2f);

                Camera previewCamera = previewUtility.camera;
                previewCamera.orthographic = true;
                previewCamera.clearFlags = CameraClearFlags.SolidColor;
                previewCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                previewCamera.cullingMask = ~0;
                previewCamera.nearClipPlane = 0.01f;
                previewCamera.farClipPlane = Mathf.Max(100f, bounds.size.z + 50f);
                previewCamera.allowHDR = false;
                previewCamera.allowMSAA = true;
                previewCamera.aspect = width / (float)height;
                previewCamera.transform.position = new Vector3(bounds.center.x, bounds.center.y, bounds.min.z - 10f);
                previewCamera.transform.rotation = Quaternion.identity;
                float verticalSize = bounds.extents.y;
                float horizontalSize = bounds.extents.x / previewCamera.aspect;
                previewCamera.orthographicSize = Mathf.Max(0.01f, Mathf.Max(verticalSize, horizontalSize) * 1.18f);

                if (previewUtility.lights != null && previewUtility.lights.Length > 0)
                {
                    previewUtility.lights[0].intensity = 1f;
                    previewUtility.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                }
                if (previewUtility.lights != null && previewUtility.lights.Length > 1)
                    previewUtility.lights[1].intensity = 0.5f;

                Texture2D blackPass = null;
                Texture2D whitePass = null;
                try
                {
                    blackPass = RenderEffectPreviewPass(previewUtility, previewCamera, Color.black, width, height);
                    whitePass = RenderEffectPreviewPass(previewUtility, previewCamera, Color.white, width, height);
                    return ReconstructTransparentEffect(blackPass, whitePass);
                }
                finally
                {
                    if (blackPass != null) DestroyImmediate(blackPass);
                    if (whitePass != null) DestroyImmediate(whitePass);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("生成特效 Prefab 预览失败，可改用手动参考图：" + exception.Message);
                return null;
            }
            finally
            {
                if (previewUtility != null) previewUtility.Cleanup();
                if (instance != null) DestroyImmediate(instance);
            }
        }

        private static Texture2D RenderEffectPreviewPass(PreviewRenderUtility previewUtility, Camera previewCamera, Color background, int width, int height)
        {
            previewCamera.backgroundColor = new Color(background.r, background.g, background.b, 1f);
            previewUtility.BeginStaticPreview(new Rect(0f, 0f, width, height));
            previewUtility.Render(true);
            return previewUtility.EndStaticPreview();
        }

        private static Texture2D ReconstructTransparentEffect(Texture2D blackPass, Texture2D whitePass)
        {
            if (blackPass == null || whitePass == null) return null;
            int width = blackPass.width;
            int height = blackPass.height;
            Color[] blackPixels = blackPass.GetPixels();
            Color[] whitePixels = whitePass.GetPixels();
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var output = new Color[blackPixels.Length];

            for (int i = 0; i < output.Length; i++)
            {
                Color black = blackPixels[i];
                Color white = whitePixels[i];
                float difference = Mathf.Max(white.r - black.r, Mathf.Max(white.g - black.g, white.b - black.b));
                float normalAlpha = Mathf.Clamp01(1f - difference);

                // Additive 材质在黑白两次渲染中不会产生标准 Alpha，使用黑底上的亮度补足覆盖率。
                float additiveAlpha = Mathf.Max(black.r, Mathf.Max(black.g, black.b));
                float alpha = Mathf.Max(normalAlpha, additiveAlpha);
                if (alpha <= 0.002f)
                {
                    output[i] = Color.clear;
                    continue;
                }

                output[i] = new Color(
                    Mathf.Clamp01(black.r / alpha),
                    Mathf.Clamp01(black.g / alpha),
                    Mathf.Clamp01(black.b / alpha),
                    alpha);
            }

            result.SetPixels(output);
            result.Apply();
            return result;
        }

        private static bool TryGetRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Bounds rendererBounds = renderer.bounds;
                Vector3 size = rendererBounds.size;
                if (size.sqrMagnitude <= 0.000001f || float.IsNaN(size.x) || float.IsInfinity(size.x)) continue;
                if (!hasBounds)
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
                else bounds.Encapsulate(rendererBounds);
            }
            return hasBounds;
        }

        private static void FixTransparentEffectPixels(Texture2D texture)
        {
            // 粒子常用 Additive / 预乘 Alpha 材质。直接放进透明 PNG 时会出现黑边、发暗或完全透明，
            // 这里转回适合 Excel 合成的直通 Alpha，并为 Additive 像素补出透明度。
            Color[] pixels = texture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color color = pixels[i];
                float rgbMax = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
                // 某些 Built-in/URP 版本在 Camera.Render 后会把透明清屏色读成 A=1 的黑色，
                // 因此不能只依赖原始 Alpha，纯黑清屏像素需要明确恢复为透明。
                if (rgbMax <= 0.003f)
                {
                    color = new Color(0f, 0f, 0f, 0f);
                }
                else if (color.a <= 0.001f)
                {
                    color.a = Mathf.Clamp01(rgbMax);
                    float divisor = Mathf.Max(color.a, 0.001f);
                    color.r = Mathf.Clamp01(color.r / divisor);
                    color.g = Mathf.Clamp01(color.g / divisor);
                    color.b = Mathf.Clamp01(color.b / divisor);
                }
                else if (color.a > 0.001f && color.a < 0.999f)
                {
                    color.r = Mathf.Clamp01(color.r / color.a);
                    color.g = Mathf.Clamp01(color.g / color.a);
                    color.b = Mathf.Clamp01(color.b / color.a);
                }
                pixels[i] = color;
            }
            texture.SetPixels(pixels);
            texture.Apply();
        }

        private static Texture2D RenderFullSpinePreview(UnityEngine.Object skeletonDataAsset, int width, int height)
        {
            GameObject spineObject = null;
            GameObject cameraObject = null;
            RenderTexture renderTexture = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (!IsSpineSkeletonDataAsset(skeletonDataAsset) || spineSkeletonAnimationType == null) return null;
                System.Reflection.MethodInfo factory = spineSkeletonAnimationType
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "NewSkeletonAnimationGameObject") return false;
                        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length == 2 &&
                               parameters[0].ParameterType.IsInstanceOfType(skeletonDataAsset) &&
                               parameters[1].ParameterType == typeof(bool);
                    });
                if (factory == null) throw new MissingMethodException("Spine SkeletonAnimation 预览创建接口不存在。");
                Component skeletonAnimation = factory.Invoke(null, new object[] { skeletonDataAsset, true }) as Component;
                if (skeletonAnimation == null) return null;
                spineObject = skeletonAnimation.gameObject;
                object skeleton = spineSkeletonAnimationType.GetProperty("Skeleton")?.GetValue(skeletonAnimation, null);
                if (skeleton == null) return null;

                spineObject.name = "ArtResourceSheet_SpinePreview";
                spineObject.hideFlags = HideFlags.HideAndDontSave;
                SetLayerRecursively(spineObject, 31);
                InvokeSpineNoArg(skeleton, "SetToSetupPose");
                InvokeSpineNoArg(skeleton, "UpdateWorldTransform");
                InvokeSpineNoArg(skeletonAnimation, "LateUpdate");

                Renderer[] renderers = spineObject.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled || renderer.bounds.size.sqrMagnitude <= 0.000001f) continue;
                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (!hasBounds) return null;

                cameraObject = new GameObject("ArtResourceSheet_SpineCamera") { hideFlags = HideFlags.HideAndDontSave };
                Camera previewCamera = cameraObject.AddComponent<Camera>();
                previewCamera.orthographic = true;
                previewCamera.clearFlags = CameraClearFlags.SolidColor;
                previewCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                previewCamera.cullingMask = 1 << 31;
                previewCamera.nearClipPlane = 0.01f;
                previewCamera.farClipPlane = 100f;
                previewCamera.allowHDR = false;
                previewCamera.allowMSAA = true;
                previewCamera.aspect = width / (float)height;
                previewCamera.transform.position = new Vector3(bounds.center.x, bounds.center.y, bounds.center.z - 10f);
                previewCamera.transform.rotation = Quaternion.identity;
                float verticalSize = bounds.extents.y;
                float horizontalSize = bounds.extents.x / previewCamera.aspect;
                previewCamera.orthographicSize = Mathf.Max(0.01f, Mathf.Max(verticalSize, horizontalSize) * 1.12f);

                renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                renderTexture.antiAliasing = 1;
                previewCamera.targetTexture = renderTexture;
                previewCamera.Render();

                RenderTexture.active = renderTexture;
                var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply();
                return result;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("生成完整 Spine 预览失败，将尝试 Unity 默认预览：" + exception.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
                if (cameraObject != null) DestroyImmediate(cameraObject);
                if (spineObject != null) DestroyImmediate(spineObject);
            }
        }

        private static void InvokeSpineNoArg(object target, string methodName)
        {
            System.Reflection.MethodInfo method = target.GetType().GetMethod(methodName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, methodName);
            method.Invoke(target, null);
        }

        private static void SetLayerRecursively(GameObject target, int layer)
        {
            target.layer = layer;
            foreach (Transform child in target.transform) SetLayerRecursively(child.gameObject, layer);
        }

        private static Texture2D RenderInspectorPreview(UnityEngine.Object asset, int width, int height)
        {
            UnityEditor.Editor previewEditor = null;
            try
            {
                previewEditor = UnityEditor.Editor.CreateEditor(asset);
                if (previewEditor == null) return null;
                Texture2D preview = previewEditor.RenderStaticPreview(
                    AssetDatabase.GetAssetPath(asset),
                    AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(asset)),
                    width,
                    height);
                if (preview != null) return preview;
                return AssetPreview.GetAssetPreview(asset);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("无法生成 Spine Preview，将留空参考图：" + exception.Message);
                return null;
            }
            finally
            {
                if (previewEditor != null) DestroyImmediate(previewEditor);
            }
        }

        private static ExportImage EncodeTexture(Texture source)
        {
            int width = Mathf.Max(1, source.width);
            int height = Mathf.Max(1, source.height);
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return new ExportImage { bytes = copy.EncodeToPNG(), width = width, height = height };
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                DestroyImmediate(copy);
            }
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Trim();
        }

        private static string GetProjectName()
        {
            string projectName = PlayerSettings.productName;
            if (!string.IsNullOrWhiteSpace(projectName)) return projectName.Trim();

            DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath);
            return projectDirectory == null || string.IsNullOrWhiteSpace(projectDirectory.Name)
                ? "项目"
                : projectDirectory.Name;
        }

        private static int CategoryOrder(string type)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "ui": return 0;
                case "spine":
                case "spine动画": return 1;
                case "effect":
                case "特效": return 2;
                case "动画控制器": return 3;
                case "模型": return 4;
                case "模型动画": return 5;
                default: return 100;
            }
        }

        private int GameCategoryOrder(string category)
        {
            EnsureCategoryDefinitions();
            category = NormalizeLegacyCategoryName(category);
            int index = categoryDefinitions.FindIndex(item => string.Equals(item.name, category, StringComparison.Ordinal));
            return index < 0 ? categoryDefinitions.Count : index;
        }

        private static string NormalizeLegacyCategoryName(string category)
        {
            return string.Equals(category, "奖金游戏", StringComparison.Ordinal) ? "彩金游戏" : category;
        }

        private static string ExportGroupLabel(ResourceRow row, bool useGameClassification)
        {
            if (!useGameClassification) return row.type;
            string category = string.IsNullOrEmpty(row.gameCategory) ? "未分类" : row.gameCategory;
            return NormalizeLegacyCategoryName(category);
        }

        private static string ExportTypeLabel(string type)
        {
            if (string.Equals(type, "特效", StringComparison.OrdinalIgnoreCase)) return "Effect";
            if (string.Equals(type, "Spine动画", StringComparison.OrdinalIgnoreCase)) return "Spine";
            return type;
        }

        private static class XlsxWriter
        {
            private const string Xmlns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            private const string Relns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            public static void Write(string path, string section, bool includeSection, bool useGameClassification, IList<ResourceRow> rows, IList<ExportImage> images)
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    int imageCount = images.Count(x => x != null && x.bytes != null);
                    AddText(zip, "[Content_Types].xml", ContentTypes(imageCount > 0));
                    AddText(zip, "_rels/.rels", PackageRels());
                    AddText(zip, "docProps/app.xml", AppProps());
                    AddText(zip, "docProps/core.xml", CoreProps());
                    AddText(zip, "xl/workbook.xml", WorkbookXml());
                    AddText(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                    AddText(zip, "xl/styles.xml", Styles());
                    AddText(zip, "xl/worksheets/sheet1.xml", Sheet(rows, section, includeSection, useGameClassification, imageCount > 0, images));
                    AddText(zip, "xl/worksheets/sheet2.xml", IndexSheet(rows, images, useGameClassification));

                    if (imageCount > 0)
                    {
                        AddText(zip, "xl/worksheets/_rels/sheet1.xml.rels", SheetRels());
                        AddText(zip, "xl/drawings/drawing1.xml", Drawing(rows, images, includeSection, useGameClassification));
                        AddText(zip, "xl/drawings/_rels/drawing1.xml.rels", DrawingRels(images));
                        int n = 1;
                        foreach (ExportImage image in images.Where(x => x != null && x.bytes != null)) AddBytes(zip, "xl/media/image" + n++ + ".png", image.bytes);
                    }
                }
            }

            private static string Sheet(IList<ResourceRow> rows, string section, bool includeSection, bool useGameClassification, bool hasImages, IList<ExportImage> images = null)
            {
                var sb = new StringBuilder();
                var mergeCells = new List<string>();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"").Append(Xmlns).Append("\" xmlns:r=\"").Append(Relns).Append("\">");
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\" showGridLines=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                int maxImageWidth = images == null ? 0 : images.Where(x => x != null).Select(x => x.width).DefaultIfEmpty(0).Max();
                // Excel caps column width at 255 characters; oversized images are
                // scaled down inside their corresponding E cell below.
                double imageColumnWidth = Math.Min(255d, Math.Max(42d, Math.Ceiling((maxImageWidth + 10d) / 7d)));
                sb.Append("<cols><col min=\"1\" max=\"1\" width=\"14\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"24\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"32\" customWidth=\"1\"/><col min=\"4\" max=\"4\" width=\"34\" customWidth=\"1\"/><col min=\"5\" max=\"5\" width=\"").Append(imageColumnWidth.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/></cols><sheetData>");
                sb.Append(Row(1, 24, 1, new[] { "类型", "名称", "输出名", "说明", "参考图" }));
                int excelRow = 2;
                if (includeSection)
                {
                    sb.Append(Row(excelRow, 32, 2, new[] { section, "", "", "", "" }));
                    mergeCells.Add("A" + excelRow + ":E" + excelRow);
                    excelRow++;
                }
                string previousGroup = null;
                for (int i = 0; i < rows.Count; i++)
                {
                    ResourceRow row = rows[i];
                    string group = ExportGroupLabel(row, useGameClassification);
                    if (!string.Equals(previousGroup, group, StringComparison.Ordinal))
                    {
                        int groupStyle = useGameClassification ? GameCategoryStyle(group) : 2;
                        int groupHeight = useGameClassification ? 70 : 32;
                        sb.Append(Row(excelRow, groupHeight, groupStyle, new[] { group, "", "", "", "" }));
                        mergeCells.Add("A" + excelRow + ":E" + excelRow);
                        excelRow++;
                        previousGroup = group;
                    }
                    int rowHeight = ImageRowHeight(images != null && i < images.Count ? images[i] : null);
                    sb.Append(Row(excelRow++, rowHeight, DataRowStyle(row.type), new[] { ExportTypeLabel(row.type), row.displayName, row.outputName, row.description, "" }));
                }
                // 阅读页包含合并的分组标题，不做自动筛选；筛选和排序使用无合并行的资源索引页。
                sb.Append("</sheetData>");
                if (mergeCells.Count > 0)
                {
                    sb.Append("<mergeCells count=\"").Append(mergeCells.Count).Append("\">");
                    foreach (string reference in mergeCells) sb.Append("<mergeCell ref=\"").Append(reference).Append("\"/>");
                    sb.Append("</mergeCells>");
                }
                sb.Append("<pageMargins left=\"0.3\" right=\"0.3\" top=\"0.5\" bottom=\"0.5\" header=\"0.2\" footer=\"0.2\"/>");
                if (hasImages) sb.Append("<drawing r:id=\"rId1\"/>");
                return sb.Append("</worksheet>").ToString();
            }

            private static string IndexSheet(IList<ResourceRow> rows, IList<ExportImage> images, bool useGameClassification)
            {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"").Append(Xmlns).Append("\">");
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\" showGridLines=\"0\"><pane xSplit=\"3\" ySplit=\"1\" topLeftCell=\"D2\" activePane=\"bottomRight\" state=\"frozen\"/></sheetView></sheetViews>");
                sb.Append("<cols><col min=\"1\" max=\"1\" width=\"8\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"18\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"17\" customWidth=\"1\"/><col min=\"4\" max=\"4\" width=\"28\" customWidth=\"1\"/><col min=\"5\" max=\"5\" width=\"32\" customWidth=\"1\"/><col min=\"6\" max=\"6\" width=\"58\" customWidth=\"1\"/><col min=\"7\" max=\"7\" width=\"12\" customWidth=\"1\"/><col min=\"8\" max=\"8\" width=\"20\" customWidth=\"1\"/><col min=\"9\" max=\"9\" width=\"54\" customWidth=\"1\"/><col min=\"10\" max=\"10\" width=\"24\" customWidth=\"1\"/><col min=\"11\" max=\"11\" width=\"16\" customWidth=\"1\"/></cols><sheetData>");
                sb.Append(Row(1, 32, 12, new[] { "序号", "游戏分类", "资源类型", "名称", "输出名", "Unity 路径", "格式", "预览尺寸(px)", "说明", "资源标识", "审核状态" }));
                for (int i = 0; i < rows.Count; i++)
                {
                    ResourceRow row = rows[i];
                    string assetPath = AssetDatabase.GetAssetPath(row.asset) ?? string.Empty;
                    string extension = Path.GetExtension(assetPath).TrimStart('.').ToUpperInvariant();
                    ExportImage image = images != null && i < images.Count ? images[i] : null;
                    string previewSize = image == null ? string.Empty : image.width + " × " + image.height;
                    string category = useGameClassification
                        ? NormalizeLegacyCategoryName(string.IsNullOrEmpty(row.gameCategory) ? "未分类" : row.gameCategory)
                        : string.Empty;
                    sb.Append(IndexRow(i + 2, i % 2 == 0 ? 13 : 14, i + 1, new[]
                    {
                        category, ExportTypeLabel(row.type), row.displayName,
                        row.outputName, assetPath, extension, previewSize, row.description,
                        GetAssetOrderKey(row.asset), ""
                    }));
                }
                sb.Append("</sheetData><autoFilter ref=\"A1:K").Append(Math.Max(1, rows.Count + 1)).Append("\"/>");
                sb.Append("<pageMargins left=\"0.3\" right=\"0.3\" top=\"0.5\" bottom=\"0.5\" header=\"0.2\" footer=\"0.2\"/>");
                return sb.Append("</worksheet>").ToString();
            }

            private static int GameCategoryStyle(string category)
            {
                switch (category)
                {
                    case "普通游戏": return 4;
                    case "免费游戏": return 5;
                    case "彩金游戏": return 6;
                    case "加载界面": return 7;
                    default: return 8;
                }
            }

            private static int DataRowStyle(string type)
            {
                string value = (type ?? string.Empty).Trim().ToLowerInvariant();
                if (value == "ui") return 3;
                if (value == "spine" || value == "spine动画") return 9;
                if (value == "effect" || value == "特效") return 10;
                return 11;
            }

            private static string Row(int row, int height, int style, string[] values)
            {
                var sb = new StringBuilder("<row r=\"").Append(row).Append("\" ht=\"").Append(height).Append("\" customHeight=\"1\">");
                for (int i = 0; i < values.Length; i++)
                    sb.Append("<c r=\"").Append((char)('A' + i)).Append(row).Append("\" s=\"").Append(style).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(E(values[i] ?? "")).Append("</t></is></c>");
                return sb.Append("</row>").ToString();
            }

            private static string IndexRow(int row, int style, int sequence, string[] values)
            {
                var sb = new StringBuilder("<row r=\"").Append(row).Append("\" ht=\"29\" customHeight=\"1\">");
                sb.Append("<c r=\"A").Append(row).Append("\" s=\"").Append(style).Append("\" t=\"n\"><v>").Append(sequence).Append("</v></c>");
                for (int i = 0; i < values.Length; i++)
                    sb.Append("<c r=\"").Append((char)('B' + i)).Append(row).Append("\" s=\"").Append(i == values.Length - 1 ? 15 : style).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(E(values[i] ?? "")).Append("</t></is></c>");
                return sb.Append("</row>").ToString();
            }

            private static string Drawing(IList<ResourceRow> rows, IList<ExportImage> images, bool includeSection, bool useGameClassification)
            {
                var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"").Append(Relns).Append("\">");
                int relation = 1;
                int zeroBasedRow = includeSection ? 2 : 1;
                string previousGroup = null;
                int maxImageWidth = images.Where(x => x != null).Select(x => x.width).DefaultIfEmpty(0).Max();
                double columnWidth = Math.Min(255d, Math.Max(42d, Math.Ceiling((maxImageWidth + 10d) / 7d)));
                double cellWidthPx = Math.Floor(columnWidth * 7d + 5d);
                for (int i = 0; i < images.Count; i++)
                {
                    string group = ExportGroupLabel(rows[i], useGameClassification);
                    if (!string.Equals(previousGroup, group, StringComparison.Ordinal))
                    {
                        zeroBasedRow++;
                        previousGroup = group;
                    }
                    if (images[i] == null || images[i].bytes == null)
                    {
                        zeroBasedRow++;
                        continue;
                    }
                    double cellHeightPx = ImageRowHeight(images[i]) * 4d / 3d;
                    double scale = Math.Min(1d, Math.Min((cellWidthPx - 10d) / images[i].width, (cellHeightPx - 10d) / images[i].height));
                    long widthEmu = Math.Max(1L, (long)Math.Floor(images[i].width * scale * 9525d));
                    long heightEmu = Math.Max(1L, (long)Math.Floor(images[i].height * scale * 9525d));
                    long leftEmu = (long)Math.Floor((cellWidthPx * 9525d - widthEmu) / 2d);
                    long topEmu = (long)Math.Floor((cellHeightPx * 9525d - heightEmu) / 2d);
                    sb.Append("<xdr:twoCellAnchor editAs=\"twoCell\"><xdr:from><xdr:col>4</xdr:col><xdr:colOff>").Append(leftEmu).Append("</xdr:colOff><xdr:row>").Append(zeroBasedRow).Append("</xdr:row><xdr:rowOff>").Append(topEmu).Append("</xdr:rowOff></xdr:from>");
                    sb.Append("<xdr:to><xdr:col>4</xdr:col><xdr:colOff>").Append(leftEmu + widthEmu).Append("</xdr:colOff><xdr:row>").Append(zeroBasedRow).Append("</xdr:row><xdr:rowOff>").Append(topEmu + heightEmu).Append("</xdr:rowOff></xdr:to>");
                    sb.Append("<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"").Append(relation).Append("\" name=\"参考图 ").Append(relation).Append("\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr></xdr:nvPicPr>");
                    sb.Append("<xdr:blipFill><a:blip r:embed=\"rId").Append(relation).Append("\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:xfrm/><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:twoCellAnchor>");
                    relation++;
                    zeroBasedRow++;
                }
                return sb.Append("</xdr:wsDr>").ToString();
            }

            private static int ImageRowHeight(ExportImage image)
            {
                return image == null ? 78 : Math.Min(409, Math.Max(78, (int)Math.Ceiling(image.height * 0.75 + 8)));
            }

            private static string DrawingRels(IList<ExportImage> images)
            {
                var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                int n = 1;
                foreach (ExportImage ignored in images.Where(x => x != null && x.bytes != null)) sb.Append("<Relationship Id=\"rId").Append(n).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image").Append(n++).Append(".png\"/>");
                return sb.Append("</Relationships>").ToString();
            }

            private static string Styles() =>
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"" + Xmlns + "\">" +
                "<fonts count=\"3\">" +
                "<font><sz val=\"12\"/><name val=\"微软雅黑\"/><family val=\"2\"/><charset val=\"134\"/></font>" +
                "<font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"12\"/><name val=\"微软雅黑\"/><family val=\"2\"/><charset val=\"134\"/></font>" +
                "<font><b/><color rgb=\"FF1F1F1F\"/><sz val=\"20\"/><name val=\"微软雅黑\"/><family val=\"2\"/><charset val=\"134\"/></font>" +
                "</fonts>" +
                "<fills count=\"15\">" +
                "<fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1B60A1\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF00B0F0\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF5B9BD5\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF5B9BD5\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF5B9BD5\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF5B9BD5\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF5B9BD5\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE2F0D9\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFCE4D6\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF3F7FC\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF2CC\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "</fills>" +
                "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"><color rgb=\"FF000000\"/></left><right style=\"thin\"><color rgb=\"FF000000\"/></right><top style=\"thin\"><color rgb=\"FF000000\"/></top><bottom style=\"thin\"><color rgb=\"FF000000\"/></bottom><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"16\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                CategoryXf(5) + CategoryXf(6) + CategoryXf(7) + CategoryXf(8) + CategoryXf(9) +
                DataXf(10) + DataXf(11) + DataXf(12) +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +
                IndexDataXf(0) + IndexDataXf(13) + IndexDataXf(14) +
                "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

            private static string CategoryXf(int fillId) =>
                "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"" + fillId + "\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>";

            private static string DataXf(int fillId) =>
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"" + fillId + "\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>";
            private static string IndexDataXf(int fillId) =>
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"" + fillId + "\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\" wrapText=\"1\"/></xf>";
            private static string ContentTypes(bool images) => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + (images ? "<Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>" : "") + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/><Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/></Types>";
            private static string PackageRels() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";
            private static string WorkbookXml() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"" + Xmlns + "\" xmlns:r=\"" + Relns + "\"><sheets><sheet name=\"美术资源表\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"资源索引\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>";
            private static string WorkbookRels() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
            private static string SheetRels() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>";
            private static string AppProps() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>Unity 美术资源表导出工具</Application></Properties>";
            private static string CoreProps() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:creator>Unity</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:created></cp:coreProperties>";
            private static string E(string value) => SecurityElement.Escape(value) ?? string.Empty;
            private static void AddText(ZipArchive zip, string name, string text) => AddBytes(zip, name, new UTF8Encoding(false).GetBytes(text));
            private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                using (Stream target = entry.Open()) target.Write(bytes, 0, bytes.Length);
            }
        }
    }
