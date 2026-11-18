using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Core.Models;
using Core.Services;
using Desktop.Services;
using Desktop.ViewModels;
using Modules.SOP;
using Modules.SOP.Models;
using SkiaSharp;

namespace Desktop.Views
{
    /// <summary>
    /// SOP模块视图
    /// </summary>
    public partial class SOPModuleView : UserControl
    {
        public SopPanelViewModel Panel { get; } = new();

        // YAML工作流路径
        private string _currentYamlPath = "";

        public SOPModuleView()
        {
            InitializeComponent();
            LoadDefaultWorkflow();
            LoadRecipeList();
        }

        /// <summary>
        /// 刷新缩略图：取主视图当前帧，编码为缩略图显示到面板。
        /// 仅当开关 EnableThumbnail 开启时执行；帧可能被后台线程替换/释放，读取失败忽略。
        /// 仅在检测成功（PASS）时调用 —— 不持续刷新，仅展示成功那一帧。
        /// </summary>
        private void RefreshThumbnailFromCurrentFrame()
        {
            if (!SopCaptureConfigManager.Instance.Config.EnableThumbnail) return;

            var vm = GetMainViewModel();
            if (vm == null) return;

            var frame = vm.RoiEditorViewModel?.CurrentImage ?? vm.CurrentImage;
            if (frame == null)
            {
                ThumbnailImage.Source = null;
                ThumbnailPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var bmp = SkBitmapToBitmapImage(frame, 360);
                if (bmp != null)
                {
                    ThumbnailImage.Source = bmp;
                    ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                // 帧正在被后台线程释放时读取可能失败，下一拍重试
            }
        }

        /// <summary>
        /// 把 SKBitmap 缩放为最大宽度 maxWidth 的缩略图并转成 WPF BitmapImage。
        /// 内部先 Copy 出独立副本，避免外部帧被并发释放导致异常。
        /// </summary>
        private static BitmapImage? SkBitmapToBitmapImage(SKBitmap src, int maxWidth)
        {
            using var copy = src.Copy();
            if (copy == null) return null;

            if (copy.Width > maxWidth)
            {
                var nh = (int)(copy.Height * (double)maxWidth / copy.Width);
                using var resized = copy.Resize(new SKImageInfo(maxWidth, nh), SKFilterQuality.Medium);
                return resized == null ? null : EncodeToBitmapImage(resized);
            }
            return EncodeToBitmapImage(copy);
        }

        /// <summary>
        /// 将 SKBitmap 编码为 PNG 并包装为可冻结的 WPF BitmapImage。
        /// </summary>
        private static BitmapImage? EncodeToBitmapImage(SKBitmap bmp)
        {
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return null;

            var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // 立即解码，脱离源流
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        /// <summary>
        /// 获取主视图模型
        /// </summary>
        private MainViewModel? GetMainViewModel()
        {
            if (DataContext is MainViewModel vm)
                return vm;
            // 尝试从父窗口获取
            var window = Window.GetWindow(this);
            if (window?.DataContext is MainViewModel mainVm)
                return mainVm;
            return null;
        }

        /// <summary>
        /// 控件载入后，将默认/上次加载的配方同步给运行时 MainViewModel.SopWorkflowPath，
        /// 确保用户不手动切换配方、直接启动 SOP 检测时也使用该配方。
        /// </summary>
        private void SOPModuleView_Loaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentYamlPath) || !System.IO.File.Exists(_currentYamlPath))
                return;
            var vm = GetMainViewModel();
            if (vm != null) vm.SopWorkflowPath = _currentYamlPath;

            // 初始化缩略图 / 违规存图 开关状态（来自持久化配置）
            var capCfg = SopCaptureConfigManager.Instance.Config;
            ThumbnailCheckBox.IsChecked = capCfg.EnableThumbnail;
            ViolationCaptureCheckBox.IsChecked = capCfg.EnableViolationCapture;
            ThumbnailHost.Visibility = capCfg.EnableThumbnail ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 「检测缩略图」开关切换：保存配置并折叠/展开缩略图区。
        /// </summary>
        private void ThumbnailCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            var enabled = ThumbnailCheckBox.IsChecked == true;
            SopCaptureConfigManager.Instance.Config.EnableThumbnail = enabled;
            SopCaptureConfigManager.Instance.Save();
            ThumbnailHost.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                ThumbnailImage.Source = null;
                ThumbnailPlaceholder.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 「违规保存截图」开关切换：仅保存配置。
        /// </summary>
        private void ViolationCaptureCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            SopCaptureConfigManager.Instance.Config.EnableViolationCapture = ViolationCaptureCheckBox.IsChecked == true;
            SopCaptureConfigManager.Instance.Save();
        }

