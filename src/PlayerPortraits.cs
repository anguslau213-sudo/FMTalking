using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using FM.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal static class PlayerPortraits
{
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const string RecordPrefix = "graphics/pictures/person/";
    private const string RecordSuffix = "/portrait";
    private static readonly Dictionary<uint, Texture2D> textures = new();
    private static readonly Dictionary<uint, Task<byte[]?>> pending = new();
    private static readonly Dictionary<uint, InboxPortrait> inboxPortraits = new();
    private static readonly string[] imageExtensions = { ".png", ".jpg", ".jpeg" };
    private static Task<PortraitIndex>? indexTask;
    private static string? portraitSession;
    private static bool reportedPortraitError;

    internal sealed record InboxPortrait(uint PublicId, string? AssetId, Il2CppSystem.Object? GenerationData);

    internal static InboxPortrait ReadInboxPortrait(uint publicId, TypedValue? regenerated, TypedValue? female,
        TypedValue? appearance, TypedValue? gameDay)
    {
        // These are the native fields and property IDs of the FM PersonPicture asset.
        try
        {
            if (regenerated is not { IsNull: false, DataType.Name: "Boolean" } || regenerated.Get() is not { } flag)
                return new InboxPortrait(publicId, null, null);
            if (!flag.Unbox<bool>())
            {
                var staticAsset = publicId <= int.MaxValue
                    ? DatabaseObjectImageHelpers.ConvertObjectIDToAddressableID((int)publicId, DatabaseTableType.Person)
                    : null;
                return new InboxPortrait(publicId, staticAsset, null);
            }
            if (female is not { IsNull: false, DataType.Name: "Boolean" } ||
                appearance is not { IsNull: false } || gameDay is not { IsNull: false })
                return new InboxPortrait(publicId, null, null);

            var person = new DynamicReference(5);
            person.Add(1970170212, TypedValue.Create(publicId));
            person.Add(1349674343, TypedValue.Create(true));
            person.Add(1316385136, TypedValue.CreateCopyFrom(female));
            person.Add(1094857046, TypedValue.CreateCopyFrom(appearance));
            person.Add(1131498817, TypedValue.CreateCopyFrom(gameDay));
            var assetId = DatabaseObjectImageHelpers.ConvertDynamicReferenceToAddressableID(person,
                DatabaseTableType.Person, out var generationData);
            return new InboxPortrait(publicId, assetId, generationData);
        }
        catch (Exception)
        {
            return new InboxPortrait(publicId, null, null);
        }
    }

    internal static void SetInboxPortraits(IReadOnlyDictionary<uint, InboxPortrait> portraits)
    {
        foreach (var portrait in portraits.Values)
            RememberNativePortrait(portrait);
    }

    internal static void RememberNativePortrait(InboxPortrait portrait)
    {
        MatchPortraitSession();
        if (portraitSession is null || string.IsNullOrWhiteSpace(portrait.AssetId))
            return;
        if (!inboxPortraits.ContainsKey(portrait.PublicId) && inboxPortraits.Count >= 128)
            inboxPortraits.Remove(inboxPortraits.Keys.First());
        inboxPortraits[portrait.PublicId] = portrait;
    }

    private static void MatchPortraitSession()
    {
        var current = GameSaveTimeline.DirectoryPath;
        if (portraitSession == current)
            return;
        portraitSession = current;
        inboxPortraits.Clear();
    }

    private static InboxPortrait? FindInboxPortrait(uint id)
    {
        MatchPortraitSession();
        return inboxPortraits.TryGetValue(id, out var portrait) ? portrait : null;
    }

    internal static VisualElement Create(string initials, uint? publicId, float size)
    {
        var avatar = new VisualElement();
        avatar.style.width = size;
        avatar.style.height = size;
        avatar.style.flexShrink = 0;
        avatar.style.alignItems = Align.Center;
        avatar.style.justifyContent = Justify.Center;
        avatar.style.backgroundColor = new Color(0.18f, 0.13f, 0.24f);
        avatar.style.borderTopLeftRadius = size / 2;
        avatar.style.borderTopRightRadius = size / 2;
        avatar.style.borderBottomLeftRadius = size / 2;
        avatar.style.borderBottomRightRadius = size / 2;
        avatar.style.overflow = Overflow.Hidden;
        avatar.pickingMode = PickingMode.Ignore;

        var fallback = new Label(string.IsNullOrWhiteSpace(initials) ? "?" : initials)
        {
            enableRichText = false,
            pickingMode = PickingMode.Ignore
        };
        fallback.style.width = size;
        fallback.style.height = size;
        fallback.style.whiteSpace = WhiteSpace.NoWrap;
        fallback.style.fontSize = Math.Max(11f, size * 0.34f);
        fallback.style.unityFontStyleAndWeight = FontStyle.Bold;
        fallback.style.unityTextAlign = TextAnchor.MiddleCenter;
        fallback.style.color = new Color(0.94f, 0.91f, 0.97f);
        avatar.Add(fallback);

        if (publicId is uint id && id != 0)
            AttachPublicPortrait(avatar, fallback, id);
        return avatar;
    }

    private static void AttachPublicPortrait(VisualElement avatar, Label fallback, uint id)
    {
        if (textures.TryGetValue(id, out var cached) && cached != null)
        {
            Attach(avatar, fallback, cached);
            return;
        }

        // Start the local lookup as soon as a verified public ID is rendered. FM's UI scheduler
        // only waits for the file read and creates the Unity texture on the main thread.
        var load = ReadFacepack(id);
        IVisualElementScheduledItem? poll = null;
        poll = avatar.schedule.Execute((Action)(() =>
        {
            if (!load.IsCompleted)
                return;
            poll?.Pause();
            try
            {
                if (textures.TryGetValue(id, out var picture) && picture != null)
                    Attach(avatar, fallback, picture);
                else if (load.IsCompletedSuccessfully && load.Result is { } bytes && DecodeFacepack(id, bytes) is { } texture)
                    Attach(avatar, fallback, texture);
                else if (FindInboxPortrait(id) is { } portrait && portrait.PublicId == id)
                    AttachInboxPortrait(avatar, fallback, portrait);
            }
            catch (Exception error) { ReportPortraitError(error); }
        })).Every(80);
    }

    private static void AttachInboxPortrait(VisualElement avatar, Label fallback, InboxPortrait portrait)
    {
        if (string.IsNullOrWhiteSpace(portrait.AssetId))
            return;
        var image = new SIImage { ScaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
        image.style.visibility = Visibility.Hidden;
        PositionImage(image);
        avatar.Add(image);
        try
        {
            image.SetImage(portrait.AssetId, portrait.GenerationData!);
            var deadline = Time.unscaledTime + 15f;
            IVisualElementScheduledItem? poll = null;
            poll = avatar.schedule.Execute((Action)(() =>
            {
                try
                {
                    if (image.image != null || image.sprite != null)
                    {
                        image.style.visibility = Visibility.Visible;
                        fallback.RemoveFromHierarchy();
                        poll?.Pause();
                    }
                    else if (Time.unscaledTime >= deadline)
                    {
                        image.RemoveFromHierarchy();
                        poll?.Pause();
                    }
                }
                catch (Exception error)
                {
                    image.RemoveFromHierarchy();
                    poll?.Pause();
                    ReportPortraitError(error);
                }
            })).Every(100);
        }
        catch
        {
            image.RemoveFromHierarchy();
            throw;
        }
    }

    private static void ReportPortraitError(Exception error)
    {
        if (reportedPortraitError)
            return;
        reportedPortraitError = true;
        Plugin.Logger.LogInfo($"FM Player Talk portrait unavailable ({error.GetType().Name}).");
    }

    private static Task<byte[]?> ReadFacepack(uint id)
    {
        if (pending.TryGetValue(id, out var load))
            return load;
        if (indexTask == null)
        {
            var roots = FindGraphicsRoots();
            indexTask = Task.Run(() => BuildIndex(roots));
        }
        var indexReady = indexTask;
        load = Task.Run(async () => (await indexReady.ConfigureAwait(false)).Read(id));
        pending.Add(id, load);
        return load;
    }

    private static Texture2D? DecodeFacepack(uint id, byte[] bytes)
    {
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(texture, new Il2CppStructArray<byte>(bytes), true))
            {
                textures[id] = texture;
                pending.Remove(id);
                var result = texture;
                texture = null;
                return result;
            }
        }
        catch (Exception error)
        {
            ReportPortraitError(error);
        }
        finally
        {
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
        }
        return null;
    }

    private static void Attach(VisualElement avatar, Label fallback, Texture2D texture)
    {
        var image = new Image { image = texture, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
        PositionImage(image);
        avatar.Add(image);
        fallback.RemoveFromHierarchy();
    }

    private static void PositionImage(VisualElement image)
    {
        image.style.position = Position.Absolute;
        image.style.top = 0;
        image.style.right = 0;
        image.style.bottom = 0;
        image.style.left = 0;
    }

    private static string[] FindGraphicsRoots()
    {
        var roots = new List<string>();
        try
        {
            var systems = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ResourcesSubsystem>());
            if (systems != null)
            {
                for (int i = 0; i < systems.Length; i++)
                {
                    var registrations = systems[i]?.TryCast<ResourcesSubsystem>()?.m_localPaths;
                    if (registrations == null)
                        continue;
                    for (int j = 0; j < registrations.Count; j++)
                    {
                        var path = registrations[j]?.Path;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), "graphics", StringComparison.OrdinalIgnoreCase))
                        {
                            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                            if (!roots.Exists(root => string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)))
                                roots.Add(normalized);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Older game bindings may not expose the registered graphics directory.
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            var standard = Path.Combine(documents, "Sports Interactive", "Football Manager 27", "graphics");
            if (!roots.Exists(path => string.Equals(path, standard, StringComparison.OrdinalIgnoreCase)))
                roots.Add(standard);
        }
        return roots.ToArray();
    }

    private static PortraitIndex BuildIndex(string[] roots)
    {
        var index = new PortraitIndex();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                var common = Path.Combine(root, "players", "sortitoutsi", "faces", "config.xml");
                foreach (var path in Directory.EnumerateFiles(root, "config.xml", options))
                {
                    if (!string.Equals(path, common, StringComparison.OrdinalIgnoreCase) && visited.Add(path))
                        ReadMappings(path, index);
                }
                if (visited.Add(common))
                    ReadMappings(common, index);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return index;
    }

    private static void ReadMappings(string config, PortraitIndex index)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(config))! + Path.DirectorySeparatorChar;
            using var stream = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "record")
                    continue;
                var target = xml.GetAttribute("to");
                if (target == null || target.Length <= RecordPrefix.Length + RecordSuffix.Length ||
                    !target.StartsWith(RecordPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !target.EndsWith(RecordSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var digits = target.AsSpan(RecordPrefix.Length, target.Length - RecordPrefix.Length - RecordSuffix.Length);
                if (digits.StartsWith("r-", StringComparison.OrdinalIgnoreCase))
                    digits = digits[2..];
                if (!uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                    continue;

                var source = xml.GetAttribute("from");
                if (string.IsNullOrWhiteSpace(source) || Path.IsPathRooted(source))
                    continue;
                // The common pack has hundreds of thousands of face_<id> names.
                // Reconstruct those on demand instead of retaining another string per ID.
                if (digits[0] != '0' && source.StartsWith("face_", StringComparison.Ordinal) &&
                    source.AsSpan(5).SequenceEqual(digits))
                    source = null;
                index.Add(id, new PortraitSource(folder, source));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (XmlException) { }
        catch (ArgumentException) { }
    }

    private static byte[]? ReadImage(PortraitSource source, uint id)
    {
        try
        {
            var stem = Path.GetFullPath(Path.Combine(source.Folder, source.RelativeImage ?? "face_" + id.ToString(CultureInfo.InvariantCulture)));
            if (!stem.StartsWith(source.Folder, StringComparison.OrdinalIgnoreCase))
                return null;

            var suffix = Path.GetExtension(stem);
            if (Array.Exists(imageExtensions, extension => string.Equals(extension, suffix, StringComparison.OrdinalIgnoreCase)))
                return ReadImageFile(stem);
            foreach (var extension in imageExtensions)
                if (ReadImageFile(stem + extension) is { } bytes)
                    return bytes;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return null;
    }

    private static byte[]? ReadImageFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length is > 0 and <= MaxImageBytes ? File.ReadAllBytes(path) : null;
    }

    private readonly record struct PortraitSource(string Folder, string? RelativeImage);

    private sealed class PortraitIndex
    {
        private readonly Dictionary<uint, PortraitSource> primary = new();
        private readonly Dictionary<uint, List<PortraitSource>> alternatives = new();

        internal void Add(uint id, PortraitSource source)
        {
            if (primary.TryAdd(id, source) || primary[id] == source)
                return;
            if (!alternatives.TryGetValue(id, out var otherSources))
            {
                otherSources = new List<PortraitSource>();
                alternatives.Add(id, otherSources);
            }
            if (!otherSources.Contains(source))
                otherSources.Add(source);
        }

        internal byte[]? Read(uint id)
        {
            if (primary.TryGetValue(id, out var source) && ReadImage(source, id) is { } bytes)
                return bytes;
            if (alternatives.TryGetValue(id, out var otherSources))
                foreach (var other in otherSources)
                    if (ReadImage(other, id) is { } alternate)
                        return alternate;
            return null;
        }
    }
}
