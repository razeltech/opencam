#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dshow.h>
#include <initguid.h>
#include <olectl.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <new>

#include <math.h>
#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "uuid.lib")

// {8E3A9B12-4F7D-4C21-9A6E-3D5C1B2A7F01}
DEFINE_GUID(CLSID_OpenCamVirtualCam,
    0x8e3a9b12, 0x4f7d, 0x4c21, 0x9a, 0x6e, 0x3d, 0x5c, 0x1b, 0x2a, 0x7f, 0x01);

static const wchar_t* kFilterName = L"OpenCam";
static const wchar_t* kSharedMemName = L"Local\\OpenCamVirtualCamSharedFrame";
static const wchar_t* kSharedMutexName = L"Local\\OpenCamVirtualCamSharedMutex";
static const uint32_t kSharedMagic = 0x4F43414D; // "OCAM"
static const uint32_t kMaxFrameWidth = 1920;
static const uint32_t kMaxFrameHeight = 1080;
static const uint32_t kMaxPixelBytes = kMaxFrameWidth * kMaxFrameHeight * 3;

#pragma pack(push, 1)
struct SharedFrameHeader {
    uint32_t magic;
    uint32_t sequence;
    uint32_t width;
    uint32_t height;
    uint32_t active;
    uint8_t  bgr24[1];
};
#pragma pack(pop)

static LONG g_dllRefCount = 0;
static HMODULE g_hModule = nullptr;

static void DllAddRef() { InterlockedIncrement(&g_dllRefCount); }
static void DllRelease() { InterlockedDecrement(&g_dllRefCount); }

static HRESULT CopyMediaType(AM_MEDIA_TYPE* pDst, const AM_MEDIA_TYPE* pSrc) {
    if (!pDst || !pSrc) return E_POINTER;
    *pDst = *pSrc;
    if (pSrc->cbFormat && pSrc->pbFormat) {
        pDst->pbFormat = (BYTE*)CoTaskMemAlloc(pSrc->cbFormat);
        if (!pDst->pbFormat) return E_OUTOFMEMORY;
        memcpy(pDst->pbFormat, pSrc->pbFormat, pSrc->cbFormat);
    } else {
        pDst->pbFormat = nullptr;
        pDst->cbFormat = 0;
    }
    if (pDst->pUnk) pDst->pUnk->AddRef();
    return S_OK;
}

static void FreeMediaType(AM_MEDIA_TYPE& mt) {
    if (mt.cbFormat != 0 && mt.pbFormat) {
        CoTaskMemFree(mt.pbFormat);
        mt.cbFormat = 0;
        mt.pbFormat = nullptr;
    }
    if (mt.pUnk) {
        mt.pUnk->Release();
        mt.pUnk = nullptr;
    }
}

static HRESULT BuildVideoMediaType(AM_MEDIA_TYPE* pmt, int width, int height, int fps) {
    ZeroMemory(pmt, sizeof(AM_MEDIA_TYPE));
    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!pvi) return E_OUTOFMEMORY;
    ZeroMemory(pvi, sizeof(VIDEOINFOHEADER));

    DWORD imageSize = (DWORD)(width * height * 3);
    pvi->AvgTimePerFrame = 10000000LL / (fps > 0 ? fps : 30);
    pvi->dwBitRate = imageSize * 8 * (DWORD)(fps > 0 ? fps : 30);
    pvi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pvi->bmiHeader.biWidth = width;
    pvi->bmiHeader.biHeight = height; // positive = bottom-up RGB24
    pvi->bmiHeader.biPlanes = 1;
    pvi->bmiHeader.biBitCount = 24;
    pvi->bmiHeader.biCompression = BI_RGB;
    pvi->bmiHeader.biSizeImage = imageSize;

    pmt->majortype = MEDIATYPE_Video;
    pmt->subtype = MEDIASUBTYPE_RGB24;
    pmt->bFixedSizeSamples = TRUE;
    pmt->bTemporalCompression = FALSE;
    pmt->lSampleSize = imageSize;
    pmt->formattype = FORMAT_VideoInfo;
    pmt->cbFormat = sizeof(VIDEOINFOHEADER);
    pmt->pbFormat = (BYTE*)pvi;
    return S_OK;
}

class OpenCamFilter;

