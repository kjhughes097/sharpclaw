using System.Text;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services;

/// <summary>An upload that passed every rule: the cleaned name, kind, real media type and bytes.</summary>
public sealed record ValidatedAttachment(string FileName, string Kind, string MediaType, byte[] Data);

/// <summary>
/// Checks uploads against attachments spec section 1. The kind comes from the extension, never
/// from anything the client declares; images must also carry the matching magic number and
/// text must be strict UTF-8, so a renamed binary can't slip through as either.
/// </summary>
public static class AttachmentValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <returns>The validated attachments, or an error message naming the file and the rule.</returns>
    public static (List<ValidatedAttachment> Attachments, string? Error) Validate(
        IReadOnlyList<AttachmentUpload>? uploads, MessagesOptions options)
    {
        var result = new List<ValidatedAttachment>();
        if (uploads is null || uploads.Count == 0)
            return (result, null);
        if (uploads.Count > options.MaxAttachments)
            return (result, $"at most {options.MaxAttachments} attachments per message.");

        foreach (var upload in uploads)
        {
            if (upload is null)
                return (result, "attachments must not contain null.");
            var name = CleanFileName(upload.FileName);
            if (name.Length is 0 or > AttachmentRules.MaxFileNameChars)
                return (result, $"attachment file names must be 1–{AttachmentRules.MaxFileNameChars} characters.");

            var kind = AttachmentRules.KindOf(name);
            if (kind is null)
                return (result, $"'{name}' is not a supported file type.");

            var max = kind == AttachmentKinds.Image ? options.MaxImageBytes : options.MaxTextBytes;
            // Reject oversized uploads before decoding them.
            if (string.IsNullOrEmpty(upload.Data) || upload.Data.Length > (max + 2L) / 3 * 4)
                return (result, string.IsNullOrEmpty(upload.Data)
                    ? $"'{name}' is empty."
                    : $"'{name}' is larger than {Describe(max)}.");

            byte[] data;
            try
            {
                data = Convert.FromBase64String(upload.Data);
            }
            catch (FormatException)
            {
                return (result, $"'{name}' data must be base64.");
            }
            if (data.Length == 0)
                return (result, $"'{name}' is empty.");
            if (data.Length > max)
                return (result, $"'{name}' is larger than {Describe(max)}.");

            string mediaType;
            if (kind == AttachmentKinds.Image)
            {
                mediaType = AttachmentRules.ImageExtensions[Path.GetExtension(name)];
                if (SniffImage(data) != mediaType)
                    return (result, $"'{name}' is not a valid {mediaType[6..].ToUpperInvariant()} image.");
            }
            else
            {
                if (!IsText(data))
                    return (result, $"'{name}' is not UTF-8 text.");
                mediaType = AttachmentRules.TextMediaType;
            }
            result.Add(new ValidatedAttachment(name, kind, mediaType, data));
        }
        return (result, null);
    }

    /// <summary>The last path segment with control characters removed and whitespace trimmed.</summary>
    public static string CleanFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return "";
        var last = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        return new string(last.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    /// <summary>The media type of a PNG, JPEG, GIF or WebP by its magic number; null for anything else.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> d)
    {
        if (d.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (d.StartsWith("GIF87a"u8) || d.StartsWith("GIF89a"u8))
            return "image/gif";
        if (d.Length >= 12 && d.StartsWith("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    /// <summary>Decodes a text attachment, dropping a UTF-8 byte order mark.</summary>
    public static string DecodeText(byte[] data) => StrictUtf8.GetString(data).TrimStart('\uFEFF');

    private static bool IsText(byte[] data)
    {
        if (Array.IndexOf(data, (byte)0) >= 0)
            return false;
        try
        {
            StrictUtf8.GetCharCount(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Describe(int bytes) => bytes switch
    {
        _ when bytes % (1024 * 1024) == 0 => $"{bytes / (1024 * 1024)} MB",
        _ when bytes % 1024 == 0 => $"{bytes / 1024} KB",
        _ => $"{bytes} bytes",
    };
}
