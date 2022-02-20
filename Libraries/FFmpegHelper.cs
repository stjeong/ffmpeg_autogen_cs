using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FFmpeg.AutoGen.Example
{
    internal static class FFmpegHelper
    {
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
    }
}