class OpenCamPin : public IPin, public IAMStreamConfig, public IKsPropertySet {
public:
    OpenCamPin(OpenCamFilter* pFilter);
    virtual ~OpenCamPin();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IPin
    STDMETHODIMP Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt) override { return E_UNEXPECTED; }
    STDMETHODIMP Disconnect() override;
    STDMETHODIMP ConnectedTo(IPin** pPin) override;
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP QueryPinInfo(PIN_INFO* pInfo) override;
    STDMETHODIMP QueryDirection(PIN_DIRECTION* pPinDir) override;
    STDMETHODIMP QueryId(LPWSTR* Id) override;
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** ppEnum) override;
    STDMETHODIMP QueryInternalConnections(IPin** apPin, ULONG* nPin) override { return E_NOTIMPL; }
    STDMETHODIMP EndOfStream() override { return S_OK; }
    STDMETHODIMP BeginFlush() override { return S_OK; }
    STDMETHODIMP EndFlush() override { return S_OK; }
    STDMETHODIMP NewSegment(REFERENCE_TIME tStart, REFERENCE_TIME tStop, double dRate) override { return S_OK; }

    // IAMStreamConfig
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** ppmt) override;
    STDMETHODIMP GetNumberOfCapabilities(int* piCount, int* piSize) override;
    STDMETHODIMP GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) override;

    // IKsPropertySet
    STDMETHODIMP Set(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData, DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData) override { return E_NOTIMPL; }
    STDMETHODIMP Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData, DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData, DWORD* pcbReturned) override;
    STDMETHODIMP QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport) override;

    HRESULT StartStreaming();
    HRESULT StopStreaming();

private:
    static DWORD WINAPI StreamThreadProc(LPVOID param);
    void RunStreamLoop();
    void FillFrame(BYTE* dst, int dstW, int dstH, uint64_t tick);

    OpenCamFilter* m_pFilter;
    IPin* m_pConnectedPin = nullptr;
    IMemInputPin* m_pMemInputPin = nullptr;
    IMemAllocator* m_pAllocator = nullptr;
    AM_MEDIA_TYPE m_mt{};
    int m_width = 1280;
    int m_height = 720;
    int m_fps = 30;
    HANDLE m_hThread = nullptr;
    HANDLE m_hStopEvent = nullptr;
    CRITICAL_SECTION m_cs;
    uint32_t m_lastSeq = 0;
    ULONGLONG m_lastSeqTick = 0;
};

