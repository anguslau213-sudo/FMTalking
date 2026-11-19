using System;
using System.Text;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class RoleBoundary
{
    private const RegexOptions MatchOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace |
        RegexOptions.Multiline;

    private const string PromptNames = @"(?:系统提示词|系统指令|开发者(?:消息|指令)|
        system\s+(?:prompt|message|instructions?)|developer\s+(?:prompt|message|instructions?))";

    private static readonly Regex HorizontalSpace = new(@"[^\S\n]+", RegexOptions.CultureInvariant);
    private static readonly Regex ChineseSpace = new(
        @"(?<=\p{IsCJKUnifiedIdeographs}) +(?=\p{IsCJKUnifiedIdeographs})", RegexOptions.CultureInvariant);

    private static readonly Regex RoleChangeRequest = new(@"
        (?<!不要)(?<!别)(?<!不用)
        (?: (?:退出|跳出|脱离)(?:你的|当前的?)?角色(?:扮演)?(?=$|[\s，。！？、；：,.!?;:]|并|后|回答|回复)
          | (?:结束|停止|暂停)(?:当前的?|你的)?角色扮演 )
        | (?:从现在起|现在)?你(?:现在|就是)?(?:是|变成|改当)\s*(?:一个|一名)?\s*
          (?:chatgpt|codex|(?:通用(?:的)?(?:ai\s*)?|ai\s*)助手)
        | (?:恢复|回到|切换到|切回|切换为)\s*(?:你的\s*)?(?:chatgpt|codex|ai\s*助手)\s*的?身份
        | (?:用|以)\s*(?:chatgpt|codex|ai\s*助手|通用(?:的)?助手)\s*的?身份(?:来)?(?:回答|回复)
        | (?<!don't\x20)(?<!do\x20not\x20)\b(?:break|step\s+out\s+of|exit|drop|stop)\s+
          (?:(?:your|the|this)\s+)?(?:character\b|role[\s-]?play(?:ing)?\b)
        | \b(?:you\s+are(?:\s+now)?|(?:act|answer|respond)\s+as|become)\s+
          (?:(?:an?|the)\s+)?(?:chatgpt|codex|(?:general(?:[-\s]purpose)?|ai)\s+assistant)\b", MatchOptions);

    private static readonly Regex PromptRequest = new(@"
        (?:输出|逐字复述|复述|显示|展示|公开|泄露|打印|贴出|复制|发给我|给我|告诉我)\s*
        (?:(?:你的|当前的?|隐藏的?|原始的?|完整的?|收到的|真正的?)\s*){0,3}" + PromptNames + @"
        | (?:把\s*(?:(?:你收到的|你的|当前的?|隐藏的?|原始的?|完整的?)\s*){0,3}" + PromptNames + @")\s*
          (?:发给我|贴给我|给我看|输出|打印|展示|念出来)
        | 你(?:的|收到的)\s*" + PromptNames + @"\s*(?:是什么|叫什么|有哪些|的内容)
        | (?:忽略|无视|绕过|覆盖|忘记)\s*(?:(?:所有|之前的?|先前的?|原有的?|当前的?|你的)\s*){0,3}
          (?:" + PromptNames + @"|角色设定|角色扮演规则)
        | \b(?:show|reveal|print|dump|repeat|quote|display|provide|give\s+me|tell\s+me)\s+
          (?:me\s+)?(?:what\s+)?(?:(?:your|the)\s+)?(?:(?:full|hidden|original|current|initial)\s+){0,2}" + PromptNames + @"
        | \b(?:what\s+is|what's|what\s+are)\s+your\s+(?:(?:hidden|original|current)\s+)?" + PromptNames + @"
        | \b(?:ignore|disregard|override|bypass)\s+(?:(?:all|the|your|previous|prior|existing)\s+){0,4}" + PromptNames,
        MatchOptions);

    private static readonly Regex ToolRequest = new(@"
        (?:^|[,，。.!！？?；;:]\s*|(?:请你?|帮我|你(?:能不能|可以|能)|现在|直接|马上|先)\s*)
        (?: (?:调用|使用|运行|执行|打开)\s*(?:你的\s*)?
            (?:mcp(?:\s*工具)?|shell|powershell|bash|cmd\s*命令|终端命令|浏览器工具)
          | 调用\s*(?:你(?:能用|可用)的|你的)?\s*工具
          | (?:读取|打开|列出|访问)\s*(?:本机文件|本地文件|文件系统|环境变量|电脑文件|
            (?:你(?:电脑上|环境中)的|本机的?|本地的?|我电脑上的)\s*(?:文件|目录|环境变量))
          | (?:联网|上网)\s*(?:帮我)?(?:搜索|查找|查一下|搜一下) )
        | (?:(?:^|[,.!?;:])\s*(?:please\s+)?|(?:can|could|would|will)\s+you\s+|you\s+(?:must|should)\s+)
        (?: (?:call|invoke)\s+(?:(?:your|the|an?)\s+)?(?:mcp\s+)?tools?\b
          | (?:run|execute)\s+(?:(?:the|an?)\s+)?(?:shell|terminal|powershell|bash)\s+(?:command|script)\b
          | (?:use|open)\s+(?:your|the)\s+(?:browser|web\s+tool|terminal)\b
          | (?:read|access|list)\s+(?:(?:my|your|the)\s+)?(?:local\s+files|environment\s+variables|file\s*system)\b
          | (?:search|browse)\s+the\s+(?:web|internet)\b )", MatchOptions);

    private static readonly Regex OutOfCharacterReply = new(@"
        (?:我是|作为)\s*(?:一[个名位种]|一个由\s*openai\s*训练的)?\s*
          (?:chatgpt|codex|(?:ai|人工智能)\s*(?:助手|语言模型)|大型语言模型)
        | (?:我(?:现在)?(?:已经)?(?:退出|跳出)了?(?:当前的?)?角色)(?=$|[\s，。！？、；：,.!?;:]|扮演)
        | (?:这是|以下是)\s*(?:我(?:的|收到的)|隐藏的?|原始的?)\s*" + PromptNames + @"
        | \b(?:i\s+am|i'm|as)\s+(?:(?:an?|the)\s+)?(?:chatgpt|codex|ai\s+(?:assistant|language\s+model)|large\s+language\s+model)\b
        | ^\s*(?:out\s+of\s+character|ooc|角色外|出戏说明)\s*[:：]
        | ^\s*\[\s*(?:ooc|out\s+of\s+character)\s*\]", MatchOptions);

    private static readonly Regex ToolTranscript = new(@"
        ^\s*(?:\[\s*(?:tool[\s_]+(?:call|result)|工具(?:调用|结果))\s*\]
             | <\s*(?:tool_call|tool_result|system|developer)(?:\s|>)
             | (?:tool[\s_]+(?:call|result)|工具(?:调用|结果))\s*[:：])
        | ""type""\s*:\s*""(?:command_execution|file_change|mcp_tool_call|web_search)""", MatchOptions);

    internal static bool IsOutOfWorldInput(string text)
    {
        var normalized = Normalize(text);
        return RoleChangeRequest.IsMatch(normalized) || PromptRequest.IsMatch(normalized) || ToolRequest.IsMatch(normalized);
    }

    internal static bool IsOutOfWorldReply(string text)
    {
        var normalized = Normalize(text);
        return OutOfCharacterReply.IsMatch(normalized) || ToolTranscript.IsMatch(normalized);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        try
        {
            text = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Keep checking the rest when a string contains invalid UTF-16.
        }
        var lines = text.ReplaceLineEndings("\n").Replace('’', '\'');
        return ChineseSpace.Replace(HorizontalSpace.Replace(lines, " "), "").Trim();
    }
}
