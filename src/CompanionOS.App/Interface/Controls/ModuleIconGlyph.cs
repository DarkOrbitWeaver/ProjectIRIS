using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CompanionOS.App.Interface.Controls;

/// <summary>Maps built-in module keys or validated local PNG assets to native shell controls.</summary>
internal static class ModuleIconGlyph
{
    private const string PngAssetPrefix = "png:";
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> PngCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? iconKey) => iconKey?.ToLowerInvariant() switch
    {
        "chat" => IconGlyph.Chat,
        "memory" => IconGlyph.Memory,
        "music" => IconGlyph.Play,
        "search" or "web" => IconGlyph.Search,
        "send" or "telegram" => IconGlyph.Send,
        "settings" => IconGlyph.Settings,
        "timer" => IconGlyph.Clock14,
        _ => IconGlyph.Body
    };

    public static Border Create(string? iconKey, double size, IBrush foreground) => new()
    {
        Width = size,
        Height = size,
        Child = CreateContent(iconKey, size, foreground)
    };

    public static void Update(Border host, string? iconKey, double size, IBrush foreground)
    {
        if (string.Equals(host.Tag as string, iconKey, StringComparison.Ordinal))
        {
            return;
        }
        host.Tag = iconKey;
        host.Child = CreateContent(iconKey, size, foreground);
    }

    private static Control CreateContent(string? iconKey, double size, IBrush foreground)
    {
        if (iconKey is not null && iconKey.StartsWith(PngAssetPrefix, StringComparison.Ordinal))
        {
            try
            {
                Bitmap? bitmap = GetPng(iconKey[PngAssetPrefix.Length..]);
                if (bitmap is not null)
                {
                    return new Image
                    {
                        Source = bitmap,
                        Width = size,
                        Height = size,
                        Stretch = Stretch.Uniform
                    };
                }
            }
            catch (Exception)
            {
                // A removed or unreadable plugin icon falls back to the shell's body glyph.
            }
        }

        return IconGlyph.Make(Resolve(iconKey), size, foreground);
    }

    private static Bitmap? GetPng(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (PngCache.TryGetValue(path, out WeakReference<Bitmap>? reference) && reference.TryGetTarget(out Bitmap? cached))
        {
            return cached;
        }

        FileInfo metadata = new(path);
        if (!metadata.Exists || (metadata.Attributes & FileAttributes.ReparsePoint) != 0 ||
            metadata.Length is <= 24 or > 131_072)
        {
            return null;
        }

        byte[] bytes;
        using (FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length is <= 24 or > 131_072)
            {
                return null;
            }
            bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
        }

        ReadOnlySpan<byte> header = bytes.AsSpan(0, 24);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) != 13 ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4)) is 0 or > 128 ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4)) is 0 or > 128)
        {
            return null;
        }

        using MemoryStream stream = new(bytes, writable: false);
        Bitmap bitmap = new(stream);

        if (PngCache.Count >= 256)
        {
            foreach (string expired in PngCache.Where(pair => !pair.Value.TryGetTarget(out _))
                         .Select(pair => pair.Key).Take(64).ToList())
            {
                PngCache.TryRemove(expired, out _);
            }
        }
        if (PngCache.Count < 256)
        {
            PngCache[path] = new WeakReference<Bitmap>(bitmap);
        }
        return bitmap;
    }
}