class OpenCamEnumMediaTypes : public IEnumMediaTypes {
public:
    OpenCamEnumMediaTypes(int width, int height, int fps) : m_ref(1), m_index(0), m_w(width), m_h(height), m_fps(fps) { DllAddRef(); }
    virtual ~OpenCamEnumMediaTypes() { DllRelease(); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IEnumMediaTypes) { *ppv = static_cast<IEnumMediaTypes*>(this); AddRef(); return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override {
        ULONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }
    STDMETHODIMP Next(ULONG cMediaTypes, AM_MEDIA_TYPE** ppMediaTypes, ULONG* pcFetched) override {
        if (!ppMediaTypes) return E_POINTER;
        ULONG fetched = 0;
        while (fetched < cMediaTypes && m_index < 3) {
            int w = m_w, h = m_h;
            if (m_index == 1) { w = 1920; h = 1080; }
            else if (m_index == 2) { w = 854; h = 480; }
            AM_MEDIA_TYPE* pmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
            if (!pmt) return E_OUTOFMEMORY;
            if (FAILED(BuildVideoMediaType(pmt, w, h, m_fps))) { CoTaskMemFree(pmt); return E_OUTOFMEMORY; }
            ppMediaTypes[fetched++] = pmt;
            m_index++;
        }
        if (pcFetched) *pcFetched = fetched;
        return fetched == cMediaTypes ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG cMediaTypes) override { m_index += (int)cMediaTypes; return m_index <= 3 ? S_OK : S_FALSE; }
    STDMETHODIMP Reset() override { m_index = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumMediaTypes** ppEnum) override {
        if (!ppEnum) return E_POINTER;
        *ppEnum = new (std::nothrow) OpenCamEnumMediaTypes(m_w, m_h, m_fps);
        return *ppEnum ? S_OK : E_OUTOFMEMORY;
    }

private:
    LONG m_ref;
    int m_index;
    int m_w, m_h, m_fps;
};

class OpenCamEnumPins : public IEnumPins {
public:
    OpenCamEnumPins(IPin* pPin) : m_ref(1), m_index(0), m_pPin(pPin) { m_pPin->AddRef(); DllAddRef(); }
    virtual ~OpenCamEnumPins() { m_pPin->Release(); DllRelease(); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IEnumPins) { *ppv = static_cast<IEnumPins*>(this); AddRef(); return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override {
        ULONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }
    STDMETHODIMP Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched) override {
        if (!ppPins) return E_POINTER;
        ULONG fetched = 0;
        if (m_index == 0 && cPins > 0) {
            ppPins[0] = m_pPin;
            m_pPin->AddRef();
            fetched = 1;
            m_index = 1;
        }
        if (pcFetched) *pcFetched = fetched;
        return fetched == cPins ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG cPins) override { m_index += (int)cPins; return m_index <= 1 ? S_OK : S_FALSE; }
    STDMETHODIMP Reset() override { m_index = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumPins** ppEnum) override {
        if (!ppEnum) return E_POINTER;
        *ppEnum = new (std::nothrow) OpenCamEnumPins(m_pPin);
        return *ppEnum ? S_OK : E_OUTOFMEMORY;
    }

private:
    LONG m_ref;
    int m_index;
    IPin* m_pPin;
};

class OpenCamFilter : public IBaseFilter, public IAMFilterMiscFlags {
public:
    OpenCamFilter() : m_ref(1) {
        DllAddRef();
        InitializeCriticalSection(&m_cs);
        m_pPin = new OpenCamPin(this);
    }
    virtual ~OpenCamFilter() {
        delete m_pPin;
        if (m_pClock) m_pClock->Release();
        DeleteCriticalSection(&m_cs);
        DllRelease();
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IPersist || riid == IID_IMediaFilter || riid == IID_IBaseFilter) {
            *ppv = static_cast<IBaseFilter*>(this);
            AddRef();
            return S_OK;
        }
        if (riid == IID_IAMFilterMiscFlags) {
            *ppv = static_cast<IAMFilterMiscFlags*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override {
        ULONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }

    // IPersist
    STDMETHODIMP GetClassID(CLSID* pClsID) override {
        if (!pClsID) return E_POINTER;
        *pClsID = CLSID_OpenCamVirtualCam;
        return S_OK;
    }

    // IMediaFilter
    STDMETHODIMP Stop() override {
        EnterCriticalSection(&m_cs);
        m_state = State_Stopped;
        m_pPin->StopStreaming();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP Pause() override {
        EnterCriticalSection(&m_cs);
        m_state = State_Paused;
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP Run(REFERENCE_TIME tStart) override {
        EnterCriticalSection(&m_cs);
        m_state = State_Running;
        m_pPin->StartStreaming();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State) override {
        if (!State) return E_POINTER;
        *State = m_state;
        return S_OK;
    }
    STDMETHODIMP SetSyncSource(IReferenceClock* pClock) override {
        EnterCriticalSection(&m_cs);
        if (m_pClock) m_pClock->Release();
        m_pClock = pClock;
        if (m_pClock) m_pClock->AddRef();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP GetSyncSource(IReferenceClock** pClock) override {
        if (!pClock) return E_POINTER;
        EnterCriticalSection(&m_cs);
        *pClock = m_pClock;
        if (m_pClock) m_pClock->AddRef();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }

    // IBaseFilter
    STDMETHODIMP EnumPins(IEnumPins** ppEnum) override {
        if (!ppEnum) return E_POINTER;
        *ppEnum = new (std::nothrow) OpenCamEnumPins(m_pPin);
        return *ppEnum ? S_OK : E_OUTOFMEMORY;
    }
    STDMETHODIMP FindPin(LPCWSTR Id, IPin** ppPin) override {
        if (!ppPin) return E_POINTER;
        if (Id && wcscmp(Id, L"Capture") == 0) {
            *ppPin = m_pPin;
            m_pPin->AddRef();
            return S_OK;
        }
        *ppPin = nullptr;
        return VFW_E_NOT_FOUND;
    }
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* pInfo) override {
        if (!pInfo) return E_POINTER;
        wcsncpy_s(pInfo->achName, kFilterName, _TRUNCATE);
        pInfo->pGraph = m_pGraph;
        if (m_pGraph) m_pGraph->AddRef();
        return S_OK;
    }
    STDMETHODIMP JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName) override {
        EnterCriticalSection(&m_cs);
        m_pGraph = pGraph;
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP QueryVendorInfo(LPWSTR* pVendorInfo) override { return E_NOTIMPL; }

    // IAMFilterMiscFlags
    STDMETHODIMP_(ULONG) GetMiscFlags() override { return AM_FILTER_MISC_FLAGS_IS_SOURCE; }

private:
    LONG m_ref;
    CRITICAL_SECTION m_cs;
    FILTER_STATE m_state = State_Stopped;
    IFilterGraph* m_pGraph = nullptr;
    IReferenceClock* m_pClock = nullptr;
    OpenCamPin* m_pPin = nullptr;
};

OpenCamPin::OpenCamPin(OpenCamFilter* pFilter) : m_pFilter(pFilter) {
    InitializeCriticalSection(&m_cs);
    BuildVideoMediaType(&m_mt, m_width, m_height, m_fps);
}

OpenCamPin::~OpenCamPin() {
    StopStreaming();
    Disconnect();
    FreeMediaType(m_mt);
    DeleteCriticalSection(&m_cs);
}

STDMETHODIMP OpenCamPin::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IPin) {
        *ppv = static_cast<IPin*>(this); AddRef(); return S_OK;
    }
    if (riid == IID_IAMStreamConfig) {
        *ppv = static_cast<IAMStreamConfig*>(this); AddRef(); return S_OK;
    }
    if (riid == IID_IKsPropertySet) {
        *ppv = static_cast<IKsPropertySet*>(this); AddRef(); return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}
STDMETHODIMP_(ULONG) OpenCamPin::AddRef() { return m_pFilter->AddRef(); }
STDMETHODIMP_(ULONG) OpenCamPin::Release() { return m_pFilter->Release(); }

STDMETHODIMP OpenCamPin::Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt) {
    if (!pReceivePin) return E_POINTER;
    EnterCriticalSection(&m_cs);
    if (m_pConnectedPin) { LeaveCriticalSection(&m_cs); return VFW_E_ALREADY_CONNECTED; }

    AM_MEDIA_TYPE mtUse{};
    if (pmt) {
        if (FAILED(QueryAccept(pmt))) { LeaveCriticalSection(&m_cs); return VFW_E_TYPE_NOT_ACCEPTED; }
        CopyMediaType(&mtUse, pmt);
    } else {
        CopyMediaType(&mtUse, &m_mt);
    }

    HRESULT hr = pReceivePin->ReceiveConnection(this, &mtUse);
    if (FAILED(hr)) {
        FreeMediaType(mtUse);
        LeaveCriticalSection(&m_cs);
        return hr;
    }

    IMemInputPin* pMemInput = nullptr;
    hr = pReceivePin->QueryInterface(IID_IMemInputPin, (void**)&pMemInput);
    if (FAILED(hr)) {
        pReceivePin->Disconnect();
        FreeMediaType(mtUse);
        LeaveCriticalSection(&m_cs);
        return hr;
    }

    IMemAllocator* pAlloc = nullptr;
    pMemInput->GetAllocator(&pAlloc);
    if (!pAlloc) {
        CoCreateInstance(CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER, IID_IMemAllocator, (void**)&pAlloc);
    }
    if (pAlloc) {
        ALLOCATOR_PROPERTIES req{}, actual{};
        req.cBuffers = 3;
        req.cbBuffer = (long)mtUse.lSampleSize;
        req.cbAlign = 1;
        req.cbPrefix = 0;
        pAlloc->SetProperties(&req, &actual);
        pMemInput->NotifyAllocator(pAlloc, FALSE);
    }

    FreeMediaType(m_mt);
    m_mt = mtUse;
    if (m_mt.formattype == FORMAT_VideoInfo && m_mt.pbFormat) {
        VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)m_mt.pbFormat;
        m_width = pvi->bmiHeader.biWidth;
        m_height = abs(pvi->bmiHeader.biHeight);
        if (pvi->AvgTimePerFrame > 0) m_fps = (int)(10000000LL / pvi->AvgTimePerFrame);
    }

