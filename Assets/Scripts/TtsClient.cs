using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 调用本地 TTS 服务（FastAPI + sherpa-onnx Kokoro），POST /tts 拿 WAV 并播放。
/// 服务地址默认 http://127.0.0.1:9710/tts，同局域网可换成电脑 IP。
/// </summary>
public class TtsClient : MonoBehaviour
{
    [Header("Server")]
    public string ttsUrl = "http://127.0.0.1:9710/tts";
    [Tooltip("说话人编号 0~102")]
    public int sid = 0;
    [Range(0.5f, 2f)] public float speed = 1.0f;
    public int timeoutSeconds = 30;

    [Header("Audio")]
    [Tooltip("播放用的 AudioSource；留空则自动从本物体找")]
    public AudioSource output;

    public bool IsSpeaking { get; private set; }
    public event Action OnStartSpeaking;
    public event Action OnEndSpeaking;
    public event Action<string> OnError;

    private void Awake()
    {
        if (output == null) output = GetComponent<AudioSource>();
    }

    /// <summary>让 TTS 念出这段文本，自动播放。</summary>
    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        StartCoroutine(SpeakRoutine(text));
    }

    private IEnumerator SpeakRoutine(string text)
    {
        IsSpeaking = true;

        var payload = JsonUtility.ToJson(new TtsReq { text = text, sid = sid, speed = speed });
        using var req = new UnityWebRequest(ttsUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(payload));
        req.downloadHandler = new DownloadHandlerAudioClip(ttsUrl, AudioType.WAV);
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[TtsClient] TTS 请求失败: {req.error}");
            OnError?.Invoke(req.error);
        }
        else
        {
            var clip = ((DownloadHandlerAudioClip)req.downloadHandler).audioClip;
            if (clip != null)
            {
                if (output == null) output = GetComponent<AudioSource>();
                if (output != null)
                {
                    output.clip = clip;
                    output.Play();
                    OnStartSpeaking?.Invoke();  // 真正出声时才通知
                    // 等播完再结束（允许中途被新 Speak 打断，协程自然回收旧 clip）
                    yield return new WaitWhile(() => output != null && output.isPlaying);
                }
                else
                {
                    Debug.LogWarning("[TtsClient] 没有 AudioSource，跳过播放");
                }
            }
            else
            {
                Debug.LogWarning("[TtsClient] 返回音频为空");
            }
        }

        IsSpeaking = false;
        OnEndSpeaking?.Invoke();
    }

    [Serializable]
    private class TtsReq
    {
        public string text;
        public int sid;
        public float speed;
    }
}
