using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace scaling_video
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
#endif
            Console.WriteLine();

            byte_ptrArray4 src_data = new byte_ptrArray4();
            byte_ptrArray4 dst_data = new byte_ptrArray4();

            int_array4 src_linesize = new int_array4();
            int_array4 dst_linesize = new int_array4();

            int src_w = 320, src_h = 240, dst_w, dst_h;
            AVPixelFormat src_pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P, dst_pix_fmt = AVPixelFormat.AV_PIX_FMT_RGB24;
            string? dst_size = null;

            int dst_bufsize = 0;
            SwsContext* sws_ctx = null;

            int i, ret;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string outputFilePath = Path.Combine(dirPath, "test.mp4");

            dst_size = "cif"; // 352x288
            // dst_size = "hd1080"; // 1920x1080

            if (VideoSizeAbbr.av_parse_video_size(&dst_w, &dst_h, dst_size) < 0)
            {
                Console.WriteLine($"Invalid size {dst_size}, must be in the form WxH or a valid abbreviation");
                return 1;
            }

            using FileStream fs = File.Create(outputFilePath);

            do
            {
                sws_ctx = ffmpeg.sws_getContext(src_w, src_h, src_pix_fmt, dst_w, dst_h, dst_pix_fmt, ffmpeg.SWS_BILINEAR, null, null, null);
                if (sws_ctx == null)
                {
                    Console.WriteLine($"Impossible to create scale context for the conversion fmt:{ffmpeg.av_get_pix_fmt_name(src_pix_fmt)} s:{src_w}x{src_h} -> fmt:{ffmpeg.av_get_pix_fmt_name(dst_pix_fmt)} s:{dst_w}x{dst_h}");
                    ret = ffmpeg.AVERROR(ffmpeg.EINVAL);
                    break;
                }

                if ((ret = ffmpeg.av_image_alloc(ref src_data, ref src_linesize, src_w, src_h, src_pix_fmt, 16)) < 0)
                {
                    Console.WriteLine("Could not allocate source image");
                    break;
                }

                if ((ret = ffmpeg.av_image_alloc(ref dst_data, ref dst_linesize, dst_w, dst_h, dst_pix_fmt, 1)) < 0)
                {
                    Console.WriteLine("Could not allocate destination image");
                    break;
                }

                dst_bufsize = ret;

                for (i = 0; i < 100; i++)
                {
                    fill_yuv_image(src_data, src_linesize, src_w, src_h, i);

                    ffmpeg.sws_scale(sws_ctx, src_data, src_linesize, 0, src_h, dst_data, dst_linesize);

                    ReadOnlySpan<byte> buffer = new ReadOnlySpan<byte>(dst_data[0], dst_bufsize);
                    fs.Write(buffer);
                }

                Console.WriteLine("Scaling succeeded. Play the output file with the command:");
                Console.WriteLine($"ffplay -f rawvideo -pix_fmt {ffmpeg.av_get_pix_fmt_name(dst_pix_fmt)} -video_size {dst_w}x{dst_h} {outputFilePath}");

            } while (false);

            if (src_data[0] != null)
            {
                ffmpeg.av_freep(&src_data);
            }

            if (dst_data[0] != null)
            {
                ffmpeg.av_freep(&dst_data);
            }

            if (sws_ctx != null)
            {
                ffmpeg.sws_freeContext(sws_ctx);
            }

            return 0;
        }

        static unsafe void fill_yuv_image(byte*[] data, int[] linesize, int width, int height, int frame_index)
        {
            int x, y;

            for (y = 0; y < height; y++)
            {
                for (x = 0; x < width; x++)
                {
                    data[0][y * linesize[0] + x] = (byte)(x + y + frame_index * 3);
                }
            }

            for (y = 0; y < height / 2; y++)
            {
                for (x = 0; x < width / 2; x++)
                {
                    data[1][y * linesize[1] + x] = (byte)(128 + y + frame_index * 2);
                    data[2][y * linesize[2] + x] = (byte)(64 + x + frame_index * 5);
                }
            }
        }

        public class VideoSizeAbbr
        {
            string _abbr;
            public string Abbr => _abbr;

            int _width;
            public int Width => _width;

            int _height;
            public int Height => _height;

            public VideoSizeAbbr(string abbr, int width, int height)
            {
                _abbr = abbr;
                _width = width;
                _height = height;
            }

            public static VideoSizeAbbr[] Video_size_abbrs => _video_size_abbrs;

            static VideoSizeAbbr[] _video_size_abbrs = new VideoSizeAbbr[] {
                 new ("ntsc",      720, 480),
                 new ("pal",       720, 576 ),
                 new ("qntsc",     352, 240 ), /* VCD compliant NTSC */
                 new ("qpal",      352, 288 ), /* VCD compliant PAL */
                 new ("sntsc",     640, 480 ), /* square pixel NTSC */
                 new ("spal",      768, 576 ), /* square pixel PAL */
                 new ("film",      352, 240 ),
                 new ("ntsc-film", 352, 240 ),
                 new ("sqcif",     128,  96 ),
                 new ("qcif",      176, 144 ),
                 new ("cif",       352, 288 ),
                 new ("4cif",      704, 576 ),
                 new ("16cif",    1408,1152 ),
                 new ("qqvga",     160, 120 ),
                 new ("qvga",      320, 240 ),
                 new ("vga",       640, 480 ),
                 new ("svga",      800, 600 ),
                 new ("xga",      1024, 768 ),
                 new ("uxga",     1600,1200 ),
                 new ("qxga",     2048,1536 ),
                 new ("sxga",     1280,1024 ),
                 new ("qsxga",    2560,2048 ),
                 new ("hsxga",    5120,4096 ),
                 new ("wvga",      852, 480 ),
                 new ("wxga",     1366, 768 ),
                 new ("wsxga",    1600,1024 ),
                 new ("wuxga",    1920,1200 ),
                 new ("woxga",    2560,1600 ),
                 new ("wqhd",     2560,1440 ),
                 new ("wqsxga",   3200,2048 ),
                 new ("wquxga",   3840,2400 ),
                 new ("whsxga",   6400,4096 ),
                 new ("whuxga",   7680,4800 ),
                 new ("cga",       320, 200 ),
                 new ("ega",       640, 350 ),
                 new ("hd480",     852, 480 ),
                 new ("hd720",    1280, 720 ),
                 new ("hd1080",   1920,1080 ),
                 new ("quadhd",   2560,1440 ),
                 new ("2k",       2048,1080 ), /* Digital Cinema System Specification */
                 new ("2kdci",    2048,1080 ),
                 new ("2kflat",   1998,1080 ),
                 new ("2kscope",  2048, 858 ),
                 new ("4k",       4096,2160 ), /* Digital Cinema System Specification */
                 new ("4kdci",    4096,2160 ),
                 new ("4kflat",   3996,2160 ),
                 new ("4kscope",  4096,1716 ),
                 new ("nhd",       640,360  ),
                 new ("hqvga",     240,160  ),
                 new ("wqvga",     400,240  ),
                 new ("fwqvga",    432,240  ),
                 new ("hvga",      480,320  ),
                 new ("qhd",       960,540  ),
                 new ("uhd2160",  3840,2160 ),
                 new ("uhd4320",  7680,4320 ),
             };

            public static unsafe int av_parse_video_size(int* width_ptr, int* height_ptr, string str)
            {
                int i;
                int n = VideoSizeAbbr.Video_size_abbrs.Length;
                int width = 0;
                int height = 0;

                for (i = 0; i < n; i++)
                {
                    if (VideoSizeAbbr.Video_size_abbrs[i].Abbr == str)
                    {
                        width = VideoSizeAbbr.Video_size_abbrs[i].Width;
                        height = VideoSizeAbbr.Video_size_abbrs[i].Height;
                        break;
                    }
                }

                if (i == n)
                {
                    string[] size = str.Split('x');
                    int.TryParse(size[0], out width);
                    int.TryParse(size[1], out height);
                }

                if (width <= 0 || height <= 0)
                {
                    return ffmpeg.AVERROR(ffmpeg.EINVAL);
                }

                *width_ptr = width;
                *height_ptr = height;
                return 0;
            }
        }

    }
}