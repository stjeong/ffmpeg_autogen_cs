using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using FFmpeg.OSDepends;
using System;
using System.IO;

namespace filtering_video
{
    internal unsafe class Program
    {
        const string filter_descr = "scale=78:24,transpose=cclock";
        /* other way:
           scale=78:24 [scl]; [scl] transpose=cclock // assumes "[in]" and "[out]" to be input output pads respectively
         */

        static AVFormatContext* fmt_ctx;
        static AVCodecContext* dec_ctx;
        static AVFilterContext* buffersink_ctx;
        static AVFilterContext* buffersrc_ctx;
        static AVFilterGraph* filter_graph;
        static int video_stream_index = -1;
        static long last_pts = ffmpeg.AV_NOPTS_VALUE;

        static unsafe int Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");

            Console.WriteLine();
#endif

            int ret;
            AVPacket* packet;
            AVFrame* frame;
            AVFrame* filt_frame;

            frame = ffmpeg.av_frame_alloc();
            filt_frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || filt_frame == null || packet == null)
            {
                Console.WriteLine("Could not allocate frame or packet");
                return 1;
            }

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";

            // https://file-examples.com/index.php/sample-video-files/sample-mp4-files/
            string inputfile = Path.Combine(dirPath, "..", "..", "..", "Samples", "file_example_MP4_1920_18MG.mp4");

            if ((ret = open_input_file(inputfile)) < 0)
            {
                goto end;
            }

            if ((ret = init_filters(filter_descr)) < 0)
            {
                goto end;
            }

            while (true)
            {
                if ((ret = ffmpeg.av_read_frame(fmt_ctx, packet)) < 0)
                {
                    break;
                }

                if (packet->stream_index == video_stream_index)
                {
                    ret = ffmpeg.avcodec_send_packet(dec_ctx, packet);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while sending a packet to the decoder");
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
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while receiving a frame from the decoder");
                            goto end;
                        }

                        frame->pts = frame->best_effort_timestamp;

                        if (ffmpeg.av_buffersrc_add_frame_flags(buffersrc_ctx, frame, (int)AV_BUFFERSRC_FLAG.KEEP_REF) < 0)
                        {
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while feeding the filtergraph");
                            break;
                        }

                        while (true)
                        {
                            ret = ffmpeg.av_buffersink_get_frame(buffersink_ctx, filt_frame);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            { 
                                break;
                            }

                            if (ret < 0)
                            {
                                goto end;
                            }

                            display_name(filt_frame, buffersink_ctx->inputs[0]->time_base);
                            ffmpeg.av_frame_unref(filt_frame);
                        }

                        ffmpeg.av_frame_unref(frame);
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }

        end:
            fixed (AVFilterGraph** pfilter = &filter_graph)
            {
                ffmpeg.avfilter_graph_free(pfilter);
            }

            fixed (AVCodecContext** pdec_ctx = &dec_ctx)
            {
                ffmpeg.avcodec_free_context(pdec_ctx);
            }

            fixed (AVFormatContext** pfmt_ctx = &fmt_ctx)
            {
                ffmpeg.avformat_close_input(pfmt_ctx);
            }

            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_frame_free(&filt_frame);
            ffmpeg.av_packet_free(&packet);

            if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
            {
                Console.WriteLine($"Error occurred: {FFmpegHelper.av_strerror(ret)}");
                return 1;
            }