        /// <summary>
        /// 配方不可用时的占位提示。
        /// 原先这里塞的是"检测车辆 / 车牌识别 / 安全验证 / 触发PLC"四条示例步骤，
        /// 配方加载失败时会显示它们，看起来像系统在做车牌检测，纯误导，已删除。
        /// </summary>
        private void ShowNoRecipeHint(string reason)
        {
            _currentYamlPath = "";
            Panel.Steps.Clear();
            AddStep("!", "未加载任何 SOP 配方", reason, "no_recipe");

            AddLog(reason);
        }

        /// <summary>
        /// 加载默认工作流：优先加载「上次加载的配方」，否则加载目录中第一个可用配方；
        /// 失败则使用示例步骤。加载成功后持久化，供下次启动默认加载。
        /// </summary>
        private void LoadDefaultWorkflow()
        {
            var yamlPath = GetLastRecipePath() ?? FindDefaultYamlFile();
            if (!string.IsNullOrEmpty(yamlPath) && System.IO.File.Exists(yamlPath))
            {
                try
                {
                    LoadStepsFromYaml(yamlPath);
                    _currentYamlPath = yamlPath;
                    SaveLastRecipePath(yamlPath);
                    AddLog($"已加载配方: {System.IO.Path.GetFileName(yamlPath)}");
                    return;
                }
                catch (Exception ex)
                {
                    ShowNoRecipeHint($"配方解析失败：{ex.Message}（文件: {yamlPath}）");
                    return;
                }
            }

            ShowNoRecipeHint($"未找到任何配方。已查找目录：{RecipeFile.SopDir}");
        }

        /// <summary>
        /// 查找默认YAML文件
        /// </summary>
        private static string? FindDefaultYamlFile() => RecipeFile.ListAll().FirstOrDefault();

        /// <summary>读取上次加载的配方（统一由 RecipeFile 记忆，存相对 configs/sop 的路径）。</summary>
        private static string? GetLastRecipePath() => RecipeFile.LoadLast();

        /// <summary>持久化上次加载的配方，供下次启动默认加载。</summary>
        private static void SaveLastRecipePath(string fullPath) => RecipeFile.SaveLast(fullPath);

