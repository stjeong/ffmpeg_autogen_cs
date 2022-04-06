using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using FFmpeg.OSDepends;
using System;
using System.IO;

namespace avio_reading
{
    internal unsafe class Program
    {
        static unsafe int Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");

            Console.WriteLine();
#endif
            AVFormatContext* fmt_ctx = null;
            AVIOContext* avio_ctx = null;

            byte* buffer = null;
            byte* avio_ctx_buffer = null;
            ulong buffer_size;
            ulong avio_ctx_buffer_size = 4096;
            
            int ret = 0;
            buffer_data bd = new buffer_data();

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string input_filename = Path.Combine(dirPath, "..", "..", "..", "Samples", "sample-10s.mp4");

            ret = ffmpeg.av_file_map(input_filename, &buffer, &buffer_size, 0, null);
            if (ret < 0)
            {
                return ret;
            }

            bd.ptr = buffer;
            bd.size = (int)buffer_size;

            do
            {
                fmt_ctx = ffmpeg.avformat_alloc_context();
                if (fmt_ctx == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    break;
                }

                avio_ctx_buffer = (byte*)ffmpeg.av_malloc(avio_ctx_buffer_size);
                if (avio_ctx_buffer == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    break;
                }

                avio_ctx = ffmpeg.avio_alloc_context(avio_ctx_buffer, (int)avio_ctx_buffer_size, 0, &bd, 
                    (avio_alloc_context_read_packet_func)read_packet, null, null);
                if (avio_ctx == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    break;
                }

                fmt_ctx->pb = avio_ctx;

                ret = ffmpeg.avformat_open_input(&fmt_ctx, null, null, null);
                if (ret < 0)
                {
                    Console.WriteLine("Could not open input");
                    break;
                }

                ret = ffmpeg.avformat_find_stream_info(fmt_ctx, null);
                if (ret < 0)
                {
                    Console.WriteLine("Could not find stream information");
                    break;
                }

                ffmpeg.av_dump_format(fmt_ctx, 0, input_filename, 0);

            } while (false);

            if (fmt_ctx != null)
            {
                ffmpeg.avformat_close_input(&fmt_ctx);
            }

            if (avio_ctx != null)
            {
                ffmpeg.av_freep(&avio_ctx->buffer);
                ffmpeg.avio_context_free(&avio_ctx);
            }

            ffmpeg.av_file_unmap(buffer, buffer_size);

            return ret;
        }

        static unsafe int read_packet(void *opaque, byte* buf, int buf_size)
        {
            buffer_data* bd = (buffer_data*)opaque;
            buf_size = (int)Math.Min(buf_size, bd->size);

            if (buf_size == 0)
            {
                return ffmpeg.AVERROR_EOF;
            }

            Console.WriteLine($"ptr:{new IntPtr(bd->ptr):x} size:{bd->size}");

            NativeMethods.MoveMemory(buf, bd->ptr, buf_size);
            bd->ptr += buf_size;
            bd->size -= buf_size;

            return buf_size;
        }
    }

    public unsafe struct buffer_data
    {
        public byte* ptr;
        public int size;
    }
}
