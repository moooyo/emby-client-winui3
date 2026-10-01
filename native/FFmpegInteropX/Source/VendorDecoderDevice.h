#pragma once

#include "EmbyDecoderApi.h"

#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cerrno>
#include <cstdint>
#include <cstring>
#include <cwchar>

extern "C"
{
#include <libavutil/buffer.h>
#include <libavutil/dict.h>
#include <libavutil/error.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
}

// FFmpeg only needs these opaque handles here; the CUDA SDK is not required.
#pragma push_macro("CUDA_VERSION")
#ifndef CUDA_VERSION
typedef struct CUctx_st* CUcontext;
typedef struct CUstream_st* CUstream;
#define CUDA_VERSION 1
#endif
extern "C"
{
#include <libavutil/hwcontext_cuda.h>
}
#pragma pop_macro("CUDA_VERSION")

struct VendorDecoderDevice
{
    AVBufferRef* Context = nullptr;
    uint32_t VendorId = 0;
    int64_t AdapterLuid = 0;
    wchar_t GpuName[128]{};

    VendorDecoderDevice() = default;
    VendorDecoderDevice(const VendorDecoderDevice&) = delete;
    VendorDecoderDevice& operator=(const VendorDecoderDevice&) = delete;

    ~VendorDecoderDevice()
    {
        av_buffer_unref(&Context);
    }
};

namespace EmbyVendorDecoderDeviceDetail
{
    inline void Reset(VendorDecoderDevice& device)
    {
        av_buffer_unref(&device.Context);
        device.VendorId = 0;
        device.AdapterLuid = 0;
        std::wmemset(device.GpuName, 0, 128);
    }

    inline int ReadD3D11Adapter(AVBufferRef* context, uint32_t expectedVendorId,
        VendorDecoderDevice& result)
    {
        if (!context || !context->data)
        {
            return AVERROR(EINVAL);
        }

        auto deviceContext = reinterpret_cast<AVHWDeviceContext*>(context->data);
        if (deviceContext->type != AV_HWDEVICE_TYPE_D3D11VA || !deviceContext->hwctx)
        {
            return AVERROR(EINVAL);
        }

        auto d3d11Context = static_cast<AVD3D11VADeviceContext*>(deviceContext->hwctx);
        if (!d3d11Context->device)
        {
            return AVERROR(ENODEV);
        }

        IDXGIDevice* dxgiDevice = nullptr;
        HRESULT hr = d3d11Context->device->QueryInterface(__uuidof(IDXGIDevice),
            reinterpret_cast<void**>(&dxgiDevice));
        if (FAILED(hr) || !dxgiDevice)
        {
            if (dxgiDevice)
            {
                dxgiDevice->Release();
            }
            return AVERROR_EXTERNAL;
        }

        IDXGIAdapter* adapter = nullptr;
        hr = dxgiDevice->GetAdapter(&adapter);
        dxgiDevice->Release();
        if (FAILED(hr) || !adapter)
        {
            if (adapter)
            {
                adapter->Release();
            }
            return AVERROR_EXTERNAL;
        }

        DXGI_ADAPTER_DESC description{};
        hr = adapter->GetDesc(&description);
        adapter->Release();
        if (FAILED(hr))
        {
            return AVERROR_EXTERNAL;
        }
        if (description.VendorId != expectedVendorId)
        {
            return AVERROR(ENODEV);
        }

        static_assert(sizeof(description.AdapterLuid) == sizeof(result.AdapterLuid));
        result.VendorId = description.VendorId;
        std::memcpy(&result.AdapterLuid, &description.AdapterLuid, sizeof(result.AdapterLuid));
        wcsncpy_s(result.GpuName, 128, description.Description, _TRUNCATE);
        return 0;
    }

    inline int CreateD3D11DerivedDevice(AVHWDeviceType type, uint32_t vendorId,
        const char* vendorOption, VendorDecoderDevice& result)
    {
        VendorDecoderDevice child;
        AVDictionary* options = nullptr;
        int status = av_dict_set(&options, "vendor_id", vendorOption, 0);
        if (status >= 0)
        {
            status = av_hwdevice_ctx_create(&child.Context, AV_HWDEVICE_TYPE_D3D11VA,
                nullptr, options, 0);
        }
        av_dict_free(&options);
        if (status < 0)
        {
            return status;
        }

        status = ReadD3D11Adapter(child.Context, vendorId, result);
        if (status < 0)
        {
            return status;
        }

        // The derived context retains the child device after this local reference is released.
        return av_hwdevice_ctx_create_derived(&result.Context, type, child.Context, 0);
    }

    struct ScopedCudaDriverModule
    {
        HMODULE Handle = LoadLibraryExW(L"nvcuda.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);

        ScopedCudaDriverModule() = default;
        ScopedCudaDriverModule(const ScopedCudaDriverModule&) = delete;
        ScopedCudaDriverModule& operator=(const ScopedCudaDriverModule&) = delete;

        ~ScopedCudaDriverModule()
        {
            if (Handle)
            {
                FreeLibrary(Handle);
            }
        }
    };

