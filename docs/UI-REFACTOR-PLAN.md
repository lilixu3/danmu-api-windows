# UI 重构方案（试点：配置页）

> **状态**：待实施。本文件是 UI 重构的唯一执行依据；每完成一步，在 §9 回填「实际命令 / 结果 / 偏差」，
> 并在 `docs/HANDOFF.md` 追加一条会话记录。
>
> **本轮范围**：只重构 **配置页**（`Views/ConfigurationView.axaml`）。
> **概览页本方案完全不碰**（仅作为「不要动的边界」出现一次，见 §6.2）。
>
> **适用范围**：`src/DanmuApi.App` 的视图层。不改 `DanmuApi.Core` / `DanmuApi.Runtime` /
> `DanmuApi.Platform` 的行为；不改 ViewModel 的业务逻辑与命令语义。

---

## 0. 结论

**不换组件库。** 先只重构配置页，用它验证「抽 token → 建外壳词汇 → 重排页面 → 渲染核对」
这套方法本身可行、可复用；验证通过后再决定是否逐页推广。

配置页被选为试点的原因不是它最烂，而是它**最安全**：见 §1.3 —— 它在视觉层的测试契约几乎为零，
可以完整重排而不触发任何断言破裂。等这套流程在配置页跑通，再拿它去啃契约密集的页面（核心页、
工具页）才有把握。

第三方主题库的否决论证压缩在附录 A。

---

## 1. 现状诊断（全部带取证）

### 1.1 配置页当前结构

`Views/ConfigurationView.axaml`（120 行）：

```
Grid ColumnDefinitions="216,*"  ColumnSpacing=20
├── [0] Border { SurfaceBrush 底, HairlineBrush 描边, 1px, CornerRadius=8, Padding=8 }
│      ├── "配置分类" (overline) + "来自当前核心 envs.js" (body-muted)
│      └── ListBox.settings-list  ItemsSource={Categories}  SelectedItem={SelectedCategory}
│             └── ItemTemplate: StackPanel{ Title(SemiBold), Description(11px muted) }
└── [1] Grid RowDefinitions="Auto,*"
       ├── [0] Grid ColumnDefinitions="*,Auto,Auto"
       │      ├── PageTitle (page-title, FontSize 覆盖为 22) + PageSubtitle
       │      ├── TextBox Width=250  Watermark="搜索全部变量名或说明"
       │      └── Button.secondary-action "刷新"
       └── [1] ScrollViewer
              └── StackPanel MaxWidth=960
                     ├── Border.notice-panel  (IsVisible={HasDiagnostic})
                     └── Border.dashboard-panel
                            └── ItemsControl ItemsSource={FilteredVariables}
                                   └── Border.configuration-row  (Padding 0,14 + 底部 1px 线)
                                          └── Grid ColumnDefinitions="*,Auto,Auto"
                                                 ├── StackPanel { Key(等宽 SemiBold), Description,
                                                 │                CurrentValueText, "来源：{Source}" }
                                                 ├── Button.compact-action "编辑"
                                                 └── Button.compact-action "删除"
                                （列表外层无虚拟化；ScrollViewer 整体滚动）
```

对应取证：`Views/ConfigurationView.axaml:27`（216px 两栏）、`:28-32`（手搓侧栏 Border）、
`:38-52`（分类 ListBox）、`:56-72`（页头 + 搜索 + 刷新）、`:74-117`（滚动区 + 变量列表）、
`:80`（`ItemsControl`，无虚拟化）、`:83-111`（行内三段式）、`:97-109`（编辑/删除两个按钮）。

### 1.2 具体问题

| # | 问题 | 取证 | 后果 |
|---|---|---|---|
| C-1 | **没有右侧详情区** | `ConfigurationView.axaml:79-115`：整页只有「侧栏 + 列表」两段 | 与 `AGENTS.md` §10 的「宽屏列表 + 右侧详情」不符；变量说明、有效值来源、默认值都挤在行内 4 行小字里 |
| C-2 | **列表无虚拟化** | `:80` 用 `ItemsControl` + 外层 `ScrollViewer`（`:74`） | 核心 `envs.js` 声明越多变量越慢；`ItemsControl` 会一次性实例化全部行 |
| C-3 | **编辑与删除视觉无差别** | `:97-109` 两个按钮都用 `Classes="compact-action"`（`App.axaml:249-257`：透明底 + `AccentBrush` 文字） | 「删除」是破坏性动作却和「编辑」长得一样，无确认前的视觉警示 |
| C-4 | **行内信息层级扁平** | `:85-96`：Key / Description / CurrentValueText / 来源 四行，字号分别是 12 / 12 / 12 / 11 | 键名与说明权重接近，扫读时抓不住重点 |
| C-5 | **侧栏容器手搓，与工具页/设置页重复** | `:28-32` 的写法在 `SettingsView.axaml:33-37`、`ToolsView.axaml:12` 几乎逐字重复 | 三处各写一遍「浅底 + 圆角 8 + 内边距 8」，改一处要改三处 |
| C-6 | **分类图标全部相同** | `ViewModels/ConfigurationPageViewModel.cs:328`：所有分类都用 `"\uE713"` | `ConfigurationCategory.Icon` 字段存在但无区分度（当前视图未渲染图标，属未使用字段） |
| C-7 | **页头无「来源/规模」上下文** | `CoreSummary`（`ConfigurationPageViewModel.cs:109-111`）已计算「stable · N 个变量 · 已配置 M 个」，但视图**从未绑定它** | 用户看不到当前核心变体与配置覆盖率 |
| C-8 | **搜索与刷新混在页头右侧** | `:62-71`：`TextBox Width=250` 固定宽 + 刷新按钮 | 窄窗口下固定 250 宽会把标题挤扁 |

