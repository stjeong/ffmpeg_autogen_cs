using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace filter_audio
{
    internal unsafe class Program
    {
        const int INPUT_SAMPLERATE = 48000;
        const AVSampleFormat INPUT_FORMAT = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        const int INPUT_CHANNEL_LAYOUT = ffmpeg.AV_CH_LAYOUT_5POINT0;
        const float VOLUME_VAL = 0.90f;
        const int FRAME_SIZE = 1024;

        static int Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            AVFilterGraph* graph;
            AVFilterContext* src;
            AVFilterContext* sink;
            AVFrame* frame;
            byte* errstr = stackalloc byte[1024];
            float duration;
            int err, nb_frames, i;

            duration = 20.0f;

            nb_frames = (int)(duration * INPUT_SAMPLERATE / FRAME_SIZE);
            if (nb_frames <= 0)
            {
                Console.WriteLine($"Invalid duration: {duration}");
                return 1;
            }

            frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                Console.WriteLine("Error allocating the frame");
                return 1;
            }

            do
            {
                err = init_filter_graph(&graph, &src, &sink);
                if (err < 0)
                {
                    Console.WriteLine("Unable to init filter graph:");
                    break;
                }

                for (i = 0; i < nb_frames; i++)
                {
                    err = get_input(frame, i);
                    if (err < 0)
                    {
                        Console.WriteLine("Error generating input frame:");
                        break;
                    }

                    err = ffmpeg.av_buffersrc_add_frame(src, frame);
                    if (err < 0)
                    {
                        ffmpeg.av_frame_unref(frame);
                        Console.WriteLine("Error submitting the frame to the filtergraph:");
                        break;
                    }

                    MD5 md5 = MD5.Create();

                    while ((err = ffmpeg.av_buffersink_get_frame(sink, frame)) >= 0)
                    {
                        err = process_output(md5, frame);
                        if (err < 0)
                        {
                            Console.WriteLine("Error processing the filtered frame:");
                            break;
                        }

                        ffmpeg.av_frame_unref(frame);
                    }

                    if (err == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        continue;
                    }
                    else if (err == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }
                    else if (err < 0)
                    {
                        Console.WriteLine("Error filtering the data:");
                        break;
                    }
                }

            } while (false);

            if (graph != null)
            {
                ffmpeg.avfilter_graph_free(&graph);
            }

            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (err < 0 && err != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                ffmpeg.av_strerror(err, errstr, 1024);

                string result = Marshal.PtrToStringAnsi(new IntPtr(errstr), getStringSize(errstr, 1024));
                Console.WriteLine(result);
                return 1;
            }

            return 0;
        }


        private static unsafe int init_filter_graph(AVFilterGraph** graph, AVFilterContext** src, AVFilterContext** sink)
        {
            AVFilterGraph* filter_graph;
            AVFilterContext* abuffer_ctx = null;
            AVFilter* abuffer = null;
            AVFilterContext* volume_ctx = null;
            AVFilter* volume = null;
            AVFilterContext* aformat_ctx = null;
            AVFilter* aformat = null;
            AVFilterContext* abuffersink_ctx = null;
            AVFilter* abuffersink = null;

            AVDictionary* options_dict = null;
            byte* ch_layout = stackalloc byte[64];

            int err = 0;

            filter_graph = ffmpeg.avfilter_graph_alloc();
            if (filter_graph == null)
            {
                Console.WriteLine("Unable to create filter graph.");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }


            abuffer = ffmpeg.avfilter_get_by_name("abuffer");
            if (abuffer == null)
            {
                Console.WriteLine("Could not find the abuffer filter.");
                return ffmpeg.AVERROR_FILTER_NOT_FOUND;
            }

            abuffer_ctx = ffmpeg.avfilter_graph_alloc_filter(filter_graph, abuffer, "src");
            if (abuffer_ctx == null)
            {
                Console.WriteLine("Could not allocate the abuffer instance.");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            ffmpeg.av_get_channel_layout_string(ch_layout, 64, 0, INPUT_CHANNEL_LAYOUT);

            string ch_layout_value = Marshal.PtrToStringAnsi(new IntPtr(ch_layout), getStringSize(ch_layout, 64));
            err = ffmpeg.av_opt_set(abuffer_ctx, "channel_layout", ch_layout_value, ffmpeg.AV_OPT_SEARCH_CHILDREN);
            err = ffmpeg.av_opt_set(abuffer_ctx, "sample_fmt", ffmpeg.av_get_sample_fmt_name(INPUT_FORMAT), ffmpeg.AV_OPT_SEARCH_CHILDREN);
            err = ffmpeg.av_opt_set_q(abuffer_ctx, "time_base", new AVRational { num = 1, den = INPUT_SAMPLERATE }, ffmpeg.AV_OPT_SEARCH_CHILDREN);
            err = ffmpeg.av_opt_set_int(abuffer_ctx, "sample_rate", INPUT_SAMPLERATE, ffmpeg.AV_OPT_SEARCH_CHILDREN);

            err = ffmpeg.avfilter_init_str(abuffer_ctx, null);
            if (err < 0)
            {
                Console.WriteLine("Could not initialze the abuffer filter.");
                return err;
            }

            volume = ffmpeg.avfilter_get_by_name("volume");
            if (volume == null)
            {
                Console.WriteLine("Could not find the volume filter.");
                return ffmpeg.AVERROR_FILTER_NOT_FOUND;
            }

            volume_ctx = ffmpeg.avfilter_graph_alloc_filter(filter_graph, volume, "volume");
            if (volume_ctx == null)
            {
                Console.WriteLine("Could not allocate the volume instance.");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            ffmpeg.av_dict_set(&options_dict, "volume", $"{VOLUME_VAL:F}", 0);
            err = ffmpeg.avfilter_init_dict(volume_ctx, &options_dict);
            ffmpeg.av_dict_free(&options_dict);
            if (err < 0)
            {
                Console.WriteLine("Could not initialze the volume filter.");
                return err;
            }

            aformat = ffmpeg.avfilter_get_by_name("aformat");
            if (aformat == null)
            {
                Console.WriteLine("Could not find the aformat filter.");
                return ffmpeg.AVERROR_FILTER_NOT_FOUND;
            }

            aformat_ctx = ffmpeg.avfilter_graph_alloc_filter(filter_graph, aformat, "aformat");
            if (aformat_ctx == null)
            {
                Console.WriteLine("Could not allocate the aformat instance.");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            string options_str = $"sample_fmts={ffmpeg.av_get_sample_fmt_name(AVSampleFormat.AV_SAMPLE_FMT_S16)}:sample_rates=44100:channel_layouts=0x{ffmpeg.AV_CH_LAYOUT_STEREO:x}";
            err = ffmpeg.avfilter_init_str(aformat_ctx, options_str);
            if (err < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Could not initialize the aformat filter.");
                return err;
            }

            abuffersink = ffmpeg.avfilter_get_by_name("abuffersink");
            if (abuffersink == null)
            {
                Console.WriteLine("Could not allocate the abuffersink instance.");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            abuffersink_ctx = ffmpeg.avfilter_graph_alloc_filter(filter_graph, abuffersink, "sink");
            if (abuffersink_ctx == null)
            {
                Console.WriteLine("Could not initialize the abuffersink instance.");
                return err;
            }

            err = ffmpeg.avfilter_init_str(abuffersink_ctx, null);
            if (err < 0)
            {
                Console.WriteLine("Could not initialize the abuffersink instance.");
                return err;
            }

            err = ffmpeg.avfilter_link(abuffer_ctx, 0, volume_ctx, 0);
            if (err >= 0)
            {
                err = ffmpeg.avfilter_link(volume_ctx, 0, aformat_ctx, 0);
            }

            if (err >= 0)
            {
                err = ffmpeg.avfilter_link(aformat_ctx, 0, abuffersink_ctx, 0);
            }

            if (err < 0)
            {
                Console.WriteLine("Error connecting filters.");
                return err;
            }

            err = ffmpeg.avfilter_graph_config(filter_graph, null);
            if (err < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error configuring the filter graph");
                return err;
            }

            *graph = filter_graph;
            *src = abuffer_ctx;
            *sink = abuffersink_ctx;

            return 0;
        }

        private static int getStringSize(byte* buffer, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (buffer[i] == 0)
                {
                    return i;
                }
            }

            return 0;
        }

        static unsafe int process_output(MD5 md5, AVFrame* frame)
        {
            int planar = ffmpeg.av_sample_fmt_is_planar((AVSampleFormat)frame->format);
            int channels = ffmpeg.av_get_channel_layout_nb_channels(frame->channel_layout);
            int planes = planar != 0 ? channels : 1;
            int bps = ffmpeg.av_get_bytes_per_sample((AVSampleFormat)frame->format);
            int plane_size = bps * frame->nb_samples * (planar != 0 ? 1 : channels);
            int i;

            for (i = 0; i < planes; i++)
            {
                Span<byte> buffer = new Span<byte>(frame->extended_data[i], plane_size);
                byte[] computed = md5.ComputeHash(buffer.ToArray()); // ToArray: copy overhead

                Console.WriteLine(BitConverter.ToString(computed).Replace("-", ""));
            }

            return 0;
        }

        static unsafe int get_input(AVFrame* frame, int frame_num)
        {
            int err, i, j;

            frame->sample_rate = INPUT_SAMPLERATE;
            frame->format = (int)INPUT_FORMAT;
            frame->channel_layout = INPUT_CHANNEL_LAYOUT;
            frame->nb_samples = FRAME_SIZE;
            frame->pts = frame_num * FRAME_SIZE;

            err = ffmpeg.av_frame_get_buffer(frame, 0);
            if (err < 0)
            {
                return err;
            }

            for (i = 0; i < 5; i++)
            {
                float* data = (float*)frame->extended_data[i];

                for (j = 0; j < frame->nb_samples; j++)
                {
                    data[j] = (float)Math.Sin(2 * Math.PI * (frame_num + j) * (i + 1) / FRAME_SIZE);
                }
            }

            return 0;
        }
    }
}