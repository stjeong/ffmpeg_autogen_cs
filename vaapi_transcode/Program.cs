using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace vaapi_encode
{
    public unsafe class Program
    {
        static AVFormatContext* ifmt_ctx = null;
        static AVFormatContext* ofmt_ctx = null;
        static AVBufferRef* hw_device_ctx = null;
        static AVCodecContext* decoder_ctx = null;
        static int video_stream = -1;
        static AVStream* ost;
        static int initialized = 0;

        static unsafe AVPixelFormat get_vaapi_format(AVCodecContext* ctx, AVPixelFormat* pix_fmts)
        {
            AVPixelFormat* p;

            for (p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_VAAPI)
                {
                    return *p;
                }
            }

            Console.WriteLine("Unable to decode this file using VA-API.");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        static unsafe int open_input_file(string filename)
        {
            int ret;
            AVCodec* decoder = null;
            AVStream* video = null;

            fixed (AVFormatContext** pfmt_ctx = &ifmt_ctx)
            {
                if ((ret = ffmpeg.avformat_open_input(pfmt_ctx, filename, null, null)) < 0)
                {
                    Console.WriteLine($"Cannot open input file '{filename}', Error code: {FFmpegHelper.av_err2str(ret)}");
                    return ret;
                }
            }

            if ((ret = ffmpeg.avformat_find_stream_info(ifmt_ctx, null)) < 0)
            {
                Console.WriteLine($"Cannot find input stream information. Error code: {FFmpegHelper.av_err2str(ret)}");
                return ret;
            }

            ret = ffmpeg.av_find_best_stream(ifmt_ctx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (ret < 0)
            {
                Console.WriteLine($"Cannot find a video stream in the input file. Error code: {FFmpegHelper.av_err2str(ret)}");
                return ret;
            }

            video_stream = ret;

            if ((decoder_ctx = ffmpeg.avcodec_alloc_context3(decoder)) == null)
            {
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            video = ifmt_ctx->streams[video_stream];
            if ((ret = ffmpeg.avcodec_parameters_to_context(decoder_ctx, video->codecpar)) < 0)
            {
                Console.WriteLine($"{nameof(ffmpeg.avcodec_parameters_to_context)} error. Error code: {FFmpegHelper.av_err2str(ret)} ");
                return ret;
            }

            decoder_ctx->hw_device_ctx = ffmpeg.av_buffer_ref(hw_device_ctx);
            if (decoder_ctx->hw_device_ctx == null)
            {
                Console.WriteLine("A hardware device reference create failed.");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            decoder_ctx->get_format = (AVCodecContext_get_format_func)get_vaapi_format;
            if ((ret = ffmpeg.avcodec_open2(decoder_ctx, decoder, null)) < 0)
            {
                Console.WriteLine($"Failed to open codec for decoding. Error code: {FFmpegHelper.av_err2str(ret)}");
            }

            return ret;
        }

        static unsafe int encode_write(AVCodecContext* encoder_ctx, AVPacket* enc_pkt, AVFrame* frame)
        {
            int ret = 0;

            ffmpeg.av_packet_unref(enc_pkt);

            if ((ret = ffmpeg.avcodec_send_frame(encoder_ctx, frame)) < 0)
            {
                Console.WriteLine($"Error during encoding. Error code: {FFmpegHelper.av_err2str(ret)}");
                goto end;
            }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(encoder_ctx, enc_pkt);
                if (ret != 0)
                {
                    break;
                }

                enc_pkt->stream_index = 0;
                ffmpeg.av_packet_rescale_ts(enc_pkt, ifmt_ctx->streams[video_stream]->time_base, ofmt_ctx->streams[0]->time_base);

                ret = ffmpeg.av_interleaved_write_frame(ofmt_ctx, enc_pkt);
                if (ret < 0)
                {
                    Console.WriteLine($"Error during writing data to output file. Error code: {FFmpegHelper.av_err2str(ret)}");
                    return -1;
                }
            }

        end:
            if (ret == ffmpeg.AVERROR_EOF)
            {
                return 0;
            }

            ret = (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) ? 0 : -1;
            return ret;
        }

        static unsafe int dec_enc(AVCodecContext* encoder_ctx, AVPacket* pkt, AVCodec* enc_codec)
        {
            AVFrame* frame;
            int ret = ffmpeg.avcodec_send_packet(decoder_ctx, pkt);
            if (ret < 0)
            {
                Console.WriteLine($"Error during decoding. Error code: {FFmpegHelper.av_err2str(ret)}");
                return ret;
            }

            while (ret >= 0)
            {
                if ((frame = ffmpeg.av_frame_alloc()) == null)
                {
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }

                ret = ffmpeg.avcodec_receive_frame(decoder_ctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_frame_free(&frame);
                    return 0;
                }
                else if (ret < 0)
                {
                    Console.WriteLine($"Error while decoding. Error code: {FFmpegHelper.av_err2str(ret)}");
                    goto fail;
                }

                if (initialized == 0)
                {
                    encoder_ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(decoder_ctx->hw_frames_ctx);
                    if (encoder_ctx->hw_frames_ctx == null)
                    {
                        ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                        goto fail;
                    }

                    encoder_ctx->time_base = FFmpegHelper.av_inv_q(decoder_ctx->framerate);
                    encoder_ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
                    encoder_ctx->width = decoder_ctx->width;
                    encoder_ctx->height = decoder_ctx->height;

                    if ((ret = ffmpeg.avcodec_open2(encoder_ctx, enc_codec, null)) < 0)
                    {
                        Console.WriteLine($"Failed to open encode codec. Error code: {FFmpegHelper.av_err2str(ret)}");
                        goto fail;
                    }

                    if ((ost = ffmpeg.avformat_new_stream(ofmt_ctx, enc_codec)) == null)
                    {
                        Console.WriteLine("Failed to allocate stream for output format.");
                        ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                        goto fail;
                    }

                    ost->time_base = encoder_ctx->time_base;
                    ret = ffmpeg.avcodec_parameters_from_context(ost->codecpar, encoder_ctx);
                    if (ret < 0)
                    {
                        Console.WriteLine($"Failed to copy the stream parameters. Error code: {FFmpegHelper.av_err2str(ret)}");
                        goto fail;
                    }

                    if ((ret = ffmpeg.avformat_write_header(ofmt_ctx, null)) < 0)
                    {
                        Console.WriteLine($"Error while writing stream header. Error code: {FFmpegHelper.av_err2str(ret)}");
                        goto fail;
                    }

                    initialized = 1;
                }

                if ((ret = encode_write(encoder_ctx, pkt, frame)) < 0)
                {
                    Console.WriteLine("Error during encoding and writing");
                }

            fail:
                ffmpeg.av_frame_free(&frame);
                if (ret < 0)
                {
                    return ret;
                }
            }

            return 0;
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

            AVCodec* enc_codec;
            int ret = 0;
            AVPacket* dec_pkt;
            AVCodecContext* encoder_ctx = null;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string input_filename = Path.Combine(dirPath, "..", "..", "..", "Samples", "sample-10s.mp4");
            string encode_codec_name = "h264_nvenc";

            string output_filename = Path.Combine(dirPath, "test.mp4");
            if (File.Exists(output_filename) == true)
            {
                File.Delete(output_filename);
            }

            fixed (AVBufferRef** phw_device_ctx = &hw_device_ctx)
            fixed (AVFormatContext** pofmt_ctx = &ofmt_ctx)
            fixed (AVCodecContext** pdecoder_ctx = &decoder_ctx)
            {
                ret = ffmpeg.av_hwdevice_ctx_create(phw_device_ctx, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, null, null, 0);
                if (ret < 0)
                {
                    Console.WriteLine($"Failed to create a VAAPI device. Error code: {FFmpegHelper.av_err2str(ret)}");
                    return -1;
                }

                dec_pkt = ffmpeg.av_packet_alloc();
                if (dec_pkt == null)
                {
                    Console.WriteLine("Failed to allocate decode packet");
                    goto end;
                }

                if ((ret = open_input_file(input_filename)) < 0)
                {
                    goto end;
                }

                if ((enc_codec = ffmpeg.avcodec_find_encoder_by_name(encode_codec_name)) == null)
                {
                    Console.WriteLine($"Could not find encoder: '{encode_codec_name}'");
                    ret = -1;
                    goto end;
                }

                if ((ret = ffmpeg.avformat_alloc_output_context2(pofmt_ctx, null, null, output_filename)) < 0)
                {
                    Console.WriteLine($"Failed to deduce output format from file extension. Error code: {FFmpegHelper.av_err2str(ret)}");
                    goto end;
                }

                if ((encoder_ctx = ffmpeg.avcodec_alloc_context3(enc_codec)) == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto end;
                }

                ret = ffmpeg.avio_open(&ofmt_ctx->pb, output_filename, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0)
                {
                    Console.WriteLine($"Cannot open output file. Error code: {FFmpegHelper.av_err2str(ret)}");
                    goto end;
                }

                while (ret >= 0)
                {
                    if ((ret = ffmpeg.av_read_frame(ifmt_ctx, dec_pkt)) < 0)
                    {
                        break;
                    }

                    if (video_stream == dec_pkt->stream_index)
                    {
                        ret = dec_enc(encoder_ctx, dec_pkt, enc_codec);
                    }

                    ffmpeg.av_packet_unref(dec_pkt);
                }

                ffmpeg.av_packet_unref(dec_pkt);
                ret = dec_enc(encoder_ctx, dec_pkt, null);

                ret = encode_write(encoder_ctx, dec_pkt, null);

                ffmpeg.av_write_trailer(ofmt_ctx);

            end:
                fixed (AVFormatContext** pifmt_ctx = &ifmt_ctx)
                {
                    ffmpeg.avformat_close_input(pifmt_ctx);
                }

                ffmpeg.avformat_close_input(pofmt_ctx);

                ffmpeg.avcodec_free_context(pdecoder_ctx);
                ffmpeg.avcodec_free_context(&encoder_ctx);

                ffmpeg.av_buffer_unref(phw_device_ctx);
                ffmpeg.av_packet_free(&dec_pkt);
            }

            return 0;
        }
    }
}