    m_pConnectedPin = pReceivePin;
    m_pConnectedPin->AddRef();
    m_pMemInputPin = pMemInput;
    m_pAllocator = pAlloc;

    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP OpenCamPin::Disconnect() {
    EnterCriticalSection(&m_cs);
    if (m_pAllocator) { m_pAllocator->Decommit(); m_pAllocator->Release(); m_pAllocator = nullptr; }
    if (m_pMemInputPin) { m_pMemInputPin->Release(); m_pMemInputPin = nullptr; }
    if (m_pConnectedPin) { m_pConnectedPin->Release(); m_pConnectedPin = nullptr; }
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP OpenCamPin::ConnectedTo(IPin** pPin) {
    if (!pPin) return E_POINTER;
    EnterCriticalSection(&m_cs);
    *pPin = m_pConnectedPin;
    if (m_pConnectedPin) m_pConnectedPin->AddRef();
    LeaveCriticalSection(&m_cs);
    return *pPin ? S_OK : VFW_E_NOT_CONNECTED;
}

STDMETHODIMP OpenCamPin::ConnectionMediaType(AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    EnterCriticalSection(&m_cs);
    if (!m_pConnectedPin) { LeaveCriticalSection(&m_cs); return VFW_E_NOT_CONNECTED; }
    HRESULT hr = CopyMediaType(pmt, &m_mt);
    LeaveCriticalSection(&m_cs);
    return hr;
}

STDMETHODIMP OpenCamPin::QueryPinInfo(PIN_INFO* pInfo) {
    if (!pInfo) return E_POINTER;
    pInfo->pFilter = m_pFilter;
    if (m_pFilter) m_pFilter->AddRef();
    pInfo->dir = PINDIR_OUTPUT;
    wcsncpy_s(pInfo->achName, L"Capture", _TRUNCATE);
    return S_OK;
}

STDMETHODIMP OpenCamPin::QueryDirection(PIN_DIRECTION* pPinDir) {
    if (!pPinDir) return E_POINTER;
    *pPinDir = PINDIR_OUTPUT;
    return S_OK;
}

STDMETHODIMP OpenCamPin::QueryId(LPWSTR* Id) {
    if (!Id) return E_POINTER;
    *Id = (LPWSTR)CoTaskMemAlloc(16);
    if (!*Id) return E_OUTOFMEMORY;
    wcscpy_s(*Id, 8, L"Capture");
    return S_OK;
}

STDMETHODIMP OpenCamPin::QueryAccept(const AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    if (pmt->majortype != MEDIATYPE_Video || pmt->subtype != MEDIASUBTYPE_RGB24 || pmt->formattype != FORMAT_VideoInfo)
        return S_FALSE;
    if (!pmt->pbFormat || pmt->cbFormat < sizeof(VIDEOINFOHEADER)) return S_FALSE;
    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)pmt->pbFormat;
    if (pvi->bmiHeader.biBitCount != 24 || pvi->bmiHeader.biWidth <= 0 || pvi->bmiHeader.biHeight == 0)
        return S_FALSE;
    return S_OK;
}

STDMETHODIMP OpenCamPin::EnumMediaTypes(IEnumMediaTypes** ppEnum) {
    if (!ppEnum) return E_POINTER;
    *ppEnum = new (std::nothrow) OpenCamEnumMediaTypes(m_width, m_height, m_fps);
    return *ppEnum ? S_OK : E_OUTOFMEMORY;
}

STDMETHODIMP OpenCamPin::SetFormat(AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    if (QueryAccept(pmt) != S_OK) return VFW_E_INVALIDMEDIATYPE;
    EnterCriticalSection(&m_cs);
    FreeMediaType(m_mt);
    CopyMediaType(&m_mt, pmt);
    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)m_mt.pbFormat;
    m_width = pvi->bmiHeader.biWidth;
    m_height = abs(pvi->bmiHeader.biHeight);
    if (pvi->AvgTimePerFrame > 0) m_fps = (int)(10000000LL / pvi->AvgTimePerFrame);
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP OpenCamPin::GetFormat(AM_MEDIA_TYPE** ppmt) {
    if (!ppmt) return E_POINTER;
    *ppmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!*ppmt) return E_OUTOFMEMORY;
    EnterCriticalSection(&m_cs);
    HRESULT hr = CopyMediaType(*ppmt, &m_mt);
    LeaveCriticalSection(&m_cs);
    return hr;
}

STDMETHODIMP OpenCamPin::GetNumberOfCapabilities(int* piCount, int* piSize) {
    if (!piCount || !piSize) return E_POINTER;
    *piCount = 3;
    *piSize = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP OpenCamPin::GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) {
    if (!ppmt || !pSCC) return E_POINTER;
    if (iIndex < 0 || iIndex >= 3) return S_FALSE;

    int w = 1280, h = 720;
    if (iIndex == 1) { w = 1920; h = 1080; }
    else if (iIndex == 2) { w = 854; h = 480; }

    *ppmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!*ppmt) return E_OUTOFMEMORY;
    HRESULT hr = BuildVideoMediaType(*ppmt, w, h, 30);
    if (FAILED(hr)) { CoTaskMemFree(*ppmt); return hr; }

