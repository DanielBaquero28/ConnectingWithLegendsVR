using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// Talks to the History Speaks proxy server (Spec 3). The device makes ONE
// authenticated call per turn: POST /converse with either raw WAV bytes
// (voice path) or JSON text (typed path), and gets back
// { transcript, replyText, audioBase64 }.
//
// The only values this component holds are proxyBaseUrl (not secret) and the
// per-headset revocable deviceToken — no provider API keys exist on-device.
// Both are Inspector-set, so this MUST be a scene component (runtime-added
// components would have them empty on Quest).
public class ProxyClient : MonoBehaviour
{
    [Header("Proxy (Inspector-set; see server/README.md step 8)")]
    [SerializeField] private string proxyBaseUrl = "";
    [SerializeField] private string deviceToken = "";

    [Tooltip("Logs the request URL, HTTP code, and raw response body for every " +
             "/converse attempt (deviceToken length only — never its value).")]
    [SerializeField] private bool verboseLogging = false;

#if UNITY_EDITOR
    // Editor-only overrides for local testing against a dev proxy/token. The
    // whole block is compiled out of device builds, so Quest is unaffected.
    [Header("Editor-only overrides (ignored in device builds)")]
    [SerializeField] private bool useEditorOverrides = false;
    [SerializeField] private string editorProxyBaseUrl = "";
    [SerializeField] private string editorDeviceToken = "";
#endif

    // Spec 7: the character ID comes from the hub selection instead of a
    // hardcoded constant. Only the SOURCE of the ID changed — request shape,
    // headers, retries, and timeouts are untouched. CharacterSession falls
    // back to "einstein" (with a warning) when the scene is launched directly.
    private static string CharacterId => CharacterSession.CharacterId;

    // Same retry shape as the old direct Gemini call: 3 attempts, 15s timeout,
    // 0.5s/1.5s backoff, retry only on dropped connections / 5xx.
    private const int MAX_ATTEMPTS = 3;
    private const int TIMEOUT_SECONDS = 15;
    private static readonly float[] BACKOFF = { 0.5f, 1.5f };

    [Serializable]
    public class ConverseResponse
    {
        public string transcript;
        public string replyText;
        public string audioBase64;
        public bool emptyTranscript;
    }

    [Serializable]
    private class ConverseTextRequest
    {
        public string sessionId;
        public string characterId;
        public string text;
    }

    private void Awake()
    {
#if UNITY_EDITOR
        if (useEditorOverrides)
        {
            // Only override when a value is supplied, so flipping the toggle on
            // an empty field can't blank out a real Inspector value.
            if (!string.IsNullOrEmpty(editorProxyBaseUrl)) proxyBaseUrl = editorProxyBaseUrl;
            if (!string.IsNullOrEmpty(editorDeviceToken)) deviceToken = editorDeviceToken;
        }
#endif
    }

    private void Start()
    {
        if (string.IsNullOrEmpty(proxyBaseUrl))
            Debug.LogError("[Proxy] proxyBaseUrl not set on the ProxyClient component.");
        if (string.IsNullOrEmpty(deviceToken))
            Debug.LogError("[Proxy] deviceToken not set on the ProxyClient component.");
    }

    // Voice path: POSTs the WAV file AudioRecorder saved, as raw bytes.
    public IEnumerator ConverseWithAudio(string wavPath, string sessionId,
        Action<ConverseResponse> onDone, Action onFail)
    {
        if (!File.Exists(wavPath))
        {
            Debug.LogError("[Proxy] WAV file not found at: " + wavPath);
            onFail?.Invoke();
            yield break;
        }

        // Spec 12 Part E: the WAV is deleted the moment its bytes are in memory,
        // whether or not the read succeeded — student audio never lingers on disk.
        byte[] wavBytes = null;
        try
        {
            wavBytes = File.ReadAllBytes(wavPath);
        }
        catch (Exception e)
        {
            Debug.LogError("[Proxy] Could not read WAV file: " + e.GetType().Name);
        }
        finally
        {
            AudioPromptCleanup.DeleteQuietly(wavPath, "after read");
        }
        if (wavBytes == null)
        {
            onFail?.Invoke();
            yield break;
        }

        yield return SendWithRetries(() =>
        {
            var request = new UnityWebRequest(ConverseUrl(), "POST");
            request.uploadHandler = new UploadHandlerRaw(wavBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "audio/wav");
            request.SetRequestHeader("Authorization", "Bearer " + deviceToken);
            request.SetRequestHeader("X-Session-Id", sessionId);
            request.SetRequestHeader("X-Character-Id", CharacterId);
            request.timeout = TIMEOUT_SECONDS;
            return request;
        }, onDone, onFail);
    }