### 1.3 为什么配置页是最安全的试点（关键取证）

**视觉层零契约**：

- `Views/ConfigurationView.axaml` **没有任何 `x:Name` / `Name=` 属性**（逐行核对 120 行，零命中）。
- 没有任何运行期元素查找：`ConfigurationView.axaml.cs` 全文 11 行，只有构造函数调用
  `InitializeComponent()`；全 `src` 树内没有 `FindControl` / `GetVisualDescendants` 指向配置页内部。
- **19 个配置页测试只断言 ViewModel 对象图**：`Tests/ConfigurationPageViewModelTests.cs`（564 行）
  无 `FindControl`、无 `Classes`、无画刷/颜色断言、无布局断言。
  （该文件共 20 个 `public` 方法，其中 1 个是夹具的 `Dispose`，故断言用例为 19 个。）
- **没有任何测试导航到配置页**：`SelectedNavigationItem.Key` 只在
  `Tests/MainWindowViewModelBehaviorTests.cs:212`（`overview`）、`:233` 与
  `Tests/MainWindowPreparationTests.cs:46`（`settings`）、`Tests/OverviewAboutViewTests.cs:113`（`about`）
  被断言过。

**唯一的布局锁定在另一个文件**：`Views/ConfigurationEditorWindow.axaml` 的三行 Grid 结构、
`SizeToContent.WidthAndHeight`、`MaxWidth==920`、`MaxHeight==720`、`ScrollViewer MaxHeight==520`
被 `Tests/ConfigurationEditorControlTests.cs:149-162` 逐条断言。**本方案不修改该文件。**

### 1.4 必须原样保留的绑定面（破坏即返工）

ViewModel `ConfigurationPageViewModel` 的对外契约：

| 类别 | 成员 | 被谁依赖 |
|---|---|---|
| 命令 | `RefreshCommand` | `MainWindowViewModel.cs:555` 在选择配置页时强制刷新；无测试直接调用 |
| | `EditVariableCommand(row)` | `Tests/MainWindowViewModelBehaviorTests.cs:168` 直接 `ExecuteAsync` |
| | `DeleteVariableCommand(row)` | `Tests/ConfigurationPageViewModelTests.cs:313,334` |
| 可观察属性 | `SearchText` | 测试直接赋值（`:245`） |
| | `SelectedCategory` | 视图双向绑定 + 测试直接赋值（`:228`） |
| | `IsBusy` | 命令重入保护；视图 `IsEnabled` |
| 集合 | `Categories` | 测试断言 `Count == 4` 与分类键（`:228,269`） |
| | `FilteredVariables` | 测试经 `FindVariable` 读取（`:407`） |
| 计算属性 | `PageTitle` / `PageSubtitle` | 测试断言字符串（`:228,245`） |
| | `CoreSummary` / `DiagnosticText` / `HasSnapshot` / `HasDiagnostic` / `HasVariables` / `IsBusyState` | 视图绑定（本轮新增绑定 `CoreSummary`） |
| 事件 | `CoreConfigurationChanged` | `MainWindowViewModel.cs:106` 订阅、`:983` 退订 |
| 静态方法 | `ResolveEditorTemplate` | `Tests/ConfigurationPageViewModelTests.cs:284` 逐条断言映射 |
| 构造函数 | 7 参（`AppPaths, ISettingsStore, IUiDialogService, IAdminWriteGate, ICoreEnvClient, IAdminSessionService, IAppDiagnostics`） | 两个测试夹具都用它 |

**同样不可破坏的行为语义**（本轮一行都不改）：
- 敏感值脱敏（`MaskSensitiveValue` → `"••••••••"`，`ConfigurationPageViewModel.cs:432-436`）；
- 写入路径必须「管理员门禁 → 原子写 → 回读校验」（`:188-228`）；
- 删除走核心接口而非直写文件（`:502-542`）；
- `ADMIN_TOKEN` 拒绝在配置页编辑（`:129-135`）。

### 1.5 本方案不解决的问题

- 配置页**不加**分页、不加排序、不加按类型/来源/配置状态的筛选控件。
  `AGENTS.md` §10 提到「搜索/分类/类型/配置状态筛选」，当前实现只有「分类 + 搜索」两项；
  补齐筛选器属于**功能新增**，不在本轮 UI 重构范围，另行单独立项。
- 不实现分类图标区分（C-6）：需要先决定图标来源（见 §6.4 的图标策略），本轮保持现状。

---

## 2. 目标结构

### 2.1 线框图

**宽屏（≥ 1100px）—— 三栏**

```
┌ 页头 ─────────────────────────────────────────────────────────────────────┐
│  配置工作台  (h1)                      [核心摘要 chip]  [搜索框]  [刷新]  │
│  分类和变量均来自当前核心 envs.js…  (sub)                                 │
├────────────┬──────────────────────────────────────┬───────────────────────┤
│ 分类 rail  │ 变量列表（虚拟化 ListBox）           │ 详情面板              │
│            │                                      │                       │
│ ▸ API 配置 │  DANMU_TOKEN          [配置][敏感]   │  DANMU_TOKEN  (mono)  │
│   数据源…  │  弹幕接口访问令牌                     │  类型：文本           │
│   匹配配置 │  ────────────────────────────────    │  来源：.env  (chip)   │
│   弹幕配置 │  TEST_COUNT           [默认]         │  ───────────────      │
│   缓存配置 │  缓存条目上限                         │  当前值               │
│   系统配置 │  ────────────────────────────────    │  ••••••••             │
│            │  VOD_SERVERS          [配置]         │  ───────────────      │
│            │  自定义弹幕服务器列表                 │  说明                 │
│            │                                      │  弹幕接口访问令牌     │
│            │                                      │  ───────────────      │
│            │                                      │  [编辑] [恢复默认]    │
└────────────┴──────────────────────────────────────┴───────────────────────┘
```