    VIDEO_STREAM_CONFIG_CAPS* scc = (VIDEO_STREAM_CONFIG_CAPS*)pSCC;
    ZeroMemory(scc, sizeof(VIDEO_STREAM_CONFIG_CAPS));
    scc->guid = FORMAT_VideoInfo;
    scc->VideoStandard = AnalogVideo_None;
    scc->InputSize.cx = w; scc->InputSize.cy = h;
    scc->MinCroppingSize.cx = w; scc->MinCroppingSize.cy = h;
    scc->MaxCroppingSize.cx = w; scc->MaxCroppingSize.cy = h;
    scc->CropGranularityX = 1; scc->CropGranularityY = 1;
    scc->MinOutputSize.cx = w; scc->MinOutputSize.cy = h;
    scc->MaxOutputSize.cx = w; scc->MaxOutputSize.cy = h;
    scc->OutputGranularityX = 1; scc->OutputGranularityY = 1;
    scc->MinFrameInterval = 10000000LL / 60;
    scc->MaxFrameInterval = 10000000LL / 15;
    scc->MinBitsPerSecond = w * h * 24 * 15;
    scc->MaxBitsPerSecond = w * h * 24 * 60;
    return S_OK;
}

STDMETHODIMP OpenCamPin::Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData, DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData, DWORD* pcbReturned) {
    if (guidPropSet != AMPROPSETID_Pin || dwPropID != AMPROPERTY_PIN_CATEGORY) return E_PROP_SET_UNSUPPORTED;
    if (pcbReturned) *pcbReturned = sizeof(GUID);
    if (!pPropData) return S_OK;
    if (cbPropData < sizeof(GUID)) return E_UNEXPECTED;
    *(GUID*)pPropData = PIN_CATEGORY_CAPTURE;
    return S_OK;
}

STDMETHODIMP OpenCamPin::QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport) {
    if (guidPropSet != AMPROPSETID_Pin || dwPropID != AMPROPERTY_PIN_CATEGORY) return E_PROP_SET_UNSUPPORTED;
    if (pTypeSupport) *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    return S_OK;
}

