using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// STT 服务返回体：{"text":"..."}
[Serializable]
public class SttResponse
{
    public string text;
}

/// <summary>
/// 本地 STT（语音转文字）客户端。
/// 对接通用本地 STT 服务（FastAPI + sherpa-onnx SenseVoice）：
///   POST /recognize  请求体为 WAV 二进制，返回 {"text":"..."}
/// 支持 UDP 局域网自动发现（广播 "AgentNpcSTT.Discover" -> 收到 "AgentNpcSTT.Here:<port>"），
/// 自动发现失败时回退到 fallbackUrl。PICO 头显真机上可自动找到电脑上的服务，无需手填 IP。
/// </summary>
public class SttClient : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("识别接口完整地址。留空时使用 UDP 自动发现结果；自动发现失败则用 fallbackUrl")]
    public string serverUrl = "";
    [Tooltip("自动发现失败时的兜底地址")]
    public string fallbackUrl = "http://127.0.0.1:9700/recognize";
    [Tooltip("启动时自动进行局域网发现")]
    public bool autoDiscoverOnStart = true;
    public int discoveryPort = 9701;
    public float discoveryTimeoutSeconds = 2f;
    public int requestTimeoutSeconds = 30;

    [Header("Recording")]
    [Tooltip("请求的采样率（服务端会自动重采样到 16k）")]
    public int sampleRate = 16000;
    [Tooltip("最长录音秒数")]
    public int maxRecordSeconds = 30;
    [Tooltip("短于该秒数的录音会被丢弃")]
    public float minRecordSeconds = 0.4f;
    [Tooltip("麦克风设备序号，-1 表示默认设备")]
    public int deviceIndex = -1;

    public bool IsRecording { get; private set; }
    public bool IsRecognizing { get; private set; }
    /// <summary>自动发现得到的主机:端口，如 192.168.1.10:9700</summary>
    public string ServerAddress { get; private set; }

    public event Action OnServerFound;
    public event Action OnRecordingStarted;
    public event Action OnRecordingFinished;
    public event Action<string> OnRecognized;
    public event Action<string> OnError;

    private AudioClip clip;
    private string micDevice = "";

    private void Start()
    {
        if (autoDiscoverOnStart)
        {
            StartCoroutine(DiscoverServer());
        }
    }

    private void OnDestroy()
    {
        if (IsRecording)
        {
            StopRecording();
        }
    }

    // ---------- 局域网自动发现 ----------
    private IEnumerator DiscoverServer()
    {
        byte[] magic = Encoding.ASCII.GetBytes("AgentNpcSTT.Discover");
        UdpClient udp = null;
        try
        {
            udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.ReceiveTimeout = (int)(discoveryTimeoutSeconds * 1000);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SttClient] 创建 UDP 客户端失败: {ex.Message}");
            if (udp != null)
            {
                udp.Dispose();
                udp = null;
            }
        }

        if (udp != null)
        {
            // 广播到 255.255.255.255
            try
            {
                udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SttClient] 全局广播失败: {ex.Message}");
            }

            // 再尝试子网广播（部分设备/网络会忽略 255.255.255.255）
            string localIp = GetLocalIPv4();
            if (!string.IsNullOrEmpty(localIp))
            {
                string subnetBroadcast = localIp.Substring(0, localIp.LastIndexOf('.') + 1) + "255";
                try
                {
                    udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Parse(subnetBroadcast), discoveryPort));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SttClient] 子网广播失败: {ex.Message}");
                }
            }

            float deadline = Time.time + discoveryTimeoutSeconds;
            while (Time.time < deadline)
            {
                if (udp.Available > 0)
                {
                    string found = null;
                    try
                    {
                        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] reply = udp.Receive(ref remote);
                        string msg = Encoding.ASCII.GetString(reply);
                        const string prefix = "AgentNpcSTT.Here:";
                        if (msg.StartsWith(prefix))
                        {
                            string portStr = msg.Substring(prefix.Length).Trim();
                            if (int.TryParse(portStr, out int port) && port > 0)
                            {
                                ServerAddress = $"{remote.Address}:{port}";
                                serverUrl = $"http://{ServerAddress}/recognize";
                                found = serverUrl;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[SttClient] 接收发现应答失败: {ex.Message}");
                    }

                    if (found != null)
                    {
                        Debug.Log($"[SttClient] 自动发现 STT 服务: {serverUrl}");
                        OnServerFound?.Invoke();
                        udp.Dispose();
                        yield break;
                    }
                }
                yield return null;
            }
            udp.Dispose();
        }

        if (string.IsNullOrEmpty(serverUrl))
        {
            serverUrl = fallbackUrl;
            Debug.Log($"[SttClient] 自动发现失败，使用兜底地址: {serverUrl}");
        }
    }

    private static string GetLocalIPv4()
    {
        try
        {
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                // 只用于取得本地路由网卡 IP，不会真正发包
                s.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
        catch
        {
            try
        {
                foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }
        }
        return null;
    }

    // ---------- 录音 ----------
    public void StartRecording()
    {
        if (IsRecording)
        {
            Debug.LogWarning("[SttClient] 已在录音中");
            return;
        }
        if (IsRecognizing)
        {
            Debug.LogWarning("[SttClient] 正在识别，请稍候");
            return;
        }
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            OnError?.Invoke("未检测到麦克风设备");
            return;
        }
        int idx = deviceIndex >= 0 && deviceIndex < Microphone.devices.Length ? deviceIndex : 0;
        micDevice = Microphone.devices[idx];
        clip = Microphone.Start(micDevice, false, maxRecordSeconds, sampleRate);
        if (clip == null)
        {
            OnError?.Invoke($"麦克风启动失败（设备: {micDevice}）");
            return;
        }
        IsRecording = true;
        Debug.Log($"[SttClient] 开始录音（设备: {micDevice}，采样率: {clip.frequency}）");
        OnRecordingStarted?.Invoke();
    }

    public void StopRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;

        int pos = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);
        if (clip == null)
        {
            OnError?.Invoke("录音数据为空");
            return;
        }

        int channels = clip.channels;
        int frequency = clip.frequency > 0 ? clip.frequency : sampleRate;
        float[] buffer = new float[pos * channels];
        if (pos > 0)
        {
            clip.GetData(buffer, 0);
        }
        Destroy(clip);
        clip = null;

        if (pos <= 0 || buffer.Length == 0)
        {
            OnError?.Invoke("未采集到有效音频");
            return;
        }

        // 取单声道
        float[] mono = new float[pos];
        for (int i = 0; i < pos; i++)
        {
            mono[i] = buffer[i * channels];
        }

        float duration = (float)pos / frequency;
        if (duration < minRecordSeconds)
        {
            OnError?.Invoke($"录音太短（{duration:F1}s），已丢弃");
            return;
        }

        Debug.Log($"[SttClient] 录音结束: {duration:F1}s @ {frequency}Hz");
        OnRecordingFinished?.Invoke();
        byte[] wav = WavEncoder.Encode(mono, frequency);
        StartCoroutine(SendWav(wav));
    }

    // ---------- 识别请求 ----------
    private IEnumerator SendWav(byte[] wavBytes)
    {
        IsRecognizing = true;
        using (UnityWebRequest request = new UnityWebRequest(GetRecognizeUrl(), "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(wavBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "audio/wav");
            request.timeout = requestTimeoutSeconds;

            yield return request.SendWebRequest();

            IsRecognizing = false;
            if (request.result != UnityWebRequest.Result.Success)
            {
                string detail = request.error;
                if (!string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    detail += "\n" + request.downloadHandler.text;
                }
                OnError?.Invoke($"识别请求失败: {detail}");
                yield break;
            }

            try
            {
                SttResponse resp = JsonUtility.FromJson<SttResponse>(request.downloadHandler.text);
                if (resp == null || string.IsNullOrEmpty(resp.text))
                {
                    OnError?.Invoke("识别结果为空");
                }
                else
                {
                    OnRecognized?.Invoke(resp.text.Trim());
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"解析响应失败: {ex.Message}");
            }
        }
    }

    private string GetRecognizeUrl()
    {
        if (!string.IsNullOrEmpty(serverUrl))
        {
            return serverUrl;
        }
        return fallbackUrl;
    }
}

/// <summary>把 float 采样数组编码为 16bit PCM 单声道 WAV 字节流。</summary>
public static class WavEncoder
{
    public static byte[] Encode(float[] samples, int sampleRate)
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            int dataSize = samples.Length * 2;
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);                 // fmt chunk size
            writer.Write((short)1);           // PCM
            writer.Write((short)1);           // 单声道
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);     // byte rate
            writer.Write((short)2);           // block align
            writer.Write((short)16);          // bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            foreach (float s in samples)
            {
                short v = (short)(Mathf.Clamp(s, -1f, 1f) * 32767f);
                writer.Write(v);
            }
            return ms.ToArray();
        }
    }
}
