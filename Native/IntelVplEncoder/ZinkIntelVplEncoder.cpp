// Copyright (c) Zink.
// Intel oneVPL H.264 encoder bridge used for Intel/Quick Sync streaming.

#include <vpl/mfxvideo.h>
#include <vpl/mfxdispatcher.h>

#define ZINK_EXPORT extern "C" __declspec(dllexport)

extern "C" void* __cdecl malloc(unsigned long long size);
extern "C" void __cdecl free(void* block);
extern "C" void* __cdecl memset(void* destination, int value, unsigned long long size);
extern "C" void* __cdecl memcpy(void* destination, const void* source, unsigned long long size);

namespace
{
    constexpr mfxU32 BitstreamBufferSize = 8 * 1024 * 1024;
    constexpr unsigned short ApiMajorRequired = 2;
    constexpr unsigned short ApiMinorRequired = 2;

    enum : int
    {
        ZinkOk = 0,
        ZinkInvalidArgument = -1,
        ZinkLoadFailed = -2,
        ZinkSessionFailed = -3,
        ZinkInitFailed = -4,
        ZinkEncodeFailed = -5,
        ZinkBufferTooSmall = -6,
        ZinkSurfaceFailed = -7
    };

    struct Encoder
    {
        mfxLoader Loader = nullptr;
        mfxSession Session = nullptr;
        mfxBitstream Bitstream = {};
        int Width = 0;
        int Height = 0;
        int FrameRate = 60;
        mfxU64 FrameIndex = 0;
        bool ForceKeyFrame = true;
    };

    void DestroyEncoder(Encoder* encoder)
    {
        if (!encoder)
            return;

        if (encoder->Session)
        {
            MFXVideoENCODE_Close(encoder->Session);
            MFXClose(encoder->Session);
            encoder->Session = nullptr;
        }

        if (encoder->Bitstream.Data)
        {
            free(encoder->Bitstream.Data);
            encoder->Bitstream.Data = nullptr;
        }

        if (encoder->Loader)
        {
            MFXUnload(encoder->Loader);
            encoder->Loader = nullptr;
        }

        free(encoder);
    }

    unsigned short Align16(int value)
    {
        return static_cast<unsigned short>((value + 15) & ~15);
    }

    mfxU32 MinU32(mfxU32 left, mfxU32 right)
    {
        return left < right ? left : right;
    }

    int MaxInt(int left, int right)
    {
        return left > right ? left : right;
    }

    bool HasH264IdrFrame(const mfxU8* data, mfxU32 length)
    {
        if (!data || length < 4)
            return false;

        for (mfxU32 i = 0; i + 4 < length; ++i)
        {
            mfxU32 startCodeLength = 0;
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                startCodeLength = 3;
            }
            else if (i + 4 < length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                startCodeLength = 4;
            }

            if (startCodeLength == 0 || i + startCodeLength >= length)
                continue;

            const mfxU8 nalType = data[i + startCodeLength] & 0x1F;
            if (nalType == 5)
                return true;
        }

        return false;
    }

    void CopyNv12ToSurface(const mfxU8* nv12, int width, int height, mfxFrameSurface1* surface)
    {
        const mfxU8* srcY = nv12;
        const mfxU8* srcUV = nv12 + (width * height);

        mfxU8* dstY = surface->Data.Y;
        mfxU8* dstUV = surface->Data.UV;
        const int pitch = surface->Data.PitchLow ? surface->Data.PitchLow : width;

        for (int y = 0; y < height; ++y)
            memcpy(dstY + y * pitch, srcY + y * width, width);

        for (int y = 0; y < height / 2; ++y)
            memcpy(dstUV + y * pitch, srcUV + y * width, width);
    }
}

