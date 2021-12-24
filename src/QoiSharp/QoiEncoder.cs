using QoiSharp.Codec;
using QoiSharp.Exceptions;

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace QoiSharp;

/// <summary>
/// QOI encoder.
/// </summary>
public static class QoiEncoder
{
    /// <summary>
    /// Encodes raw pixel data into QOI.
    /// </summary>
    /// <param name="image">QOI image.</param>
    /// <returns>Encoded image.</returns>
    /// <exception cref="QoiEncodingException">Thrown when image information is invalid.</exception>
    public static byte[] Encode(QoiImage image)
    {
        var bytes = new byte[QoiCodec.HeaderSize + QoiCodec.Padding.Length + (image.Width * image.Height * (byte)image.Channels)];
        return bytes[..Encode(image, bytes)];
    }

    public static int Encode(QoiImage image, Span<byte> buffer)
    {
        int width = image.Width;
        int height = image.Height;
        int channels = (int)image.Channels;
        byte colorSpace = (byte)image.ColorSpace;

        if (image.Width == 0)
            throw new QoiEncodingException($"Invalid width: {image.Width}");

        if (image.Height == 0 || image.Height >= QoiCodec.MaxPixels / image.Width)
            throw new QoiEncodingException($"Invalid height: {image.Height}. Maximum for this image is {QoiCodec.MaxPixels / image.Width - 1}");

        if (buffer.Length < QoiCodec.HeaderSize + QoiCodec.Padding.Length + (width * height * channels))
            return -1;

        BinaryPrimitives.WriteInt32BigEndian(buffer, QoiCodec.Magic);
        BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(4), width);
        BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(8), height);

        buffer[12] = (byte)channels;
        buffer[13] = colorSpace;

        ReadOnlySpan<byte> pixels = image.Data.Span.Slice (0, width * height * channels);

        var payload = image.Channels == Channels.Rgb
            ? Encode3(pixels, buffer.Slice(QoiCodec.HeaderSize))
            : Encode4(pixels, buffer.Slice(QoiCodec.HeaderSize));
        return QoiCodec.HeaderSize + payload;
    }

    readonly struct RGB
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        public static bool operator ==(RGB left, RGB right)
            => left.R == right.R
            && left.G == right.G
            && left.B == right.B;

        public static bool operator !=(RGB left, RGB right)
            => left.R != right.R
            || left.G != right.G
            || left.B != right.B;
    }

    readonly struct RGBA
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        public RGBA(byte r, byte b, byte g, byte a)
            => (R, G, B, A) = (r, g, b, a);

        public static bool operator ==(RGBA left, RGBA right)
            => left.R == right.R
            && left.G == right.G
            && left.B == right.B
            && left.A == right.A;

        public static bool operator !=(RGBA left, RGBA right)
            => left.R != right.R
            || left.G != right.G
            || left.B != right.B
            || left.A != right.A;
    }

    static int Encode3(ReadOnlySpan<byte> input, Span<byte> buffer)
    {
        Span<RGB> index = stackalloc RGB[QoiCodec.HashTableSize];
        index.Clear();

        RGB prev = default;

        int run = 0;
        int counter = 0;
        int p = 0;

        ReadOnlySpan<RGB> pixels = MemoryMarshal.Cast<byte, RGB>(input);
        for (int i = 0; i < pixels.Length; i++)
        {
            var rgb = pixels[i];
            if (rgb == prev)
            {
                run++;
                if (run == 62 || i == pixels.Length - 1)
                {
                    buffer[p++] = (byte)(QoiCodec.Run | (run - 1));
                    run = 0;
                }
            }
            else
            {
                if (run > 0)
                {
                    buffer[p++] = (byte)(QoiCodec.Run | (run - 1));
                    run = 0;
                }

                int indexPos = (rgb.R * 3 + rgb.G * 5 + rgb.B * 7 + 255 * 11) % QoiCodec.HashTableSize;
                if (rgb == index[indexPos])
                {
                    buffer[p++] = (byte)(QoiCodec.Index | (indexPos));
                }
                else
                {
                    index[indexPos] = rgb;

                    int vr = rgb.R - prev.R;
                    int vg = rgb.G - prev.G;
                    int vb = rgb.B - prev.B;

                    int vgr = vr - vg;
                    int vgb = vb - vg;

                    if (vr is > -3 and < 2 &&
                        vg is > -3 and < 2 &&
                        vb is > -3 and < 2)
                    {
                        counter++;
                        buffer[p++] = (byte)(QoiCodec.Diff | (vr + 2) << 4 | (vg + 2) << 2 | (vb + 2));
                    }
                    else if (vgr is > -9 and < 8 &&
                             vg is > -33 and < 32 &&
                             vgb is > -9 and < 8
                            )
                    {
                        buffer[p++] = (byte)(QoiCodec.Luma | (vg + 32));
                        buffer[p++] = (byte)((vgr + 8) << 4 | (vgb + 8));
                    }
                    else
                    {
                        if (p + 4 < buffer.Length)
                        {
                            buffer[p] = QoiCodec.Rgb;
                            buffer[p+1] = rgb.R;
                            buffer[p+2] = rgb.G;
                            buffer[p+3] = rgb.B;
                            p += 4;
                        }
                    }
                }
            }
            prev = rgb;
        }

        QoiCodec.Padding.Span.CopyTo(buffer.Slice (p));
        p += QoiCodec.Padding.Length;

        return p;
    }

    public static int Encode4(ReadOnlySpan<byte> input, Span<byte> buffer)
    {
        Span<RGBA> index = stackalloc RGBA[QoiCodec.HashTableSize];
        index.Clear();

        RGBA prev = default;

        int run = 0;
        int counter = 0;
        int p = 0;

        ReadOnlySpan<RGBA> pixels = MemoryMarshal.Cast<byte, RGBA>(input);
        for (int i =0; i < pixels.Length; i ++)
        {
            var rgba = pixels[i];

            if (prev == rgba)
            {
                run++;
                if (run == 62 || pixels.Length == 0)
                {
                    buffer[p++] = (byte)(QoiCodec.Run | (run - 1));
                    run = 0;
                }
            }
            else
            {
                if (run > 0)
                {
                    buffer[p++] = (byte)(QoiCodec.Run | (run - 1));
                    run = 0;
                }

                int indexPos = (rgba.R * 3 + rgba.G * 5 + rgba.B * 7 + rgba.A * 11) % QoiCodec.HashTableSize;
                if (rgba == index[indexPos])
                {
                    buffer[p++] = (byte)(QoiCodec.Index | (indexPos));
                }
                else
                {
                    index[indexPos] = rgba;

                    if (rgba.A == prev.A)
                    {
                        int vr = rgba.R - prev.R;
                        int vg = rgba.G - prev.G;
                        int vb = rgba.B - prev.B;

                        int vgr = vr - vg;
                        int vgb = vb - vg;

                        if (vr is > -3 and < 2 &&
                            vg is > -3 and < 2 &&
                            vb is > -3 and < 2)
                        {
                            counter++;
                            buffer[p++] = (byte)(QoiCodec.Diff | (vr + 2) << 4 | (vg + 2) << 2 | (vb + 2));
                        }
                        else if (vgr is > -9 and < 8 &&
                                 vg is > -33 and < 32 &&
                                 vgb is > -9 and < 8
                                )
                        {
                            buffer[p++] = (byte)(QoiCodec.Luma | (vg + 32));
                            buffer[p++] = (byte)((vgr + 8) << 4 | (vgb + 8));
                        }
                        else
                        {
                            if (p + 4 < buffer.Length)
                            {
                                buffer[p] = QoiCodec.Rgb;
                                buffer[p + 1] = rgba.R;
                                buffer[p + 2] = rgba.G;
                                buffer[p + 3] = rgba.B;
                                p += 4;
                            }
                        }
                    }
                    else
                    {
                        if (p + 5 < buffer.Length)
                        {
                            buffer[p] = QoiCodec.Rgba;
                            buffer[p + 1] = rgba.R;
                            buffer[p + 2] = rgba.G;
                            buffer[p + 3] = rgba.B;
                            buffer[p + 4] = rgba.A;
                            p += 5;
                        }
                    }
                }
            }
            prev = rgba;
        }

        QoiCodec.Padding.Span.CopyTo(buffer.Slice(p));
        p += QoiCodec.Padding.Length;

        return p;
    }

}