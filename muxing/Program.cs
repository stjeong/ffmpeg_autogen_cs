using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Diagnostics;
using System.IO;

namespace muxing
{
    internal unsafe class Program
    {
        const float STREAM_DURATION = 10.0f;
        const int STREAM_FRAME_RATE = 25;
        const AVPixelFormat STREAM_PIX_FMT = AVPixelFormat.AV_PIX_FMT_YUV420P;
        const int SCALE_FLAGS = ffmpeg.SWS_BICUBIC;

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
            OutputStream video_st = new OutputStream();
            OutputStream audio_st = new OutputStream();

            AVOutputFormat* fmt;
            AVFormatContext* oc;
            AVCodec* audio_codec = null;
            AVCodec* video_codec = null;
            int ret;
            int have_video = 0;
            int have_audio = 0;
            bool encode_video = false, encode_audio = false;
            AVDictionary* opt = null;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string filename = Path.Combine(dirPath, "test.mp4");

            ffmpeg.avformat_alloc_output_context2(&oc, null, null, filename);
            if (oc == null)
            {
                Console.WriteLine("Could not deduce output format from file extension: using MPEG.");
                ffmpeg.avformat_alloc_output_context2(&oc, null, "mpeg", filename);
            }

            if (oc == null)
            {
                return 1;
            }

            fmt = oc->oformat;

            if (fmt->video_codec != AVCodecID.AV_CODEC_ID_NONE)
            {
                // AccessViolationException at FFmpeg.AutoGen 5.0.0
                // fmt->video_codec = AVCodecID.AV_CODEC_ID_H264;

                // Use this instead.
                // add_stream(&video_st, oc, &video_codec, AVCodecID.AV_CODEC_ID_H264);

                add_stream(&video_st, oc, &video_codec, fmt->video_codec);
                have_video = 1;
                encode_video = true;
            }
            
            if (fmt->audio_codec != AVCodecID.AV_CODEC_ID_NONE)
            {
                add_stream(&audio_st, oc, &audio_codec, fmt->audio_codec);
                have_audio = 1;
                encode_audio = true;
            }

            if (have_video == 1)
            {
                open_video(oc, video_codec, &video_st, opt);
            }

            if (have_audio == 1)
            {
                open_audio(oc, audio_codec, &audio_st, opt);
            }

            ffmpeg.av_dump_format(oc, 0, filename, 1);

            if ((fmt->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&oc->pb, filename, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0)
                {
                    Console.WriteLine($"Could not open '{filename}': {FFmpegHelper.av_strerror(ret)}");
                    return 1;
                }
            }

            ret = ffmpeg.avformat_write_header(oc, &opt);
            if (ret < 0)
            {
                Console.WriteLine($"Error occurred when opening output file: {FFmpegHelper.av_strerror(ret)}");
                return 1;
            }

            while (encode_video || encode_audio)
            {
                if (encode_video && (!encode_audio || ffmpeg.av_compare_ts(video_st.next_pts, video_st.enc->time_base, audio_st.next_pts, audio_st.enc->time_base) <= 0))
                {
                    encode_video = write_video_frame(oc, &video_st) == 0;
                } else
                {
                    encode_audio = write_audio_frame(oc, &audio_st) == 0;
                }
            }

            ffmpeg.av_write_trailer(oc);

            if (have_video == 1)
            {
                close_stream(oc, &video_st);
            }

            if (have_audio == 1)
            {
                close_stream(oc, &audio_st);
            }

            if ((fmt->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ffmpeg.avio_closep(&oc->pb);
            }

            ffmpeg.avformat_free_context(oc);

            return 0;
        }

        public unsafe static void log_packet(AVFormatContext* fmt_ctx, AVPacket* pkt)
        {
            AVRational* time_base = &(fmt_ctx->streams[pkt->stream_index]->time_base);

            Console.WriteLine($"pts:{pkt->pts,9:0,0} pts_time: {FFmpegHelper.av_ts2timestr(pkt->pts, time_base),10:#0.000000} dts: {pkt->dts,9:0,0} dts_time: {FFmpegHelper.av_ts2timestr(pkt->dts, time_base),10:#0.000000} duration: {pkt->duration,5:0,0} duration_time: {FFmpegHelper.av_ts2timestr(pkt->duration, time_base):0.000000} stream_index: {pkt->stream_index}");
        }