HRESULT OpenCamPin::StartStreaming() {
    if (m_hThread) return S_OK;
    if (m_pAllocator) m_pAllocator->Commit();
    m_hStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    m_hThread = CreateThread(nullptr, 0, StreamThreadProc, this, 0, nullptr);
    return m_hThread ? S_OK : E_FAIL;
}

HRESULT OpenCamPin::StopStreaming() {
    if (m_hStopEvent) SetEvent(m_hStopEvent);
    if (m_hThread) {
        WaitForSingleObject(m_hThread, 3000);
        CloseHandle(m_hThread);
        m_hThread = nullptr;
    }
    if (m_hStopEvent) {
        CloseHandle(m_hStopEvent);
        m_hStopEvent = nullptr;
    }
    if (m_pAllocator) m_pAllocator->Decommit();
    return S_OK;
}

DWORD WINAPI OpenCamPin::StreamThreadProc(LPVOID param) {
    ((OpenCamPin*)param)->RunStreamLoop();
    return 0;
}

void OpenCamPin::RunStreamLoop() {
    DWORD intervalMs = (DWORD)(1000 / (m_fps > 0 ? m_fps : 30));
    REFERENCE_TIME rtStart = 0;
    REFERENCE_TIME rtStep = 10000000LL / (m_fps > 0 ? m_fps : 30);
    uint64_t tick = 0;

    while (WaitForSingleObject(m_hStopEvent, intervalMs) == WAIT_TIMEOUT) {
        if (!m_pMemInputPin || !m_pAllocator) continue;
        IMediaSample* pSample = nullptr;
        if (FAILED(m_pAllocator->GetBuffer(&pSample, nullptr, nullptr, 0)) || !pSample) continue;

        BYTE* pData = nullptr;
        if (SUCCEEDED(pSample->GetPointer(&pData)) && pData) {
            long bufSize = pSample->GetSize();
            long needed = (long)(m_width * m_height * 3);
            if (bufSize >= needed) {
                FillFrame(pData, m_width, m_height, tick++);
                pSample->SetActualDataLength(needed);
                REFERENCE_TIME rtEnd = rtStart + rtStep;
                pSample->SetTime(&rtStart, &rtEnd);
                pSample->SetSyncPoint(TRUE);
                rtStart = rtEnd;
                m_pMemInputPin->Receive(pSample);
            }
        }
        pSample->Release();
    }
}

static const uint8_t* GetGlyph5x7(char c) {
    static const uint8_t G_SPACE[7] = {0,0,0,0,0,0,0};
    static const uint8_t G_A[7] = {0x0E,0x11,0x11,0x1F,0x11,0x11,0x11};
    static const uint8_t G_C[7] = {0x0E,0x11,0x10,0x10,0x10,0x11,0x0E};
    static const uint8_t G_E[7] = {0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F};
    static const uint8_t G_L[7] = {0x10,0x10,0x10,0x10,0x10,0x10,0x1F};
    static const uint8_t G_M[7] = {0x11,0x1B,0x15,0x15,0x11,0x11,0x11};
    static const uint8_t G_N[7] = {0x11,0x19,0x15,0x13,0x11,0x11,0x11};
    static const uint8_t G_O[7] = {0x0E,0x11,0x11,0x11,0x11,0x11,0x0E};
    static const uint8_t G_P[7] = {0x1E,0x11,0x11,0x1E,0x10,0x10,0x10};
    static const uint8_t G_S[7] = {0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E};
    static const uint8_t G_T[7] = {0x1F,0x04,0x04,0x04,0x04,0x04,0x04};
    switch (c) {
        case 'A': return G_A; case 'C': return G_C; case 'E': return G_E;
        case 'L': return G_L; case 'M': return G_M; case 'N': return G_N;
        case 'O': return G_O; case 'P': return G_P; case 'S': return G_S;
        case 'T': return G_T; default: return G_SPACE;
    }
}

static void DrawText5x7(BYTE* dst, int dstW, int dstH, const char* text, int centerX, int topY, int scale, BYTE r, BYTE g, BYTE b) {
    int len = (int)strlen(text);
    int charW = 6 * scale;
    int totalW = len * charW - scale;
    int startX = centerX - totalW / 2;
    int stride = dstW * 3;
    for (int i = 0; i < len; i++) {
        const uint8_t* glyph = GetGlyph5x7(text[i]);
        int gx = startX + i * charW;
        for (int row = 0; row < 7; row++) {
            uint8_t bits = glyph[row];
            for (int col = 0; col < 5; col++) {
                if ((bits & (1 << (4 - col))) == 0) continue;
                for (int sy = 0; sy < scale; sy++) {
                    int py = topY + row * scale + sy;
                    if (py < 0 || py >= dstH) continue;
                    // DirectShow RGB24 is bottom-up
                    BYTE* pRow = dst + (dstH - 1 - py) * stride;
                    for (int sx = 0; sx < scale; sx++) {
                        int px = gx + col * scale + sx;
                        if (px < 0 || px >= dstW) continue;
                        pRow[px * 3 + 0] = b;
                        pRow[px * 3 + 1] = g;
                        pRow[px * 3 + 2] = r;
                    }
                }
            }
        }
    }
}

