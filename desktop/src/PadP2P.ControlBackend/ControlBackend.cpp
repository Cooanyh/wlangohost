#include "stdafx.h"
#include "WlanHostedNetworkWinRT.h"

#include <atomic>
#include <cstdlib>
#include <cwchar>
#include <memory>
#include <string>
#include <thread>
#include <vector>
#include <sddl.h>
#include <aclapi.h>
#include <strsafe.h>

using Microsoft::WRL::Wrappers::RoInitializeWrapper;

namespace
{
    enum class NetworkState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    };

    std::string Base64Encode(const std::string& value)
    {
        static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        std::string result;
        result.reserve(((value.size() + 2) / 3) * 4);
        for (size_t i = 0; i < value.size(); i += 3)
        {
            const unsigned int a = static_cast<unsigned char>(value[i]);
            const unsigned int b = i + 1 < value.size() ? static_cast<unsigned char>(value[i + 1]) : 0;
            const unsigned int c = i + 2 < value.size() ? static_cast<unsigned char>(value[i + 2]) : 0;
            const unsigned int block = (a << 16) | (b << 8) | c;
            result.push_back(alphabet[(block >> 18) & 63]);
            result.push_back(alphabet[(block >> 12) & 63]);
            result.push_back(i + 1 < value.size() ? alphabet[(block >> 6) & 63] : '=');
            result.push_back(i + 2 < value.size() ? alphabet[block & 63] : '=');
        }
        return result;
    }

    std::string Base64Decode(const std::string& value)
    {
        static const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        if (value.size() > 32768 || value.size() % 4 != 0)
        {
            throw std::runtime_error("Invalid IPC text encoding");
        }

        std::string result;
        result.reserve((value.size() / 4) * 3);
        for (size_t i = 0; i < value.size(); i += 4)
        {
            unsigned int block = 0;
            for (size_t j = 0; j < 4; ++j)
            {
                const char ch = value[i + j];
                const size_t index = ch == '=' ? 0 : alphabet.find(ch);
                if (index == std::string::npos)
                {
                    throw std::runtime_error("Invalid IPC text encoding");
                }
                block = (block << 6) | static_cast<unsigned int>(index);
            }

            result.push_back(static_cast<char>((block >> 16) & 0xff));
            if (value[i + 2] != '=') result.push_back(static_cast<char>((block >> 8) & 0xff));
            if (value[i + 3] != '=') result.push_back(static_cast<char>(block & 0xff));
        }
        return result;
    }

    std::wstring Utf8ToWide(const std::string& value)
    {
        if (value.empty()) return {};
        const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (length <= 0) throw std::runtime_error("Invalid UTF-8 in IPC command");
        std::wstring result(static_cast<size_t>(length), L'\0');
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), length))
        {
            throw std::runtime_error("Invalid UTF-8 in IPC command");
        }
        return result;
    }

    std::string WideToUtf8(const std::wstring& value)
    {
        if (value.empty()) return {};
        const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if (length <= 0) return {};
        std::string result(static_cast<size_t>(length), '\0');
        if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), length, nullptr, nullptr))
        {
            return {};
        }
        return result;
    }

    std::vector<std::string> Split(const std::string& line)
    {
        std::vector<std::string> fields;
        size_t start = 0;
        while (true)
        {
            const size_t separator = line.find('|', start);
            fields.push_back(line.substr(start, separator == std::string::npos ? separator : separator - start));
            if (separator == std::string::npos) break;
            start = separator + 1;
        }
        return fields;
    }

    std::wstring StateName(NetworkState state)
    {
        switch (state)
        {
        case NetworkState::Starting: return L"starting";
        case NetworkState::Running: return L"running";
        case NetworkState::Stopping: return L"stopping";
        case NetworkState::Error: return L"error";
        default: return L"stopped";
        }
    }

    class ControlBackend final : public IWlanHostedNetworkListener
    {
    public:
        ControlBackend()
            : m_pipe(INVALID_HANDLE_VALUE), m_stateEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr)), m_peerCount(0), m_configured(false)
        {
            if (!m_stateEvent) throw std::runtime_error("Failed to create backend state event");
            InitializeCriticalSection(&m_writeLock);
            m_network.RegisterListener(this);
        }

        ~ControlBackend() override
        {
            m_network.RegisterListener(nullptr);
            m_network.RegisterPrompt(nullptr);
            DeleteCriticalSection(&m_writeLock);
            if (m_stateEvent) CloseHandle(m_stateEvent);
        }

        int Run(const std::wstring& pipeName, DWORD parentPid)
        {
            HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
            if (!parent) return 2;

            PSECURITY_DESCRIPTOR securityDescriptor = nullptr;
            std::wstring sddl;
            if (!BuildCurrentUserSecurityDescriptor(sddl, securityDescriptor))
            {
                CloseHandle(parent);
                return 3;
            }

            SECURITY_ATTRIBUTES attributes{};
            attributes.nLength = sizeof(attributes);
            attributes.lpSecurityDescriptor = securityDescriptor;
            const std::wstring fullPipeName = L"\\\\.\\pipe\\" + pipeName;
            m_pipe = CreateNamedPipeW(
                fullPipeName.c_str(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                4096,
                4096,
                0,
                &attributes);
            LocalFree(securityDescriptor);

            if (m_pipe == INVALID_HANDLE_VALUE)
            {
                CloseHandle(parent);
                return 4;
            }

            HANDLE mainThread = OpenThread(THREAD_TERMINATE, FALSE, GetCurrentThreadId());
            if (!mainThread)
            {
                CloseHandle(m_pipe);
                m_pipe = INVALID_HANDLE_VALUE;
                CloseHandle(parent);
                return 5;
            }

            HANDLE monitorStop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!monitorStop)
            {
                CloseHandle(mainThread);
                CloseHandle(m_pipe);
                m_pipe = INVALID_HANDLE_VALUE;
                CloseHandle(parent);
                return 6;
            }

            std::thread parentMonitor([parent, mainThread, monitorStop]()
            {
                HANDLE handles[] = { parent, monitorStop };
                if (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0)
                {
                    CancelSynchronousIo(mainThread);
                }
            });

            int result = 0;
            BOOL connected = ConnectNamedPipe(m_pipe, nullptr);
            if (!connected && GetLastError() != ERROR_PIPE_CONNECTED)
            {
                result = 7;
            }
            else
            {
                SendState();
                std::string line;
                bool commandRequestedExit = false;
                while (ReadLine(line))
                {
                    if (!HandleCommand(line))
                    {
                        commandRequestedExit = true;
                        break;
                    }
                }

                if (commandRequestedExit)
                {
                    // Preserve the final STATE/ACK frames before disconnecting this pipe instance.
                    FlushFileBuffers(m_pipe);
                }
                else
                {
                    StopNetworkAndWait();
                }
            }

            SetEvent(monitorStop);
            parentMonitor.join();
            DisconnectNamedPipe(m_pipe);
            CloseHandle(m_pipe);
            m_pipe = INVALID_HANDLE_VALUE;
            CloseHandle(monitorStop);
            CloseHandle(mainThread);
            CloseHandle(parent);
            return result;
        }

        void OnDeviceConnected(std::wstring remoteHostName) override
        {
            const int count = ++m_peerCount;
            WriteMessage("PEER_CONNECTED|" + Base64Encode(WideToUtf8(remoteHostName)));
            SendState(count);
        }

        void OnDeviceDisconnected(std::wstring deviceId) override
        {
            int count = m_peerCount.load();
            while (count > 0 && !m_peerCount.compare_exchange_weak(count, count - 1)) {}
            count = m_peerCount.load();
            WriteMessage("PEER_DISCONNECTED|" + Base64Encode(WideToUtf8(deviceId)));
            SendState(count);
        }

        void OnAdvertisementStarted() override
        {
            m_state = NetworkState::Running;
            SendState();
            SetEvent(m_stateEvent);
        }

        void OnAdvertisementStopped(std::wstring) override
        {
            m_state = NetworkState::Stopped;
            m_peerCount = 0;
            SendState();
            SetEvent(m_stateEvent);
        }

        void OnAdvertisementAborted(std::wstring message) override
        {
            m_state = NetworkState::Error;
            WriteMessage("ERROR|" + Base64Encode(WideToUtf8(message)));
            SendState();
            SetEvent(m_stateEvent);
        }

        void OnAsyncException(std::wstring message) override
        {
            m_state = NetworkState::Error;
            WriteMessage("ERROR|" + Base64Encode(WideToUtf8(message)));
            SendState();
            SetEvent(m_stateEvent);
        }

        void LogMessage(std::wstring message) override
        {
            UNREFERENCED_PARAMETER(message);
        }

    private:
        static bool BuildCurrentUserSecurityDescriptor(std::wstring& sddl, PSECURITY_DESCRIPTOR& descriptor)
        {
            HANDLE token = nullptr;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
            DWORD needed = 0;
            GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
            std::vector<BYTE> buffer(needed);
            if (!GetTokenInformation(token, TokenUser, buffer.data(), needed, &needed))
            {
                CloseHandle(token);
                return false;
            }

            LPWSTR sidText = nullptr;
            const auto user = reinterpret_cast<TOKEN_USER*>(buffer.data());
            if (!ConvertSidToStringSidW(user->User.Sid, &sidText))
            {
                CloseHandle(token);
                return false;
            }
            sddl = L"D:P(A;;GA;;;" + std::wstring(sidText) + L")";
            LocalFree(sidText);
            CloseHandle(token);
            return ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr) == TRUE;
        }

        bool ReadLine(std::string& line)
        {
            line.clear();
            while (line.size() < 32768)
            {
                char value = 0;
                DWORD read = 0;
                if (!ReadFile(m_pipe, &value, 1, &read, nullptr) || read == 0) return false;
                if (value == '\n') return true;
                if (value != '\r') line.push_back(value);
            }
            WriteMessage("ERROR|" + Base64Encode("IPC command exceeded the size limit"));
            return false;
        }

        void WriteMessage(const std::string& message)
        {
            if (m_pipe == INVALID_HANDLE_VALUE) return;
            std::string data = message + "\n";
            EnterCriticalSection(&m_writeLock);
            size_t offset = 0;
            while (offset < data.size())
            {
                DWORD written = 0;
                if (!WriteFile(m_pipe, data.data() + offset, static_cast<DWORD>(data.size() - offset), &written, nullptr) || written == 0) break;
                offset += written;
            }
            LeaveCriticalSection(&m_writeLock);
        }

        void SendState()
        {
            SendState(m_peerCount.load());
        }

        void SendState(int count)
        {
            WriteMessage("STATE|" + WideToUtf8(StateName(m_state.load())) + "|" + std::to_string(count));
        }

        bool HandleCommand(const std::string& line)
        {
            try
            {
                const auto fields = Split(line);
                if (fields.empty()) return true;

                if (fields[0] == "CONFIG" && fields.size() == 4)
                {
                    if (m_state != NetworkState::Stopped)
                    {
                        WriteMessage("ERROR|" + Base64Encode("Network settings can only be configured while stopped"));
                        return true;
                    }
                    const std::wstring ssid = Utf8ToWide(Base64Decode(fields[1]));
                    const std::wstring passphrase = Utf8ToWide(Base64Decode(fields[2]));
                    if (ssid.empty() || passphrase.size() < 8 || passphrase.size() > 63)
                    {
                        WriteMessage("ERROR|" + Base64Encode("SSID or passphrase is invalid"));
                        return true;
                    }
                    m_network.SetSSID(ssid);
                    m_network.SetPassphrase(passphrase);
                    m_network.SetAutoAccept(fields[3] == "1");
                    m_configured = true;
                    WriteMessage("ACK|CONFIG");
                    return true;
                }

                if (fields[0] == "START" && fields.size() == 1)
                {
                    if (!m_configured)
                    {
                        WriteMessage("ERROR|" + Base64Encode("Configure the network before starting it"));
                        return true;
                    }
                    if (m_state == NetworkState::Running || m_state == NetworkState::Starting)
                    {
                        WriteMessage("ACK|START");
                        return true;
                    }
                    m_state = NetworkState::Starting;
                    ResetEvent(m_stateEvent);
                    SendState();
                    try
                    {
                        m_network.Start();
                        // Match the upstream CLI: wait on the WinRT status event before reading another command.
                        const DWORD waitResult = WaitForSingleObjectEx(m_stateEvent, 30000, FALSE);
                        if (waitResult != WAIT_OBJECT_0)
                        {
                            m_state = NetworkState::Error;
                            SetEvent(m_stateEvent);
                            WriteMessage("ERROR|" + Base64Encode("Timed out while waiting for Wi-Fi Direct to start"));
                            SendState();
                        }
                        WriteMessage("ACK|START");
                    }
                    catch (WlanHostedNetworkException& e)
                    {
                        m_state = NetworkState::Error;
                        SetEvent(m_stateEvent);
                        WriteMessage("ERROR|" + Base64Encode(std::string(e.what()) + " (HRESULT " + std::to_string(static_cast<long>(e.GetErrorCode())) + ")"));
                        SendState();
                    }
                    return true;
                }

                if (fields[0] == "STOP" && fields.size() == 1)
                {
                    if (m_state == NetworkState::Stopped)
                    {
                        WriteMessage("ACK|STOP");
                        return false;
                    }
                    StopNetworkAndWait();
                    WriteMessage("ACK|STOP");
                    return false;
                }

                if (fields[0] == "STATUS" && fields.size() == 1)
                {
                    SendState();
                    WriteMessage("ACK|STATUS");
                    return true;
                }

                if (fields[0] == "SHUTDOWN" && fields.size() == 1)
                {
                    StopNetworkAndWait();
                    WriteMessage("ACK|SHUTDOWN");
                    return false;
                }
            }
            catch (const std::exception& e)
            {
                WriteMessage("ERROR|" + Base64Encode(e.what()));
                return true;
            }

            WriteMessage("ERROR|" + Base64Encode("Unknown IPC command"));
            return true;
        }

        void StopNetworkAndWait()
        {
            if (m_state != NetworkState::Running && m_state != NetworkState::Starting && m_state != NetworkState::Error) return;
            m_state = NetworkState::Stopping;
            ResetEvent(m_stateEvent);
            try
            {
                m_network.Stop();
                if (WaitForSingleObject(m_stateEvent, 10000) != WAIT_OBJECT_0)
                {
                    m_state = NetworkState::Error;
                    WriteMessage("ERROR|" + Base64Encode("Timed out while waiting for Wi-Fi Direct to stop"));
                    SendState();
                }
            }
            catch (WlanHostedNetworkException& e)
            {
                m_state = NetworkState::Error;
                SetEvent(m_stateEvent);
                WriteMessage("ERROR|" + Base64Encode(std::string(e.what()) + " (HRESULT " + std::to_string(static_cast<long>(e.GetErrorCode())) + ")"));
                SendState();
            }
        }

        HANDLE m_pipe;
        HANDLE m_stateEvent;
        std::atomic<NetworkState> m_state{ NetworkState::Stopped };
        std::atomic<int> m_peerCount;
        bool m_configured;
        CRITICAL_SECTION m_writeLock{};
        WlanHostedNetworkHelper m_network;
    };
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc != 5 || wcscmp(argv[1], L"--pipe") != 0 || wcscmp(argv[3], L"--parent-pid") != 0)
    {
        return 1;
    }

    wchar_t* end = nullptr;
    const unsigned long parentPid = wcstoul(argv[4], &end, 10);
    if (!end || *end != L'\0' || parentPid == 0)
    {
        return 1;
    }

    RoInitializeWrapper initialize(RO_INIT_MULTITHREADED);
    if (FAILED(initialize)) return 2;

    try
    {
        ControlBackend backend;
        return backend.Run(argv[2], static_cast<DWORD>(parentPid));
    }
    catch (...)
    {
        return 10;
    }
}
