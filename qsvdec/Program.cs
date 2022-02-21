using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using FFmpeg.OSDepends;
using System;
using System.IO;

namespace qsvdec
{
    // https://ffmpeg.org/doxygen/trunk/qsvdec_8c-example.html

    // C# - ffmpeg(FFmpeg.AutoGen)를 이용한 qsvdec.c 예제 포팅
    // https://www.sysnet.pe.kr/2/0/12975

    internal unsafe class Program
    {
        const float STREAM_DURATION = 10.0f;
        const int STREAM_FRAME_RATE = 25;
        const AVPixelFormat STREAM_PIX_FMT = AVPixelFormat.AV_PIX_FMT_YUV420P;
        const int SCALE_FLAGS = ffmpeg.SWS_BICUBIC;

        static AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

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

            AVFormatContext* input_ctx = null;
            AVStream* video_st = null;
            AVCodecContext* decoder_ctx = null;
            AVCodec* decoder;

            AVPacket* pkt = null;
            AVFrame* frame = null, sw_frame = null;

            AVIOContext* output_ctx = null;

            int ret = 0, i;
            AVBufferRef* device_ref = null;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";

            // https://file-examples.com/index.php/sample-video-files/sample-mp4-files/
            string inputfile = Path.Combine(dirPath, "..", "..", "..", "Samples", "file_example_MP4_1920_18MG.mp4");
            string outputfile = Path.Combine(dirPath, "test.mp4");

            try
            {
                ret = ffmpeg.avformat_open_input(&input_ctx, inputfile, null, null);
                if (ret < 0)
                {
                    Console.WriteLine($"Cannot open input file: {inputfile}");
                    throw new ApplicationException();
                }

                for (i = 0; i < input_ctx->nb_streams; i++)
                {
                    AVStream* st = input_ctx->streams[i];

                    if (st->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 && video_st == null)
                    {
                        video_st = st;
                    }
                    else
                    {
                        st->discard = AVDiscard.AVDISCARD_ALL;
                    }
                }

                if (video_st == null)
                {
                    Console.WriteLine("No H.264 video stream in the input file");
                    throw new ApplicationException();
                }

                bool useCuda = true;

                AVHWDeviceType hwDeviceType = (useCuda == true) ? AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA : AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
                _hwPixelFormat = (useCuda == true) ? AVPixelFormat.AV_PIX_FMT_CUDA : AVPixelFormat.AV_PIX_FMT_QSV;
                string formatName = (useCuda == true) ? "h264_cuvid" : "h264_qsv";

                ret = ffmpeg.av_hwdevice_ctx_create(&device_ref, hwDeviceType, "auto", null, 0);
                if (ret < 0)
                {
                    ret.ThrowExceptionIfError();
                    Console.WriteLine("Cannot open the hardware device");
                    throw new ApplicationException();
                }

                decoder = ffmpeg.avcodec_find_decoder_by_name(formatName);
                if (decoder == null)
                {
                    Console.WriteLine($"The {formatName} decoder is not present in libavcodec");
                    throw new ApplicationException();
                }

                decoder_ctx = ffmpeg.avcodec_alloc_context3(decoder);
                if (decoder_ctx == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    throw new ApplicationException();
                }

                decoder_ctx->codec_id = AVCodecID.AV_CODEC_ID_H264;
                if (video_st->codecpar->extradata_size != 0)
                {
                    decoder_ctx->extradata = (byte *)ffmpeg.av_mallocz((ulong)(video_st->codecpar->extradata_size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
                    if (decoder_ctx->extradata == null)
                    {
                        ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                        throw new ApplicationException();
                    }

                    NativeMethods.MoveMemory(decoder_ctx->extradata, video_st->codecpar->extradata, video_st->codecpar->extradata_size);
                    decoder_ctx->extradata_size = video_st->codecpar->extradata_size;
                }

                decoder_ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device_ref);
                decoder_ctx->get_format = (AVCodecContext_get_format_func)get_format;

                ret = ffmpeg.avcodec_open2(decoder_ctx, null, null);
                if (ret < 0)
                {
                    Console.WriteLine("Error opening the decode: ");
                    throw new ApplicationException();
                }

                ret = ffmpeg.avio_open(&output_ctx, outputfile, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0)
                {
                    Console.WriteLine("Error opening the output context: ");
                    throw new ApplicationException();
                }

                frame = ffmpeg.av_frame_alloc();
                sw_frame = ffmpeg.av_frame_alloc();
                pkt = ffmpeg.av_packet_alloc();

                if (frame == null || sw_frame == null || pkt == null)
                {
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    throw new ApplicationException();
                }

                while (ret >= 0)
                {
                    ret = ffmpeg.av_read_frame(input_ctx, pkt);
                    if (ret < 0)
                    {
                        break;
                    }

                    if (pkt->stream_index == video_st->index)
                    {
                        ret = decode_packet(decoder_ctx, frame, sw_frame, pkt, output_ctx, dirPath);
                    }

                    ffmpeg.av_packet_unref(pkt);
                }
               
                ret = decode_packet(decoder_ctx, frame, sw_frame, null, output_ctx, dirPath);
                Console.WriteLine($"ffplay -autoexit -f rawvideo -pixel_format {ffmpeg.av_get_pix_fmt_name(decoder_ctx->sw_pix_fmt)} -video_size {decoder_ctx->width}x{decoder_ctx->height} {outputfile}");
            }
            catch (Exception)
            {
            }
            finally
            {
                if (ret < 0)
                {
                    Console.WriteLine(FFmpegHelper.av_strerror(ret));
                }

                ffmpeg.avformat_close_input(&input_ctx);

                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_frame_free(&sw_frame);
                ffmpeg.av_packet_free(&pkt);

                ffmpeg.avcodec_free_context(&decoder_ctx);

                ffmpeg.av_buffer_unref(&device_ref);

                ffmpeg.avio_close(output_ctx);
            }

            return ret;
        }