        /// <summary>
        /// 扫描 configs/sop 下的所有配方 YAML，填充产品配方下拉框。
        /// 显示名优先取 YAML 内的 sop.name，回退为文件名。
        /// </summary>
        private void LoadRecipeList()
        {
            RecipeComboBox.Items.Clear();

            var found = RecipeFile.ListAll();

            foreach (var f in found)
            {
                var baseName = System.IO.Path.GetFileNameWithoutExtension(f);
                var display = baseName;
                try
                {
                    var text = File.ReadAllText(f);
                    // [^\r\n#]+ 仅取本行 name 值：排除换行（防止吞掉 version/description）和行内 # 注释
                    var m = Regex.Match(text, @"^\s{0,4}name\s*:\s*([^\r\n#]+)$", RegexOptions.Multiline);
                    if (m.Success)
                    {
                        var val = m.Groups[1].Value.Trim().Trim('"').Trim('\'');
                        if (!string.IsNullOrEmpty(val))
                            display = $"{val}  ·  {baseName}";
                    }
                }
                catch
                {
                    // 解析失败则回退到文件名
                }

                RecipeComboBox.Items.Add(new RecipeItem
                {
                    DisplayName = display,
                    FullPath = System.IO.Path.GetFullPath(f)
                });
            }

            // 选中当前已加载的配方
            if (!string.IsNullOrEmpty(_currentYamlPath))
            {
                var cur = System.IO.Path.GetFullPath(_currentYamlPath);
                foreach (RecipeItem item in RecipeComboBox.Items)
                {
                    if (item.FullPath.Equals(cur, StringComparison.OrdinalIgnoreCase))
                    {
                        RecipeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 刷新产品配方下拉列表（生成/另存新配方后可调用，无需重启程序）
        /// </summary>
        public void RefreshRecipeList() => LoadRecipeList();

        /// <summary>
        /// 产品配方下拉框切换：重载对应工作流并同步给运行时
        /// </summary>
        private void RecipeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = RecipeComboBox.SelectedItem as RecipeItem;
            if (item == null) return;

            // 联动重命名/删除按钮可用状态
            var hasSelection = !string.IsNullOrEmpty(item.FullPath);
            RenameRecipeMenuItem.IsEnabled = hasSelection;
            DeleteRecipeMenuItem.IsEnabled = hasSelection;

            var target = System.IO.Path.GetFullPath(item.FullPath);
            var current = string.IsNullOrEmpty(_currentYamlPath) ? "" : System.IO.Path.GetFullPath(_currentYamlPath);
            if (target.Equals(current, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                LoadStepsFromYaml(item.FullPath);
                _currentYamlPath = item.FullPath;

                // 持久化本次选择，下次启动默认加载该配方
                SaveLastRecipePath(_currentYamlPath);

                // 同步给 MainViewModel，运行时将使用新配方
                var vm = GetMainViewModel();
                if (vm != null) vm.SopWorkflowPath = _currentYamlPath;

                // 换产品配方 → 统计归零（不同产品的产量/不良不应混算）
                ResetCycleStats(silent: true);

                AddLog($"已切换产品配方: {item.DisplayName}");
            }
            catch (Exception ex)
            {
                AddLog($"切换配方失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 展开配方管理菜单（新建 / 重命名 / 删除）
        /// </summary>
        private void RecipeActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecipeActionsContextMenu != null)
            {
                RecipeActionsContextMenu.PlacementTarget = RecipeActionsButton;
                RecipeActionsContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                RecipeActionsContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// 重命名当前配方：仅修改 YAML 中的 sop.name（保留文件名与全部内容）
        /// </summary>
        private void RenameRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecipeComboBox.SelectedItem is not RecipeItem sel) return;
            if (string.IsNullOrEmpty(sel.FullPath) || !System.IO.File.Exists(sel.FullPath))
            {
                MessageBox.Show("该配方文件不存在，无法重命名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var current = ReadSopName(sel.FullPath);
            var dlg = new InputDialog("重命名配方", "请输入新的产品名称：", current)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true) return;

            var newName = dlg.ResultText;
            if (string.IsNullOrEmpty(newName) || newName == current) return;

            try
            {
                ReplaceSopNameInYaml(sel.FullPath, newName);
                AddLog($"配方已重命名为: {newName}");
                LoadRecipeList();
                foreach (RecipeItem item in RecipeComboBox.Items)
                {
                    if (item.FullPath.Equals(System.IO.Path.GetFullPath(sel.FullPath), StringComparison.OrdinalIgnoreCase))
                    {
                        RecipeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 删除当前选中的配方文件（确认后删除，不可恢复）
        /// </summary>
        private void DeleteRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecipeComboBox.SelectedItem is not RecipeItem sel) return;
            if (string.IsNullOrEmpty(sel.FullPath) || !System.IO.File.Exists(sel.FullPath))
            {
                MessageBox.Show("该配方文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var r = MessageBox.Show(
                $"确定要删除配方「{sel.DisplayName}」吗？\n文件：{sel.FullPath}\n\n此操作不可恢复！",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes) return;

            try
            {
                System.IO.File.Delete(sel.FullPath);
                AddLog($"已删除配方文件: {System.IO.Path.GetFileName(sel.FullPath)}");
                LoadRecipeList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 读取 YAML 中 sop 根节点下的 name（保留引号剥离）
        /// </summary>
        private static string ReadSopName(string path)
        {
            var lines = System.IO.File.ReadAllLines(path);
            var sopIndent = -1;
            var inSop = false;

            foreach (var l in lines)
            {
                var t = l.TrimStart();
                var ind = l.Length - t.Length;

                if (!inSop)
                {
                    if (t == "sop:" || t.StartsWith("sop:"))
                    {
                        inSop = true;
                        sopIndent = ind;
                    }
                    continue;
                }

                if (ind > sopIndent && t.StartsWith("name:"))
                {
                    var v = t.Substring("name:".Length).Trim();
                    if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                        v = v.Substring(1, v.Length - 2);
                    return v;
                }

                if (ind <= sopIndent && t.Length > 0) break;
            }

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// 新建配方按钮：弹出对话框，生成新的 YAML 并刷新下拉框
        /// </summary>
        private void NewRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            var templates = new List<RecipeItem>();
            foreach (RecipeItem item in RecipeComboBox.Items)
                templates.Add(item);

            var dlg = new NewRecipeWindow(templates)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true) return;

            var productName = dlg.ProductName;
            var templatePath = dlg.TemplatePath;

            try
            {
                var sopDir = RecipeFile.SopDir;
                System.IO.Directory.CreateDirectory(sopDir);

                var fileName = BuildRecipeFileName(sopDir, productName);
                var fullPath = System.IO.Path.Combine(sopDir, fileName);

                if (string.IsNullOrEmpty(templatePath))
                {
                    // 空白模板：写入最小可用骨架
                    System.IO.File.WriteAllText(fullPath, BuildBlankRecipeYaml(productName), System.Text.Encoding.UTF8);
                    AddLog($"已新建空白配方: {fileName}");
                }
                else
                {
                    // 基于现有配方：复制整文件后仅替换 sop.name
                    System.IO.File.Copy(templatePath, fullPath, overwrite: false);
                    ReplaceSopNameInYaml(fullPath, productName);
                    AddLog($"已基于模板新建配方: {fileName}（源: {System.IO.Path.GetFileName(templatePath)}）");
                }

                LoadRecipeList();

                // 自动选中并切换到新配方
                var target = System.IO.Path.GetFullPath(fullPath);
                foreach (RecipeItem item in RecipeComboBox.Items)
                {
                    if (item.FullPath.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        RecipeComboBox.SelectedItem = item;
                        break;
                    }
                }

                AddLog("提示：可在「🎯 标定区域」中定义区域、在「⚙️ 配置」中调整步骤，并替换 model.path 为专用模型");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"新建配方失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AddLog($"新建配方失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成不冲突的配方文件名：sop_产品名.yaml，冲突时追加 _2/_3...
        /// </summary>
        private static string BuildRecipeFileName(string sopDir, string productName)
        {
            var baseName = "sop_" + SanitizeFileName(productName);
            if (baseName.Length <= 4) // 仅 "sop_"
                baseName = "sop_new";

            var name = baseName;
            var i = 2;
            while (System.IO.File.Exists(System.IO.Path.Combine(sopDir, name + ".yaml")))
            {
                name = $"{baseName}_{i}";
                i++;
            }
            return name + ".yaml";
        }

        /// <summary>
        /// 清洗文件名中的非法字符，空格转下划线
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var s = sb.ToString().Trim().Replace(' ', '_');
            return string.IsNullOrEmpty(s) ? "new" : s;
        }

        /// <summary>
        /// 仅替换 YAML 中 sop 根节点下的 name（保留其余字段与注释）
        /// </summary>
        private static void ReplaceSopNameInYaml(string path, string newName)
        {
            var lines = System.IO.File.ReadAllLines(path);
            var sopIndent = -1;
            var inSop = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                var indent = lines[i].Length - trimmed.Length;

                if (!inSop)
                {
                    if (trimmed == "sop:" || trimmed.StartsWith("sop:"))
                    {
                        inSop = true;
                        sopIndent = indent;
                    }
                    continue;
                }

                // sop 块内的第一个 name: 子项即产品名
                if (indent > sopIndent && trimmed.StartsWith("name:"))
                {
                    lines[i] = new string(' ', indent) + $"name: \"{newName}\"";
                    break;
                }

                // 遇到下一个顶层块（缩进 <= sop）则停止，避免误改其它段
                if (indent <= sopIndent && trimmed.Length > 0)
                    break;
            }

            System.IO.File.WriteAllLines(path, lines);
        }

        /// <summary>
        /// 空白模板：最小可用骨架（步骤1 + 成品漏放校验步）
        /// </summary>
        private static string BuildBlankRecipeYaml(string productName)
        {
            return $@"sop:
  name: ""{productName}""
  version: ""1.0""
  description: ""通过界面新建的空白配方，请在 ROI 标定工具中定义区域并补全步骤""
  settings:
    detectionMode: ""UnifiedDetection""
    confidenceThreshold: 0.5
    stableFrames: 3
    timeoutSeconds: 30
    enableSkipDetection: true
    enableTimeoutDetection: true
  regions: {{}}
  steps:
    - id: ""1""
      name: ""步骤1""
      description: ""请在此定义第一个作业步骤（如取料）""
      timeout: 30
      detection:
        method: ""time_elapsed""
        stable_frames: 2
    - id: ""2""
      name: ""成品校验（漏放检测）""
      description: ""最终成品区漏放校验：请补全 required_objects 列出必放物料""
      timeout: 30
      detection:
        method: ""time_elapsed""
        stable_frames: 2
      required_objects: []
  model:
    path: ""yolo_models/sop_custom_yolov8s.onnx""
    type: ""ObjectDetection""
    confidence: 0.5
    iou: 0.45
    useGpu: true
    classes: []
  monitoring:
    enabled: true
    person_classes: [""person""]
    helmet_classes: [""helmet"", ""safe_helmet"", ""hard_hat""]
    regions: []
    min_confidence: 0.5
    cooldown_seconds: 5
";
        }

        /// <summary>
        /// 从YAML文件加载SOP步骤
        /// </summary>
        private void LoadStepsFromYaml(string yamlPath)
        {
            var workflow = SOPYamlConverter.LoadFromYaml(yamlPath);
            LoadWorkflowSteps(workflow);
        }

        /// <summary>
        /// 从SOPWorkflow加载步骤到UI列表
        /// </summary>
        private void LoadWorkflowSteps(SOPWorkflow workflow)
        {
            Panel.Steps.Clear();

            foreach (var step in workflow.Steps.OrderBy(s => s.Order))
            {
                AddStep(
                    step.StepId.ToString(),
                    step.StepName,
                    step.Description ?? "",
                    step.StepId.ToString()
                );
            }
        }

        /// <summary>
        /// 添加步骤
        /// </summary>
        public void AddStep(string stepNumber, string name, string description, string? id = null)
        {
            var stepItem = new SOPStepItem
            {
                Id = id ?? stepNumber,
                StepNumber = stepNumber,
                Name = name,
                Description = description,
                Status = StepStatus.Pending
            };
            Panel.Steps.Add(stepItem);
        }

        /// <summary>
        /// 添加日志。
        /// 日志统一进「独立运行日志窗口」：这里只写入共享缓冲（<see cref="SopRunLog"/>），
        /// 窗口打开时实时显示、后开时回填历史。
        /// </summary>
        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            SopRunLog.Instance.Append($"[{timestamp}] {message}");
        }

        /// <summary>
        /// 配置按钮点击
        /// </summary>
        /// <summary>
        /// 打开 SOP 区域标定窗口（多相机）：收集所有已连接相机的最新帧与当前 YAML，
        /// 区域按所属相机自动加载到对应画面，保存后重载工作流。
        /// </summary>
        private void RegionButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = GetMainViewModel();
            if (vm == null) return;

            // 收集所有已连接相机的最新帧（主相机优先取 ROI 预览帧；其余取槽位最新帧）
            var frames = new Dictionary<string, SKBitmap>();

            // 主相机：相机预览帧实时写入 RoiEditorViewModel.CurrentImage；
            // 仅运行 SOP 检测时才会同步到 MainViewModel.CurrentImage。
            // 优先取相机预览帧，保证"只开相机、未运行 SOP"也能标定。
            var mainFrame = vm.RoiEditorViewModel?.CurrentImage ?? vm.CurrentImage;
            if (mainFrame != null)
            {
                frames[CameraManager.PrimaryCameraId] = mainFrame.Copy();
            }

            foreach (var slot in CameraManager.Instance.Slots)
            {
                if (slot.CameraId == CameraManager.PrimaryCameraId) continue;
                if (!slot.IsConnected || slot.LatestImageData == null) continue;
                var bmp = CameraFrameConverter.ToSKBitmap(slot.LatestImageData);
                if (bmp != null)
                {
                    frames[slot.CameraId] = bmp;
                }
            }

            if (frames.Count == 0)
            {
                MessageBox.Show("请先开启相机并采集一帧画面，再标定区域。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new SOPRegionEditorWindow(frames, _currentYamlPath);
            if (win.ShowDialog() == true && !string.IsNullOrEmpty(win.SavedYamlPath))
            {
                ReloadFromYaml(win.SavedYamlPath);
            }
        }

        /// <summary>
        /// 用指定 YAML 重新加载步骤与区域显示
        /// </summary>
        private void ReloadFromYaml(string path)
        {
            try
            {
                LoadStepsFromYaml(path);
                _currentYamlPath = path;
                AddLog($"区域已更新，已重载工作流: {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                AddLog($"重载工作流失败: {ex.Message}");
            }
        }

        private async void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            // 打开 SOP 流程预览与编辑窗口：传入当前配方 YAML，保存后自动重载
            var configWindow = new SOPConfigWindow(string.IsNullOrEmpty(_currentYamlPath) ? null : _currentYamlPath);
            configWindow.Owner = Window.GetWindow(this);

            // 传递当前运行时检测配置（手部检测等）
            var viewModel = GetMainViewModel();
            if (viewModel == null) return;
            configWindow.SetHandPoseConfig(viewModel.GetHandPoseConfig());

            if (configWindow.ShowDialog() == true)
            {
                // YAML 已保存 → 重载工作流步骤
                if (!string.IsNullOrEmpty(configWindow.SavedYamlPath))
                {
                    try
                    {
                        LoadStepsFromYaml(configWindow.SavedYamlPath);
                        _currentYamlPath = configWindow.SavedYamlPath;
                        AddLog($"SOP流程已修改并重载: {System.IO.Path.GetFileName(_currentYamlPath)}");

                    }
                    catch (Exception ex)
                    {
                        AddLog($"重载工作流失败: {ex.Message}");
                    }
                }

                // 应用运行时检测配置
                await viewModel.ApplySopConfigurationAsync(_currentYamlPath, configWindow.GetHandPoseConfig());

            }
        }

        /// <summary>
        /// 运行按钮点击
        /// </summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 连接到主视图的 SOP 实时检测
            var viewModel = GetMainViewModel();
            if (viewModel == null)
            {
                MessageBox.Show("无法获取主视图模型", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 如果已经在运行，则停止
            if (viewModel.IsSOPDetecting)
            {
                await viewModel.StopSOPDetectionAsync();
                Panel.SetStatus("检测已停止", new SolidColorBrush(Colors.Gray));
                Panel.SetResult("--", new SolidColorBrush(Colors.Gray));
                Panel.StopTimer();
                ThumbnailImage.Source = null;
                ThumbnailPlaceholder.Visibility = Visibility.Visible;
                UpdateRunButtonState(false);
                AddLog("SOP 实时检测已停止");
                return;
            }

            // 启动 SOP 实时检测
            Panel.SetStatus("启动中...", new SolidColorBrush(Color.FromRgb(24, 144, 255)));
            AddLog("正在启动 SOP 实时检测...");

            // 传递当前YAML工作流路径给MainViewModel
            viewModel.SopWorkflowPath = _currentYamlPath;

            await viewModel.StartSOPDetectionCommand.ExecuteAsync(null);

            // 更新 UI 状态
            if (viewModel.IsSOPDetecting)
            {
                Panel.SetStatus("实时检测中", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
                Panel.SetResult("检测中...", new SolidColorBrush(Colors.Gray));
                ClearStepDurations();
                Panel.StartTimer();
                UpdateRunButtonState(true);
                AddLog("SOP 实时检测已启动");
            }
            else
            {
                Panel.SetStatus("启动失败", new SolidColorBrush(Colors.Red));
                Panel.SetResult("NG", new SolidColorBrush(Colors.Red));
                UpdateRunButtonState(false);
                AddLog("SOP 实时检测启动失败");
            }

            // 订阅 SOP 事件以更新结果
            SubscribeToSOPEvents(viewModel);
        }

        /// <summary>
        /// 订阅 SOP 事件
        /// </summary>
        private void SubscribeToSOPEvents(MainViewModel viewModel)
        {
            if (viewModel.SOPModuleInstance == null) return;

            // 取消旧订阅
            UnsubscribeFromSOPEvents(viewModel);

            // 订阅新事件
            viewModel.SOPModuleInstance.WorkflowCompleted += OnSOPWorkflowCompleted;
            viewModel.SOPModuleInstance.ViolationDetected += OnSOPViolationDetected;
            viewModel.SOPModuleInstance.StepChanged += OnSOPStepChanged;
            viewModel.SOPModuleInstance.StepCompleted += OnSOPStepCompleted;
        }

        /// <summary>
        /// 取消 SOP 事件订阅
        /// </summary>
        private void UnsubscribeFromSOPEvents(MainViewModel viewModel)
        {
            if (viewModel.SOPModuleInstance == null) return;

            viewModel.SOPModuleInstance.WorkflowCompleted -= OnSOPWorkflowCompleted;
            viewModel.SOPModuleInstance.ViolationDetected -= OnSOPViolationDetected;
            viewModel.SOPModuleInstance.StepChanged -= OnSOPStepChanged;
            viewModel.SOPModuleInstance.StepCompleted -= OnSOPStepCompleted;
        }

        /// <summary>
        /// 更新运行按钮状态
        /// </summary>
        private void UpdateRunButtonState(bool isRunning)
        {
            if (isRunning)
            {
                RunButton.Content = "⏹️ 停止";
                RunButton.Background = new SolidColorBrush(Color.FromRgb(255, 77, 79)); // 红色
            }
            else
            {
                RunButton.Content = "▶️ 运行";
                RunButton.Background = new SolidColorBrush(Color.FromRgb(82, 196, 26)); // 绿色
            }
        }

        /// <summary>
        /// 复位按钮点击：清除当前周期结果并把 SOP 流程复位到第一步。
        /// 运行中会先停止实时检测，避免复位后推理线程继续推进步骤。
        /// 注意：Modbus 的「锁存输出」按需求只能由管理员在通信设置里复位，此处不代劳。
        /// </summary>
        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetMainViewModel();
            if (viewModel == null)
            {
                MessageBox.Show("无法获取主视图模型", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                ResetButton.IsEnabled = false;

                // 1) 运行中先停止（停止内部会清缓存/告警并复位状态机）
                if (viewModel.IsSOPDetecting)
                {
                    await viewModel.StopSOPDetectionAsync();
                }
                ThumbnailImage.Source = null;
                ThumbnailPlaceholder.Visibility = Visibility.Visible;

                // 2) 复位状态机工作流（回到第一步）
                viewModel.ResetSOPWorkflow();

                // 3) 复位界面：步骤状态、耗时、计时器、结果与总状态
                ResetStepDisplayState();

                Panel.ResetTimer();

                Panel.SetStatus("已复位", new SolidColorBrush(Colors.Gray));
                Panel.SetResult("--", new SolidColorBrush(Colors.Gray));

                UpdateRunButtonState(false);
                AddLog("🔄 SOP 流程已复位，从第一步重新开始");
            }
            catch (Exception ex)
            {
                AddLog($"复位失败: {ex.Message}");
                MessageBox.Show($"复位失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ResetButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 把步骤列表显示状态全部复位为「未开始」，并清空耗时
        /// </summary>
        private void ResetStepDisplayState()
        {
            foreach (var step in Panel.Steps)
            {
                step.Status = StepStatus.Pending;
                step.Duration = TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 打开独立运行日志窗口
        /// </summary>
        private void LogButton_Click(object sender, RoutedEventArgs e) => SopLogWindow.ShowWindow();

        /// <summary>
        /// SOP 工作流完成事件处理
        /// </summary>
        private void OnSOPWorkflowCompleted(object? sender, Modules.SOP.Models.SOPCompletedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Panel.StopTimer();
                // 将步骤时间轴的耗时统计合并到上方流程卡片
                Panel.SyncDurations(e.StepHistory);
                var isPass = !e.HasViolations;

                // ⭐ 统计：一个完整周期结束（PASS / NG 都计入循环次数）
                Panel.CycleCount++;
                if (!isPass) Panel.NgCycleCount++;

                if (isPass)
                {
                    Panel.SetResult("PASS", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
                    Panel.SetStatus("检测完成", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
                    AddLog($"✅ SOP 检测全部通过 | 总耗时: {Panel.ElapsedText}");
                    // 缩略图由各步成功事件 OnSOPStepCompleted 逐步刷新（末步成功也会刷新），
                    // 此处不再整体刷新，避免重复。
                }
                else
                {
                    Panel.SetResult("FAIL", new SolidColorBrush(Colors.Red));
                    Panel.SetStatus("检测失败", new SolidColorBrush(Colors.Red));
                    AddLog($"❌ SOP 检测不通过，违规数: {e.Violations.Count} | 总耗时: {Panel.ElapsedText}");
                    // 违规（NG）自动保存当前帧截图，作为证据留存
                    SaveViolationSnapshot(e);
                }

                // ⭐ 修复：最后一步完成时 SOPStateMachine.CompleteCurrentStep 走 Completed 分支，
                // 不会触发 StepChanged 事件；这里手动把最后一步 UI 标为 Completed，
                // 否则步骤 5 会一直显示"进行中"。
                if (Panel.Steps.Count > 0)
                {
                    Panel.SetStepResult(Panel.Steps.Count - 1, isPass);
                }
            });
        }

        /// <summary>
        /// SOP 违规检测事件处理
        /// </summary>
        private void OnSOPViolationDetected(object? sender, Modules.SOP.Models.ViolationEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Panel.SetResult("NG", new SolidColorBrush(Colors.Red));

                // ⭐ 统计：违规事件累计（同一周期可能触发多次）
                Panel.ViolationCount++;

                AddLog($"⚠ 违规: [{e.Violation.Type}] {e.Violation.Description}");
            });
        }

        /// <summary>
        /// 清零循环/违规统计。换配方或用户双击数字时调用。
        /// 注意：复位（🔄）刻意不清零——复位只是重开一个周期，产量/不良应跨周期累计。
        /// </summary>
        private void ResetCycleStats(bool silent = false)
        {
            Panel.CycleCount = 0;
            Panel.NgCycleCount = 0;
            Panel.ViolationCount = 0;

            if (!silent) AddLog("🧮 循环/违规统计已清零");
        }

        private void CycleCountTextBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ResetCycleStats();
        }

        private void ViolationCountTextBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ResetCycleStats();
        }

        /// <summary>
        /// 违规（NG）时把当前帧保存为 PNG 截图，按日期分子目录存放。
        /// 仅当开关 EnableViolationCapture 开启时执行；帧可能被后台线程释放，失败静默忽略。
        /// </summary>
        private void SaveViolationSnapshot(Modules.SOP.Models.SOPCompletedEventArgs e)
        {
            try
            {
                var cfg = SopCaptureConfigManager.Instance.Config;
                if (!cfg.EnableViolationCapture) return;

                var vm = GetMainViewModel();
                if (vm == null) return;

                var frame = vm.RoiEditorViewModel?.CurrentImage ?? vm.CurrentImage;
                if (frame == null) return;

                // 复制独立副本，避免持有外部帧引用被后台线程释放
                using var copy = frame.Copy();
                if (copy == null) return;

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var root = string.IsNullOrWhiteSpace(cfg.CaptureDirectory) ? "sop_captures" : cfg.CaptureDirectory;
                var dateDir = System.IO.Path.Combine(baseDir, root, DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dateDir);

                var ts = DateTime.Now.ToString("HHmmss_fff");
                var name = string.IsNullOrEmpty(e.WorkflowName) ? "sop" : SanitizeFileName(e.WorkflowName);
                var fileName = $"{ts}_NG_{name}.png";
                var filePath = System.IO.Path.Combine(dateDir, fileName);

                using var img = SKImage.FromBitmap(copy);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.OpenWrite(filePath);
                data.SaveTo(fs);

                AddLog($"📸 违规截图已保存: {fileName}");
            }
            catch (Exception ex)
            {
                AddLog($"违规截图保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// SOP 步骤变化事件处理
        /// </summary>
        private void OnSOPStepChanged(object? sender, Modules.SOP.Models.StepChangedEventArgs e)
        {
            var history = ((SOPModule)sender!).StateMachine!.StepHistory.ToArray();
            Dispatcher.BeginInvoke(() =>
            {
                Panel.SetCurrentStep(e.CurrentStepId);
                AddLog($"步骤推进: {e.PreviousStepId} → {e.CurrentStepId} ({e.StepName})");
                // 同步已结束步骤的耗时到对应卡片
                Panel.SyncDurations(history);
            });
        }

        /// <summary>
        /// 每步完成事件处理：每步成功（无违规）即把当前帧刷新到缩略图。
        /// 这与旧版「仅整体 PASS 才刷新」不同——现在每一步成功都会更新缩略图，
        /// 便于操作员逐步核对。违规步骤不刷新（避免用 NG 帧覆盖成功帧）。
        /// </summary>
        private void OnSOPStepCompleted(object? sender, Modules.SOP.Models.StepCompletedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (e.HasViolations) return;
                RefreshThumbnailFromCurrentFrame();
                AddLog($"📷 步骤 {e.Step.StepId} 完成(成功)，缩略图已更新");
            });
        }

        #region 步骤耗时同步

        /// <summary>
        /// 清空所有步骤的耗时显示（每次重新运行前调用）
        /// </summary>
        private void ClearStepDurations()
        {
            foreach (var step in Panel.Steps) step.Duration = TimeSpan.Zero;
        }

        #endregion
    }

    /// <summary>
    /// 产品配方下拉项
    /// </summary>
    public class RecipeItem
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public override string ToString() => DisplayName;
    }
}
