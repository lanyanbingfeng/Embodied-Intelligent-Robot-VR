using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// 世界空间对话 UI：玩家输入文字 -> 发给 LlmClient -> 显示模型回复。
/// 配合 World Space Canvas 使用，PC 端用鼠标 + 键盘交互。
/// </summary>
public class ChatUI : MonoBehaviour
{
    [Header("References")]
    public LlmClient llm;
    public TMP_InputField inputField;
    public Button sendButton;
    public ScrollRect scrollRect;
    public RectTransform content;

    [Tooltip("语音识别客户端（可选）")]
    public SttClient stt;
    [Tooltip("TTS 客户端：模型回复后自动念出来（可选）")]
    public TtsClient tts;
    [Tooltip("按住说话：按住录音，松开识别（可选）")]
    public Button micButton;
    [Tooltip("麦克风按钮上的文字（可选）")]
    public TextMeshProUGUI micButtonLabel;

    [Header("Message Template")]
    public TextMeshProUGUI messageTemplate;

    [Header("Colors")]
    public Color userColor = new Color(0.55f, 0.80f, 1f);
    public Color assistantColor = Color.white;
    public Color errorColor = new Color(1f, 0.55f, 0.55f);
    public Color pendingColor = new Color(0.72f, 0.72f, 0.72f);

    [Header("Labels")]
    public string userLabel = "你";
    public string assistantLabel = "AI";

    private TextMeshProUGUI pendingMessage;

    private void Start()
    {
        if (llm == null) llm = GetComponent<LlmClient>();

        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);
        if (inputField != null)
        {
            inputField.onSubmit.AddListener(OnInputSubmit);
            inputField.text = string.Empty;
        }

        if (llm != null)
        {
            llm.OnRequestStarted += OnRequestStarted;
            llm.OnReplyReceived += OnReplyReceived;
            llm.OnRequestFailed += OnRequestFailed;
        }

        if (messageTemplate != null) messageTemplate.gameObject.SetActive(false);

