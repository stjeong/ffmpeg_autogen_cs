using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FFmpeg.AutoGen.Example
{
    public enum AV_BUFFERSRC_FLAG
    {
        NO_CHECK_FORMAT = 1,
        PUSH = 4,
        KEEP_REF = 8,
    }

    internal static class FFmpegHelper
    {
        public static AVRational AV_TIME_BASE_Q
        {
            get
            {
                return new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
            }
        }

        public static unsafe double av_q2d(AVRational* ar)
        {
            return (ar->num / (double)ar->den);
        }

        public static unsafe string av_ts2timestr(long pts, in AVRational av)
        {
            fixed (AVRational* pav = &av)
            {
                return (av_q2d(pav) * pts).ToString();
            }
        }

        public static unsafe string av_ts2timestr(long pts, AVRational *av)
        {
            if (pts == 0)
            {
                return "NOPTS";
            }

            return (av_q2d(av) * pts).ToString("G6");
        }

        public static unsafe string av_err2str(int error)
        {
            return av_strerror(error);
        }

        public static unsafe string av_strerror(int error)
        {
            var bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
            return message ?? "";
        }

        public static int ThrowExceptionIfError(this int error)
        {
            if (error < 0) throw new ApplicationException(av_strerror(error));
            return error;
        }

        public static unsafe string AnsiToString(byte* ptr)
        {
            return Marshal.PtrToStringAnsi(new IntPtr(ptr)) ?? "";
        }

        // PGM File Viewer (browser-based)
        // ; https://smallpond.ca/jim/photomicrography/pgmViewer/index.html
        public static unsafe void pgm_save(byte* buf, int wrap, int xsize, int ysize, string filename)
        {
            using FileStream fs = new FileStream(filename, FileMode.Create);

            byte[] header = Encoding.ASCII.GetBytes($"P5\n{xsize} {ysize}\n255\n");
            fs.Write(header);

            // C# - byte * (바이트 포인터)를 FileStream으로 쓰는 방법
            // https://www.sysnet.pe.kr/2/0/12913
            for (int i = 0; i < ysize; i++)
            {
                byte* ptr = buf + (i * wrap);
                ReadOnlySpan<byte> pos = new Span<byte>(ptr, xsize);

                fs.Write(pos);
            }
        }

        public static void PrintHwDevices()
        {
            AVHWDeviceType type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                Console.WriteLine($"{ffmpeg.av_hwdevice_get_type_name(type)}");
            }
        }

        public static unsafe void PrintCodecs()
        {
            const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;

            void* iter = null;

            for (; ; )
            {
                AVCodec* cur = ffmpeg.av_codec_iterate(&iter);
                if (cur == null)
                {
                    break;
                }

                Console.WriteLine($"{FFmpegHelper.AnsiToString(cur->name)}({FFmpegHelper.AnsiToString(cur->long_name)})");

                AVCodecHWConfig* config = null;
                for (int n = 0; (config = ffmpeg.avcodec_get_hw_config(cur, n)) != null; n++)
                {

                    if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX)
                    {
                        Console.WriteLine($"\thw-accel: {config->pix_fmt}, type: {config->device_type}, decoder: {ffmpeg.av_codec_is_decoder(cur)}, encoder: {ffmpeg.av_codec_is_encoder(cur)}");
                    }
                }
            }
        }

        public static int get_format_from_sample_fmt(out string fmt, AVSampleFormat sample_fmt)
        {
            fmt = "";

            foreach (var item in sample_fmt_entry.entries)
            {
                if (item.sample_fmt == sample_fmt)
                {
                    fmt = (BitConverter.IsLittleEndian) ? item.fmt_le : item.fmt_be;
                    return 0;
                }
            }

            return ffmpeg.AVERROR(ffmpeg.EINVAL);
        }

        public class sample_fmt_entry
        {
            public AVSampleFormat sample_fmt;
            public string fmt_be = "";
            public string fmt_le = "";

            public static sample_fmt_entry[] entries = new sample_fmt_entry[]
            {
            new sample_fmt_entry { sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_U8, fmt_be = "u8", fmt_le = "u8" },
            new sample_fmt_entry { sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16, fmt_be = "s16be", fmt_le = "s16le" },
            new sample_fmt_entry { sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S32, fmt_be = "s32be", fmt_le = "s32le" },
            new sample_fmt_entry { sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT, fmt_be = "f32be", fmt_le = "f32le" },
            new sample_fmt_entry { sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_DBL, fmt_be = "f64be", fmt_le = "f64le" },
            };
        }

    }
}