            return 0;
        }

        static unsafe int open_input_file(string filename)
        {
            AVCodec* dec;
            int ret;

            fixed (AVFormatContext** pfmt_ctx = &fmt_ctx)
            {
                if ((ret = ffmpeg.avformat_open_input(pfmt_ctx, filename, null, null)) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot open input file");
                    return ret;
                }
            }

            if ((ret = ffmpeg.avformat_find_stream_info(fmt_ctx, null)) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot find stream information");
                return ret;
            }

            ret = ffmpeg.av_find_best_stream(fmt_ctx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &dec, 0);
            if (ret < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot find a video stream in the input file");
                return ret;
            }

            video_stream_index = ret;

            dec_ctx = ffmpeg.avcodec_alloc_context3(dec);
            if (dec_ctx == null)
            {
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            ffmpeg.avcodec_parameters_to_context(dec_ctx, fmt_ctx->streams[video_stream_index]->codecpar);

            if ((ret = ffmpeg.avcodec_open2(dec_ctx, dec, null)) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot open video decoder");
                return ret;
            }

            return ret;
        }

        static unsafe int init_filters(string filter_descr)
        {
            int ret = 0;
            AVFilter* buffersrc = ffmpeg.avfilter_get_by_name("buffer");
            AVFilter* buffersink = ffmpeg.avfilter_get_by_name("buffersink");
            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
            AVRational time_base = fmt_ctx->streams[video_stream_index]->time_base;
            Span<int> pix_fmts = stackalloc int[]
            {
                (int)AVPixelFormat.AV_PIX_FMT_GRAY8,
            };

            filter_graph = ffmpeg.avfilter_graph_alloc();
            if (outputs == null || inputs == null || filter_graph == null)
            {
                ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto end;
            }

            string args = $"video_size={dec_ctx->width}x{dec_ctx->height}:pix_fmt={(int)dec_ctx->pix_fmt}:"
                + $"time_base={time_base.num}/{time_base.den}:pixel_aspect={dec_ctx->sample_aspect_ratio.num}/{dec_ctx->sample_aspect_ratio.den}";

            fixed (AVFilterContext** pbuffersrc_ctx = &buffersrc_ctx)
            {
                ret = ffmpeg.avfilter_graph_create_filter(pbuffersrc_ctx, buffersrc, "in", args, null, filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create buffer source");
                    goto end;
                }
            }

            fixed (AVFilterContext** pbuffersink_ctx = &buffersink_ctx)
            {
                ret = ffmpeg.avfilter_graph_create_filter(pbuffersink_ctx, buffersink, "out", null, null, filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create buffer sink");
                    goto end;
                }
            }

            fixed (int* pfmts = pix_fmts)
            {
                ret = ffmpeg.av_opt_set_bin(buffersink_ctx, "pix_fmts", (byte*)pfmts, pix_fmts.Length * sizeof(int), ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output pixel format");
                    goto end;
                }
            }

            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = buffersrc_ctx;
            outputs->pad_idx = 0;
            outputs->next = null;

            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = buffersink_ctx;
            inputs->pad_idx = 0;
            inputs->next = null;

            if ((ret = ffmpeg.avfilter_graph_parse_ptr(filter_graph, filter_descr, &inputs, &outputs, null)) < 0)
            {
                goto end;
            }

            if ((ret = ffmpeg.avfilter_graph_config(filter_graph, null)) < 0)
            {
                goto end;
            }


        end:
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);

            return ret;
        }

        static unsafe void display_name(AVFrame* frame, AVRational time_base)
        {
            int x, y;
            byte* p0;
            byte* p;
            long delay;
            string drawing = " .-+#";

            if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
            {
                if (last_pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    delay = ffmpeg.av_rescale_q(frame->pts - last_pts, time_base, FFmpegHelper.AV_TIME_BASE_Q);
                    if (delay > 0 && delay < 1_000_000)
                    {
                        NativeMethods.uSleep(delay); // https://www.sysnet.pe.kr/2/0/12980
                    }
                }

                last_pts = frame->pts;
            }


            p0 = frame->data[0];
            Console.Clear();
            for (y = 0; y < frame->height; y++)
            {
                p = p0;

                for (x = 0; x < frame->width; x++)
                {
                    Console.Write(drawing[*(p++) / 52]);
                }

                Console.WriteLine();
                p0 += frame->linesize[0];
            }
        }
    }

}