        if (micButton != null)
        {
            EventTrigger trigger = micButton.gameObject.GetComponent<EventTrigger>();
            if (trigger == null) trigger = micButton.gameObject.AddComponent<EventTrigger>();
            EventTrigger.Entry down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => OnMicDown());
            EventTrigger.Entry up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            up.callback.AddListener(_ => OnMicUp());
            trigger.triggers.Add(down);
            trigger.triggers.Add(up);
        }
        if (stt == null) stt = GetComponent<SttClient>();
        if (stt != null)
        {
            stt.OnRecognized += OnRecognized;
            stt.OnError += OnSttError;
        }

        AddMessage(assistantLabel, "你好，我是本地 AI 助手，有什么可以帮你的？",
            assistantColor, TextAlignmentOptions.Left);
        ScrollToBottom();
    }

    private void OnDestroy()
    {
        if (llm != null)
        {
            llm.OnRequestStarted -= OnRequestStarted;
            llm.OnReplyReceived -= OnReplyReceived;
            llm.OnRequestFailed -= OnRequestFailed;
        }
        if (stt != null)
        {
            stt.OnRecognized -= OnRecognized;
            stt.OnError -= OnSttError;
        }
    }

    private void OnSendClicked()
    {
        SendCurrentInput();
    }

    // PC 端在输入框按回车
    private void OnInputSubmit(string text)
    {
        SendCurrentInput();
    }

    private void SendCurrentInput()
    {
        if (llm == null || llm.IsBusy) return;
        string text = inputField != null ? inputField.text : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        AddMessage(userLabel, text.Trim(), userColor, TextAlignmentOptions.Right);
        inputField.text = string.Empty;
        inputField.ActivateInputField();
        llm.SendUserMessage(text);
    }

    private void OnRequestStarted()
    {
        CleanupTtsWait();
        SetInteractable(false);
        pendingMessage = AddMessage(assistantLabel, "正在思考…", pendingColor, TextAlignmentOptions.Left);
        ScrollToBottom();
    }

    private void OnReplyReceived(string reply)
    {
        // 如果有 TTS：先不显示文字，pendingMessage 保持"语音生成中…"转圈，
        // 等 TTS 真正出声（OnStartSpeaking）再把文字显示出来。
        if (tts != null)
        {
            pendingReply = reply;
            if (pendingMessage != null)
            {
                pendingMessage.text = "语音生成中";
                pendingMessage.color = pendingColor;
            }
            StartCoroutine(DotsRoutine());
            tts.OnStartSpeaking += HandleTtsStarted;
            tts.OnError += HandleTtsError;
            tts.Speak(reply);
        }
        else
        {
            ShowReply(reply);
        }
    }

    private string pendingReply;
    private Coroutine dotsRoutine;

    private IEnumerator DotsRoutine()
    {
        while (true)
        {
            for (int i = 0; i <= 3; i++)
            {
                if (pendingMessage != null)
                    pendingMessage.text = "语音生成中" + new string('.', i);
                yield return new WaitForSeconds(0.3f);
            }
        }
    }

    private void HandleTtsStarted()
    {
        CleanupTtsWait();
        ShowReply(pendingReply);
    }

    private void HandleTtsError(string err)
    {
        Debug.LogWarning("[ChatUI] TTS 失败，直接显示文字: " + err);
        CleanupTtsWait();
        ShowReply(pendingReply);
    }

    private void CleanupTtsWait()
    {
        if (dotsRoutine != null) { StopCoroutine(dotsRoutine); dotsRoutine = null; }
        if (tts != null)
        {
            tts.OnStartSpeaking -= HandleTtsStarted;
            tts.OnError -= HandleTtsError;
        }
    }

    private void ShowReply(string reply)
    {
        if (pendingMessage != null)
        {
            pendingMessage.text = FormatMessage(assistantLabel, reply);
            pendingMessage.color = assistantColor;
            pendingMessage = null;
        }
        else
        {
            AddMessage(assistantLabel, reply, assistantColor, TextAlignmentOptions.Left);
        }
        SetInteractable(true);
        if (inputField != null) inputField.ActivateInputField();
        ScrollToBottom();
    }

    private void OnRequestFailed(string error)
    {
        if (pendingMessage != null)
        {
            pendingMessage.text = FormatMessage(assistantLabel, "（出错了）" + error);
            pendingMessage.color = errorColor;
            pendingMessage = null;
        }
        else
        {
            AddMessage(assistantLabel, "（出错了）" + error, errorColor, TextAlignmentOptions.Left);
        }
        SetInteractable(true);
        if (inputField != null) inputField.ActivateInputField();
        ScrollToBottom();
    }

    private void SetInteractable(bool interactable)
    {
        if (sendButton != null) sendButton.interactable = interactable;
        if (inputField != null) inputField.interactable = interactable;
    }

    private TextMeshProUGUI AddMessage(string speaker, string text, Color color, TextAlignmentOptions alignment)
    {
        if (messageTemplate == null || content == null) return null;

        TextMeshProUGUI msg = Instantiate(messageTemplate, content);
        msg.gameObject.SetActive(true);
        msg.text = FormatMessage(speaker, text);
        msg.color = color;
        msg.alignment = alignment;
        msg.textWrappingMode = TextWrappingModes.Normal;
        ScrollToBottom();
        return msg;
    }

    private string FormatMessage(string speaker, string text)
    {
        return "<b>" + speaker + "：</b>" + text;
    }

    private void ScrollToBottom()
    {
        if (scrollRect == null) return;
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
    }

    private void OnMicDown()
    {
        if (stt == null || stt.IsRecognizing) return;
        stt.StartRecording();
        if (micButtonLabel != null) micButtonLabel.text = "聆听中…";
    }

    private void OnMicUp()
    {
        if (stt == null) return;
        stt.StopRecording();
        if (micButtonLabel != null) micButtonLabel.text = "语音";
    }

    private void OnRecognized(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (inputField != null) inputField.text = text;
        SendCurrentInput();
    }

    private void OnSttError(string error)
    {
        Debug.LogWarning("[ChatUI] 语音识别失败: " + error);
        AddMessage(assistantLabel, "（语音识别失败）" + error, errorColor, TextAlignmentOptions.Left);
        SetInteractable(true);
        if (micButtonLabel != null) micButtonLabel.text = "语音";
    }
}