**中屏（900–1100px）—— 两栏**：分类 rail + 列表；详情面板收为列表下方的
`Expander`（默认收起，选中项变化时自动展开）。**不弹窗**，避免打断用户连续编辑。

**窄屏（< 900px）—— 单栏**：分类 rail 转为顶部横向 chip 组（`WrapPanel`，保留
`ListBox` + `SelectedItem` 绑定不变，只改 `ItemsPanel`）；列表在中间；详情面板同样用
`Expander` 置于列表下方。

### 2.2 布局收敛的判定与手法

阈值与核心页保持一致（`CorePageView.axaml.cs:19` 用 `< 900`），避免同应用出现两套窄屏语义。
实现在 `ConfigurationView.axaml.cs` 的 `SizeChanged` 中完成，**手法照抄** `CorePageView.axaml.cs:17-29`：

```csharp
// 参考实现：CorePageView.axaml.cs:17-29
var narrow = Bounds.Width < 900;
if (narrow == _isNarrow) return;
_isNarrow = narrow;
SourceGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "1.55*,1*");
Grid.SetColumn(ExploreColumn, narrow ? 0 : 1);
Grid.SetRow(ExploreColumn, narrow ? 1 : 0);
```

本页同理：改 `ColumnDefinitions`、`Grid.SetRow/SetColumn`、切换 `ItemsPanel` 方向。
**不允许**用 `WrapPanel` 整体替换列表容器、不允许改变控件层级导致 `DataContext` 继承链断裂
（列表项的 `CommandParameter` 依赖 `RelativeSource AncestorType=ListBox`，见 §3.3）。

---

## 3. 实施步骤

> **每一步结束都必须满足**（任一不满足即视为该步未完成，不得进入下一步）：
> ① `dotnet build src/DanmuApi.App/DanmuApi.App.csproj -c Release` → 0 error / 0 warning；
> ② 全量测试与 §9.1 基线**逐项一致**（配置页 19 个用例**零修改**通过）；
> ③ 按该步要求渲染 PNG 并逐张目视核对；
> ④ 回填 §9.2；⑤ 追加 `docs/HANDOFF.md`。

### 步骤 1：抽出 token（零外观变更）

1. 新建 `src/DanmuApi.App/Styles/Tokens.axaml`（`ResourceDictionary`），把
   `App.axaml:9-91` 的两个 `ThemeDictionaries` **原样搬运**，键名一个不改——
   这样其余所有页面的 `{DynamicResource ...}` 引用继续有效。
2. 新增本轮需要的 token（深浅各一套）：
   - `ChipNeutralBrush`（中性胶囊底，浅 `#EEF0F3` / 深 `#2B3038`）
     与 `ChipNeutralTextBrush`（浅 `TextSecondaryBrush` 同值 / 深同值）。
     现有 `status-pill` 只有 healthy/failed/warning/accent 四种语义（`App.axaml:402-436`），
     而配置页需要「已配置 / 核心默认 / 未配置」这类**非状态**标签。
3. `App.axaml` 的 `<Application.Resources>` 改为：
   ```xml
   <ResourceDictionary>
     <ResourceDictionary.MergedDictionaries>
       <ResourceInclude Source="avares://DanmuApi.App/Styles/Tokens.axaml" />
     </ResourceDictionary.MergedDictionaries>
   </ResourceDictionary>
   ```
4. 确认 `App.axaml` 中 `Application.Styles` 部分**本步不动**。

**验收**：构建 0/0；全量测试与基线一致；渲染任意两页（如核心页 + 设置页）深浅各一，
与本步之前的图肉眼看无差异。

### 步骤 2：建立外壳词汇（只加不改，双写旧类名）

新建 `src/DanmuApi.App/Styles/Shell.axaml`（`Styles`），只落地**配置页本轮用到的子集**。
数值来源：`tools/DesignPreview/Styles/Shell.axaml`（已定稿的一套），映射见下。

**强制：每个新类名与它对应的旧类名双写在同一条 `Style Selector` 里**，
例如 `Style Selector="Button.primary, Button.primary-action"`。
理由：本轮只有配置页迁移到新类名，其余页面仍用旧类名；双写保证任何中间态下
全应用外观零漂移。**这条策略不允许省略。**

