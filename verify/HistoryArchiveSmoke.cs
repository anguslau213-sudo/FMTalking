using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FmPlayerTalk;

public static class HistoryArchiveSmoke
{
    public static string Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-history-" + Guid.NewGuid().ToString("N"));
        var chats = Path.Combine(root, "conversations");
        Directory.CreateDirectory(chats);
        var key = new string('A', 64);
        var path = Path.Combine(chats, key + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 0, turns = new[]
            {
                new { user = "我们聊聊。", assistant = "好，教练。", gameDate = "2026-07-17T00:00:00", isIncoming = false },
                new { user = "", assistant = "这是之前的来信。", gameDate = "2026-07-18T00:00:00", isIncoming = true }
            }
        });
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(chats, new string('B', 64) + ".json"), "{\"stage\":2,\"day\":\"2026-07-18\"}");
        File.WriteAllText(Path.Combine(chats, new string('C', 64) + ".json"), "{not-json");
        File.WriteAllBytes(Path.Combine(chats, "outside.json"), bytes);
        var read = Conversation.ReadStoredHistory(root);
        Require(read.Count == 1 && read[0].Key == key && read[0].Turns.Length == 2 &&
            read[0].Turns[0].User == "我们聊聊。" && read[0].Turns[1].IsIncoming &&
            read[0].LastGameDate == new DateTime(2026, 7, 18), "Only valid bounded history files are read");
        Require(File.ReadAllBytes(path).SequenceEqual(bytes) && !File.Exists(Path.Combine(root, "departed-contacts.json")),
            "Reading neither rewrites history nor creates contacts");
        Require(Conversation.ReadStoredHistory("relative").Count == 0, "Relative folders are rejected");
        File.WriteAllText(Path.Combine(chats, new string('D', 64) + ".json"),
            "{\"turns\":[{\"user\":\"Hi\",\"assistant\":\"one\",\"Assistant\":\"two\"}]}");
        File.WriteAllText(Path.Combine(chats, new string('E', 64) + ".json"),
            "{\"Turns\":[{\"User\":\"Hi\",\"Assistant\":\"Old style\"}]}");
        Require(Conversation.ReadStoredHistory(root).Count == 2, "Legacy property casing is readable while duplicate turn fields are rejected");
        var linkRoot = Path.Combine(Path.GetTempPath(), "fm-player-talk-history-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(linkRoot, root);
        Require(Conversation.ReadStoredHistory(linkRoot).Count == 0, "A linked session directory is rejected");
        var linkedFile = Path.Combine(chats, new string('F', 64) + ".json");
        File.CreateSymbolicLink(linkedFile, path);
        Require(Conversation.ReadStoredHistory(root).Count == 2, "A linked history file is rejected");
        File.WriteAllText(Path.Combine(chats, new string('1', 64) + ".json"), "{\"turns\":[]}");
        File.WriteAllText(Path.Combine(chats, new string('2', 64) + ".json"), JsonSerializer.Serialize(new
        {
            turns = Enumerable.Repeat(new { user = "Hi", assistant = "Hello" }, 501).ToArray()
        }));
        File.WriteAllBytes(Path.Combine(chats, new string('3', 64) + ".json"), new byte[2 * 1024 * 1024 + 1]);
        File.WriteAllText(Path.Combine(chats, new string('4', 64) + ".json"),
            "{\"turns\":[{\"user\":\"Hi\",\"assistant\":\"Hello\",\"isIncoming\":false,\"IsIncoming\":true}]}");
        Require(Conversation.ReadStoredHistory(root).Count == 2, "Empty, oversized and ambiguous history is ignored");
        File.WriteAllBytes(Path.Combine(chats, new string('5', 64) + ".json"), Encoding.UTF8.Preamble.ToArray().Concat(bytes).ToArray());
        Require(Conversation.ReadStoredHistory(root).Count == 3, "A legacy UTF-8 BOM remains readable");
        return "PASS: read-only current-session history archive, legacy text, malformed/state files, duplicate fields, and links.";
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("History archive check failed: " + scenario);
    }
}