void OpenCamPin::FillFrame(BYTE* dst, int dstW, int dstH, uint64_t tick) {
    HANDLE hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, kSharedMemName);
    if (hMap) {
        SharedFrameHeader* hdr = (SharedFrameHeader*)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
        if (hdr) {
            ULONGLONG now = GetTickCount64();
            if (hdr->magic == kSharedMagic && hdr->active == 1 && hdr->width > 0 && hdr->height > 0 &&
                hdr->width <= kMaxFrameWidth && hdr->height <= kMaxFrameHeight) {
                if (hdr->sequence != m_lastSeq) {
                    m_lastSeq = hdr->sequence;
                    m_lastSeqTick = now;
                }
                if (m_lastSeqTick != 0 && (now - m_lastSeqTick) <= 1500) {
                    int srcW = (int)hdr->width;
                    int srcH = (int)hdr->height;
                    int dstStride = dstW * 3;
                    int srcStride = srcW * 3;

                    for (int y = 0; y < dstH; y++) {
                        int sy = (y * srcH) / dstH;
                        int dstRow = (dstH - 1 - y) * dstStride;
                        const BYTE* srcPtr = hdr->bgr24 + sy * srcStride;
                        BYTE* dstPtr = dst + dstRow;
                        if (srcW == dstW) {
                            memcpy(dstPtr, srcPtr, (size_t)dstStride);
                        } else {
                            for (int x = 0; x < dstW; x++) {
                                int sx = (x * srcW) / dstW;
                                dstPtr[x * 3 + 0] = srcPtr[sx * 3 + 0];
                                dstPtr[x * 3 + 1] = srcPtr[sx * 3 + 1];
                                dstPtr[x * 3 + 2] = srcPtr[sx * 3 + 2];
                            }
                        }
                    }
                    UnmapViewOfFile(hdr);
                    CloseHandle(hMap);
                    return;
                }
            }
            UnmapViewOfFile(hdr);
        }
        CloseHandle(hMap);
    }

    // Old-TV static/glitch background + cute animated OpenCam lens + "PLEASE CONNECT OPENCAM"
    int stride = dstW * 3;
    int cx = dstW / 2;
    int cy = (int)(dstH * 0.43);
    double phase = (double)tick * 0.12;
    double pulse = 0.5 + 0.5 * sin(phase);
    double outerR = (dstH * 0.14) + pulse * (dstH * 0.014);
    double midR = outerR * 0.72;
    double innerR = outerR * 0.36;
    double pupilR = outerR * 0.16;

    int dotOffsetX = (int)(cos(phase * 1.5) * (outerR * 0.54));
    int dotOffsetY = (int)(sin(phase * 1.5) * (outerR * 0.54));

    int glitchBandY = (int)((tick * 17) % (uint64_t)dstH);
    bool glitchActive = (tick % 45) < 4;
    uint32_t rng = (uint32_t)(tick * 2654435761u + 17u);

    for (int y = 0; y < dstH; y++) {
        int topDownY = dstH - 1 - y;
        BYTE* row = dst + y * stride;
        int dy = topDownY - cy;
        bool scanDim = (topDownY & 1) == 0;
        bool inGlitchBand = glitchActive && abs(topDownY - glitchBandY) < 6;

        for (int x = 0; x < dstW; x++) {
            rng = rng * 1664525u + 1013904223u;
            int noise = (int)((rng >> 24) & 0x0F);
            int base = scanDim ? (12 + noise) : (18 + noise);
            if (inGlitchBand) base += 22;

            BYTE b = (BYTE)(base + 6);
            BYTE g = (BYTE)(base + 2);
            BYTE r = (BYTE)base;

            int dx = x - cx;
            double dist = sqrt((double)(dx * dx + dy * dy));

            if (dist <= pupilR) {
                b = (BYTE)(215 + 35 * pulse);
                g = (BYTE)(150 + 30 * pulse);
                r = (BYTE)(70 + 25 * pulse);
            } else if (dist <= innerR) {
                b = 0x68; g = 0x42; r = 0x22;
            } else if (dist <= midR) {
                b = 0x2B; g = 0x22; r = 0x1D;
            } else if (fabs(dist - outerR) < 3.0) {
                b = (BYTE)(225 + 26 * pulse);
                g = (BYTE)(120 + 20 * pulse);
                r = (BYTE)(52 + 15 * pulse);
            } else if (fabs(dist - (outerR + 16.0 + pulse * 10.0)) < 1.5) {
                b = (BYTE)(90 + 40 * pulse);
                g = (BYTE)(55 + 20 * pulse);
                r = (BYTE)(30 + 10 * pulse);
            }

            int odx = dx - dotOffsetX;
            int ody = dy - dotOffsetY;
            if (odx * odx + ody * ody <= 25) {
                b = 0x9E; g = 0xD4; r = 0x45;
            }

            row[x * 3 + 0] = b;
            row[x * 3 + 1] = g;
            row[x * 3 + 2] = r;
        }
    }

    int textScale = dstH >= 720 ? 5 : 3;
    int textY = cy + (int)(outerR + 36);
    DrawText5x7(dst, dstW, dstH, "PLEASE CONNECT OPENCAM", cx, textY, textScale, 255, 255, 255);
}

