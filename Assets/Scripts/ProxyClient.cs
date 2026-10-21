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

    // 3 attempts, 0.5s/1.5s backoff, retry only on dropped connections / 5xx.
    // Spec 12 Part L: the timeout is 25s — above the server's own 20s turn
    // deadline — and a timed-out attempt (or a 504 from the server's deadline)
    // is NEVER resent: the server already spent the turn, so re-sending would
    // only bill it twice. The student sees the in-character network line.
    private const int MAX_ATTEMPTS = 3;
    private const int TIMEOUT_SECONDS = 25;
    private static readonly float[] BACKOFF = { 0.5f, 1.5f };

    [Serializable]
    public class ConverseResponse
    {
        public string transcript;
        public string replyText;
        public string audioBase64;
        public bool emptyTranscript;
        // Spec 12 Part N: true when replyText is the crisis message or the
        // safety redirect rather than a character reply. Absent on older
        // servers, which JsonUtility reads as false.
        public bool safetyRedirect;
    }

    [Serializable]
    private class ConverseTextRequest
    {
        public string sessionId;
        public string characterId;
        public string text;
    }

    // Spec 12 Part G: metadata only — the student's text is NEVER sent.
    [Serializable]
    private class CrisisReportRequest
    {
        public string sessionId;
        public string characterId;
        public string source;
    }

    // Spec 12 Part M: "Report this answer" — metadata only. The server
    // attaches the last character reply itself; the device sends no text.
    [Serializable]
    private class ReportRequest
    {
        public string sessionId;
        public string characterId;
    }

    // Shared by the fire-and-forget metadata posts (/crisis-report, /report).
    private const int METADATA_POST_ATTEMPTS = 3;
    private const int METADATA_POST_TIMEOUT_SECONDS = 5;

    // Spec 12 Part Q: Back to Hub mid-request. AbortConverse() aborts the
    // /converse attempt in flight and makes SendWithRetries return without
    // calling onDone or onFail, so a late server response is never shown.
    // One-way: the scene is being left, so it is never reset. The metadata
    // posts are not affected.
    private UnityWebRequest inFlightConverse;
    private bool converseAborted = false;

    public void AbortConverse()
    {
        converseAborted = true;
        if (inFlightConverse != null) inFlightConverse.Abort();
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

    // Spec 12 Part G: tells the server that the LOCAL SafetyFilter caught a
    // crisis on the typed path, so it is logged and alerted like a server-
    // detected one. Fire-and-forget: StartCoroutine and move on. It never
    // touches the AI controller's request lock, never blocks the UI, sends no
    // student text, and dies silently with the scene if the student leaves.
    public IEnumerator PostCrisisReport(string sessionId)
    {
        string json = JsonUtility.ToJson(new CrisisReportRequest
        {
            sessionId = sessionId,
            characterId = CharacterId,
            source = "typed",
        });
        yield return PostMetadata("/crisis-report", json, "Crisis report");
    }

    // Spec 12 Part M: the student pressed "Report this answer". Same
    // fire-and-forget shape as the crisis report: never blocks the UI, never
    // touches the request lock, carries no text — the server looks up the
    // reply it is about from the session. A 409 (nothing to report yet) or a
    // 200 "duplicate" are both final; only dropped connections and 5xx retry.
    public IEnumerator PostReport(string sessionId)
    {
        string json = JsonUtility.ToJson(new ReportRequest
        {
            sessionId = sessionId,
            characterId = CharacterId,
        });
        yield return PostMetadata("/report", json, "Answer report");
    }

    // Shared delivery loop for the metadata-only posts: 3 attempts, 5 s
    // timeout each, 0.5 s / 1.5 s backoff, retry only on dropped connections
    // or 5xx. Bodies are small JSON built by the callers; the log line never
    // includes the body or the URL's query (there is none).
    private IEnumerator PostMetadata(string path, string json, string label)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

        for (int attempt = 0; attempt < METADATA_POST_ATTEMPTS; attempt++)
        {
            var request = new UnityWebRequest(proxyBaseUrl.TrimEnd('/') + path, "POST");
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + deviceToken);
            request.timeout = METADATA_POST_TIMEOUT_SECONDS;

            yield return request.SendWebRequest();

            bool ok = request.result == UnityWebRequest.Result.Success;
            bool retryable = request.result == UnityWebRequest.Result.ConnectionError ||
                             (request.result == UnityWebRequest.Result.ProtocolError &&
                              request.responseCode >= 500 && request.responseCode <= 599);
            long code = request.responseCode;
            request.Dispose();

            if (ok)
            {
                Debug.Log($"[Proxy] {label} delivered.");
                yield break;
            }
            Debug.LogWarning($"[Proxy] {label} attempt {attempt + 1}/{METADATA_POST_ATTEMPTS} failed (HTTP {code}).");
            if (!retryable) yield break;
            if (attempt < METADATA_POST_ATTEMPTS - 1)
                yield return new WaitForSeconds(BACKOFF[attempt]);
        }
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
            if (converseAborted) yield break; // left the scene during a backoff wait

            UnityWebRequest request = buildRequest();

            if (verboseLogging)
                Debug.Log($"[ProxyClient] Attempt {attempt + 1}/{MAX_ATTEMPTS} POST {request.url} " +
                          $"(deviceToken length: {(deviceToken != null ? deviceToken.Length : 0)})");

            inFlightConverse = request;
            yield return request.SendWebRequest();
            inFlightConverse = null;

            if (converseAborted)
            {
                // Spec 12 Part Q: Back to Hub. Whatever came back is dropped.
                Debug.Log("[Proxy] /converse abandoned (leaving the scene); response ignored.");
                request.Dispose();
                yield break;
            }

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
            // Spec 12 Part L: a client-side timeout (Unity reports it as a
            // ConnectionError whose error text is "Request timeout") and a 504
            // (the server's own turn deadline) are never retried.
            bool isTimeout = request.result == UnityWebRequest.Result.ConnectionError &&
                             request.error != null &&
                             request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isGatewayTimeout = request.responseCode == 504;
            bool isProtocol5xx = request.result == UnityWebRequest.Result.ProtocolError &&
                                 request.responseCode >= 500 && request.responseCode <= 599 &&
                                 !isGatewayTimeout;
            bool isConnectionError = request.result == UnityWebRequest.Result.ConnectionError && !isTimeout;
            bool retryable = isConnectionError || isProtocol5xx;

            Debug.LogError($"[Proxy] Attempt {attempt + 1}/{MAX_ATTEMPTS} failed: {request.error} (HTTP {request.responseCode})" +
                           (isTimeout || isGatewayTimeout ? " — timed out, not retrying" : ""));
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
