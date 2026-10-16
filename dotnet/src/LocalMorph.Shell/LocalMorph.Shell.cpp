#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <appmodel.h>
#include <atomic>
#include <string>
#include <vector>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

static HMODULE module;
static std::atomic<long> objects{0};
static std::atomic<long> locks{0};
static constexpr CLSID commandId = {0x761079a6, 0x3c8c, 0x4781, {0xaf, 0x52, 0xfb, 0x07, 0xd1, 0xc4, 0x3c, 0x0a}};

static HRESULT CopyText(PCWSTR text, PWSTR* result)
{
    if (!result) return E_POINTER;
    *result = nullptr;
    const auto bytes = (wcslen(text) + 1) * sizeof(wchar_t);
    *result = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
    if (!*result) return E_OUTOFMEMORY;
    memcpy(*result, text, bytes);
    return S_OK;
}

static bool Supported(IShellItemArray* selection)
{
    if (!selection) return false;
    DWORD count = 0;
    if (FAILED(selection->GetCount(&count)) || count == 0) return false;
    static constexpr PCWSTR extensions[] = {
        L".mp4", L".m4v", L".mov", L".mkv", L".webm", L".avi", L".wmv", L".flv", L".mpg", L".mpeg",
        L".ts", L".m2ts", L".mts", L".3gp", L".ogv", L".vob", L".asf", L".divx", L".mxf", L".f4v",
        L".mp3", L".wav", L".aac", L".m4a", L".flac", L".ogg", L".oga", L".opus", L".wma", L".aiff",
        L".aif", L".alac", L".amr", L".ac3", L".mka", L".caf", L".ape", L".wv", L".mid",
        L".png", L".jpg", L".jpeg", L".gif", L".webp", L".bmp", L".tif", L".tiff", L".heic", L".heif",
        L".avif", L".jxl", L".svg", L".psd", L".ico", L".tga", L".dds", L".exr", L".hdr", L".pbm",
        L".pgm", L".ppm", L".apng", L".jp2", L".cr2", L".cr3", L".nef", L".arw", L".dng", L".orf",
        L".rw2", L".raf", L".eps", L".ai",
        L".pdf", L".docx", L".doc", L".odt", L".rtf", L".txt", L".md", L".markdown", L".html", L".htm",
        L".epub", L".xlsx", L".xls", L".ods", L".csv", L".pptx", L".ppt", L".odp", L".tex", L".rst",
        L".wpd", L".pages", L".numbers", L".key", L".srt", L".vtt", L".ass", L".ssa"
    };
    for (DWORD index = 0; index < count; ++index)
    {
        ComPtr<IShellItem> item;
        SFGAOF attributes = 0;
        if (FAILED(selection->GetItemAt(index, &item)) ||
            FAILED(item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, &attributes)) ||
            !(attributes & SFGAO_FILESYSTEM) || (attributes & SFGAO_FOLDER)) return false;
        PWSTR path = nullptr;
        if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) return false;
        const auto extension = PathFindExtensionW(path);
        bool supported = false;
        for (const auto known : extensions) if (_wcsicmp(extension, known) == 0) { supported = true; break; }
        CoTaskMemFree(path);
        if (!supported) return false;
    }
    return true;
}

static HRESULT ApplicationId(std::wstring& result)
{
    std::vector<wchar_t> path(32768);
    const auto length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::wstring directory(path.data(), length);
    directory.resize(directory.find_last_of(L'\\'));
    const auto fullName = directory.substr(directory.find_last_of(L'\\') + 1);
    UINT32 size = 0;
    auto error = PackageFamilyNameFromFullName(fullName.c_str(), &size, nullptr);
    if (error != ERROR_INSUFFICIENT_BUFFER) return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_INVALID_DATA : error);
    std::vector<wchar_t> family(size);
    error = PackageFamilyNameFromFullName(fullName.c_str(), &size, family.data());
    if (error != ERROR_SUCCESS) return HRESULT_FROM_WIN32(error);
    result = std::wstring(family.data()) + L"!App";
    return S_OK;
}

class Command final : public IExplorerCommand
{
    std::atomic<ULONG> references{1};
public:
    Command() { ++objects; }
    ~Command() { --objects; }
    IFACEMETHODIMP QueryInterface(REFIID id, void** value) override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (id != IID_IUnknown && id != __uuidof(IExplorerCommand)) return E_NOINTERFACE;
        *value = static_cast<IExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = --references; if (!count) delete this; return count; }
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override { return CopyText(L"Convert with LocalMorph", title); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override { if (icon) *icon = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override { return CopyText(L"Add files to LocalMorph. Conversion starts only when you choose Convert.", tip); }
    IFACEMETHODIMP GetCanonicalName(GUID* id) override { if (!id) return E_POINTER; *id = commandId; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray* selection, BOOL, EXPCMDSTATE* state) override
    {
        if (!state) return E_POINTER;
        *state = Supported(selection) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) override
    {
        if (!Supported(selection)) return E_INVALIDARG;
        std::wstring app;
        auto result = ApplicationId(app);
        if (FAILED(result)) return result;
        ComPtr<IApplicationActivationManager> activation;
        result = CoCreateInstance(CLSID_ApplicationActivationManager, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&activation));
        if (FAILED(result)) return result;
        DWORD processId = 0;
        return activation->ActivateForFile(app.c_str(), selection, L"open", &processId);
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { if (!flags) return E_POINTER; *flags = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override { if (commands) *commands = nullptr; return E_NOTIMPL; }
};

class Factory final : public IClassFactory
{
    std::atomic<ULONG> references{1};
public:
    Factory() { ++objects; }
    ~Factory() { --objects; }
    IFACEMETHODIMP QueryInterface(REFIID id, void** value) override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (id != IID_IUnknown && id != IID_IClassFactory) return E_NOINTERFACE;
        *value = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = --references; if (!count) delete this; return count; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID id, void** value) override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = new (std::nothrow) Command();
        if (!command) return E_OUTOFMEMORY;
        auto result = command->QueryInterface(id, value);
        command->Release();
        return result;
    }
    IFACEMETHODIMP LockServer(BOOL locked) override { if (locked) ++locks; else --locks; return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID id, REFIID interfaceId, void** value)
{
    if (!value) return E_POINTER;
    *value = nullptr;
    if (id != commandId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    auto result = factory->QueryInterface(interfaceId, value);
    factory->Release();
    return result;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects == 0 && locks == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