ZINK_EXPORT int ZinkIntelVpl_CreateEncoder(
    int width,
    int height,
    int frameRate,
    int bitrate,
    void** handle)
{
    if (!handle || width <= 0 || height <= 0 || frameRate <= 0 || bitrate <= 0)
        return ZinkInvalidArgument;

    *handle = nullptr;
    Encoder* encoder = static_cast<Encoder*>(malloc(sizeof(Encoder)));
    if (!encoder)
        return ZinkInitFailed;
    memset(encoder, 0, sizeof(Encoder));
    encoder->FrameRate = 60;
    encoder->ForceKeyFrame = true;

    encoder->Width = width;
    encoder->Height = height;
    encoder->FrameRate = frameRate;

    encoder->Loader = MFXLoad();
    if (!encoder->Loader)
    {
        DestroyEncoder(encoder);
        return ZinkLoadFailed;
    }

    mfxVariant variant = {};
    mfxConfig implConfig = MFXCreateConfig(encoder->Loader);
    variant.Type = MFX_VARIANT_TYPE_U32;
    variant.Data.U32 = MFX_IMPL_TYPE_HARDWARE;
    if (!implConfig ||
        MFXSetConfigFilterProperty(implConfig, (mfxU8*)"mfxImplDescription.Impl", variant) != MFX_ERR_NONE)
    {
        DestroyEncoder(encoder);
        return ZinkSessionFailed;
    }

    mfxConfig codecConfig = MFXCreateConfig(encoder->Loader);
    variant = {};
    variant.Type = MFX_VARIANT_TYPE_U32;
    variant.Data.U32 = MFX_CODEC_AVC;
    if (!codecConfig ||
        MFXSetConfigFilterProperty(codecConfig, (mfxU8*)"mfxImplDescription.mfxEncoderDescription.encoder.CodecID", variant) != MFX_ERR_NONE)
    {
        DestroyEncoder(encoder);
        return ZinkSessionFailed;
    }

    mfxConfig apiConfig = MFXCreateConfig(encoder->Loader);
    variant = {};
    variant.Type = MFX_VARIANT_TYPE_U32;
    variant.Data.U32 = (static_cast<mfxU32>(ApiMajorRequired) << 16) | static_cast<mfxU32>(ApiMinorRequired);
    if (!apiConfig ||
        MFXSetConfigFilterProperty(apiConfig, (mfxU8*)"mfxImplDescription.ApiVersion.Version", variant) != MFX_ERR_NONE)
    {
        DestroyEncoder(encoder);
        return ZinkSessionFailed;
    }

    if (MFXCreateSession(encoder->Loader, 0, &encoder->Session) != MFX_ERR_NONE)
    {
        DestroyEncoder(encoder);
        return ZinkSessionFailed;
    }

    mfxVideoParam params = {};
    params.mfx.CodecId = MFX_CODEC_AVC;
    params.mfx.TargetUsage = MFX_TARGETUSAGE_BEST_SPEED;
    params.mfx.TargetKbps = static_cast<mfxU16>(MaxInt(1, bitrate / 1000));
    params.mfx.MaxKbps = params.mfx.TargetKbps;
    params.mfx.RateControlMethod = MFX_RATECONTROL_CBR;
    params.mfx.GopPicSize = static_cast<mfxU16>(MaxInt(1, frameRate * 2));
    params.mfx.GopRefDist = 1;
    params.mfx.IdrInterval = 1;
    params.mfx.NumSlice = 1;
    params.mfx.FrameInfo.FrameRateExtN = static_cast<mfxU32>(frameRate);
    params.mfx.FrameInfo.FrameRateExtD = 1;
    params.mfx.FrameInfo.FourCC = MFX_FOURCC_NV12;
    params.mfx.FrameInfo.ChromaFormat = MFX_CHROMAFORMAT_YUV420;
    params.mfx.FrameInfo.PicStruct = MFX_PICSTRUCT_PROGRESSIVE;
    params.mfx.FrameInfo.CropW = static_cast<mfxU16>(width);
    params.mfx.FrameInfo.CropH = static_cast<mfxU16>(height);
    params.mfx.FrameInfo.Width = Align16(width);
    params.mfx.FrameInfo.Height = Align16(height);
    params.IOPattern = MFX_IOPATTERN_IN_SYSTEM_MEMORY;

    mfxExtCodingOption codingOption = {};
    codingOption.Header.BufferId = MFX_EXTBUFF_CODING_OPTION;
    codingOption.Header.BufferSz = sizeof(codingOption);
    codingOption.CAVLC = MFX_CODINGOPTION_OFF;
    codingOption.EndOfStream = MFX_CODINGOPTION_ON;
    codingOption.PicTimingSEI = MFX_CODINGOPTION_OFF;

    mfxExtCodingOption2 codingOption2 = {};
    codingOption2.Header.BufferId = MFX_EXTBUFF_CODING_OPTION2;
    codingOption2.Header.BufferSz = sizeof(codingOption2);
    codingOption2.RepeatPPS = MFX_CODINGOPTION_ON;
    codingOption2.ExtBRC = MFX_CODINGOPTION_OFF;

    mfxExtBuffer* extBuffers[2] = {
        reinterpret_cast<mfxExtBuffer*>(&codingOption),
        reinterpret_cast<mfxExtBuffer*>(&codingOption2)
    };
    params.NumExtParam = 2;
    params.ExtParam = extBuffers;

    mfxStatus initStatus = MFXVideoENCODE_Init(encoder->Session, &params);
    if (initStatus < MFX_ERR_NONE)
    {
        DestroyEncoder(encoder);
        return ZinkInitFailed;
    }

    encoder->Bitstream.MaxLength = BitstreamBufferSize;
    encoder->Bitstream.Data = static_cast<mfxU8*>(malloc(encoder->Bitstream.MaxLength));
    if (!encoder->Bitstream.Data)
    {
        DestroyEncoder(encoder);
        return ZinkInitFailed;
    }
    memset(encoder->Bitstream.Data, 0, encoder->Bitstream.MaxLength);

    *handle = encoder;
    return ZinkOk;
}

