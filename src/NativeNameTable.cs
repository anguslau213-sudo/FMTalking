using System;
using System.Collections.Generic;

namespace FmPlayerTalk;

// Names observed through verified native bindings in the current loaded world and locale.
// A public UID alone must never select a translation copied from another career.
internal static class NativeNameTable
{
    private const int MaximumNames = 8192;
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, string> Names = new();
    private static string? session;
    private static string language = "";
    private static long version;

    internal static long Version { get { lock (Gate) return version; } }

    internal static void SetScope(string? currentSession, string currentLanguage)
    {
        lock (Gate)
        {
            if (session == currentSession && language == currentLanguage)
                return;
            session = currentSession;
            language = currentLanguage;
            Names.Clear();
            version++;
        }
    }

    internal static bool Remember(long expectedVersion, string currentLanguage, uint publicId, string? name)
    {
        if (publicId is 0 or uint.MaxValue || string.IsNullOrWhiteSpace(name) || name.Length > 160)
            return false;
        foreach (var character in name)
            if (char.IsControl(character))
                return false;
        lock (Gate)
        {
            if (session == null || language.Length == 0 || expectedVersion != version || language != currentLanguage ||
                Names.Count >= MaximumNames && !Names.ContainsKey(publicId))
                return false;
            Names[publicId] = name.Trim();
            return true;
        }
    }

    internal static string? Find(string currentLanguage, uint publicId)
    {
        lock (Gate)
            return session != null && language == currentLanguage && Names.TryGetValue(publicId, out var name) ? name : null;
    }

    internal static IReadOnlyDictionary<uint, string> Capture(long expectedVersion, string currentLanguage)
    {
        lock (Gate)
            return session != null && expectedVersion == version && language == currentLanguage
                ? new Dictionary<uint, string>(Names) : new Dictionary<uint, string>();
    }
}