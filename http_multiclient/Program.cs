using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace http_multiclient
{
    internal unsafe class Program
    {
        static void process_client(AVIOContext* client, string in_uri)
        {
            AVIOContext* input = null;
            byte* buf = stackalloc byte[1024];
            Span<byte> buffer = new(buf, 1024);
            int ret, n, reply_code;
            byte* resource = null;
            string? text = null;

            while ((ret = ffmpeg.avio_handshake(client)) > 0)
            {
                ffmpeg.av_opt_get(client, "resource", ffmpeg.AV_OPT_SEARCH_CHILDREN, &resource);
                text = Marshal.PtrToStringAnsi(new IntPtr(resource));

                if (string.IsNullOrEmpty(text) == false)
                {
                    break;
                }

                ffmpeg.av_freep(&resource);
            }

            if (ret < 0)
            {
                goto end;
            }

            ffmpeg.av_log(client, ffmpeg.AV_LOG_TRACE, $"resource=0x{new IntPtr(resource):x}\n");

            if (text != null && text[0] == '/' /* && text.Substring(1) == in_uri */)
            {
                reply_code = 200;
            }
            else
            {
                reply_code = ffmpeg.AVERROR_HTTP_NOT_FOUND;
            }

            if ((ret = ffmpeg.av_opt_set_int(client, "reply_code", reply_code, ffmpeg.AV_OPT_SEARCH_CHILDREN)) < 0)
            {
                ffmpeg.av_log(client, ffmpeg.AV_LOG_ERROR, $"Failed to set reply_code: {FFmpegHelper.av_err2str(ret)}\n");
                goto end;
            }

            ffmpeg.av_log(client, ffmpeg.AV_LOG_TRACE, $"Set reply code to {reply_code}\n");

            while ((ret = ffmpeg.avio_handshake(client)) > 0) ;

            if (ret < 0)
            {
                goto end;
            }

            Console.WriteLine("Handshake performed");

            if (reply_code != 200)
            {
                goto end;
            }

            Console.WriteLine("Opening input file");
            if ((ret = ffmpeg.avio_open2(&input, in_uri, ffmpeg.AVIO_FLAG_READ, null, null)) < 0)
            {
                ffmpeg.av_log(input, ffmpeg.AV_LOG_ERROR, $"Failed to open input: {in_uri}: {FFmpegHelper.av_err2str(ret)}\n");
                goto end;
            }

            for (; ; )
            {
                n = ffmpeg.avio_read(input, buf, buffer.Length);
                if (n < 0)
                {
                    if (n == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    ffmpeg.av_log(input, ffmpeg.AV_LOG_ERROR, $"Error reading from input: {FFmpegHelper.av_err2str(n)}\n");
                    break;
                }

                ffmpeg.avio_write(client, buf, n);
                ffmpeg.avio_flush(client);
            }

        end:
            Console.WriteLine("Flushing client");
            ffmpeg.avio_flush(client);
            Console.WriteLine("Closing clinet");
            ffmpeg.avio_close(client);
            Console.WriteLine("Closing input");
            ffmpeg.avio_close(input);
            ffmpeg.av_freep(&resource);
        }

        static unsafe int Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");

            Console.WriteLine();
#endif

            AVDictionary* options = null;
            AVIOContext* client = null;
            AVIOContext* server = null;
            string in_uri, out_uri;
            int ret;

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_TRACE);

            out_uri = "http://127.0.0.1:15384/";

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";

            // in container, encoded
            // in_uri = Path.Combine(dirPath, "..", "..", "..", "Samples", "sample-10s.mp4");
            // can not play! (do you know how to play?)

            // no container, encoded
            in_uri = Path.Combine(dirPath, "..", "..", "..", "Samples", "mpeg1video_q0.m1v");
            Console.WriteLine($"ffplay -autoexit -f mpegvideo {out_uri}");

            // no container, decoded(raw)
            // in_uri = Path.Combine(dirPath, "..", "..", "..", "hw_decode", "bin", "debug", "test.dat");
            // Console.WriteLine($"ffplay -autoexit -f rawvideo -pixel_format nv12 -video_size 1920x1080 {out_uri}");

            ffmpeg.avformat_network_init();

            if ((ret = ffmpeg.av_dict_set(&options, "listen", "2", 0)) < 0)
            {
                Console.WriteLine($"Failed to set listen mode for server: {FFmpegHelper.av_err2str(ret)}");
                return ret;
            }

            if ((ret = ffmpeg.avio_open2(&server, out_uri, ffmpeg.AVIO_FLAG_WRITE, null, &options)) < 0)
            {
                Console.WriteLine($"Failed to open server: {FFmpegHelper.av_err2str(ret)}");
                return ret;
            }

            Console.WriteLine("Entering main loop");

            for (; ;)
            {
                if ((ret = ffmpeg.avio_accept(server, &client)) < 0)
                {
                    goto end;
                }

                Console.WriteLine("Accepted client, forking process.");

                AVIOContext** pClient = &client;

                Thread t = new Thread((obj) =>
                {
                    Console.WriteLine("Client....");
                    process_client(*pClient, in_uri);
                });

                t.Start();
            }

        end:

            ffmpeg.avio_close(server);
            if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
            {
                Console.WriteLine($"Some errors occurred: {FFmpegHelper.av_err2str(ret)}");
                return 1;
            }

            return 0;
        }
    }

}