ZINK_EXPORT int ZinkIntelVpl_EncodeNv12(
    void* handle,
    const mfxU8* nv12,
    int nv12Length,
    mfxI64 timestamp90k,
    mfxU8* output,
    int outputCapacity,
    int* outputLength,
    int* isKeyFrame)
{
    if (!handle || !nv12 || !output || !outputLength || !isKeyFrame)
        return ZinkInvalidArgument;

    Encoder* encoder = static_cast<Encoder*>(handle);

    *outputLength = 0;
    *isKeyFrame = 0;

    const int expectedLength = encoder->Width * encoder->Height * 3 / 2;
    if (nv12Length < expectedLength)
        return ZinkInvalidArgument;

    mfxFrameSurface1* surface = nullptr;
    mfxStatus status = MFXMemory_GetSurfaceForEncode(encoder->Session, &surface);
    if (status < MFX_ERR_NONE || !surface)
        return ZinkSurfaceFailed;

    if (!surface->FrameInterface || !surface->FrameInterface->Map || !surface->FrameInterface->Unmap)
    {
        if (surface->FrameInterface && surface->FrameInterface->Release)
            surface->FrameInterface->Release(surface);
        return ZinkSurfaceFailed;
    }

    status = surface->FrameInterface->Map(surface, MFX_MAP_WRITE);
    if (status < MFX_ERR_NONE)
    {
        surface->FrameInterface->Release(surface);
        return ZinkSurfaceFailed;
    }

    CopyNv12ToSurface(nv12, encoder->Width, encoder->Height, surface);
    surface->Data.TimeStamp = static_cast<mfxU64>(timestamp90k < 0 ? 0 : timestamp90k);
    surface->Data.FrameOrder = static_cast<mfxU32>(encoder->FrameIndex++);

    status = surface->FrameInterface->Unmap(surface);
    if (status < MFX_ERR_NONE)
    {
        surface->FrameInterface->Release(surface);
        return ZinkSurfaceFailed;
    }

    mfxEncodeCtrl ctrl = {};
    if (encoder->ForceKeyFrame)
    {
        ctrl.FrameType = MFX_FRAMETYPE_I | MFX_FRAMETYPE_IDR | MFX_FRAMETYPE_REF;
        encoder->ForceKeyFrame = false;
    }

    mfxSyncPoint syncPoint = {};
    status = MFXVideoENCODE_EncodeFrameAsync(encoder->Session, &ctrl, surface, &encoder->Bitstream, &syncPoint);

    if (surface->FrameInterface && surface->FrameInterface->Release)
        surface->FrameInterface->Release(surface);

    if (status == MFX_WRN_DEVICE_BUSY || status == MFX_ERR_MORE_DATA)
        return ZinkOk;

    if (status < MFX_ERR_NONE)
        return ZinkEncodeFailed;

    if (!syncPoint)
        return ZinkOk;

    status = MFXVideoCORE_SyncOperation(encoder->Session, syncPoint, 100);
    if (status < MFX_ERR_NONE)
        return ZinkEncodeFailed;

    const mfxU8* data = encoder->Bitstream.Data + encoder->Bitstream.DataOffset;
    const mfxU32 length = encoder->Bitstream.DataLength;
    if (length > 0)
    {
        if (length > static_cast<mfxU32>(outputCapacity))
            return ZinkBufferTooSmall;

        memcpy(output, data, length);
        *outputLength = static_cast<int>(length);
        *isKeyFrame = HasH264IdrFrame(data, length) ? 1 : 0;
    }

    encoder->Bitstream.DataOffset = 0;
    encoder->Bitstream.DataLength = 0;
    return ZinkOk;
}

ZINK_EXPORT int ZinkIntelVpl_ForceKeyFrame(void* handle)
{
    if (!handle)
        return ZinkInvalidArgument;

    Encoder* encoder = static_cast<Encoder*>(handle);
    encoder->ForceKeyFrame = true;
    return ZinkOk;
}

ZINK_EXPORT void ZinkIntelVpl_DestroyEncoder(void* handle)
{
    Encoder* encoder = static_cast<Encoder*>(handle);
    DestroyEncoder(encoder);
}
