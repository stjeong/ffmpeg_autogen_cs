using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace transcoding
{
    public unsafe struct FilteringContext
    {
        public AVFilterContext* buffersink_ctx;
        public AVFilterContext* buffersrc_ctx;
        public AVFilterGraph* filter_graph;

        public AVPacket* enc_pkt;
        public AVFrame* filtered_frame;
    }

    public unsafe struct StreamContext
    {
        public AVCodecContext* dec_ctx;
        public AVCodecContext* enc_ctx;

        public AVFrame* dec_frame;
    }

    internal unsafe class Program
    {
        static AVFormatContext* ifmt_ctx;
        static AVFormatContext* ofmt_ctx;

        static FilteringContext* filter_ctx;
        static StreamContext* stream_ctx;

        static int open_input_file(string filename)
        {
            int ret;
            uint i;

            ifmt_ctx = null;
            fixed (AVFormatContext** pfmt_ctx = &ifmt_ctx)
            {
                if ((ret = ffmpeg.avformat_open_input(pfmt_ctx, filename, null, null)) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot open input file\n");
                    return ret;
                }

                if ((ret = ffmpeg.avformat_find_stream_info(ifmt_ctx, null)) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot find stream information\n");
                    return ret;
                }

                ulong ctxSize = (ulong)sizeof(StreamContext);

                stream_ctx = (StreamContext*)ffmpeg.av_calloc(ifmt_ctx->nb_streams, ctxSize);

                if (stream_ctx == null)
                {
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }
            }

            for (i = 0; i < ifmt_ctx->nb_streams; i++)
            {
                AVStream* stream = ifmt_ctx->streams[i];
                AVCodec* dec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                AVCodecContext* codec_ctx;

                if (dec == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Failed to find decoder for stream #{i}\n");
                    return ffmpeg.AVERROR_DECODER_NOT_FOUND;
                }

                codec_ctx = ffmpeg.avcodec_alloc_context3(dec);
                if (codec_ctx == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Failed to allocate the decoder context for stream #{i}\n");
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }

                ret = ffmpeg.avcodec_parameters_to_context(codec_ctx, stream->codecpar);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Failed to copy decoder parameters to input decoder context for stream {i}\n");
                    return ret;
                }

                if (codec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || codec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    if (codec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        codec_ctx->framerate = ffmpeg.av_guess_frame_rate(ifmt_ctx, stream, null);
                    }

                    ret = ffmpeg.avcodec_open2(codec_ctx, dec, null);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Failed to open decoder for stream #{i}\n");
                        return ret;
                    }
                }

                stream_ctx[i].dec_ctx = codec_ctx;

                stream_ctx[i].dec_frame = ffmpeg.av_frame_alloc();
                if (stream_ctx[i].dec_frame == null)
                {
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }
            }

            ffmpeg.av_dump_format(ifmt_ctx, 0, filename, 0);
            return 0;
        }

        static unsafe int open_output_file(string filename)
        {
            AVStream* out_stream;
            AVStream* in_stream;
            AVCodecContext* dec_ctx, enc_ctx;
            AVCodec* encoder;
            int ret;
            uint i;

            ofmt_ctx = null;
            fixed (AVFormatContext** pfmt_ctx = &ofmt_ctx)
            {
                ffmpeg.avformat_alloc_output_context2(pfmt_ctx, null, null, filename);
                if (ofmt_ctx == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Could not create output context\n");
                    return ffmpeg.AVERROR_UNKNOWN;
                }
            }

            for (i = 0; i < ifmt_ctx->nb_streams; i++)
            {
                out_stream = ffmpeg.avformat_new_stream(ofmt_ctx, null);
                if (out_stream == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Failed allocating output stream\n");
                    return ffmpeg.AVERROR_UNKNOWN;
                }

                in_stream = ifmt_ctx->streams[i];
                dec_ctx = stream_ctx[i].dec_ctx;

                if (dec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || dec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    encoder = ffmpeg.avcodec_find_encoder(dec_ctx->codec_id);
                    if (encoder == null)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Necessary encoder not found\n");
                        return ffmpeg.AVERROR_INVALIDDATA;
                    }

                    enc_ctx = ffmpeg.avcodec_alloc_context3(encoder);
                    if (enc_ctx == null)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Failed to allocate the encoder context\n");
                        return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    }

                    if (dec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        enc_ctx->height = dec_ctx->height;
                        enc_ctx->width = dec_ctx->width;
                        enc_ctx->sample_aspect_ratio = dec_ctx->sample_aspect_ratio;
                        if (encoder->pix_fmts != null)
                        {
                            enc_ctx->pix_fmt = encoder->pix_fmts[0];
                        }
                        else
                        {
                            enc_ctx->pix_fmt = dec_ctx->pix_fmt;
                        }

                        enc_ctx->time_base = FFmpegHelper.av_inv_q(dec_ctx->framerate);
                    }
                    else
                    {
                        // 2022-03-15 - cdba98bb80 - lavc 59.24.100 - avcodec.h codec_par.h
                        //   Update AVCodecContext for the new channel layout API: add ch_layout,
                        //   deprecate channels/channel_layout
                        // ret = FFmpegHelper.av_channel_layout_copy(&enc_ctx->channel_layout, &dec_ctx->channel_layout);

                        enc_ctx->sample_rate = dec_ctx->sample_rate;
                        enc_ctx->channel_layout = dec_ctx->channel_layout;
                        enc_ctx->channels = ffmpeg.av_get_channel_layout_nb_channels(enc_ctx->channel_layout);
                        enc_ctx->sample_fmt = encoder->sample_fmts[0];
                        enc_ctx->time_base = new AVRational { num = 1, den = enc_ctx->sample_rate };
                    }

                    if ((ofmt_ctx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
                    {
                        enc_ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                    }

                    ret = ffmpeg.avcodec_open2(enc_ctx, encoder, null);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Cannot open video encoder for stream {i}\n");
                        return ret;
                    }

                    ret = ffmpeg.avcodec_parameters_from_context(out_stream->codecpar, enc_ctx);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Failed to copy encoder parameters to output stream #{i}\n");
                        return ret;
                    }

                    out_stream->time_base = enc_ctx->time_base;
                    stream_ctx[i].enc_ctx = enc_ctx;
                }
                else if (dec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_UNKNOWN)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"Elementary stream #{i} is of unknown type, cannot proceed\n");
                    return ffmpeg.AVERROR_INVALIDDATA;
                }
                else
                {
                    ret = ffmpeg.avcodec_parameters_copy(out_stream->codecpar, in_stream->codecpar);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Copying parameters for stream #{i} failed\n");
                        return ret;
                    }

                    out_stream->time_base = in_stream->time_base;
                }
            }

            ffmpeg.av_dump_format(ofmt_ctx, 0, filename, 1);

            if ((ofmt_ctx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&ofmt_ctx->pb, filename, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Could not open output file'{filename}'");
                    return ret;
                }
            }

            ret = ffmpeg.avformat_write_header(ofmt_ctx, null);
            if (ret < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error occurred when opening output file\n");
                return ret;
            }

            return 0;
        }

        static unsafe int init_filter(FilteringContext* fctx, AVCodecContext* dec_ctx, AVCodecContext* enc_ctx, string filter_spec)
        {
            string args;
            int ret = 0;
            AVFilter* buffersrc = null;
            AVFilter* buffersink = null;
            AVFilterContext* buffersrc_ctx = null;
            AVFilterContext* buffersink_ctx = null;
            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
            AVFilterGraph* filter_graph = ffmpeg.avfilter_graph_alloc();

            if (outputs == null || inputs == null || filter_graph == null)
            {
                ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto end;
            }

            if (dec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                buffersrc = ffmpeg.avfilter_get_by_name("buffer");
                buffersink = ffmpeg.avfilter_get_by_name("buffersink");
                if (buffersrc == null || buffersink == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "filtering source or sink element not found\n");
                    ret = ffmpeg.AVERROR_UNKNOWN;
                    goto end;
                }

                AVRational ts = dec_ctx->time_base;
                AVRational sar = dec_ctx->sample_aspect_ratio;
                args = $"video_size={dec_ctx->width}x{dec_ctx->height}:pix_fmt={(int)dec_ctx->pix_fmt}:time_base={ts.num}/{ts.den}:pixel_aspect={sar.num}/{sar.den}";
                ret = ffmpeg.avfilter_graph_create_filter(&buffersrc_ctx, buffersrc, "in", args, null, filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create buffer source\n");
                    goto end;
                }

                ret = ffmpeg.avfilter_graph_create_filter(&buffersink_ctx, buffersink, "out", null, null, filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create buffer sink\n");
                    goto end;
                }

                int pix_fmt_size = sizeof(AVPixelFormat); //  enc_ctx->pix_fmt;
                ret = ffmpeg.av_opt_set_bin(buffersink_ctx, "pix_fmts", (byte*)&enc_ctx->pix_fmt, pix_fmt_size, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output pixel format\n");
                    goto end;
                }
            }
            else if (dec_ctx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                buffersrc = ffmpeg.avfilter_get_by_name("abuffer");
                buffersink = ffmpeg.avfilter_get_by_name("abuffersink");
                if (buffersrc == null || buffersink == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "filtering source or sink element not found\n");
                    ret = ffmpeg.AVERROR_UNKNOWN;
                    goto end;
                }

                if (dec_ctx->channel_layout == 0)
                {
                    dec_ctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(dec_ctx->channels);
                }

                AVRational ts = dec_ctx->time_base;

                args = $"time_base={ts.num}/{ts.den}:sample_rate={dec_ctx->sample_rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name(dec_ctx->sample_fmt)}:channel_layout=0x{dec_ctx->channel_layout.ToString("x")}";
                ret = ffmpeg.avfilter_graph_create_filter(&buffersrc_ctx, buffersrc, "in", args, null, filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create audio buffer source\n");
                    goto end;
                }

                ret = ffmpeg.avfilter_graph_create_filter(&buffersink_ctx, buffersink, "out", null, null, filter_graph);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot create audio buffer sink\n");
                    goto end;
                }

                int sample_fmt_size = sizeof(AVSampleFormat);
                ret = ffmpeg.av_opt_set_bin(buffersink_ctx, "sample_fmts", (byte*)&enc_ctx->sample_fmt, sample_fmt_size, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output sample format\n");
                    goto end;
                }

                int channel_layout_size = sizeof(ulong);
                ret = ffmpeg.av_opt_set_bin(buffersink_ctx, "channel_layouts", (byte*)&enc_ctx->channel_layout, channel_layout_size, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output channel layout\n");
                    goto end;
                }

                int sample_rate_size = sizeof(int);
                ret = ffmpeg.av_opt_set_bin(buffersink_ctx, "sample_rates", (byte*)&enc_ctx->sample_rate, sample_rate_size, ffmpeg.AV_OPT_SEARCH_CHILDREN);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Cannot set output sample rate\n");
                    goto end;
                }
            }
            else
            {
                ret = ffmpeg.AVERROR_UNKNOWN;
                goto end;
            }

            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = buffersrc_ctx;
            outputs->pad_idx = 0;
            outputs->next = null;

            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = buffersink_ctx;
            inputs->pad_idx = 0;
            inputs->next = null;

            if (outputs->name == null || inputs->name == null)
            {
                ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto end;
            }

            if ((ret = ffmpeg.avfilter_graph_parse_ptr(filter_graph, filter_spec, &inputs, &outputs, null)) < 0)
            {
                goto end;
            }

            if ((ret = ffmpeg.avfilter_graph_config(filter_graph, null)) < 0)
            {
                goto end;
            }

            fctx->buffersrc_ctx = buffersrc_ctx;
            fctx->buffersink_ctx = buffersink_ctx;
            fctx->filter_graph = filter_graph;

        end:
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);

            return ret;
        }

        static unsafe int init_filters()
        {
            string filter_spec;
            uint i;
            int ret;

            ulong fcSize = (ulong)sizeof(FilteringContext);

            filter_ctx = (FilteringContext*)ffmpeg.av_malloc_array(ifmt_ctx->nb_streams, fcSize);
            if (filter_ctx == null)
            {
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            for (i = 0; i < ifmt_ctx->nb_streams; i++)
            {
                filter_ctx[i].buffersrc_ctx = null;
                filter_ctx[i].buffersink_ctx = null;
                filter_ctx[i].filter_graph = null;

                if (ifmt_ctx->streams[i]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO
                    && ifmt_ctx->streams[i]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    continue;
                }

                if (ifmt_ctx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    filter_spec = "null";
                }
                else
                {
                    filter_spec = "anull";
                }

                ret = init_filter(&filter_ctx[i], stream_ctx[i].dec_ctx, stream_ctx[i].enc_ctx, filter_spec);
                if (ret != 0)
                {
                    return ret;
                }

                filter_ctx[i].enc_pkt = ffmpeg.av_packet_alloc();
                if (filter_ctx[i].enc_pkt == null)
                {
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }

                filter_ctx[i].filtered_frame = ffmpeg.av_frame_alloc();
                if (filter_ctx[i].filtered_frame == null)
                {
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }
            }

            return 0;
        }

        static unsafe int encode_write_frame(uint stream_index, int flush)
        {
            StreamContext* stream = &stream_ctx[stream_index];
            FilteringContext* filter = &filter_ctx[stream_index];
            AVFrame* filt_frame = flush != 0 ? null : filter->filtered_frame;
            AVPacket* enc_pkt = filter->enc_pkt;
            int ret;

            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, "Encoding frame\n");
            ffmpeg.av_packet_unref(enc_pkt);

            ret = ffmpeg.avcodec_send_frame(stream->enc_ctx, filt_frame);

            if (ret < 0)
            {
                return ret;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_packet(stream->enc_ctx, enc_pkt);

                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    return 0;
                }

                enc_pkt->stream_index = (int)stream_index;
                ffmpeg.av_packet_rescale_ts(enc_pkt, stream->enc_ctx->time_base, ofmt_ctx->streams[stream_index]->time_base);

                ffmpeg.av_log(null, ffmpeg.AV_LOG_DEBUG, "Muxing frame\n");
                ret = ffmpeg.av_interleaved_write_frame(ofmt_ctx, enc_pkt);
            }

            return ret;
        }

        static unsafe int filter_encode_write_frame(AVFrame* frame, uint stream_index)
        {
            FilteringContext* filter = &filter_ctx[stream_index];
            int ret;

            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, "Pushing decoded frame to filters\n");
            ret = ffmpeg.av_buffersrc_add_frame_flags(filter->buffersrc_ctx, frame, 0);
            if (ret < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Error while feeding the filtergraph\n");
                return ret;
            }

            while (true)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, "Pulling filtered frame from filters\n");
                ret = ffmpeg.av_buffersink_get_frame(filter->buffersink_ctx, filter->filtered_frame);

                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    {
                        ret = 0;
                    }

                    break;
                }

                filter->filtered_frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                ret = encode_write_frame(stream_index, 0);
                ffmpeg.av_frame_unref(filter->filtered_frame);

                if (ret < 0)
                {
                    break;
                }
            }

            return ret;
        }

        static unsafe int flush_encoder(uint stream_index)
        {
            if ((stream_ctx[stream_index].enc_ctx->codec->capabilities & ffmpeg.AV_CODEC_CAP_DELAY) == 0)
            {
                return 0;
            }

            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, $"Flushing stream #{stream_index} encoder\n");
            return encode_write_frame(stream_index, 1);
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

            int ret;
            AVPacket* packet = null;
            uint stream_index;
            uint i;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string input_filename = Path.Combine(dirPath, "..", "..", "..", "Samples", "sample-10s.mp4");

            string output_filename = Path.Combine(dirPath, "test.mp4");

            if ((ret = open_input_file(input_filename)) < 0)
            {
                goto end;
            }

            if ((ret = open_output_file(output_filename)) < 0)
            {
                goto end;
            }

            if ((ret = init_filters()) < 0)
            {
                goto end;
            }

            if ((packet = ffmpeg.av_packet_alloc()) == null)
            {
                goto end;
            }

            while (true)
            {
                if ((ret = ffmpeg.av_read_frame(ifmt_ctx, packet)) < 0)
                {
                    break;
                }

                stream_index = (uint)packet->stream_index;
                ffmpeg.av_log(null, ffmpeg.AV_LOG_DEBUG, $"Demuxer gave frame of stream_index {stream_index}\n");

                if (filter_ctx[stream_index].filter_graph != null)
                {
                    StreamContext* stream = &stream_ctx[stream_index];

                    ffmpeg.av_log(null, ffmpeg.AV_LOG_DEBUG, "Going to reencode & filter the frame\n");
                    ffmpeg.av_packet_rescale_ts(packet, ifmt_ctx->streams[stream_index]->time_base, stream->dec_ctx->time_base);
                    ret = ffmpeg.avcodec_send_packet(stream->dec_ctx, packet);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Decoding failed\n");
                        break;
                    }

                    while (ret >= 0)
                    {
                        ret = ffmpeg.avcodec_receive_frame(stream->dec_ctx, stream->dec_frame);
                        if (ret == ffmpeg.AVERROR_EOF || ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            break;
                        }
                        else if (ret < 0)
                        {
                            goto end;
                        }

                        stream->dec_frame->pts = stream->dec_frame->best_effort_timestamp;
                        ret = filter_encode_write_frame(stream->dec_frame, stream_index);
                        if (ret < 0)
                        {
                            goto end;
                        }
                    }
                }
                else
                {
                    ffmpeg.av_packet_rescale_ts(packet, ifmt_ctx->streams[stream_index]->time_base, ofmt_ctx->streams[stream_index]->time_base);
                    ret = ffmpeg.av_interleaved_write_frame(ofmt_ctx, packet);
                    if (ret < 0)
                    {
                        goto end;
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }

            for (i = 0; i < ifmt_ctx->nb_streams; i ++)
            {
                if (filter_ctx[i].filter_graph == null)
                {
                    continue;
                }

                ret = filter_encode_write_frame(null, i);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Flushing filter failed\n");
                    goto end;
                }

                ret = flush_encoder(i);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Flushing encoder failed\n");
                    goto end;
                }
            }

            ffmpeg.av_write_trailer(ofmt_ctx);

        end:
            ffmpeg.av_packet_free(&packet);

            for (i = 0; i < ifmt_ctx->nb_streams; i ++)
            {
                ffmpeg.avcodec_free_context(&stream_ctx[i].dec_ctx);
                if (ofmt_ctx != null && ofmt_ctx->nb_streams > i && ofmt_ctx->streams[i] != null && stream_ctx[i].enc_ctx != null)
                {
                    ffmpeg.avcodec_free_context(&stream_ctx[i].enc_ctx);
                }

                if (filter_ctx != null && filter_ctx[i].filter_graph != null)
                {
                    ffmpeg.avfilter_graph_free(&filter_ctx[i].filter_graph);
                    ffmpeg.av_packet_free(&filter_ctx[i].enc_pkt);
                    ffmpeg.av_frame_free(&filter_ctx[i].filtered_frame);
                }

                ffmpeg.av_frame_free(&stream_ctx[i].dec_frame);
            }

            ffmpeg.av_free(filter_ctx);
            ffmpeg.av_free(stream_ctx);

            fixed (AVFormatContext** pfmt_ctx = &ifmt_ctx)
            {
                ffmpeg.avformat_close_input(pfmt_ctx);
                if (ofmt_ctx != null && ((ofmt_ctx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0))
                {
                    ffmpeg.avio_closep(&ofmt_ctx->pb);
                }
            }

            ffmpeg.avformat_free_context(ofmt_ctx);

            if (ret < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Error occurred: {FFmpegHelper.av_err2str(ret)}");
            }

            return ret != 0 ? 1 : 0;
        }
    }
}
