using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class DesktopTaskRequestTests
{
    private readonly DeterministicIntentPlanner _planner = new(TimeProvider.System);
    private static readonly IntentPlanningContext CurrentWindow = new(
        ForegroundApplication: new ForegroundApplicationContext(42, "测试窗口", "fixture"));

    [Theory]
    [InlineData("在当前窗口输入你好", "输入你好")]
    [InlineData("在当前软件里选择第一项", "选择第一项")]
    [InlineData("在当前窗口选择当前手动选择的方案", "选择当前手动选择的方案")]
    [InlineData("操作这个窗口展开选项", "展开选项")]
    [InlineData("当前窗口填写姓名", "填写姓名")]
    [InlineData("请帮我在这个软件中滚动到下一页", "滚动到下一页")]
    [InlineData("在当前窗口输入你好，然后选择选项并点击预览", "输入你好，然后选择选项并点击预览")]
    public void ExplicitCurrentWindowTaskKeepsGoalAndNeedsBoundWindow(string text, string goal)
    {
        Assert.True(DesktopTaskRequest.TryParse(text, out var request));
        Assert.True(request!.UsesCurrentWindow);
        Assert.Null(request.ApplicationName);
        Assert.Equal(goal, request.Goal);
        var waiting = _planner.Plan(text, new());
        Assert.Equal(UniversalIntentKind.DesktopTask, waiting.Kind);
        Assert.Equal(IntentPlanReadiness.NeedsContext, waiting.Readiness);
        var ready = _planner.Plan(text, CurrentWindow);
        Assert.Equal(UniversalIntentKind.DesktopTask, ready.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, ready.Readiness);
        Assert.Equal("desktop.task.step", ready.Capability);
    }

    [Theory]
    [InlineData("在记事本里输入你好", "记事本", "输入你好")]
    [InlineData("请帮我在记事本中填写会议记录", "记事本", "填写会议记录")]
    [InlineData("使用计算器，计算1加1", "计算器", "计算1加1")]
    [InlineData("使用计算器，计算1加1，然后选择结果", "计算器", "计算1加1，然后选择结果")]
    [InlineData("用记事本,输入你好，然后选择全部", "记事本", "输入你好，然后选择全部")]
    [InlineData("请使用记事本，输入“你好，世界”，然后选择全部", "记事本", "输入“你好，世界”，然后选择全部")]
    [InlineData("打开记事本然后输入你好", "记事本", "输入你好")]
    [InlineData("打开记事本，然后输入你好，再选择全部", "记事本", "输入你好，再选择全部")]
    [InlineData("启动计算器并计算1加1", "计算器", "计算1加1")]
    [InlineData("我说的是打开记事本然后输入你好", "记事本", "输入你好")]
    public void ExplicitNamedApplicationAndCompoundTaskPreserveExactGoal(string text, string application, string goal)
    {
        Assert.True(DesktopTaskRequest.TryParse(text, out var request));
        Assert.False(request!.UsesCurrentWindow);
        Assert.Equal(application, request.ApplicationName);
        Assert.Equal(goal, request.Goal);
        var plan = _planner.Plan(text, CurrentWindow);
        Assert.Equal(UniversalIntentKind.DesktopTask, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(application, plan.Target);
    }

    [Theory]
    [InlineData("用计算器计算1加1", "计算器", "计算1加1")]
    [InlineData("帮我使用记事本输入你好", "记事本", "输入你好")]
    [InlineData("使用记事本输入你好，然后选择全部", "记事本", "输入你好，然后选择全部")]
    [InlineData("用计算器计算1加1，然后选择结果", "计算器", "计算1加1，然后选择结果")]
    [InlineData("用Word输入中国", "Word", "输入中国")]
    [InlineData("用Word输入这里是测试", "Word", "输入这里是测试")]
    [InlineData("使用记事本输入“你好中国”", "记事本", "输入“你好中国”")]
    [InlineData("在Word输入你好", "Word", "输入你好")]
    [InlineData("在Word输入的确如此", "Word", "输入的确如此")]
    [InlineData("用Word写入的确如此", "Word", "写入的确如此")]
    [InlineData("用Word输入框架设计", "Word", "输入框架设计")]
    [InlineData("用Word输入栏杆高度", "Word", "输入栏杆高度")]
    [InlineData("用Word写一段会议纪要", "Word", "写一段会议纪要")]
    [InlineData("在Word里写一段会议纪要", "Word", "写一段会议纪要")]
    [InlineData("使用Word写入你好", "Word", "写入你好")]
    [InlineData("用Word新建文档", "Word", "新建文档")]
    [InlineData("在Word中创建文档", "Word", "创建文档")]
    [InlineData("请 使用Word输入你好", "Word", "输入你好")]
    [InlineData("用中信证券输入中国", "中信证券", "输入中国")]
    [InlineData("在中信证券里输入中国", "中信证券", "输入中国")]
    [InlineData("使用阿里云盘创建测试目录", "阿里云盘", "创建测试目录")]
    [InlineData("在阿里云盘里新建测试目录", "阿里云盘", "新建测试目录")]
    public void NamedSoftwareActionDoesNotRequireArtificialLocationDelimiter(string text, string application, string goal)
    {
        Assert.True(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Equal(application, request!.ApplicationName);
        Assert.Equal(goal, request.Goal);
        Assert.Equal(UniversalIntentKind.DesktopTask, _planner.Plan(text, CurrentWindow).Kind);
    }

    [Theory]
    [InlineData("用Excel在A1输入你好", "Excel", "在A1输入你好")]
    [InlineData("在Excel的A1单元格输入你好", "Excel", "的A1单元格输入你好")]
    [InlineData("使用Excel在B2单元格写入中国", "Excel", "在B2单元格写入中国")]
    [InlineData("在Excel里把A1填成你好", "Excel", "把A1填成你好")]
    [InlineData("用Word在正文输入你好", "Word", "在正文输入你好")]
    [InlineData("打开Word然后写一段会议纪要", "Word", "写一段会议纪要")]
    [InlineData("打开Word然后在正文输入你好", "Word", "在正文输入你好")]
    [InlineData("打开Word，然后帮我输入你好", "Word", "帮我输入你好")]
    [InlineData("打开Excel然后在A1写入你好", "Excel", "在A1写入你好")]
    public void ExplicitLocationRemainsInGoalInsteadOfApplicationName(string text, string application, string goal)
    {
        Assert.True(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Equal(application, request!.ApplicationName);
        Assert.Equal(goal, request.Goal);
        var plan = _planner.Plan(text, CurrentWindow);
        Assert.Equal(UniversalIntentKind.DesktopTask, plan.Kind);
        Assert.Equal(application, plan.Target);
    }

    [Theory]
    [InlineData("怎么在记事本里输入你好")]
    [InlineData("如何在记事本里填写内容")]
    [InlineData("如果在记事本里输入你好会怎样")]
    [InlineData("假如打开记事本然后输入你好")]
    [InlineData("不要在记事本里输入你好")]
    [InlineData("别打开记事本然后输入你好")]
    [InlineData("在记事本里不要输入你好")]
    [InlineData("在记事本里如果输入你好会怎样")]
    [InlineData("在记事本里怎么填写内容")]
    [InlineData("“在记事本里输入你好”")]
    [InlineData("\"打开记事本然后输入你好\"")]
    [InlineData("他说在记事本里输入你好")]
    [InlineData("在记事本里“输入你好”是什么意思")]
    [InlineData("我没有让你在记事本里输入你好")]
    [InlineData("使用记事本，不要输入你好")]
    [InlineData("使用记事本，“输入你好”是什么意思")]
    [InlineData("用Word不要写一段会议纪要")]
    [InlineData("用Word在正文不要输入你好")]
    [InlineData("在Excel的A1单元格不要输入你好")]
    [InlineData("用Word怎么写一段会议纪要")]
    [InlineData("用Word写一段会议纪要会怎么样？")]
    [InlineData("在Excel的A1单元格输入你好会发生什么？")]
    public void QuestionHypothesisQuotationAndNegationNeverCreateDesktopTask(string text)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Null(request);
        Assert.NotEqual(UniversalIntentKind.DesktopTask, _planner.Plan(text, CurrentWindow).Kind);
    }

    [Theory]
    [InlineData("在记事本里点击保存按钮会发生什么？")]
    [InlineData("在记事本里输入你好是什么意思")]
    [InlineData("打开记事本然后输入你好会怎样")]
    [InlineData("在记事本里输入你好，但不要执行")]
    [InlineData("使用计算器，计算1加1会怎样")]
    public void ActionWordsInsideExplanatoryQuestionOrRevocationDoNotAuthorizeTask(string text)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Null(request);
        Assert.NotEqual(UniversalIntentKind.DesktopTask, _planner.Plan(text, CurrentWindow).Kind);
    }

    [Theory]
    [InlineData("在记事本里输入“明天会发生什么？”")]
    [InlineData("在记事本里输入“不要执行”")]
    [InlineData("在记事本里输入\"如果今天下雨怎么办？\"")]
    [InlineData("用Word写入“不要执行”")]
    [InlineData("用Word输入“在中国里搜索创建表格”")]
    public void QuotedTextToEnterIsDataRatherThanAnOuterQuestionOrNegation(string text)
    {
        Assert.True(DesktopTaskRequest.TryParse(text, out _));
        Assert.Equal(UniversalIntentKind.DesktopTask, _planner.Plan(text, CurrentWindow).Kind);
    }

    [Theory]
    [InlineData("打开Steam", "Steam")]
    [InlineData("打开记事本", "记事本")]
    [InlineData("我说的是打开我桌面上的Steam", "我桌面上的Steam")]
    public void PlainOpenKeepsExistingApplicationRoute(string text, string target)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out _));
        var plan = _planner.Plan(text, new(PreferInstalledApplication: true));
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal(target, plan.Target);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void TrustedCompleteApplicationNameWinsOverWordsThatResembleTaskSyntax()
    {
        // Only the Host supplies this flag after resolving the whole installed name.
        var plan = _planner.Plan("打开启动并设置助手", new(PreferInstalledApplication: true));
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal("启动并设置助手", plan.Target);
    }

    [Theory]
    [InlineData("搜索天气", UniversalIntentKind.SearchForeground)]
    [InlineData("在当前窗口搜索天气", UniversalIntentKind.SearchForeground)]
    [InlineData("在网易云音乐里搜索周杰伦", UniversalIntentKind.ApplicationSearch)]
    [InlineData("打开网易云音乐搜索周杰伦", UniversalIntentKind.ApplicationSearch)]
    [InlineData("在网易云音乐里搜索输入中国", UniversalIntentKind.ApplicationSearch)]
    [InlineData("在微信中搜索创建表格", UniversalIntentKind.ApplicationSearch)]
    [InlineData("在当前窗口搜索写入中国", UniversalIntentKind.SearchForeground)]
    [InlineData("打开网易云音乐然后搜索输入中国", UniversalIntentKind.ApplicationSearch)]
    public void ExistingSearchRoutesAreNotCapturedByGenericTasks(string text, UniversalIntentKind expected)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out _));
        var plan = _planner.Plan(text, CurrentWindow);
        Assert.Equal(expected, plan.Kind);
    }

    [Theory]
    [InlineData("在Nova输入框中搜索版本说明", "Nova输入框", "版本说明")]
    [InlineData("在Nova输入框搜索版本说明", "Nova", "版本说明")]
    [InlineData("在Nova输入栏里搜索写入说明", "Nova输入栏", "写入说明")]
    [InlineData("在Nova输入框里面查找输入框架设计", "Nova输入框", "输入框架设计")]
    [InlineData("在Nova输入框中搜索“选择的千问”", "Nova输入框", "选择的千问")]
    public void CompleteSearchApplicationFieldSuffixIsNotAnInputAction(string text, string application, string query)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Null(request);
        var plan = _planner.Plan(text, CurrentWindow);
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(new ApplicationSearchRequest(application, query), plan.ApplicationSearch);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("请使用当前手动选择的千问回答。")]
    [InlineData("使用当前选中的千问回答。")]
    public void ASelectedModelDescriptionDoesNotAuthorizeAnApplicationTask(string text)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Null(request);
        var plan = _planner.Plan(text, CurrentWindow);
        Assert.Equal(UniversalIntentKind.Conversation, plan.Kind);
        Assert.False(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("在C:\\Windows\\system32\\cmd.exe里输入命令")]
    [InlineData("在https://example.invalid里点击链接")]
    [InlineData("在记事本里输入\u0000内容")]
    [InlineData("在记事本里")]
    public void MalformedScopeCannotCreateTask(string text)
    {
        Assert.False(DesktopTaskRequest.TryParse(text, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void ExcessiveRequestIsRejectedWithoutTruncatingGoal()
    {
        Assert.False(DesktopTaskRequest.TryParse("在记事本里输入" + new string('x', 4000), out var request));
        Assert.Null(request);
    }
}
