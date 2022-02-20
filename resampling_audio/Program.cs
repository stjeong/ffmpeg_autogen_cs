using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;

namespace resampling_audio
{
    // https://ffmpeg.org/doxygen/trunk/resampling_audio_8c-example.html
    internal unsafe class Program
    {
        static int Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();
#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
            Console.WriteLine($"LIBAVFORMAT Version: {ffmpeg.LIBAVFORMAT_VERSION_MAJOR}.{ffmpeg.LIBAVFORMAT_VERSION_MINOR}");
            Console.WriteLine();
#endif
            long src_ch_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
            long dst_ch_layout = ffmpeg.AV_CH_LAYOUT_SURROUND;

            int src_rate = 48000, dst_rate = 44100;

            byte** src_data = null;
            byte** dst_data = null;
            int src_nb_channels = 0, dst_nb_channels = 0;
            int src_linesize, dst_linesize;
            int src_nb_samples = 1024, dst_nb_samples, max_dst_nb_samples;
            AVSampleFormat src_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_DBL, dst_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

            int dst_bufsize;
            string fmt = "";
            SwrContext* swr_ctx;
            double t;
            int ret;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string dst_filename = Path.Combine(dirPath, "test.data");

            using FileStream dst_file = File.OpenWrite(dst_filename);

            swr_ctx = ffmpeg.swr_alloc();
            if (swr_ctx == null)
            {
                Console.WriteLine("Could not allocate resampler context");
                ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto end;
            }

            ffmpeg.av_opt_set_int(swr_ctx, "in_channel_layout", src_ch_layout, 0);
            ffmpeg.av_opt_set_int(swr_ctx, "in_sample_rate", src_rate, 0);
            ffmpeg.av_opt_set_sample_fmt(swr_ctx, "in_sample_fmt", src_sample_fmt, 0);

            ffmpeg.av_opt_set_int(swr_ctx, "out_channel_layout", dst_ch_layout, 0);
            ffmpeg.av_opt_set_int(swr_ctx, "out_sample_rate", dst_rate, 0);
            ffmpeg.av_opt_set_sample_fmt(swr_ctx, "out_sample_fmt", dst_sample_fmt, 0);

            if ((ret = ffmpeg.swr_init(swr_ctx)) < 0)
            {
                Console.WriteLine("Failed to initialize the resampling context");
                goto end;
            }

            src_nb_channels = ffmpeg.av_get_channel_layout_nb_channels((ulong)src_ch_layout);
            ret = ffmpeg.av_samples_alloc_array_and_samples(&src_data, &src_linesize, src_nb_channels, src_nb_samples, src_sample_fmt, 0);
            if (ret < 0)
            {
                Console.WriteLine("Could not allocate source samples");
                goto end;
            }

            max_dst_nb_samples = dst_nb_samples = (int)ffmpeg.av_rescale_rnd(src_nb_samples, dst_rate, src_rate, AVRounding.AV_ROUND_UP);
            dst_nb_channels = ffmpeg.av_get_channel_layout_nb_channels((ulong)dst_ch_layout);
            ret = ffmpeg.av_samples_alloc_array_and_samples(&dst_data, &dst_linesize, dst_nb_channels, dst_nb_samples, dst_sample_fmt, 0);
            if (ret < 0)
            {
                Console.WriteLine("Could not allocate destination samples");
                goto end;
            }

            t = 0;

            do
            {
                fill_samples((double*)src_data[0], src_nb_samples, src_nb_channels, src_rate, &t);

                dst_nb_samples = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(swr_ctx, src_rate) + src_nb_samples, dst_rate, src_rate, AVRounding.AV_ROUND_UP);
                if (dst_nb_samples > max_dst_nb_samples)
                {
                    ffmpeg.av_freep(&dst_data[0]);
                    ret = ffmpeg.av_samples_alloc(dst_data, &dst_linesize, dst_nb_channels, dst_nb_samples, dst_sample_fmt, 1);
                    if (ret < 0)
                    {
                        break;
                    }

                    max_dst_nb_samples = dst_nb_samples;
                }

                ret = ffmpeg.swr_convert(swr_ctx, dst_data, dst_nb_samples, src_data, src_nb_samples);
                if (ret < 0)
                {
                    Console.WriteLine("Error while converting");
                    goto end;
                }

                dst_bufsize = ffmpeg.av_samples_get_buffer_size(&dst_linesize, dst_nb_channels, ret, dst_sample_fmt, 1);
                if (dst_bufsize < 0)
                {
                    Console.WriteLine("Could not get sample buffer size");
                    goto end;
                }

                Console.WriteLine($"t:{t} in:{src_nb_samples} out:{ret}");
                dst_file.Write(new ReadOnlySpan<byte>(dst_data[0], dst_bufsize));
            } while (t < 10);

            if ((ret = get_format_from_sample_fmt(out fmt, dst_sample_fmt)) < 0)
            {
                goto end;
            }

            Console.WriteLine($"Resampling succeeded. Play the output file with the command:\n ffplay -autoexit -f {fmt} -channel_layout {dst_ch_layout} -channels {dst_nb_channels} -ar {dst_rate} {dst_filename}");

        end:
            if (src_data != null)
            {
                ffmpeg.av_freep(&src_data[0]);
            }

            ffmpeg.av_freep(&src_data);

            if (dst_data != null)
            {
                ffmpeg.av_freep(&dst_data[0]);
            }

            ffmpeg.av_freep(&dst_data);

            ffmpeg.swr_free(&swr_ctx);

            return 0;
        }

        static void fill_samples(double* dst, int nb_samples, int nb_channels, int sample_rate, double* t)
        {
            int i, j;
            double tincr = 1.0 / sample_rate;
            double* dstp = dst;
            const double c = 2 * Math.PI * 440.0;

            for (i = 0; i < nb_samples; i++)
            {
                *dstp = Math.Sin(c * *t);
                for (j = 1; j < nb_channels; j++)
                {
                    dstp[j] = dstp[0];
                }

                dstp += nb_channels;
                *t += tincr;
            }
        }

        static int get_format_from_sample_fmt(out string fmt, AVSampleFormat sample_fmt)
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