        public unsafe static AVPixelFormat get_format(AVCodecContext* avctx, AVPixelFormat* pix_fmts)
        {
            AVPixelFormat* p;

            for (p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == _hwPixelFormat)
                {
                    return *p;
                }
            }

            Console.WriteLine("Failed to get HW surface format.");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        public unsafe static int decode_packet(AVCodecContext* decoder_ctx, AVFrame* frame, AVFrame* sw_frame, AVPacket* pkt, AVIOContext* output_ctx, string outputDir)
        {
            int ret = ffmpeg.avcodec_send_packet(decoder_ctx, pkt);
            if (ret < 0)
            {
                Console.WriteLine("Error during decoding");
                return ret;
            }

            while (ret >= 0)
            {
                uint i;
                int j;

                ret = ffmpeg.avcodec_receive_frame(decoder_ctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                else if (ret < 0)
                {
                    Console.WriteLine("Error during decoding");
                    return ret;
                }

                ret = ffmpeg.av_hwframe_transfer_data(sw_frame, frame, 0);
                if (ret < 0)
                {
                    Console.WriteLine("Error transferring the data to system memory");
                }
                else
                {
                    if (sw_frame->format == (int)AVPixelFormat.AV_PIX_FMT_NV12)
                    {
                        for (i = 0; i < byte_array8.Size && sw_frame->data[i] != null; i++)
                        {
                            for (j = 0; j < (sw_frame->height >> (i > 0 ? 1 : 0)); j++)
                            {
                                ffmpeg.avio_write(output_ctx, sw_frame->data[i] + j * sw_frame->linesize[i], sw_frame->width);
                            }
                        }

                        if (decoder_ctx->frame_number == 100)
                        {
                            string outputFile = Path.Combine(outputDir, "noname_" + decoder_ctx->frame_number + ".pgm");
                            FFmpegHelper.pgm_save(sw_frame->data[0], sw_frame->linesize[0], sw_frame->width, sw_frame->height, outputFile);
                        }
                    }
                }

                ffmpeg.av_frame_unref(sw_frame);
                ffmpeg.av_frame_unref(frame);

                if (ret < 0)
                {
                    return ret;
                }
            }

            return 0;
        }
    }

}
