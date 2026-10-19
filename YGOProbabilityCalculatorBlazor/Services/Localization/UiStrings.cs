namespace YGOProbabilityCalculatorBlazor.Services.Localization;

/// <summary>Primary chrome strings. Default locale is zh-CN for the OCG/CN fork.</summary>
public static class UiStrings {
    public const string AppTitle = "游戏王起手概率计算器";
    public const string Help = "帮助";
    public const string Back = "返回";
    public const string ThemePreference = "主题";
    public const string ThemeSystem = "跟随系统";
    public const string ThemeLight = "浅色";
    public const string ThemeDark = "深色";

    public const string Import = "导入";
    public const string ImportDeck = "导入卡组";
    public const string ImportYdk = "导入 YDK";
    public const string ImportYdke = "导入 YDKe";
    public const string Cancel = "取消";
    public const string Calculate = "计算";
    public const string Calculating = "计算中…";
    public const string Categories = "分类";
    public const string CategoriesHint = "· 管理卡牌角色";
    public const string Combos = "Combo";
    public const string Deck = "卡组";
    public const string AddCard = "添加卡牌";
    public const string SearchCards = "搜索卡名…";
    public const string HandSize = "手牌张数";
    public const string ClearWorkspace = "清空工作区";
    public const string Settings = "计算设置";
    public const string CalculationModel = "计算模型";
    public const string ModelOpeningHand = "仅起手（无效果模拟）";
    public const string ModelProgressive = "渐进（默认）";
    public const string ModelFastOnly = "仅快速";
    public const string ModelExactOnly = "仅精确";
    public const string CardSimulation = "卡牌效果模拟";
    public const string DrawActivationPriority = "抽卡发动优先级";
    public const string EncounterOrder = "遭遇顺序（默认）";
    public const string CustomPriority = "自定义优先级";
    public const string StorageSafetyLimit = "存储安全上限";
    public const string StorageStandard = "标准（32,768 条目 / 262,144 格）";
    public const string StorageHigh = "高（65,536 条目 / 524,288 格）";
    public const string StorageVeryHigh = "很高（131,072 条目 / 1,048,576 格）";
    public const string StorageHelp = "更高上限可能让更复杂的精确计算跑完，并占用更多浏览器内存；不会改变概率正确性。";
    public const string TotalProbability = "总概率";
    public const string EmptyDeck = "尚未添加卡牌。可导入 .ydk / YDKe，或手动添加。";
    public const string EmptyCombos = "尚未定义 combo。添加分类与条件后即可计算。";
    public const string Remove = "移除";
    public const string Save = "保存";
    public const string PageTitle = "游戏王起手概率计算器";
    public const string PageDescription = "精确计算游戏王起手与卡组 combo 概率，支持重叠分类、指定卡与自定义约束。面向 OCG / 简体中文卡池（百鸽 ygocdb）。";
}