| 新类名 | 双写的旧类名 | 数值（来自 DesignPreview/Shell.axaml） | 用途 |
|---|---|---|---|
| `TextBlock.h1` | `TextBlock.page-title` | 18px / SemiBold / `TextPrimaryBrush` | 页主标题（配置页当前是 26px 覆盖为 22，统一收敛到 18） |
| `TextBlock.sub` | `TextBlock.page-subtitle` | 12px / `TextSecondaryBrush` | 页副标题 |
| `TextBlock.section` | `TextBlock.section-title` | 13px / SemiBold | 区块标题 |
| `TextBlock.cap` | `TextBlock.body-muted` | 11px / `TextSecondaryBrush` | 说明文字 |
| `TextBlock.overline` | 同名（保留） | 10.5px / SemiBold / 字距 1.2 | 眉标 |
| `TextBlock.mono` | `TextBlock.mono-text` | Cascadia Mono,Consolas / 11.5px | 等宽 |
| `Border.card` | `Border.dashboard-panel` | 圆角 10 / 1px `HairlineBrush` / `SurfaceBrush` / Padding 14,12 | 主卡片 |
| `Border.inset` | `Border.muted-row` | 圆角 8 / `SurfaceMutedBrush` / Padding 10,9 | 内嵌块 |
| `Border.rule` | —（新增） | 仅 `BorderThickness="0,0,0,1"` + `HairlineBrush` | 行分隔线 |
| `Border.rail` | —（新增） | 圆角 10 / 1px / `SurfaceBrush` / Padding 6 | 分类侧栏容器 |
| `Button.primary` | `Button.primary-action` | 圆角 7 / Padding 13,6 / 12.5px / SemiBold / `MinHeight` 30 | 主操作 |
| `Button.secondary` | `Button.secondary-action` | 圆角 7 / 1px 描边 / `MinHeight` 30 | 次操作 |
| `Button.ghost` | `Button.ghost-action` | 透明底 / `MinHeight` 28 | 三线操作 |
| `Button.danger` | `Button.danger-action` | 描边 `DangerBrush` / 透明底 | 破坏性操作 |
| `Button.compact` | `Button.compact-action` | Padding 9,3 / 12px / 圆角 6 | 行内小操作 |
| `Border.chip` + `.ok/.warn/.bad/.neutral` | `Border.status-pill` + `.healthy/.failed/.warning/.accent` | 圆角 999 / Padding 8,2 / 11px SemiBold | 状态与来源标签 |
| `Button.rail-item` + `.active` | —（新增） | 圆角 7 / Padding 10,7 / 左对齐 / `MinHeight` 30 | 分类项（用 `ListBoxItem` 选择器实现，见 §3.3） |

**注意**：`Button.danger` 不要与 `App.axaml:304-311` 的旧 `danger-action`、以及
`App.axaml:500-507` 的**第二个** `danger-action` 定义冲突——`App.axaml` 里 `danger-action`
被定义了两次（`App.axaml:304` 和 `:500`），后者覆盖前者。本步**不改 `App.axaml` 的样式**，
只在 `Shell.axaml` 里定义新类名并双写旧名；两处重复定义的问题在 §6.5 单独记录，本轮不处理
（处理会改变所有使用 `danger-action` 的页面外观，超出试点范围）。

**验收**：构建 0/0；全量测试与基线一致；渲染核心页 + 设置页深浅各一，与本步之前无差异
（证明双写策略生效、其余页面零漂移）。

### 步骤 3：重写 `ConfigurationView.axaml`

1. **页头**：三列 `Grid ColumnDefinitions="*,Auto,Auto"`
   - 左：`PageTitle`（`h1`）+ `PageSubtitle`（`sub`）
   - 中：`CoreSummary` 绑定为 `Border.chip.neutral`（**新绑定，解决 C-7**）
   - 右：搜索 `TextBox`（去掉固定 `Width=250`，改 `MinWidth=200` + `MaxWidth=320`）
     + 刷新按钮（`Button.secondary`）
2. **分类 rail**：`Border.rail` 包 `ListBox`，`ItemsSource={Categories}`、
   `SelectedItem={SelectedCategory, Mode=TwoWay}`（**绑定形态一字不改**）。
   - `ListBox.rail-list` 的 `ItemTemplate` 用 `TextBlock.section`（分类名）+
     `TextBlock.cap`（描述）。
   - 使用 `ListBoxItem` 的 `:selected` 选择器实现选中态（`AccentSoftBrush` 底 + `AccentBrush` 文字），
     参考 `App.axaml:541-552` 的 `settings-list` 写法。
3. **变量列表**：`Border.card` 内改为
   ```xml
   <ListBox ItemsSource="{Binding FilteredVariables}"
            SelectedItem="{Binding SelectedVariable, Mode=TwoWay}"
            ScrollViewer.HorizontalScrollBarVisibility="Disabled">
     <ListBox.ItemsPanel>
       <ItemsPanelTemplate><VirtualizingStackPanel /></ItemsPanelTemplate>
     </ListBox.ItemsPanel>
   ```
   （**解决 C-2**；`VirtualizingStackPanel` 的用法照抄 `Views/LogsView.axaml:102`）
   - 行模板：`Grid ColumnDefinitions="*,Auto"`，左列 `Key`（`mono`）+ `Description`（`cap`，单行截断），
     右列两个 `Border.chip`（来源 + 已配置/默认），**不再在行内放按钮**（编辑动作移到详情面板，
     行内仅保留选中）。
   - 行容器 `Border.rule`（底部 1px 线）替代原 `configuration-row`。
4. **详情面板**：`Border.card`，`Grid RowDefinitions="Auto,Auto,*,Auto"`
   - 键名（`mono`，15px SemiBold）、类型 + 来源（`Border.chip`）、
     当前值（`SelectableTextBlock` + `mono`，敏感值已是 `••••••••`，不额外处理）
   - 说明（`cap`，多行）
   - 底部动作行：`Button.primary`「编辑」+ `Button.danger`「恢复默认（删除显式值）」
     （**解决 C-3**：破坏性动作不再是同款蓝色文字）
   - 未选中时显示占位文案（`cap`：「在左侧选择一个变量查看详情」）
5. **诊断条**：`Border.notice-panel` 保留（它已是语义正确的组件），只把内部文案类换成 `cap`。

