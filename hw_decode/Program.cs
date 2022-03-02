using CustomMessageLoop;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace hw_decode
{
    internal unsafe class Program
    {
        const int INBUF_SIZE = 4096;
        static AVPixelFormat _hw_pix_fmt = AVPixelFormat.AV_PIX_FMT_NONE;
        static AVBufferRef* _hw_device_ctx = null;

        static void Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            Console.WriteLine($"LIBAVFORMAT Version: {ffmpeg.LIBAVFORMAT_VERSION_MAJOR}.{ffmpeg.LIBAVFORMAT_VERSION_MINOR}");

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string outputFilePath = Path.Combine(dirPath, "test.dat");
            string input_file_path = Path.Combine(dirPath, "..", "..", "..", "Samples", "file_example_MP4_1920_18MG.mp4");

            // C# - Console 응용 프로그램에서 UI 스레드 구현 방법
            // ; https://www.sysnet.pe.kr/2/0/12139
            //
            // 닷넷 프로그램 실습 #4 콘솔 응용 프로그램의 메시지 루프
            // ; https://youtu.be/gOJw_zTki7c
            using (MessageLoop mml = new MessageLoop())
            {
                mml.Loaded += (obj, arg) =>
                {
                    /* hwdevice type: cuda, dxva2, d3d11va, opencl */
                    video_decode_example("cuda", input_file_path, outputFilePath);

                    Console.WriteLine("Will close after 5 seconds");
                };

                mml.Run();

                Thread.Sleep(5000);
            }
        }

        static unsafe void video_decode_example(string hwdeviceName, string filename, string outputFileName)
        {
            AVFormatContext* input_ctx = null;
            AVStream* video = null;
            AVCodecContext* decoder_ctx = null;
            AVCodec* decoder = null;
            AVPacket* packet = null;

            // https://ffmpeg.org/doxygen/trunk/codec_8h_source.html#l00420
            int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;
            int ret = 0;

            AVHWDeviceType type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

            Console.WriteLine("Available device types:");
            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                Console.WriteLine($"\t{ffmpeg.av_hwdevice_get_type_name(type)}");
            }

            type = ffmpeg.av_hwdevice_find_type_by_name(hwdeviceName);
            if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                Console.WriteLine($"Device type {hwdeviceName} is not supported.");
                return;
            }

            do
            {
                packet = ffmpeg.av_packet_alloc();
                if (packet == null)
                {
                    Console.WriteLine("Failed to allocate AVPacket");
                    break;
                }

                if (ffmpeg.avformat_open_input(&input_ctx, filename, null, null) < 0)
                {
                    Console.WriteLine($"Cannot open input file: {filename}");
                    break;
                }

                if (ffmpeg.avformat_find_stream_info(input_ctx, null) < 0)
                {
                    Console.WriteLine("Cannot find input stream information.");
                    break;
                }

                /* find the video stream information */
                ret = ffmpeg.av_find_best_stream(input_ctx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
                if (ret < 0)
                {
                    Console.WriteLine("Cannot find a video stream in the input file\n");
                    break;
                }

                int video_stream = ret;

                for (int i = 0; ; i++)
                {
                    AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(decoder, i);
                    if (config == null)
                    {
                        string? decoderName = Marshal.PtrToStringUTF8(new IntPtr(decoder->name));
                        Console.WriteLine($"Decoder {decoderName} does not support device type {ffmpeg.av_hwdevice_get_type_name(type)}");
                        break;
                    }

                    if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX &&
                            config->device_type == type)
                    {
                        _hw_pix_fmt = config->pix_fmt;
                        break;
                    }
                }

                if (_hw_pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    break;
                }

                decoder_ctx = ffmpeg.avcodec_alloc_context3(decoder);
                if (decoder_ctx == null)
                {
                    Console.WriteLine("Could not allocate video codec context");
                    break;
                }

                video = input_ctx->streams[video_stream];
                if (ffmpeg.avcodec_parameters_to_context(decoder_ctx, video->codecpar) < 0)
                {
                    break;
                }

                decoder_ctx->get_format = (AVCodecContext_get_format_func)get_hw_format;

                if (hw_decoder_init(decoder_ctx, type) < 0)
                {
                    break;
                }

                if ((ret = ffmpeg.avcodec_open2(decoder_ctx, decoder, null)) < 0)
                {
                    Console.WriteLine($"Failed to open codec for stream {video_stream}");
                    break;
                }

                using FileStream output_file = File.OpenWrite(outputFileName);

                while (ret >= 0)
                {
                    if ((ret = ffmpeg.av_read_frame(input_ctx, packet)) < 0)
                    {
                        break;
                    }

                    if (video_stream == packet->stream_index)
                    {
                        ret = decode_write(decoder_ctx, packet, output_file);
                    }
                }

                ffmpeg.av_packet_unref(packet);

                /* flush the decoder */
                ret = decode_write(decoder_ctx, null, output_file);

                Console.WriteLine($"ffplay -autoexit -f rawvideo -pixel_format nv12 -video_size {decoder_ctx->width}x{decoder_ctx->height} {outputFileName}");

            } while (false);

            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (decoder_ctx != null)
            {
                ffmpeg.avcodec_free_context(&decoder_ctx);
            }

            if (input_ctx != null)
            {
                ffmpeg.avformat_close_input(&input_ctx);
            }

            if (_hw_device_ctx != null)
            {
                fixed (AVBufferRef** ppRef = &_hw_device_ctx)
                {
                    ffmpeg.av_buffer_unref(ppRef);
                }
            }
        }

        static unsafe int decode_write(AVCodecContext* avctx, AVPacket* packet, FileStream output_file)
        {
            AVFrame* frame = null;
            AVFrame* sw_frame = null;
            AVFrame* tmp_frame = null;
            byte* buffer = null;
            int size = 0;
            int ret = 0;

            ret = ffmpeg.avcodec_send_packet(avctx, packet);
            if (ret < 0)
            {
                Console.WriteLine("Error during decoding");
                return ret;
            }

            while (true)
            {
                if ((frame = ffmpeg.av_frame_alloc()) == null || (sw_frame = ffmpeg.av_frame_alloc()) == null)
                {
                    Console.WriteLine("Can not alloc frame");
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto fail;
                }

                ret = ffmpeg.avcodec_receive_frame(avctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_frame_free(&frame);
                    ffmpeg.av_frame_free(&sw_frame);
                    return 0;
                }
                else if (ret < 0)
                {
                    Console.WriteLine("Error while decoding");
                    goto fail;
                }

                if (frame->format == (int)_hw_pix_fmt)
                {
                    /* retrieve data from GPU to CPU */
                    if ((ret = ffmpeg.av_hwframe_transfer_data(sw_frame, frame, 0)) < 0)
                    {
                        Console.WriteLine("Error transferring the data to system memory");
                        goto fail;
                    }

                    tmp_frame = sw_frame;
                }
                else
                {
                    tmp_frame = frame;
                }

                size = ffmpeg.av_image_get_buffer_size((AVPixelFormat)tmp_frame->format, tmp_frame->width,
                                                tmp_frame->height, 1);
                buffer = (byte*)ffmpeg.av_malloc((ulong)size);
                if (buffer == null)
                {
                    Console.WriteLine("Can not alloc buffer");
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto fail;
                }

                FFmpeg.AutoGen.byte_ptrArray4* ptrFrameData = (FFmpeg.AutoGen.byte_ptrArray4*)&tmp_frame->data;
                FFmpeg.AutoGen.int_array4* ptrLineSize = (FFmpeg.AutoGen.int_array4*)&tmp_frame->linesize;

                ret = ffmpeg.av_image_copy_to_buffer(buffer, size,
                                              *ptrFrameData, *ptrLineSize,
                                      (AVPixelFormat)tmp_frame->format,
                                      tmp_frame->width, tmp_frame->height, 1);
                if (ret < 0)
                {
                    Console.WriteLine("Can not copy image to buffer");
                    goto fail;
                }

                if (avctx->frame_number == 50)
                {
                    if (tmp_frame->format == (int)AVPixelFormat.AV_PIX_FMT_NV12)
                    {
                        OpenCvSharp.Mat rgbMat = new OpenCvSharp.Mat();
                        OpenCvSharp.Mat nvMat = new OpenCvSharp.Mat(tmp_frame->height * 3 / 2, tmp_frame->width,
                            OpenCvSharp.MatType.CV_8UC1, new IntPtr(buffer));

                        OpenCvSharp.Cv2.CvtColor(nvMat, rgbMat, OpenCvSharp.ColorConversionCodes.YUV2BGR_NV12);
                        OpenCvSharp.Cv2.ImShow("test", rgbMat);
                        return ret;
                    }
                }

                ReadOnlySpan<byte> contents = new Span<byte>(buffer, size);
                output_file.Write(contents);

            fail:
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_frame_free(&sw_frame);
                ffmpeg.av_freep(&buffer);
                if (ret < 0)
                {
                    return ret;
                }
            }
        }

        static unsafe int hw_decoder_init(AVCodecContext* ctx, AVHWDeviceType type)
        {
            int err = 0;

            fixed (AVBufferRef** ptr = &_hw_device_ctx)
            {
                if ((err = ffmpeg.av_hwdevice_ctx_create(ptr, type, null, null, 0)) < 0)
                {
                    Console.WriteLine("Failed to create specified HW device.");
                    return err;
                }

                ctx->hw_device_ctx = ffmpeg.av_buffer_ref(*ptr);
            }

            return err;
        }

        static unsafe AVPixelFormat get_hw_format(AVCodecContext* ctx, AVPixelFormat* pix_fmts)
        {
            AVPixelFormat* p;

            for (p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == _hw_pix_fmt)
                {
                    return *p;
                }
            }

            Console.WriteLine("Failed to get HW surface format.");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }
    }
}
