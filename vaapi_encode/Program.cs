#define USE_CUDA

using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace vaapi_encode
{
    /* ffmpeg -hwaccels
Hardware acceleration methods:
cuda
dxva2
d3d11va
opencl    
    */
    public unsafe class Program
    {
        static int width, height;

        static unsafe int set_hwframe_ctx(AVCodecContext* ctx, AVBufferRef* hw_device_ctx)
        {
            AVBufferRef* hw_frames_ref;
            AVHWFramesContext* frames_ctx = null;
            int err = 0;

            if ((hw_frames_ref = ffmpeg.av_hwframe_ctx_alloc(hw_device_ctx)) == null)
            {
#if USE_CUDA
                Console.WriteLine("Failed to create CUDA frame context.");
#else
                Console.WriteLine("Failed to create VAAPI frame context.");
#endif
                return -1;
            }

            frames_ctx = (AVHWFramesContext*)(hw_frames_ref->data);
#if USE_CUDA
            frames_ctx->format = AVPixelFormat.AV_PIX_FMT_CUDA;
#else
            frames_ctx->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
#endif
            frames_ctx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            frames_ctx->width = width; 
            frames_ctx->height = height;
            frames_ctx->initial_pool_size = 20;
            if ((err = ffmpeg.av_hwframe_ctx_init(hw_frames_ref)) < 0)
            {
                Console.WriteLine($"Failed to initialize VAAPI frame context. Error code: {FFmpegHelper.av_err2str(err)}");
                ffmpeg.av_buffer_unref(&hw_frames_ref);
                return err;
            }

            ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(hw_frames_ref);
            if (ctx->hw_frames_ctx == null)
            {
                err = ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            ffmpeg.av_buffer_unref(&hw_frames_ref);
            return err;
        }

        static unsafe int encode_write(AVCodecContext* avctx, AVFrame* frame, FileStream fs)
        {
            int ret = 0;
            AVPacket* enc_pkt;

            if ((enc_pkt = ffmpeg.av_packet_alloc()) == null)
            {
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            if ((ret = ffmpeg.avcodec_send_frame(avctx, frame)) < 0)
            {
                Console.WriteLine($"Error code: {FFmpegHelper.av_err2str(ret)}");
                goto end;
            }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(avctx, enc_pkt);
                if (ret != 0)
                {
                    break;
                }

                enc_pkt->stream_index = 0;
                ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(enc_pkt->data, enc_pkt->size);
                fs.Write(data);
                ffmpeg.av_packet_unref(enc_pkt);
            }

        end:
            ffmpeg.av_packet_free(&enc_pkt);

            if (ret == ffmpeg.AVERROR_EOF)
            {
                return ret;
            }

            ret = (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) ? 0 : -1;
            return ret;
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

            int size, err;
            AVFrame* sw_frame = null, hw_frame = null;
            AVCodecContext* avctx = null;
            AVCodec* codec = null;
#if USE_CUDA
            string enc_name = "h264_nvenc";
#else
            string enc_name = "h264_vaapi";
#endif
            AVBufferRef* hw_device_ctx = null;

            width = 1920;
            height = 1080;
            size = width * height;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";

            // You can get test.dat by running "hw_decode" sample
            string input_filename = Path.Combine(dirPath, "..", "..", "..", "hw_decode", "bin", "Debug", "test.dat");
            string output_filename = Path.Combine(dirPath, "test.h264");

            FileStream fin = File.OpenRead(input_filename);
            FileStream fout = File.OpenWrite(output_filename);

#if USE_CUDA
            err = ffmpeg.av_hwdevice_ctx_create(&hw_device_ctx, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0);
#else
            err = ffmpeg.av_hwdevice_ctx_create(&hw_device_ctx, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, null, null, 0);
#endif

            if (err < 0)
            {
#if USE_CUDA
                Console.WriteLine($"Failed to create a CUDA device. Error code: {FFmpegHelper.av_err2str(err)}");
#else
                Console.WriteLine($"Failed to create a VAAPI device. Error code: {FFmpegHelper.av_err2str(err)}");
#endif
                goto close;
            }

            if ((codec = ffmpeg.avcodec_find_encoder_by_name(enc_name)) == null)
            {
                Console.WriteLine("Could not find encoder.");
                err = -1;
                goto close;
            }

            if ((avctx = ffmpeg.avcodec_alloc_context3(codec)) == null)
            {
                err = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto close;
            }

            avctx->width = width;
            avctx->height = height;
            avctx->time_base = new AVRational() { num = 1, den = 25 };
            avctx->framerate = new AVRational() { num = 25, den = 1 };
            avctx->sample_aspect_ratio = new AVRational() { num = 1, den = 1 };

#if USE_CUDA
            avctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;
#else
            avctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
#endif

            if ((err = set_hwframe_ctx(avctx, hw_device_ctx)) < 0)
            {
                Console.WriteLine("Failed to set hwframe context.");
                goto close;
            }

            if ((err = ffmpeg.avcodec_open2(avctx, codec, null)) < 0)
            {
                Console.WriteLine($"Cannot open video encoder codec. Error code: {FFmpegHelper.av_err2str(err)}");
                goto close;
            }

            while (true)
            {
                if ((sw_frame = ffmpeg.av_frame_alloc()) == null)
                {
                    err = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto close;
                }

                sw_frame->width = width;
                sw_frame->height = height;
                sw_frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;

                if ((err = ffmpeg.av_frame_get_buffer(sw_frame, 0)) < 0)
                {
                    goto close;
                }

                Span<byte> data = new Span<byte>(sw_frame->data[0], size);
                if ((err = fin.Read(data)) <= 0)
                {
                    break;
                }

                data = new Span<byte>(sw_frame->data[1], size / 2);
                if ((err = fin.Read(data)) <= 0)
                {
                    break;
                }

                if ((hw_frame = ffmpeg.av_frame_alloc()) == null)
                {
                    err = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto close;
                }

                if ((err = ffmpeg.av_hwframe_get_buffer(avctx->hw_frames_ctx, hw_frame, 0)) < 0)
                {
                    Console.WriteLine($"Error code: {FFmpegHelper.av_err2str(err)}");
                    goto close;
                }

                if (hw_frame->hw_frames_ctx == null)
                {
                    err = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto close;
                }

                if ((err = ffmpeg.av_hwframe_transfer_data(hw_frame, sw_frame, 0)) < 0)
                {
                    Console.WriteLine($"Error while transferring frame data to surface. Error code: {FFmpegHelper.av_err2str(err)}");
                    goto close;
                }

                if ((err = (encode_write(avctx, hw_frame, fout))) < 0)
                {
                    Console.WriteLine("Failed to encode");
                    goto close;
                }

                ffmpeg.av_frame_free(&hw_frame);
                ffmpeg.av_frame_free(&sw_frame);
            }

            err = encode_write(avctx, null, fout);
            if (err == ffmpeg.AVERROR_EOF)
            {
                Console.WriteLine($"ffplay -autoexit -f h264 -framerate 25 {output_filename}");
                err = 0;
            }

            close:
            if (fin != null)
            {
                fin.Close();
            }

            if (fout != null)
            {
                fout.Close();
            }

            ffmpeg.av_frame_free(&sw_frame);
            ffmpeg.av_frame_free(&hw_frame);
            ffmpeg.avcodec_free_context(&avctx);
            ffmpeg.av_buffer_unref(&hw_device_ctx);

            return err;
        }
    }
}