**验收**：构建 0/0；`Tests/ConfigurationPageViewModelTests.cs` 19 个用例**零修改**通过；
渲染下列 12 张并逐张核对（渲染方法见 §5.3）：
`config-light-wide` / `config-dark-wide` / `config-light-medium` / `config-dark-medium` /
`config-light-narrow` / `config-dark-narrow` / `config-selected-wide` / `config-selected-narrow` /
`config-search-wide` / `config-sensitive-wide` / `config-diagnostic-wide` / `config-empty-wide`。
核对点：三档宽度无文字挤压、无横向溢出；深浅主题下分类选中态可见；来源胶囊语义正确；
敏感值显示为 `••••••••`；搜索态 `PageTitle` 变为「搜索结果」时页头不跳位。

### 步骤 4：新增详情面板所需的只读 ViewModel 属性

详情面板需要「当前选中的变量」。**只加一个双向属性，不新增命令**：

在 `ViewModels/ConfigurationPageViewModel.cs` 增加：

```csharp
[ObservableProperty]
private ConfigurationVariableRow? _selectedVariable;
```

并在 `RefreshRows()` 末尾按引用相等性重新定位选中项（列表重建后 `SelectedVariable` 会指向旧实例，
必须重新解析，否则详情面板显示陈旧数据）：

```csharp
// RefreshRows() 内，FilteredVariables 重建之后
SelectedVariable = SelectedVariable is null
    ? null
    : FilteredVariables.FirstOrDefault(row => row.Key == SelectedVariable.Key);
```

派生显示属性（只读，供详情面板绑定）：

```csharp
public bool HasSelectedVariable => SelectedVariable is not null;
public string SelectedVariableKey => SelectedVariable?.Key ?? string.Empty;
public string SelectedVariableDescription => SelectedVariable?.Description ?? string.Empty;
public string SelectedVariableType => SelectedVariable?.Type ?? string.Empty;
public string SelectedVariableSource => SelectedVariable?.Source ?? string.Empty;
public string SelectedVariableValue => SelectedVariable?.CurrentValueText ?? string.Empty;
public bool CanResetSelectedVariable => SelectedVariable?.IsConfigured == true;
```

`partial void OnSelectedVariableChanged(...)` 里对上述属性发 `OnPropertyChanged`。

**约束（不可违反）**：
- 不新增命令。「编辑」「恢复默认」直接绑定已存在的 `EditVariableCommand` / `DeleteVariableCommand`，
  用 `CommandParameter="{Binding SelectedVariable}"` 传入。
- **不改变** `RefreshRows()` 的筛选语义与顺序（`OrderBy(Key, Ordinal)`，`ConfigurationPageViewModel.cs:352`），
  但允许在其末尾追加「重新解析选中项」——这属于视图状态维护，不是筛选逻辑。
- 新增属性全部只读派生，不引入任何写路径。
- 敏感值仍走 `CurrentValueText`（已脱敏），**不得**在新增属性里绕过 `MaskSensitiveValue`。

**验收**：构建 0/0；`Tests/ConfigurationPageViewModelTests.cs` 既有 19 个用例零修改通过；
新增一个**最小**用例证明选中项在列表重建后不丢（见 §5.2）；
渲染 `config-selected-wide` / `config-selected-narrow` 两张确认详情面板内容正确。

### 步骤 5：窄屏与中屏收敛

在 `Views/ConfigurationView.axaml.cs` 增加 `SizeChanged` 处理（手法照抄 `CorePageView.axaml.cs:17-29`）：

| 宽度 | 分类 rail | 变量列表 | 详情面板 |
|---|---|---|---|
| ≥ 1100 | 左栏（固定 216，与工具页 208 语义靠拢但本页保持 216 以免与设置页不一致） | 中栏（`*`） | 右栏（320） |
| 900–1100 | 左栏 | 中栏（`*`） | 收起为列表下方 `Expander`（`Grid.Row=1`，跨列） |
| < 900 | 顶部横向 chip 组（`ItemsPanel` 改 `WrapPanel`） | 单栏（`Grid.Row=1`） | 列表下方 `Expander`（`Grid.Row=2`） |

注意：切换 `ItemsPanel` 需要重建 `ItemsPanelTemplate` 并**保留**原有的
`SelectedItem` 绑定与 `ItemTemplate`（`CorePageView.axaml.cs` 只改 Grid 属性，本页多一层面板切换，
必须实测「切换后选中项不丢、二次切换不重复订阅事件」）。

**验收**：构建 0/0；全量测试与基线一致；渲染 960 / 1100 / 1280 / 1920 四档 × 浅深共 8 张，
逐张核对「三档布局切换点正确、切换后选中项保持、无重叠」。

### 步骤 6：收口

1. 全文搜索确认配置页不再引用旧类名（`page-title` / `page-subtitle` / `body-muted` /
   `section-title` / `mono-text` / `dashboard-panel` / `compact-action` / `secondary-action`
   在 `ConfigurationView.axaml` 内应全部替换为新类名）。
2. **不要**删除 `Shell.axaml` 中的旧类名双写——其余页面仍在用（§8 推广时逐页替换，全部替换完才删）。
3. 渲染全站页面（约 100 张）确认其余页面外观零漂移。
4. 更新 `docs/HANDOFF.md`。

**验收**：构建 0/0；全量测试与基线一致；全站渲染逐张核对无回归；
`dotnet publish` 后 `--release-self-test` exit 0、`--verify-app-release` exit 0。

---

## 4. 不修改的文件（边界硬约束）