class OpenCamClassFactory : public IClassFactory {
public:
    OpenCamClassFactory() : m_ref(1) { DllAddRef(); }
    virtual ~OpenCamClassFactory() { DllRelease(); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override {
        ULONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }
    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override {
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        OpenCamFilter* f = new (std::nothrow) OpenCamFilter();
        if (!f) return E_OUTOFMEMORY;
        HRESULT hr = f->QueryInterface(riid, ppv);
        f->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL fLock) override {
        if (fLock) DllAddRef(); else DllRelease();
        return S_OK;
    }

private:
    LONG m_ref;
};

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow() {
    return g_dllRefCount == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (rclsid != CLSID_OpenCamVirtualCam) return CLASS_E_CLASSNOTAVAILABLE;
    OpenCamClassFactory* cf = new (std::nothrow) OpenCamClassFactory();
    if (!cf) return E_OUTOFMEMORY;
    HRESULT hr = cf->QueryInterface(riid, ppv);
    cf->Release();
    return hr;
}

STDAPI DllRegisterServer() {
    wchar_t dllPath[MAX_PATH]{};
    GetModuleFileNameW(g_hModule, dllPath, MAX_PATH);

    wchar_t clsidKey[128]{};
    swprintf_s(clsidKey, L"CLSID\\{8E3A9B12-4F7D-4C21-9A6E-3D5C1B2A7F01}");

    HKEY hKey = nullptr;
    if (RegCreateKeyExW(HKEY_CLASSES_ROOT, clsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &hKey, nullptr) == ERROR_SUCCESS) {
        RegSetValueExW(hKey, nullptr, 0, REG_SZ, (const BYTE*)kFilterName, (DWORD)((wcslen(kFilterName) + 1) * sizeof(wchar_t)));
        HKEY hInproc = nullptr;
        if (RegCreateKeyExW(hKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &hInproc, nullptr) == ERROR_SUCCESS) {
            RegSetValueExW(hInproc, nullptr, 0, REG_SZ, (const BYTE*)dllPath, (DWORD)((wcslen(dllPath) + 1) * sizeof(wchar_t)));
            const wchar_t* tm = L"Both";
            RegSetValueExW(hInproc, L"ThreadingModel", 0, REG_SZ, (const BYTE*)tm, (DWORD)((wcslen(tm) + 1) * sizeof(wchar_t)));
            RegCloseKey(hInproc);
        }
        RegCloseKey(hKey);
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IFilterMapper2* pFm = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2, (void**)&pFm);
    if (SUCCEEDED(hr) && pFm) {
        REGPINTYPES pinTypes = { &MEDIATYPE_Video, &MEDIASUBTYPE_RGB24 };
        REGFILTERPINS2 pin = {};
        pin.dwFlags = REG_PINFLAG_B_OUTPUT;
        pin.cInstances = 1;
        pin.nMediaTypes = 1;
        pin.lpMediaType = &pinTypes;
        pin.nMediums = 0;
        pin.lpMedium = nullptr;
        pin.clsPinCategory = &PIN_CATEGORY_CAPTURE;

        REGFILTER2 rf2 = {};
        rf2.dwVersion = 2;
        rf2.dwMerit = MERIT_DO_NOT_USE;
        rf2.cPins2 = 1;
        rf2.rgPins2 = &pin;

        hr = pFm->RegisterFilter(CLSID_OpenCamVirtualCam, kFilterName, nullptr, &CLSID_VideoInputDeviceCategory, kFilterName, &rf2);
        pFm->Release();
    }
    CoUninitialize();
    return hr;
}

STDAPI DllUnregisterServer() {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IFilterMapper2* pFm = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2, (void**)&pFm)) && pFm) {
        pFm->UnregisterFilter(&CLSID_VideoInputDeviceCategory, kFilterName, CLSID_OpenCamVirtualCam);
        pFm->Release();
    }
    CoUninitialize();

    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"CLSID\\{8E3A9B12-4F7D-4C21-9A6E-3D5C1B2A7F01}");
    return S_OK;
}