        public static int write_frame(AVFormatContext* fmt_ctx, AVCodecContext* c, AVStream* st, AVFrame* frame, AVPacket* pkt)
        {
            int ret = ffmpeg.avcodec_send_frame(c, frame);
            if (ret < 0)
            {
                Console.WriteLine($"Error sending a frame to the encoder: {ret}");
                ret.ThrowExceptionIfError();
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_packet(c, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                else if (ret < 0)
                {
                    Console.WriteLine("Error encoding a frame: {ret}");
                    ret.ThrowExceptionIfError();
                }

                ffmpeg.av_packet_rescale_ts(pkt, c->time_base, st->time_base);
                pkt->stream_index = st->index;

                log_packet(fmt_ctx, pkt);
                ret = ffmpeg.av_interleaved_write_frame(fmt_ctx, pkt);

                if (ret < 0)
                {
                    Console.WriteLine("Error while writing output packet: {ret}");
                    ret.ThrowExceptionIfError();
                }

            }

            return ret == ffmpeg.AVERROR_EOF ? 1 : 0;
        }

        public unsafe static void add_stream(OutputStream* ost, AVFormatContext* oc, AVCodec** codec, AVCodecID codec_id)
        {
            AVCodecContext* c;
            int i;

            *codec = ffmpeg.avcodec_find_encoder(codec_id);
            if (*codec == null)
            {
                throw new ApplicationException($"Could not find encoder for '{ffmpeg.avcodec_get_name(codec_id)}'");
            }

            ost->tmp_pkt = ffmpeg.av_packet_alloc();
            if (ost->tmp_pkt == null)
            {
                throw new ApplicationException("Could not allocate AVPacket");
            }

            ost->st = ffmpeg.avformat_new_stream(oc, null);
            if (ost->st == null)
            {
                throw new ApplicationException("Could not allocate stream");
            }

            ost->st->id = (int)(oc->nb_streams - 1);
            c = ffmpeg.avcodec_alloc_context3(*codec);
            if (c == null)
            {
                throw new ApplicationException("Could not alloc an encoding context");
            }

            ost->enc = c;

            switch ((*codec)->type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    c->sample_fmt = (*codec)->sample_fmts != null ? (*codec)->sample_fmts[0] : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                    c->bit_rate = 64000;
                    c->sample_rate = 44100;
                    if ((*codec)->supported_samplerates != null)
                    {
                        c->sample_rate = (*codec)->supported_samplerates[0];
                        for (i = 0; (*codec)->supported_samplerates[i] != 0; i++)
                        {
                            if ((*codec)->supported_samplerates[i] == 44100)
                            {
                                c->sample_rate = 44100;
                            }
                        }
                    }

                    c->channels = ffmpeg.av_get_channel_layout_nb_channels(c->channel_layout);
                    c->channel_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
                    if ((*codec)->channel_layouts != null)
                    {
                        c->channel_layout = (*codec)->channel_layouts[0];
                        for (i = 0; (*codec)->channel_layouts[i] != 0; i++)
                        {
                            if ((*codec)->channel_layouts[i] == ffmpeg.AV_CH_LAYOUT_STEREO)
                            {
                                c->channel_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
                            }
                        }
                    }

                    c->channels = ffmpeg.av_get_channel_layout_nb_channels(c->channel_layout);
                    ost->st->time_base = new AVRational { num = 1, den = c->sample_rate };
                    break;

                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    c->codec_id = codec_id;
                    c->bit_rate = 400000;
                    c->width = 352;
                    c->height = 288;
                    ost->st->time_base = new AVRational { num = 1, den = STREAM_FRAME_RATE };
                    c->time_base = ost->st->time_base;

                    c->gop_size = 12;
                    c->pix_fmt = STREAM_PIX_FMT;
                    if (c->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
                    {
                        c->max_b_frames = 2;
                    }

                    if (c->codec_id == AVCodecID.AV_CODEC_ID_MPEG1VIDEO)
                    {
                        c->mb_decision = 2;
                    }
                    break;

                default:
                    break;
            }

            if ((oc->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
            {
                c->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }
        }

        public unsafe static AVFrame* alloc_audio_frame(AVSampleFormat sample_fmt, ulong channel_layout, int sample_rate, int nb_samples)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            int ret;

            if (frame == null)
            {
                throw new ApplicationException("Error allocating an audio frame");
            }

            frame->format = (int)sample_fmt;
            frame->channel_layout = channel_layout;
            frame->sample_rate = sample_rate;
            frame->nb_samples = nb_samples;

            if (nb_samples != 0)
            {
                ret = ffmpeg.av_frame_get_buffer(frame, 0);
                if (ret < 0)
                {
                    throw new ApplicationException("Error allocating an audio buffer");
                }
            }

            return frame;
        }

        public unsafe static void open_audio(AVFormatContext* oc, AVCodec* codec, OutputStream* ost, AVDictionary* opt_arg)
        {
            AVCodecContext* c;
            int nb_samples;
            int ret;
            AVDictionary* opt = null;

            c = ost->enc;

            ffmpeg.av_dict_copy(&opt, opt_arg, 0);
            ret = ffmpeg.avcodec_open2(c, codec, &opt);
            ffmpeg.av_dict_free(&opt);
            if (ret < 0)
            {
                Console.WriteLine("Could not open audio codec: {ret}");
                ret.ThrowExceptionIfError();
            }

            ost->t = 0;
            ost->tincr = (float)(2 * Math.PI * 110.0 / c->sample_rate);
            ost->tincr2 = ost->tincr / c->sample_rate;

            if ((c->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE)
            {
                nb_samples = 10000;
            }
            else
            {
                nb_samples = c->frame_size;
            }

            ost->frame = alloc_audio_frame(c->sample_fmt, c->channel_layout, c->sample_rate, nb_samples);
            ost->tmp_frame = alloc_audio_frame(AVSampleFormat.AV_SAMPLE_FMT_S16, c->channel_layout, c->sample_rate, nb_samples);

            ret = ffmpeg.avcodec_parameters_from_context(ost->st->codecpar, c);
            if (ret < 0)
            {
                throw new ApplicationException("Could not copy the stream parameters");
            }

            ost->swr_ctx = ffmpeg.swr_alloc();
            if (ost->swr_ctx == null)
            {
                throw new ApplicationException("Could not allocate resampler context");
            }

            ffmpeg.av_opt_set_int(ost->swr_ctx, "in_channel_count", c->channels, 0);
            ffmpeg.av_opt_set_int(ost->swr_ctx, "in_sample_rate", c->sample_rate, 0);
            ffmpeg.av_opt_set_sample_fmt(ost->swr_ctx, "in_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);
            ffmpeg.av_opt_set_int(ost->swr_ctx, "out_channel_count", c->channels, 0);
            ffmpeg.av_opt_set_int(ost->swr_ctx, "out_sample_rate", c->sample_rate, 0);
            ffmpeg.av_opt_set_sample_fmt(ost->swr_ctx, "out_sample_fmt", c->sample_fmt, 0);

            if ((ret = ffmpeg.swr_init(ost->swr_ctx)) < 0)
            {
                throw new ApplicationException("Failed to initialize the resampling context");
            }
        }

        public unsafe static AVFrame* get_audio_frame(OutputStream* ost)
        {
            AVFrame* frame = ost->tmp_frame;
            int j, i, v;
            short* q = (short*)frame->data[0];

            if (ffmpeg.av_compare_ts(ost->next_pts, ost->enc->time_base, (long)STREAM_DURATION, new AVRational { num = 1, den = 1 }) > 0)
            {
                return null;
            }

            for (j = 0; j < frame->nb_samples; j++)
            {
                v = (int)(Math.Sin(ost->t) * 10000);

                for (i = 0; i < ost->enc->channels; i++)
                {
                    *q++ = (short)v;
                }

                ost->t += ost->tincr;
                ost->tincr += ost->tincr2;
            }

            frame->pts = ost->next_pts;
            ost->next_pts += frame->nb_samples;

            return frame;
        }

        public unsafe static int write_audio_frame(AVFormatContext* oc, OutputStream* ost)
        {
            AVCodecContext* c;
            AVFrame* frame;
            int ret;
            long dst_nb_samples = 0;

            c = ost->enc;
            frame = get_audio_frame(ost);

            if (frame != null)
            {
                dst_nb_samples = ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(ost->swr_ctx, c->sample_rate) + frame->nb_samples,
                    c->sample_rate, c->sample_rate, AVRounding.AV_ROUND_UP);

                Debug.Assert(dst_nb_samples == frame->nb_samples);

                ret = ffmpeg.av_frame_make_writable(ost->frame);
                ret.ThrowExceptionIfError();

                ret = ffmpeg.swr_convert(ost->swr_ctx, (byte**)&ost->frame->data, (int)dst_nb_samples, (byte**)&frame->data, frame->nb_samples);
                if (ret < 0)
                {
                    throw new ApplicationException($"Error while converting: {ret}");
                }

                frame = ost->frame;
                frame->pts = ffmpeg.av_rescale_q(ost->samples_count, new AVRational { num = 1, den = c->sample_rate }, c->time_base);
                ost->samples_count += (int)dst_nb_samples;
            }

            return write_frame(oc, c, ost->st, frame, ost->tmp_pkt);
        }

        public unsafe static AVFrame* alloc_picture(AVPixelFormat pix_fmt, int width, int height)
        {
            AVFrame* picture;
            int ret;

            picture = ffmpeg.av_frame_alloc();
            if (picture == null)
            {
                return null;
            }

            picture->format = (int)pix_fmt;
            picture->width = width;
            picture->height = height;

            ret = ffmpeg.av_frame_get_buffer(picture, 0);
            if (ret < 0)
            {
                throw new ApplicationException($"Could not allocate frame data: {ret}");
            }

            return picture;
        }

        public unsafe static void open_video(AVFormatContext* oc, AVCodec* codec, OutputStream* ost, AVDictionary* opt_arg)
        {
            int ret;
            AVCodecContext* c = ost->enc;
            AVDictionary* opt = null;

            ffmpeg.av_dict_copy(&opt, opt_arg, 0);

            ret = ffmpeg.avcodec_open2(c, codec, &opt);
            ret.ThrowExceptionIfError();

            ost->frame = alloc_picture(c->pix_fmt, c->width, c->height);
            if (ost->frame == null)
            {
                throw new ApplicationException("Could not allocate video frame");
            }

            ost->tmp_frame = null;
            if (c->pix_fmt != AVPixelFormat.AV_PIX_FMT_YUV420P)
            {
                ost->tmp_frame = alloc_picture(AVPixelFormat.AV_PIX_FMT_YUV420P, c->width, c->height);
                if (ost->tmp_frame == null)
                {
                    throw new ApplicationException("Could not allocate temporary picture");
                }
            }

            ret = ffmpeg.avcodec_parameters_from_context(ost->st->codecpar, c);
            if (ret < 0)
            {
                throw new ApplicationException("Could not copy the stream parameters");
            }
        }

        public unsafe static void fill_yuv_image(AVFrame* pict, int frame_index, int width, int height)
        {
            int x, y, i;
            i = frame_index;

            for (y = 0; y < height; y++)
            {
                for (x = 0; x < width; x++)
                {
                    pict->data[0][y * pict->linesize[0] + x] = (byte)(x + y + i * 3);
                }
            }

            for (y = 0; y < height / 2; y ++)
            {
                for (x = 0; x < width / 2; x ++)
                {
                    pict->data[1][y * pict->linesize[1] + x] = (byte)(128 + y + i * 2);
                    pict->data[2][y * pict->linesize[2] + x] = (byte)(64 + y + i * 5);
                }
            }
        }

        public unsafe static AVFrame* get_video_frame(OutputStream* ost)
        {
            AVCodecContext* c = ost->enc;

            if (ffmpeg.av_compare_ts(ost->next_pts, c->time_base, (long)STREAM_DURATION, new AVRational { num = 1, den = 1 }) > 0)
            {
                return null;
            }

            if (ffmpeg.av_frame_make_writable(ost->frame) < 0)
            {
                throw new ApplicationException("av_frame_make_writable failed");
            }

            if (c->pix_fmt != AVPixelFormat.AV_PIX_FMT_YUV420P)
            {
                if (ost->sws_ctx == null)
                {
                    ost->sws_ctx = ffmpeg.sws_getContext(c->width, c->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                        c->width, c->height, c->pix_fmt, SCALE_FLAGS, null, null, null);

                    if (ost->sws_ctx == null)
                    {
                        throw new ApplicationException("Could not initialize the conversion context");
                    }
                }

                fill_yuv_image(ost->tmp_frame, (int)ost->next_pts, c->width, c->height);
                ffmpeg.sws_scale(ost->sws_ctx, ost->tmp_frame->data,
                    ost->tmp_frame->linesize, 0, c->height, ost->frame->data, ost->frame->linesize);
            } else
            {
                fill_yuv_image(ost->frame, (int)ost->next_pts, c->width, c->height);
            }

            ost->frame->pts = ost->next_pts++;

            return ost->frame;
        }

        public unsafe static int write_video_frame(AVFormatContext* oc, OutputStream* ost)
        {
            return write_frame(oc, ost->enc, ost->st, get_video_frame(ost), ost->tmp_pkt);
        }

        public unsafe static void close_stream(AVFormatContext* oc, OutputStream* ost)
        {
            ffmpeg.avcodec_free_context(&ost->enc);
            ffmpeg.av_frame_free(&ost->frame);
            ffmpeg.av_frame_free(&ost->tmp_frame);
            ffmpeg.av_packet_free(&ost->tmp_pkt);
            ffmpeg.sws_freeContext(ost->sws_ctx);
            ffmpeg.swr_free(&ost->swr_ctx);
        }
    }

    public unsafe struct OutputStream
    {
        public AVStream* st;
        public AVCodecContext* enc;

        public long next_pts;
        public int samples_count;

        public AVFrame* frame;
        public AVFrame* tmp_frame;

        public AVPacket* tmp_pkt;

        public float t;
        public float tincr;
        public float tincr2;

        public SwsContext* sws_ctx;
        public SwrContext* swr_ctx;
    }
}