    // Typed path: POSTs JSON. JsonUtility handles all string escaping.
    public IEnumerator ConverseWithText(string text, string sessionId,
        Action<ConverseResponse> onDone, Action onFail)
    {
        string json = JsonUtility.ToJson(new ConverseTextRequest
        {
            sessionId = sessionId,
            characterId = CharacterId,
            text = text,
        });
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

        yield return SendWithRetries(() =>
        {
            var request = new UnityWebRequest(ConverseUrl(), "POST");
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + deviceToken);
            request.timeout = TIMEOUT_SECONDS;
            return request;
        }, onDone, onFail);
    }

    private string ConverseUrl()
    {
        return proxyBaseUrl.TrimEnd('/') + "/converse";
    }

    // A UnityWebRequest cannot be re-sent, so each attempt builds a fresh one.
    private IEnumerator SendWithRetries(Func<UnityWebRequest> buildRequest,
        Action<ConverseResponse> onDone, Action onFail)
    {
        string jsonResponse = null;
        bool gotResponse = false;

        for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
        {
            UnityWebRequest request = buildRequest();

            if (verboseLogging)
                Debug.Log($"[ProxyClient] Attempt {attempt + 1}/{MAX_ATTEMPTS} POST {request.url} " +
                          $"(deviceToken length: {(deviceToken != null ? deviceToken.Length : 0)})");

            yield return request.SendWebRequest();

            if (verboseLogging)
            {
                // Spec 12 Part F: the raw body holds the transcript and reply, so it
                // is only ever printed in the Editor. Device builds log the size.
                string rawBody = request.downloadHandler != null ? request.downloadHandler.text : "";
#if UNITY_EDITOR
                Debug.Log($"[ProxyClient] HTTP {request.responseCode} body: {rawBody}");
#else
                Debug.Log($"[ProxyClient] HTTP {request.responseCode} body length: {(rawBody != null ? rawBody.Length : 0)}");
#endif
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                jsonResponse = request.downloadHandler.text;
                gotResponse = true;
                request.Dispose();
                break;
            }

            // Retry only on a dropped connection or a 5xx server error.
            // 401 (bad/revoked token) and other 4xx fail immediately.
            bool isProtocol5xx = request.result == UnityWebRequest.Result.ProtocolError &&
                                 request.responseCode >= 500 && request.responseCode <= 599;
            bool isConnectionError = request.result == UnityWebRequest.Result.ConnectionError;
            bool retryable = isConnectionError || isProtocol5xx;

            Debug.LogError($"[Proxy] Attempt {attempt + 1}/{MAX_ATTEMPTS} failed: {request.error} (HTTP {request.responseCode})");
            request.Dispose();

            if (retryable && attempt < MAX_ATTEMPTS - 1)
            {
                yield return new WaitForSeconds(BACKOFF[attempt]);
                continue;
            }
            break; // non-retryable, or out of attempts
        }

        if (!gotResponse)
        {
            onFail?.Invoke();
            yield break;
        }

        ConverseResponse response = null;
        try
        {
            response = JsonUtility.FromJson<ConverseResponse>(jsonResponse);
        }
        catch (Exception e)
        {
            // Type only: a parser message could echo fragments of the body.
            Debug.LogError("[Proxy] Response parse error: " + e.GetType().Name);
        }

        if (response == null)
        {
            onFail?.Invoke();
        }
        else
        {
            onDone?.Invoke(response);
        }
    }
}
