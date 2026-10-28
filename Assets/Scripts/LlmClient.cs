using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// 一条对话消息
[Serializable]
public class ChatMessage
{
    public string role;    // system / user / assistant
    public string content;
}

// 请求体（OpenAI 兼容）
[Serializable]
public class ChatRequest
{
    public string model;
    public List<ChatMessage> messages;
    public float temperature;
    public int max_tokens;
    public bool stream;
}

// 响应体（OpenAI 兼容）
[Serializable]
public class ChatResponse
{
    public List<Choice> choices;
}

[Serializable]
public class Choice
{
    public ChatMessage message;
    public string finish_reason;
}

/// <summary>
/// 通过 HTTP 调用本地 llama.cpp（OpenAI 兼容 /v1/chat/completions），维护多轮对话历史。
/// </summary>
public class LlmClient : MonoBehaviour
{
    [Header("Server")]
    public string serverUrl = "http://127.0.0.1:3000/v1/chat/completions";
    public string model = "local";
    [Range(0f, 2f)] public float temperature = 0.7f;
    public int maxTokens = 1024;
    public int timeoutSeconds = 120;

    [Header("Prompt")]
    [TextArea(2, 6)] public string systemPrompt = "你是一个乐于助人的中文助手，请用简洁的中文回答。";
    [Tooltip("如果存在，启动时自动从该文件读取人格，覆盖上面的 systemPrompt")]
    public string personaFileName = "persona.md";

    [Header("State")]
    [SerializeField] private List<ChatMessage> history = new List<ChatMessage>();
    public bool IsBusy { get; private set; }

    public event Action OnRequestStarted;
    public event Action<string> OnReplyReceived;
    public event Action<string> OnRequestFailed;

    private void Awake()
    {
        LoadPersonaFromFile();
        ResetConversation();
    }

    /// <summary>从外部文件读取人格，覆盖 systemPrompt。优先读 StreamingAssets/persona.md，其次读项目根目录 ../llama-local/persona.md。</summary>
    public void LoadPersonaFromFile()
    {
        string[] candidates = new string[]
        {
            Path.Combine(Application.streamingAssetsPath, personaFileName),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "llama-local", personaFileName)),
        };
        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    systemPrompt = File.ReadAllText(path, Encoding.UTF8);
                    Debug.Log($"[LlmClient] 已加载人格文件: {path} ({systemPrompt.Length} 字)");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LlmClient] 读取 {path} 失败: {ex.Message}");
            }
        }
        Debug.LogWarning($"[LlmClient] 未找到人格文件（尝试过: {string.Join(", ", candidates)}），使用 Inspector 默认 systemPrompt");
    }

    public void ResetConversation()
    {
        history.Clear();
        history.Add(new ChatMessage { role = "system", content = systemPrompt });
    }

    public void SendUserMessage(string userText)
    {
        if (IsBusy)
        {
            Debug.LogWarning("[LlmClient] 上一条请求还在处理中，请稍候。");
            return;
        }
        if (string.IsNullOrWhiteSpace(userText))
        {
            return;
        }
        history.Add(new ChatMessage { role = "user", content = userText.Trim() });
        StartCoroutine(PostRequest());
    }

    private IEnumerator PostRequest()
    {
        IsBusy = true;
        OnRequestStarted?.Invoke();

        ChatRequest requestData = new ChatRequest
        {
            model = model,
            messages = history,
            temperature = temperature,
            max_tokens = maxTokens,
            stream = false
        };
        string json = JsonUtility.ToJson(requestData);

        using (UnityWebRequest request = new UnityWebRequest(serverUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = timeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string detail = request.error;
                if (!string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    detail += "\n" + request.downloadHandler.text;
                }
                IsBusy = false;
                OnRequestFailed?.Invoke(detail);
                yield break;
            }

            string reply = null;
            string parseError = null;
            try
            {
                ChatResponse response = JsonUtility.FromJson<ChatResponse>(request.downloadHandler.text);
                if (response != null && response.choices != null && response.choices.Count > 0
                    && response.choices[0].message != null)
                {
                    reply = response.choices[0].message.content;
                }
                else
                {
                    parseError = "模型返回内容为空。";
                }
            }
            catch (Exception ex)
            {
                parseError = "解析响应失败：" + ex.Message;
            }

            IsBusy = false;
            if (parseError != null)
            {
                OnRequestFailed?.Invoke(parseError + "\n原始响应：" + request.downloadHandler.text);
            }
            else
            {
                history.Add(new ChatMessage { role = "assistant", content = reply });
                OnReplyReceived?.Invoke(reply);
            }
        }
    }
}