    inline int ReadCudaAdapter(AVBufferRef* context, VendorDecoderDevice& result)
    {
        if (!context || !context->data)
        {
            return AVERROR(EINVAL);
        }

        auto deviceContext = reinterpret_cast<AVHWDeviceContext*>(context->data);
        if (deviceContext->type != AV_HWDEVICE_TYPE_CUDA || !deviceContext->hwctx)
        {
            return AVERROR(EINVAL);
        }

        auto cudaContext = static_cast<AVCUDADeviceContext*>(deviceContext->hwctx);
        if (!cudaContext->cuda_ctx)
        {
            return AVERROR(ENODEV);
        }

        ScopedCudaDriverModule driver;
        if (!driver.Handle)
        {
            return AVERROR(ENODEV);
        }

        using CudaPushCurrent = int(WINAPI*)(void*);
        using CudaPopCurrent = int(WINAPI*)(void**);
        using CudaGetDevice = int(WINAPI*)(int*);
        using CudaGetDeviceName = int(WINAPI*)(char*, int, int);
        using CudaGetDeviceLuid = int(WINAPI*)(char*, unsigned int*, int);

        auto pushCurrent = reinterpret_cast<CudaPushCurrent>(GetProcAddress(driver.Handle, "cuCtxPushCurrent_v2"));
        auto popCurrent = reinterpret_cast<CudaPopCurrent>(GetProcAddress(driver.Handle, "cuCtxPopCurrent_v2"));
        auto getDevice = reinterpret_cast<CudaGetDevice>(GetProcAddress(driver.Handle, "cuCtxGetDevice"));
        auto getDeviceName = reinterpret_cast<CudaGetDeviceName>(GetProcAddress(driver.Handle, "cuDeviceGetName"));
        auto getDeviceLuid = reinterpret_cast<CudaGetDeviceLuid>(GetProcAddress(driver.Handle, "cuDeviceGetLuid"));
        if (!pushCurrent || !popCurrent || !getDevice || !getDeviceName)
        {
            return AVERROR(ENOSYS);
        }

        if (pushCurrent(cudaContext->cuda_ctx) != 0)
        {
            return AVERROR_EXTERNAL;
        }

        int cudaDevice = 0;
        char gpuName[256]{};
        char luid[8]{};
        unsigned int nodeMask = 0;
        bool hasLuid = false;
        int status = getDevice(&cudaDevice);
        if (status == 0)
        {
            status = getDeviceName(gpuName, static_cast<int>(sizeof(gpuName)), cudaDevice);
            gpuName[sizeof(gpuName) - 1] = '\0';
        }
        if (status == 0 && getDeviceLuid)
        {
            // A CUDA device using TCC may not expose a Windows adapter LUID.
            hasLuid = getDeviceLuid(luid, &nodeMask, cudaDevice) == 0;
        }

        // Always restore the caller's current context, including failed metadata queries.
        void* poppedContext = nullptr;
        int popStatus = popCurrent(&poppedContext);
        if (status != 0 || popStatus != 0)
        {
            return AVERROR_EXTERNAL;
        }

        wchar_t wideName[256]{};
        if (MultiByteToWideChar(CP_UTF8, 0, gpuName, -1, wideName, 256) == 0)
        {
            return AVERROR_EXTERNAL;
        }

        result.VendorId = 0x10DE;
        if (hasLuid)
        {
            static_assert(sizeof(luid) == sizeof(result.AdapterLuid));
            std::memcpy(&result.AdapterLuid, luid, sizeof(result.AdapterLuid));
        }
        wcsncpy_s(result.GpuName, 128, wideName, _TRUNCATE);
        return 0;
    }
}

// Replaces result on success and clears it on failure. The returned context is owned by result.
inline int CreateVendorDecoderDevice(EmbyDecoderApi api, VendorDecoderDevice& result)
{
    EmbyVendorDecoderDeviceDetail::Reset(result);
    VendorDecoderDevice candidate;
    int status = AVERROR(EINVAL);
    switch (api)
    {
    case EmbyDecoderApi::IntelQsv:
        status = EmbyVendorDecoderDeviceDetail::CreateD3D11DerivedDevice(
            AV_HWDEVICE_TYPE_QSV, 0x8086, "0x8086", candidate);
        break;
    case EmbyDecoderApi::AmdAmf:
        status = EmbyVendorDecoderDeviceDetail::CreateD3D11DerivedDevice(
            AV_HWDEVICE_TYPE_AMF, 0x1002, "0x1002", candidate);
        break;
    case EmbyDecoderApi::NvidiaNvdec:
        status = av_hwdevice_ctx_create(&candidate.Context, AV_HWDEVICE_TYPE_CUDA, "0", nullptr, 0);
        if (status >= 0)
        {
            status = EmbyVendorDecoderDeviceDetail::ReadCudaAdapter(candidate.Context, candidate);
        }
        break;
    default:
        break;
    }

    if (status < 0)
    {
        return status;
    }
    if (!candidate.Context)
    {
        return AVERROR_EXTERNAL;
    }

    result.Context = candidate.Context;
    candidate.Context = nullptr;
    result.VendorId = candidate.VendorId;
    result.AdapterLuid = candidate.AdapterLuid;
    wcsncpy_s(result.GpuName, 128, candidate.GpuName, _TRUNCATE);
    return 0;
}
