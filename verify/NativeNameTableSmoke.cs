using System;
using FmPlayerTalk;

public static class NativeNameTableSmoke
{
    public static string Run()
    {
        try
        {
            NativeNameTable.SetScope("synthetic-career-a", "zh-CN");
            var originalScope = NativeNameTable.Version;
            Require(NativeNameTable.Find("zh-CN", 85139014) == null && NativeNameTable.Find("zh-CN", 2002079779) == null,
                "No snapshot UID creates a default name before native observation");
            Require(NativeNameTable.Remember(originalScope, "zh-CN", 2002079779, "当前存档甲") &&
                NativeNameTable.Find("zh-CN", 2002079779) == "当前存档甲", "A verified current name is retained");
            var snapshot = NativeNameTable.Capture(originalScope, "zh-CN");
            Require(NativeNameTable.Remember(originalScope, "zh-CN", 2002079779, "当前显示名") &&
                snapshot[2002079779] == "当前存档甲", "A running lookup receives a stable copied name snapshot");
            NativeNameTable.SetScope("synthetic-career-b", "zh-CN");
            Require(NativeNameTable.Find("zh-CN", 2002079779) == null &&
                !NativeNameTable.Remember(originalScope, "zh-CN", 2002079779, "迟到的旧档姓名") &&
                NativeNameTable.Capture(originalScope, "zh-CN").Count == 0,
                "A reused generated UID and delayed observation cannot cross career scopes");
            var secondScope = NativeNameTable.Version;
            Require(NativeNameTable.Remember(secondScope, "zh-CN", 2002079779, "另一个存档乙"), "Observe another career's same UID");
            NativeNameTable.SetScope("synthetic-career-b", "en");
            Require(NativeNameTable.Find("zh-CN", 2002079779) == null && NativeNameTable.Find("en", 2002079779) == null &&
                !NativeNameTable.Remember(secondScope, "zh-CN", 2002079779, "旧语言"), "Names do not leak across locale changes");
            var englishScope = NativeNameTable.Version;
            Require(NativeNameTable.Remember(englishScope, "en", 12, "Current Person") &&
                !NativeNameTable.Remember(englishScope, "en", 0, "Invalid") &&
                !NativeNameTable.Remember(englishScope, "en", uint.MaxValue, "Invalid") &&
                !NativeNameTable.Remember(englishScope, "en", 13, "Invalid\nName"), "Malformed identities are rejected");
            NativeNameTable.SetScope(null, "en");
            Require(NativeNameTable.Find("en", 12) == null && !NativeNameTable.Remember(NativeNameTable.Version, "en", 12, "Menu Person"),
                "Leaving a loaded career clears its name cache");
            return "PASS: current-world names, generated UID reuse, locale isolation, stale lookup rejection and no hardcoded defaults.";
        }
        finally { NativeNameTable.SetScope(null, ""); }
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException("Native name scope check failed: " + scenario);
    }
}
