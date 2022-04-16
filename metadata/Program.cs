using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace metadata
{
    internal unsafe class Program
    {
        static void Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            Console.WriteLine($"LIBAVFORMAT Version: {ffmpeg.LIBAVFORMAT_VERSION_MAJOR}.{ffmpeg.LIBAVFORMAT_VERSION_MINOR}");

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string src_filename = Path.Combine(dirPath, "..", "..", "..", "..", "Samples", "sample-10s.mp4");
            show_metadata(src_filename);
        }

        private static void show_metadata(string filePath)
        {
            AVFormatContext* fmt_ctx = null;

            do
            {
                int ret = ffmpeg.avformat_open_input(&fmt_ctx, filePath, null, null);
                if (ret != 0)
                {
                    break;
                }

                ret = ffmpeg.avformat_find_stream_info(fmt_ctx, null);
                if (ret < 0)
                {
                    Console.WriteLine("Cannot find stream information");
                    break;
                }

                AVDictionaryEntry* tag = null;

                while ((tag = ffmpeg.av_dict_get(fmt_ctx->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
                {
                    string? key = Marshal.PtrToStringAnsi(new IntPtr(tag->key));
                    string? value = Marshal.PtrToStringAnsi(new IntPtr(tag->value));

                    Console.WriteLine($"{key} = {value}");
                }

            } while (false);

            if (fmt_ctx != null)
            {
                ffmpeg.avformat_close_input(&fmt_ctx);
            }
        }
    }
}