| 文件 | 为什么不能动 |
|---|---|
| `Views/ConfigurationEditorWindow.axaml` / `.axaml.cs` | 布局被 `Tests/ConfigurationEditorControlTests.cs:149-162` 逐条断言（三行 Grid、`SizeToContent`、`MaxWidth 920`、`MaxHeight 720`、`ScrollViewer MaxHeight 520`、按钮文案） |
| `Views/OverviewView.axaml` / `.axaml.cs` | **用户明确要求概览页不动** |
| `App.axaml` 的 `Application.Styles` 段 | 本轮只动 `<Application.Resources>`（步骤 1）；改样式段会影响所有页面 |
| `ViewModels/ConfigurationPageViewModel.cs` 的既有成员 | 除步骤 4 明确列出的新增项外，一行不改 |
| `Controls/*.cs` 的 13 个自研控件 | 它们通过 `Classes.Add("...")` 复用应用样式；本轮不动，避免连锁影响 |

---

## 5. 验证方法（可复现命令）

### 5.1 构建

```powershell
dotnet build src/DanmuApi.App/DanmuApi.App.csproj -c Release
```
要求 `0 error / 0 warning`（`Directory.Build.props` 设了 `TreatWarningsAsErrors=true`）。

### 5.2 全量测试

```powershell
$env:DANMU_TEST_NODE_EXE = "<node.exe 路径>"
dotnet test src/DanmuApi.Tests/DanmuApi.Tests.csproj -c Release
```
要求：失败数 **0**；总数 = 基线 + 步骤 4 新增的选中项用例数；跳过数（opt-in）随环境变化不计入。

步骤 4 新增的最小用例（建议名）：
`SelectedVariableSurvivesRefreshAndClearsWhenFilteredOut` —— 断言选中某变量后
`RefreshCommand` 执行、`SelectedVariable.Key` 不变；把 `SearchText` 改成不匹配时
`SelectedVariable` 归空、`HasSelectedVariable == false`。

配置页既有 19 个用例**只允许因「新增属性导致对象图变化」而零修改通过**；
任何需要改动既有断言的情况都视为破坏了 §1.4 契约，必须停下来重新设计。

### 5.3 渲染核对

```powershell
$env:DANMU_TEST_RENDER_DIRECTORY = "artifacts/ui-refactor-config-<步骤号>"
dotnet test src/DanmuApi.Tests/DanmuApi.Tests.csproj -c Release
```
`Tests/AvaloniaTestAssembly.cs:16-20` 在设置该变量后启用 headless drawing；
现有 `Save*Preview` 辅助方法会把 PNG 写入该目录。配置页当前**没有**渲染用例，
需在 `Tests/ConfigurationPageViewModelTests.cs` 中新增一个渲染用例（沿用该仓库既有写法，
参考 `Tests/NotificationSettingsViewTests.cs:26-31`：读环境变量 → `new RenderTargetBitmap(...)` →
`bitmap.Render(window)` → `bitmap.Save(...)`），宿主窗口尺寸按 §3 步骤 5 的四档宽度参数化。

要求：
- **每一张都要看**。不接受「代码里写了什么」代替「图上是什么」——
  `docs/HANDOFF.md:183` 记录过自研渲染管线产出 28 张全坏图且被兜底色掩盖的教训。
- 出现纯色块、错位、文字重叠、空白区域异常，必须查明原因后才可继续。

### 5.4 发布自检（仅步骤 6）

```powershell
dotnet publish src/DanmuApi.App/DanmuApi.App.csproj -c Release -o artifacts/ui-refactor-publish
artifacts/ui-refactor-publish/DanmuApi.App.exe --release-self-test artifacts/ui-refactor-self-test.txt
artifacts/ui-refactor-publish/DanmuApi.App.exe --verify-app-release artifacts/ui-refactor-publish
```

---

## 6. 边界与红线

### 6.1 来自 `AGENTS.md` §7 的工程硬标准

1. 失败必须显式上报（Failed 状态 + 失败原因），禁止 `catch { return 默认值 }` 式吞错。
2. 先取证后动手；修复必须可验证。
3. 诊断信息不得静默丢弃。
4. 敏感值（TOKEN / COOKIE / API_KEY）永不写日志、永不原样返回 UI。
5. 不硬编码个人绝对路径。

### 6.2 概览页（用户明确要求不动）

`Views/OverviewView.axaml`、`Views/OverviewView.axaml.cs`、以及页内私有调色板
（`OverviewView.axaml:8-39`）与本轮**完全无关**。本轮任何步骤都不得修改这些文件。
概览页的私有调色板与全局 token 并存的问题，留待后续单独立项。

### 6.3 协议层不得触碰

`.env` 原子写 + 回读校验（`ConfigurationPageViewModel.cs:188-210`）、
核心删除接口调用（`:502-542`）、管理员门禁（`:137,245`）——本轮只改 XAML 与只读派生属性，
**不得**改动这些路径的任何一行。

### 6.4 图标策略（本轮不动）

配置页当前不使用图标（`ConfigurationCategory.Icon` 字段未被视图渲染）。
本轮**不引入**图标包、不引入几何 `Path` 图标。分类图标区分（C-6）与全应用图标统一
（现存在字形字体、手写 `Path`、纯字符三种并存）留待后续单独立项。

### 6.5 记录在案、本轮不处理的既有缺陷

