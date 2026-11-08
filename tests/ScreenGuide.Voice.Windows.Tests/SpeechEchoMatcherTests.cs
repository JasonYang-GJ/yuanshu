using ScreenGuide.Voice.Windows;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class SpeechEchoMatcherTests
{
    [Theory]
    [InlineData("停")]
    [InlineData("停停")]
    [InlineData("停，停！")]
    [InlineData("停止")]
    [InlineData("等一下")]
    [InlineData("你说错了")]
    [InlineData("我想问另外一个问题")]
    public void CanInterrupt_AcceptsUsefulBargeInPhrases(string text)
    {
        Assert.True(SpeechEchoMatcher.CanInterrupt(text));
    }

    [Theory]
    [InlineData("啊")]
    [InlineData("嗯")]
    [InlineData("")]
    public void CanInterrupt_RejectsShortNoise(string text)
    {
        Assert.False(SpeechEchoMatcher.CanInterrupt(text));
    }

    [Theory]
    [InlineData("正在打开微信", "好的，正在打开微信。")]
    [InlineData("这个页面是闲鱼发布界面", "我看到这个页面是闲鱼发布界面，你可以填写商品信息。")]
    [InlineData("好的正在打开微信请稍等", "正在打开微信")]
    [InlineData("打开微信正在", "正在打开微信")]
    public void IsLikelyAssistantEcho_DetectsPlaybackText(string recognized, string assistant)
    {
        Assert.True(SpeechEchoMatcher.IsLikelyAssistantEcho(recognized, assistant));
    }

    [Theory]
    [InlineData("我在微信里搜索文件传输助手")]
    [InlineData("我在用微信，怎么搜索")]
    [InlineData("我在家看书")]
    [InlineData("我在学习，请解释海浪形成的原因。")]
    public void ShortAcknowledgementDoesNotHideTheNextUserSentence(string recognized)
    {
        Assert.False(SpeechEchoMatcher.IsLikelyAssistantEcho(recognized, "我在。"));
    }

    [Theory]
    [InlineData("我在")]
    [InlineData("我在。")]
    [InlineData("我 在！")]
    public void ExactShortAcknowledgementStillCountsAsEcho(string recognized)
    {
        Assert.True(SpeechEchoMatcher.IsLikelyAssistantEcho(recognized, "我在。"));
    }

    [Fact]
    public void IsLikelyAssistantEcho_DoesNotHideUserCorrection()
    {
        Assert.False(SpeechEchoMatcher.IsLikelyAssistantEcho(
            "不对我问的是第三题",
            "这是一张数学试卷，第一题的答案是A。"));
    }
}
