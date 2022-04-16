using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace filtering_audio
{
    internal unsafe class Program
    {
        [Flags]
        public enum AV_BUFFERSRC_FLAG
        {
            NO_CHECK_FORMAT = 1,
            FLAG_PUSH = 4,
            FLAG_KEEP_REF = 8,
        }

        static void Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            AVFormatContext* fmt_ctx = null;
            AVCodecContext* dec_ctx = null;
            int audio_stream_index = -1;
            AVFilterGraph* filter_graph = null;
            string filter_descr = "aresample=8000,aformat=sample_fmts=s16:channel_layouts=mono";
            AVFilterContext* buffersrc_ctx = null;
            AVFilterContext* buffersink_ctx = null;

            int ret;
            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            AVFrame* filter_frame = ffmpeg.av_frame_alloc();

            if (packet == null || frame == null || filter_frame == null)
            {
                Console.WriteLine("Could not allocate frame or packet");
                return;
            }

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string src_filename = Path.Combine(dirPath, "..", "..", "..", "..", "Samples", "sample-12s.mp3");

            do
            {
                if ((ret = open_input_file(src_filename, &fmt_ctx, out audio_stream_index, &dec_ctx)) < 0)
                {
                    Console.WriteLine("Could not allocate frame or packet");
                    break;
                }

                if ((ret = init_filters(filter_descr, fmt_ctx, audio_stream_index, &filter_graph, dec_ctx, &buffersrc_ctx, &buffersink_ctx)) < 0)
                {
                    break;
                }

                while (true)
                {
                    if ((ret = ffmpeg.av_read_frame(fmt_ctx, packet)) < 0)
                    {
                        break;
                    }

                    if (packet->stream_index == audio_stream_index)
                    {
                        ret = ffmpeg.avcodec_send_packet(dec_ctx, packet);
                        if (ret < 0)
                        {
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while sending a packet to the decoder\n");
                            break;
                        }

                        while (ret >= 0)
                        {
                            ret = ffmpeg.avcodec_receive_frame(dec_ctx, frame);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            {
                                break;
                            }
                            else if (ret < 0)
                            {
                                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while receiving a frame from the decoder\n");
                                goto End_Of_Process;
                            }

                            if (ret >= 0)
                            {
                                if (ffmpeg.av_buffersrc_add_frame_flags(buffersrc_ctx, frame, (int)AV_BUFFERSRC_FLAG.FLAG_KEEP_REF) < 0)
                                {
                                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while feeding the audio filtergraph\n");
                                    break;
                                }

                                while (true)
                                {
                                    ret = ffmpeg.av_buffersink_get_frame(buffersink_ctx, filter_frame);
                                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                                    {
                                        break;
                                    }

                                    if (ret < 0)
                                    {
                                        goto End_Of_Process;
                                    }

                                    print_frame(filter_frame);
                                    ffmpeg.av_frame_unref(filter_frame);
                                }

                                ffmpeg.av_frame_unref(frame);
                            }
                        }
                    }

                    ffmpeg.av_packet_unref(packet);
                }

            } while (false);

        End_Of_Process:

            if (filter_graph != null)
            {
                ffmpeg.avfilter_graph_free(&filter_graph);
            }

            if (dec_ctx != null)
            {
                ffmpeg.avcodec_free_context(&dec_ctx);
            }

            if (fmt_ctx != null)
            {
                ffmpeg.avformat_close_input(&fmt_ctx);
            }

            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (filter_frame != null)
            {
                ffmpeg.av_frame_free(&filter_frame);
            }
        }

        private static unsafe void print_frame(AVFrame* frame)
        {
            int n = frame->nb_samples * ffmpeg.av_get_channel_layout_nb_channels(frame->channel_layout);
            ushort* p = (ushort*)frame->data[0];
            ushort* p_end = p + n;

            while (p < p_end)
            {
                Console.Write(*p & 0xFF);
                Console.Write((*p >> 8) & 0xFF);
                p++;
            }
        }

        private static unsafe int init_filters(string filters_descr, AVFormatContext* fmt_ctx, int audio_stream_index
            , AVFilterGraph** filter_graph, AVCodecContext* dec_ctx, AVFilterContext** buffersrc_ctx, AVFilterContext** buffersink_ctx)
        {
            string? args = null;
            int ret = 0;
            AVFilter* aBufferSrc = ffmpeg.avfilter_get_by_name("abuffer");
            AVFilter* aBufferSink = ffmpeg.avfilter_get_by_name("abuffersink");
            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();

            AVSampleFormat[] out_sample_fmts = new AVSampleFormat[] { AVSampleFormat.AV_SAMPLE_FMT_S16, AVSampleFormat.AV_SAMPLE_FMT_NONE };
            long[] out_channel_layouts = { ffmpeg.AV_CH_LAYOUT_MONO, -1 };
            int[] out_sample_rates = { 8000, -1 };
            AVFilterLink* outlink = null;
            AVRational time_base = fmt_ctx->streams[audio_stream_index]->time_base;

            do
            {
                *filter_graph = ffmpeg.avfilter_graph_alloc();
                if (outputs == null || inputs == null || *filter_graph == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    break;
                }

                if (dec_ctx->channel_layout == 0)
                {
                    dec_ctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(dec_ctx->channels);
                }

                args = $"time_base={time_base.num}/{time_base.den}:sample_rate={dec_ctx->sample_rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name(dec_ctx->sample_fmt)}:channel_layout=0x{dec_ctx->channel_layout.ToString("x")}";
                ret = ffmpeg.avfilter_graph_create_filter(buffersrc_ctx, aBufferSrc, "in", args, null, *filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create audio buffer source\n");
                    break;
                }

                ret = ffmpeg.avfilter_graph_create_filter(buffersink_ctx, aBufferSink, "out", null, null, *filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create audio buffer sink\n");
                    break;
                }

                ret = av_opt_set_int_list(*buffersink_ctx, "sample_fmts", out_sample_fmts, -1, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output sample format\n");
                    break;
                }

                ret = av_opt_set_int_list(*buffersink_ctx, "channel_layouts", out_channel_layouts, -1, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output channel layout\n");
                    break;
                }

                ret = av_opt_set_int_list(*buffersink_ctx, "sample_rates", out_sample_rates, -1, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output sample rate\n");
                    break;
                }

                outputs->name = ffmpeg.av_strdup("in");
                outputs->filter_ctx = *buffersrc_ctx;
                outputs->pad_idx = 0;
                outputs->next = null;

                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = *buffersink_ctx;
                inputs->pad_idx = 0;
                inputs->next = null;

                if ((ret = ffmpeg.avfilter_graph_parse_ptr(*filter_graph, filters_descr, &inputs, &outputs, null)) < 0)
                {
                    break;
                }

                if ((ret = ffmpeg.avfilter_graph_config(*filter_graph, null)) < 0)
                {
                    break;
                }

                outlink = (*buffersink_ctx)->inputs[0];

#pragma warning disable CA2014 // Do not use stackalloc in loops
                byte* buffer = stackalloc byte[512];
#pragma warning restore CA2014 // Do not use stackalloc in loops
                ffmpeg.av_get_channel_layout_string(buffer, 512, -1, outlink->channel_layout);
                string channel_layout_string = new string((sbyte*)buffer, 0, getStringSize(buffer, 512), System.Text.Encoding.ASCII);
                ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, $"Output: srate: {outlink->sample_rate}Hz fmt: {ffmpeg.av_get_sample_fmt_name((AVSampleFormat)outlink->format) ?? "?"} chlayout: {channel_layout_string}");

            } while (false);

            if (inputs != null)
            {
                ffmpeg.avfilter_inout_free(&inputs);
            }

            if (outputs != null)
            {
                ffmpeg.avfilter_inout_free(&outputs);
            }

            return ret;
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

        private unsafe static int av_opt_set_int_list(AVFilterContext* obj, string name, long[] val, int term, int flags)
        {
            fixed (void* ptr = &val[0])
            {
                return av_opt_set_int_list(obj, 8, name, ptr, term, flags);
            }
        }

        private unsafe static int av_opt_set_int_list(AVFilterContext* obj, string name, int[] val, int term, int flags)
        {
            fixed (void* ptr = &val[0])
            {
                return av_opt_set_int_list(obj, 4, name, ptr, term, flags);
            }
        }

        private unsafe static int av_opt_set_int_list(AVFilterContext* obj, string name, AVSampleFormat[] val, int term, int flags)
        {
            fixed (void* ptr = &val[0])
            {
                return av_opt_set_int_list(obj, 4, name, ptr, term, flags);
            }
        }

        private unsafe static int av_opt_set_int_list(AVFilterContext* obj, uint elementSize, string name, void* ptr, int term, int flags)
        {
            uint size = ffmpeg.av_int_list_length_for_size(elementSize, ptr, (ulong)term);
            if (size > (int.MaxValue / elementSize))
            {
                return ffmpeg.AVERROR(ffmpeg.EINVAL);
            }
            else
            {
                return ffmpeg.av_opt_set_bin(obj, name, (byte*)ptr, (int)(size * elementSize), flags);
            }
        }

        private unsafe static int open_input_file(string filePath, AVFormatContext** fmt_ctx, out int audio_stream_index
            , AVCodecContext** dec_ctx)
        {
            AVCodec* decoder = null;
            int ret;

            audio_stream_index = -1;

            if ((ret = ffmpeg.avformat_open_input(fmt_ctx, filePath, null, null)) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot open input file\n");
                return ret;
            }

            if ((ret = ffmpeg.avformat_find_stream_info(*fmt_ctx, null)) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot find stream information\n");
                return ret;
            }

            ret = ffmpeg.av_find_best_stream(*fmt_ctx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (ret < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot find an audio stream in the input file");
                return ret;
            }

            audio_stream_index = ret;

            *dec_ctx = ffmpeg.avcodec_alloc_context3(decoder);
            if (*dec_ctx == null)
            {
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            ffmpeg.avcodec_parameters_to_context(*dec_ctx, (*fmt_ctx)->streams[audio_stream_index]->codecpar);

            if ((ret = ffmpeg.avcodec_open2(*dec_ctx, decoder, null)) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot open audio decoder");
                return ret;
            }

            return 0;
        }
    }
}