| 缺陷 | 取证 | 为何本轮不处理 |
|---|---|---|
| `danger-action` 在 `App.axaml` 被定义两次 | `App.axaml:304-311` 与 `:500-507`，后者覆盖前者（前者是浅底红字、后者是红描边） | 修正会改变所有使用 `danger-action` 的页面外观，超出试点范围 |
| `App.axaml` 单一文件承载 token + 约 90 条样式 + 23 个 DataTemplate | `App.axaml` 共 543 行（`DangerSoftBrush` 底 + `DangerBrush` 文字的 `danger-action` 在 `:304`，红描边版在 `:500`，后者生效） | 步骤 1 只抽 token；样式拆分在推广阶段做 |
| 全应用 `ControlTheme` 使用数为 0 | 全仓搜索无命中 | 引入 `ControlTheme` 是更大的改造，需单独评估 |
| `BoxShadow` 颜色必须是字面量 | `App.axaml:332-334` 注释：写成 `{DynamicResource}` 会让 `App.Initialize()` 抛 `FormatException`，曾导致 72 个 UI 测试全挂（`HANDOFF.md:199`） | 配置页新卡片沿用现有字面量，不引入主题化投影 |

---

## 7. 回退方案

- 每一步一次独立提交，提交信息写明「步骤 N：<做了什么>」。
- 若步骤 3 的三栏结构在 960px 下无法成立（例如详情面板 320 宽 + 分类 216 宽 + 列表最小宽度
  之和超过可用宽度），**退回**「分类 rail + 列表」两栏，详情面板一律用列表下方 `Expander`，
  不弹窗。此决定需在本文件记录原因。
- 若步骤 1 的 `ResourceInclude` 合并导致任何 `{DynamicResource}` 解析失败（症状：启动时
  `App.Initialize()` 抛异常，或控件背景变透明），立即回退步骤 1，改为**复制**而非**移动** token
  （即 `App.axaml` 保留原 ThemeDictionaries，`Tokens.axaml` 仅作后续新增 token 的载体），
  并在本文件说明原因。

---

## 8. 试点成功判据（决定是否推广到其他页面）

全部满足才算通过：

1. `Tests/ConfigurationPageViewModelTests.cs` 既有 19 个用例**零修改**通过。
2. 全量测试与基线逐项一致（总数差异仅来自步骤 4 新增用例）。
3. 960 / 1100 / 1280 / 1920 四档宽度下无文字挤压、无横向溢出、无控件重叠。
4. 深浅两主题下：分类选中态可见、来源/配置状态胶囊语义正确、敏感值显示为 `••••••••`。
5. `Styles/Tokens.axaml` 与 `Styles/Shell.axaml` 中新增的类名是**通用**的
   （不出现 `config-*` 这类为单页定制的名字），可直接被其他页面复用。
6. 其余页面渲染图与本轮之前**逐张无差异**（证明双写策略与 token 搬运没有副作用）。

**判据修正（步骤 0/1 实测后确定，重要）**：第 6 条无法按「逐张 bit-identical」执行 ——
headless 渲染的文字抗锯齿存在**固有非确定性**。实测：同一份代码连跑两次，
`artifacts/ui-refactor-step1/` 与 `ui-refactor-step1b/` 有 **27 帧 SHA256 不同，
且不稳定的帧集合两次完全一致**（27 张：`overview-*` 10 张、`requests-*` 4 张、
`shell-*-running/addresses` 12 张、`startup-main-preparing-settings.png`）。
像素抽样确认差异落在文字笔画边缘（`requests-1100-light.png` 在 y≈756–762 的文本行，
97,989 个抽样点中 17 点超阈值，最大通道差 456，表现为红/蓝通道互换），
`startup-main-preparing-settings.png` 最大通道差仅 5。

因此第 6 条改判为：**差异帧必须仍是这 27 张固有抖动集合的子集，新增差异帧数 = 0**。
其余 67 张必须逐张 SHA256 一致。核对脚本：

```powershell
# $a = 基线目录，$b = 本步骤目录；输出差异帧名单，与上面 27 张对比
Get-ChildItem $a -Filter *.png | ForEach-Object {
  if ((Get-FileHash $_.FullName -Algorithm SHA256).Hash -ne
      (Get-FileHash (Join-Path $b $_.Name) -Algorithm SHA256).Hash) { $_.Name }
}
```

任一条不满足，先修好再谈推广；若第 5 条不成立，说明词汇设计有问题，需要重新设计而不是继续推广。

**推广到其他页面时的顺序建议**（每页独立一轮，按契约密度从低到高）：

1. 无 `x:Name`、无布局断言的页面：`BackupView`、`ServiceManagementView`
2. 有 `x:Name` 但无布局数值断言的页面：`ActivityView`、`AboutView`、`ApiDebugView`、`FavoritesView`、
   `SearchResultsView`、`AutoMatchView`、`ManualSearchView`、`ConfigurationView`（本轮已完成）
3. 有布局数值断言的页面（需同步评估每条断言）：`ToolsView`（侧栏 208 宽）、`DanmuDownloadView`、
   `RequestRecordsView`、`LogsView`
4. 最高风险页：`CorePageView`（十余条布局与字体断言）、`OverviewView`（用户暂不动）

---

## 9. 基线与结果记录表

### 9.1 基线（步骤 0 填写，实施前先跑一次）

| 项 | 值 |
|---|---|
| 构建结果 | `0 警告 0 错误`（`DanmuApi.App.csproj -c Release`，11.18 s） |
| 测试结果（`PASS / 失败 / 跳过 / 总计`） | `957 / 0 / 9 / 966`，35 s |
| 配置页用例数 | 19（`Tests/ConfigurationPageViewModelTests.cs`，该文件 20 个 `public` 方法中 1 个是夹具 `Dispose`） |
| 基线渲染目录 | `artifacts/ui-refactor-baseline/`，**94 帧** |

**工具链实测**（本机无 PATH 上的 dotnet，必须用绝对路径）：

