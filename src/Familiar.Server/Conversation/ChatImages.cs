namespace Familiar.Server.Conversation;

/// <summary>A picture as uploaded: its id for the message to name, and its size.</summary>
public sealed record ImageView(Guid Id, int Width, int Height);

/// <summary>A picture that isn't a JPEG Familiar will keep; the message says why.</summary>
public sealed class ChatImageException(string message) : Exception(message);

/// <summary>
/// Pictures in the chat (BACKLOG item 35): a JPEG checked by walking its markers, and kept without
/// what a camera writes beside the picture - EXIF (with its GPS position), XMP, comments - so a
/// photo taken at home doesn't keep the house's coordinates. Clients already shrink and re-save
/// pictures, which drops those; this makes sure whatever sent it.
/// </summary>
public static class ChatImages
{
    /// <summary>The picture without its metadata, and its size. Throws if it isn't a whole JPEG of a sensible size.</summary>
    public static (byte[] Bytes, int Width, int Height) Clean(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ChatImageException("Send the picture as a JPEG.");
        }

        if (jpeg.Length > Domain.Conversations.MessageImage.MaxBytes)
        {
            throw new ChatImageException("That picture is too big. Pictures are shrunk before sending; try another.");
        }

        using var output = new MemoryStream(jpeg.Length);
        output.Write([0xFF, 0xD8]);
        int width = 0, height = 0;
        var i = 2;
        var ended = false;
        while (i < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                throw new ChatImageException("That picture is damaged.");
            }

            while (i < jpeg.Length && jpeg[i] == 0xFF)
            {
                i++;
            }

            if (i >= jpeg.Length)
            {
                break;
            }

            var marker = jpeg[i++];
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                ended = true;
                break;
            }

            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Write([0xFF, marker]);
                continue;
            }

            if (i + 2 > jpeg.Length)
            {
                throw new ChatImageException("That picture is damaged.");
            }

            var length = (jpeg[i] << 8) | jpeg[i + 1];
            if (length < 2 || i + length > jpeg.Length)
            {
                throw new ChatImageException("That picture is damaged.");
            }

            var segment = jpeg.Slice(i, length);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && length >= 7)
            {
                height = (segment[3] << 8) | segment[4];
                width = (segment[5] << 8) | segment[6];
            }

            // Kept: everything the picture needs. Dropped: APP1-APP13 and APP15 (EXIF, XMP, ICC's
            // neighbours and makers' notes) and comments. APP0 (JFIF) and APP14 (Adobe colour) stay.
            var metadata = marker is >= 0xE1 and <= 0xEF and not 0xEE or 0xFE;
            if (!metadata)
            {
                output.Write([0xFF, marker]);
                output.Write(segment);
            }

            i += length;
            if (marker == 0xDA)
            {
                // The compressed picture runs to the next marker that isn't a stuffed 0xFF or a restart.
                var start = i;
                while (i + 1 < jpeg.Length && !(jpeg[i] == 0xFF && jpeg[i + 1] != 0x00 && jpeg[i + 1] is not (>= 0xD0 and <= 0xD7)))
                {
                    i++;
                }

                output.Write(jpeg[start..i]);
            }
        }

        if (!ended || width == 0 || height == 0)
        {
            throw new ChatImageException("That picture is damaged.");
        }

        if (width > Domain.Conversations.MessageImage.MaxSide || height > Domain.Conversations.MessageImage.MaxSide)
        {
            throw new ChatImageException("That picture is too big. Pictures are shrunk before sending; try another.");
        }

        return (output.ToArray(), width, height);
    }
}