```powershell
C:\Tools\dotnet-sdk-8.0\dotnet.exe build src\DanmuApi.App\DanmuApi.App.csproj -c Release
$env:DANMU_TEST_NODE_EXE = "C:\Tools\node-v24.19.0-win-x64\node.exe"
C:\Tools\dotnet-sdk-8.0\dotnet.exe test src\DanmuApi.Tests\DanmuApi.Tests.csproj -c Release --no-build
```

**渲染目录必须传绝对路径**（重要踩坑，本步骤取证）：

`DANMU_TEST_RENDER_DIRECTORY` 若传相对路径，测试宿主会把 `Directory.CreateDirectory(相对路径)`
解析到 `src\DanmuApi.Tests\bin\Release\net8.0-windows10.0.19041.0\` 下，而 `bitmap.Save()`
走的是另一套路径解析，最终抛 `DirectoryNotFoundException` —— **30 个渲染用例全部失败，
但 UI 本身毫无问题**。必须写成绝对路径：

```powershell
$env:DANMU_TEST_RENDER_DIRECTORY = "C:\c\danmu-api-windows\artifacts\ui-refactor-baseline"
```

此坑已在本机用户环境变量中固化（`[Environment]::SetEnvironmentVariable(..., 'User')`），
后续步骤直接沿用，不要改回相对路径。

### 9.2 各步骤结果

| 步骤 | 构建 | 测试 | 渲染帧数 | 渲染目录 | 偏差与说明 |
|---|---|---|---|---|---|
| 0 基线 | 0 警告 0 错误 | 957 / 0 / 9 / 966 | 94 | `artifacts/ui-refactor-baseline/` | 相对路径导致 30 个渲染用例假失败，见 §9.1 |
| 1 抽出 token | | | | | |
| 2 建立外壳词汇 | | | | | |
| 3 重写 ConfigurationView | | | | | |
| 4 新增只读 VM 属性 | | | | | |
| 5 窄屏与中屏收敛 | | | | | |
| 6 收口 | | | | | |

---

## 附录 A：为什么不做第三方主题库整体替换（要点）

完整论证已在上一轮调研中确认，此处保留要点备查：

1. **自定义程度已超过临界点**：约 90 条全局样式、其中 15 处是 `/template/ ContentPresenter`
   覆写（`App.axaml:222,234,245,271,284,301,362,369,396,474,481,484,508,521`）；
   13 个 code-only 自研控件（`Controls/`，`TagPicker` 446 行、`ColorPaletteEditor` 376 行，
   `ColorWheel.cs:41-45` 自绘指针环）；这些控件通过 `Classes.Add("...")` 复用应用样式类，
   换库后需逐个重新验证。
2. **语言不兼容**：核心页定稿语言（`Border.workbench` 圆角 10 + 极浅投影 + `wb-strip` 分带 +
   `status-pill` 胶囊 + `row-entry` 行式入口，见 `CorePageView.axaml:186-301`）与
   Material（elevation 阴影 + FAB）、Semi（高对比圆角 + 大字号）、Ursa（自带颜色选择器，
   与 `ColorPaletteEditor` 重复）均冲突。
3. **会作废已踩的坑**：Fluent 深色 `Expander` 折叠头近黑底需覆盖模板
   （`CorePageView.axaml:82-94`）；`BoxShadow` 颜色必须是字面量否则 `App.Initialize()` 抛
   `FormatException` 并挂掉全部 72 个 UI 测试（`App.axaml:332-334`、`HANDOFF.md:199`）。
4. **测试契约会大面积失效**：数十条断言绑定了具体布局数值与 Fluent 模板尺寸
   （`Tests/CorePageViewTests.cs:118-140,164,170-171,180`、
   `Tests/OverviewViewTests.cs:27-40`、`Tests/OverviewAboutViewTests.cs:80-83,164-166,213-217`、
   `Tests/ToolWorkflowViewTests.cs:83-84`、`Tests/MainWindowPreparationTests.cs:69-79`）。
5. **收益上限低**：第三方库能给的只有「更好的默认控件观感」（已被现有样式覆盖）与
   「现成图标集」（用几何 `Path` + 集中样式即可解决）。

**结论**：不换库。按本方案的「抽 token → 建词汇 → 逐页重排 → 渲染核对」推进。

---

## 附录 B：术语与文件索引

| 术语 | 含义 | 定义位置 |
|---|---|---|
| token | 设计变量（颜色/字号/圆角/间距） | `Styles/Tokens.axaml`（步骤 1 新建） |
| 外壳词汇 | 全应用共用的样式类集合 | `Styles/Shell.axaml`（步骤 2 新建） |
| 双写 | 新类名与旧类名写进同一条 `Style Selector` | §3 步骤 2 |
| 契约名 | 测试通过 `FindControl` 查找的 `x:Name` | 配置页无；见 `docs/UI-REFACTOR-PLAN` 推广章节 |
| 契约数值 | 测试断言的具体尺寸/列定义 | `Tests/ConfigurationEditorControlTests.cs:149-162`（属编辑器窗口，本方案不动） |

参考文件（只读）：
- `docs/specs/04-acceptance-checklist.md` —— 验收清单
- `docs/HANDOFF.md` —— 动态交接页，每步追加
- `tools/DesignPreview/Styles/Shell.axaml` —— 外壳词汇的数值来源（已定稿）
- `tools/DesignPreview/Styles/Tokens.axaml` —— 注意：其 token 名与本应用不同（`BgBrush`/`InkBrush`），
  属预览工具私有，**不要**照搬键名；本应用以 `App.axaml` 的键名为准
- `artifacts/core-redesign-9/` —— 核心页定稿渲染（视觉基准参考）
